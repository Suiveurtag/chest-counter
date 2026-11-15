//! "Start with Windows" and "start with Fortnite", through the per-user Run key (no admin rights, no scheduled
//! task), and the standby watcher behind the second one.

use std::path::{Path, PathBuf};
use std::process::Command;
use std::os::windows::process::CommandExt;
use std::time::Duration;

use winreg::enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE};
use winreg::RegKey;

use crate::win;

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const DETACHED_PROCESS: u32 = 0x0000_0008;

/// The installed executable, or None in a debug build (development): nothing is registered then.
pub fn exe() -> Option<PathBuf> {
    if cfg!(debug_assertions) {
        return None;
    }
    std::env::current_exe().ok()
}

fn is_default(data_dir: &Path) -> bool {
    crate::same_path(data_dir, &crate::default_data_dir())
}

/// ` --data "C:\..."` for a non-default data folder, else nothing.
pub fn data_arg(data_dir: &Path) -> String {
    if is_default(data_dir) {
        String::new()
    } else {
        format!(" --data \"{}\"", data_dir.display().to_string().trim_end_matches('\\'))
    }
}

fn data_args(data_dir: &Path) -> Vec<String> {
    if is_default(data_dir) {
        vec![]
    } else {
        vec!["--data".into(), data_dir.display().to_string()]
    }
}

/// Run value names: one pair per data folder, so a second (test) profile does not overwrite the first.
fn value_name(data_dir: &Path, key: &str, suffix: &str) -> String {
    let tail = if is_default(data_dir) { String::new() } else { format!("-{}", &key[key.len() - 8..]) };
    format!("ChestCounterSquad{suffix}{tail}")
}

/// Writes or removes the Run entries and starts or stops the standby. Returns an error message, or None.
pub fn apply(with_windows: bool, with_fortnite: bool, data_dir: &Path, key: &str) -> Option<String> {
    let Some(exe) = exe() else {
        return (with_windows || with_fortnite).then(|| "startup options need the release build of ChestCounterSquad.exe".into());
    };
    let exe_s = exe.display().to_string();
    let result = (|| -> std::io::Result<()> {
        let (run, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey_with_flags(RUN_KEY, KEY_READ | KEY_WRITE)?;
        set(&run, &value_name(data_dir, key, ""), with_windows.then(|| format!("\"{exe_s}\" --minimized{}", data_arg(data_dir))))?;
        set(&run, &value_name(data_dir, key, "-Standby"), with_fortnite.then(|| format!("\"{exe_s}\" --standby{}", data_arg(data_dir))))?;
        Ok(())
    })();
    if let Err(e) = result {
        return Some(e.to_string());
    }
    if with_fortnite {
        standby_ensure_running(&exe, data_dir, key);
    } else {
        standby_stop(key);
    }
    None
}

fn set(run: &RegKey, name: &str, value: Option<String>) -> std::io::Result<()> {
    match value {
        None => match run.delete_value(name) {
            Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(e),
            _ => Ok(()),
        },
        // Refreshed on every start: follows the exe when it is moved.
        Some(v) if run.get_value::<String, _>(name).ok().as_deref() != Some(v.as_str()) => run.set_value(name, &v),
        Some(_) => Ok(()),
    }
}

// ---- the standby ----
//
// Windows cannot start a program when another one starts without admin rights, so a tiny windowless process started
// at logon checks every few seconds whether Fortnite is running, and opens the application when it is and the
// application is not open. It reads nothing from the game: it only looks at the list of running programs. Idea taken
// from LED-N/Fortnite-ChestCounter (FortniteWatch.cs), rewritten.

/// CC_GAME_PROCESS: another process name, to test the standby without Fortnite.
fn game_process() -> String {
    match std::env::var("CC_GAME_PROCESS") {
        Ok(name) if !name.is_empty() => if name.to_ascii_lowercase().ends_with(".exe") { name } else { format!("{name}.exe") },
        _ => "FortniteClient-Win64-Shipping.exe".into(),
    }
}

fn mutex_name(key: &str) -> String {
    format!(r"Local\{key}-standby")
}

fn stop_name(key: &str) -> String {
    format!(r"Local\{key}-standby-stop")
}

fn standby_ensure_running(exe: &Path, data_dir: &Path, key: &str) {
    if win::mutex_exists(&mutex_name(key)) {
        return;
    }
    // Started again at next logon if this fails.
    let _ = Command::new(exe)
        .arg("--standby")
        .args(data_args(data_dir))
        .creation_flags(CREATE_NO_WINDOW | DETACHED_PROCESS)
        .spawn();
}

fn standby_stop(key: &str) {
    win::signal_event(&stop_name(key));
}

/// The standby process itself (`--standby`). Returns its exit code.
pub fn run_standby(data_dir: &Path, key: &str) -> i32 {
    let Some(single) = win::mutex(&mutex_name(key)) else { return 1 };
    if !single.wait(Some(Duration::ZERO)) {
        return 0; // already waiting
    }
    let Some(stop) = win::event(&stop_name(key)) else { return 1 };
    win::set_idle_priority(true);
    let Ok(exe) = std::env::current_exe() else { return 1 };
    let game = game_process();

    loop {
        if stop.wait(Some(Duration::from_secs(5))) {
            return 0;
        }
        if !win::process_running(&game) {
            continue;
        }
        if !win::mutex_exists(&format!(r"Local\{key}")) {
            let _ = Command::new(&exe).arg("--minimized").args(data_args(data_dir)).spawn();
        }
        // Arm again only for the next Fortnite launch: the player may have closed the application on purpose.
        while win::process_running(&game) {
            if stop.wait(Some(Duration::from_secs(10))) {
                return 0;
            }
        }
    }
}
