//! Desktop entry point (Tauri shell around the .NET engine).
//!   ChestCounterSquad.exe [--minimized] [--data <dir>]   the application (one per data folder)
//!   ChestCounterSquad.exe --standby [--data <dir>]       the invisible "start with Fortnite" watcher

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod engine;
mod hotkeys;
mod overlay;
mod shell;
mod startup;
mod win;

use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

pub fn default_data_dir() -> PathBuf {
    let appdata = std::env::var_os("APPDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
    appdata.join("ChestCounterSquad")
}

fn full_path(p: &Path) -> PathBuf {
    std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf())
}

fn normalized(p: &Path) -> String {
    full_path(p).display().to_string().trim_end_matches(['\\', '/']).to_uppercase()
}

pub fn same_path(a: &Path, b: &Path) -> bool {
    normalized(a) == normalized(b)
}

/// Names of the per-data-folder kernel objects (mutex, "show yourself" event, standby). Same names as the former
/// WinForms shell, so an old standby still finds a new application.
pub fn instance_key(data_dir: &Path) -> String {
    let hash = Sha256::digest(normalized(data_dir).as_bytes());
    let hex: String = hash.iter().map(|b| format!("{b:02X}")).collect();
    format!("ChestCounterSquad-{}", &hex[..16])
}

pub struct Args {
    pub data_dir: PathBuf,
    pub minimized: bool,
    pub standby: bool,
}

fn parse_args() -> Args {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let has = |name: &str| args.iter().any(|a| a.eq_ignore_ascii_case(name));
    let value = |name: &str| args.iter().position(|a| a.eq_ignore_ascii_case(name)).and_then(|i| args.get(i + 1)).cloned();
    Args {
        data_dir: full_path(&value("--data").map(PathBuf::from).unwrap_or_else(default_data_dir)),
        minimized: has("--minimized"),
        standby: has("--standby"),
    }
}

fn main() {
    let args = parse_args();
    let key = instance_key(&args.data_dir);

    if args.standby {
        std::process::exit(startup::run_standby(&args.data_dir, &key));
    }

    // Started by the standby, this process inherits its Idle priority class: put it back to normal.
    win::set_idle_priority(false);

    let Some(single) = win::create_owned_mutex(&format!(r"Local\{key}")) else {
        // Already open for this data folder: bring that window forward instead of starting a second engine.
        win::signal_event(&format!(r"Local\{key}-show"));
        return;
    };

    let code = shell::run(args, key);
    drop(single);
    std::process::exit(code);
}

