//! The .NET engine (src/ChestCounter.Host) as a child process: replay tracking, history, squad sync and the local
//! HTTP API the interface talks to. One JSON object per line each way (see that project's Program.cs). The engine
//! exits when its stdin closes, so it never outlives the shell, even when the shell is killed.

use std::io::{BufRead, BufReader, Write};
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde::Deserialize;
use serde_json::{json, Value};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// What the engine says on stdout.
#[derive(Debug, Deserialize)]
#[serde(tag = "event", rename_all = "camelCase")]
pub enum EngineEvent {
    Ready { url: String, token: String },
    Changed { topic: String },
    PickFolder { id: i64, start: Option<String> },
    Fatal { message: String },
}

pub struct Engine {
    /// "http://127.0.0.1:47821/" (always ends with a slash).
    pub url: String,
    pub token: String,
    stdin: Mutex<Option<ChildStdin>>,
    child: Mutex<Child>,
    agent: ureq::Agent,
}

impl Engine {
    /// Starts the engine and waits for it to listen. Returns the engine and its stdout, which carries the events
    /// that follow "ready".
    pub fn start(exe: &Path, data_dir: &Path) -> Result<(Engine, BufReader<ChildStdout>), String> {
        let mut child = Command::new(exe)
            .arg("--data")
            .arg(data_dir)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|e| format!("Could not start the engine ({}): {e}", exe.display()))?;
        let mut stdout = BufReader::new(child.stdout.take().expect("piped"));
        let stdin = child.stdin.take();

        let mut line = String::new();
        loop {
            line.clear();
            match stdout.read_line(&mut line) {
                Ok(0) | Err(_) => {
                    let _ = child.wait();
                    return Err("The engine stopped while starting.".into());
                }
                Ok(_) => {}
            }
            match serde_json::from_str::<EngineEvent>(line.trim()) {
                Ok(EngineEvent::Ready { url, token }) => {
                    let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(30)).build();
                    let engine = Engine { url, token, stdin: Mutex::new(stdin), child: Mutex::new(child), agent };
                    return Ok((engine, stdout));
                }
                Ok(EngineEvent::Fatal { message }) => {
                    let _ = child.wait();
                    return Err(message);
                }
                _ => {} // nothing else comes before "ready"
            }
        }
    }

    /// The origin the interface is served from, for the token injection: "http://127.0.0.1:47821".
    pub fn origin(&self) -> String {
        self.url.trim_end_matches('/').to_string()
    }

    pub fn send(&self, message: Value) {
        if let Some(stdin) = self.stdin.lock().unwrap().as_mut() {
            let _ = writeln!(stdin, "{message}");
            let _ = stdin.flush();
        }
    }

    pub fn get(&self, path: &str) -> Result<Value, String> {
        self.agent
            .get(&format!("{}{}", self.url, path.trim_start_matches('/')))
            .set("X-CC-Token", &self.token)
            .call()
            .map_err(|e| e.to_string())?
            .into_json()
            .map_err(|e| e.to_string())
    }

    pub fn post(&self, path: &str, body: Value) -> Result<Value, String> {
        self.agent
            .post(&format!("{}{}", self.url, path.trim_start_matches('/')))
            .set("X-CC-Token", &self.token)
            .send_json(body)
            .map_err(|e| e.to_string())?
            .into_json()
            .map_err(|e| e.to_string())
    }

    /// Asks the engine to stop (it saves and closes its database), and waits a little for it.
    pub fn stop(&self) {
        self.send(json!({ "cmd": "quit" }));
        self.stdin.lock().unwrap().take(); // closing stdin is the second way to say it
        let mut child = self.child.lock().unwrap();
        let until = Instant::now() + Duration::from_secs(5);
        while Instant::now() < until {
            if let Ok(Some(_)) = child.try_wait() {
                return;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        let _ = child.kill();
    }
}

/// Reads the engine's events until it exits, handing each one to `on_event`. Returns when stdout closes.
pub fn read_events(stdout: BufReader<ChildStdout>, mut on_event: impl FnMut(EngineEvent)) {
    for line in stdout.lines() {
        let Ok(line) = line else { break };
        if let Ok(event) = serde_json::from_str::<EngineEvent>(line.trim()) {
            on_event(event);
        }
    }
}
