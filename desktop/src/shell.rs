//! Everything the desktop shows around the engine: the main window (the React interface served by the engine),
//! the tray icon and its menu, shortcuts and Windows startup entries. The overlay lives in overlay.rs.
//!
//! The engine owns all data and settings. The shell re-reads the settings on every "settings" change the engine
//! announces (including the ones it makes itself from the tray, the shortcuts or the overlay) and applies them.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde_json::{json, Value};
use tauri::menu::{CheckMenuItemBuilder, Menu, MenuBuilder, MenuItemBuilder};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::webview::Color;
use tauri::{AppHandle, Manager, RunEvent, Theme, Url, WebviewUrl, WebviewWindowBuilder, WindowEvent, Wry};
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_global_shortcut::{GlobalShortcutExt, Shortcut, ShortcutState};
use tauri_plugin_notification::NotificationExt;
use tauri_plugin_window_state::{AppHandleExt, StateFlags};

use crate::engine::{self, Engine, EngineEvent};
use crate::{hotkeys, overlay, startup, win, Args};

const TITLE: &str = "Chest Counter Squad";
const NIGHT: Color = Color(0x0E, 0x0B, 0x1D, 0xFF);

pub struct Shell {
    pub data_dir: PathBuf,
    pub key: String,
    pub engine: Engine,
    pub overlay: overlay::OverlayState,
    settings: Mutex<Value>,
    menu: Mutex<Option<Menu<Wry>>>,
    hotkeys: Mutex<HashMap<&'static str, (String, Shortcut)>>,
    startup_applied: Mutex<Option<(bool, bool)>>,
    quitting: AtomicBool,
    refresh_queued: AtomicBool,
}

pub type ShellState = Arc<Shell>;

impl Shell {
    pub fn settings(&self) -> Value {
        self.settings.lock().unwrap().clone()
    }

    pub fn menu(&self) -> Option<Menu<Wry>> {
        self.menu.lock().unwrap().clone()
    }

    /// The token reaches the page by script injection, only on the engine's own origin; never in the address.
    pub fn token_script(&self) -> String {
        format!(
            "if (location.origin === {}) {{ window.__CC_TOKEN = {}; }}",
            json!(self.engine.origin()),
            json!(self.engine.token)
        )
    }

    pub fn is_ours(&self, url: &Url) -> bool {
        url.as_str().starts_with(&self.engine.url) || url.as_str() == "about:blank"
    }

    pub fn webview_dir(&self) -> PathBuf {
        self.data_dir.join("webview")
    }
}

/// The two languages of the interface, for the few strings the shell draws itself.
pub fn t<'a>(settings: &Value, en: &'a str, fr: &'a str) -> &'a str {
    if settings["language"].as_str() == Some("fr") {
        fr
    } else {
        en
    }
}

fn engine_exe() -> PathBuf {
    // CC_ENGINE: another engine build (development).
    if let Some(p) = std::env::var_os("CC_ENGINE") {
        return PathBuf::from(p);
    }
    let here = std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)).unwrap_or_default();
    here.join("engine").join("ChestCounterEngine.exe")
}

pub fn run(args: Args, key: String) -> i32 {
    let (engine, events) = match Engine::start(&engine_exe(), &args.data_dir) {
        Ok(started) => started,
        Err(message) => {
            win::error_box(TITLE, &format!("Chest Counter Squad could not start.\n\n{message}"));
            return 1;
        }
    };
    let settings = engine.get("api/settings").unwrap_or_else(|_| json!({}));
    let minimized = args.minimized || settings["startMinimized"].as_bool() == Some(true);
    let shell: ShellState = Arc::new(Shell {
        data_dir: args.data_dir,
        key,
        engine,
        overlay: overlay::OverlayState::default(),
        settings: Mutex::new(settings),
        menu: Mutex::new(None),
        hotkeys: Mutex::new(HashMap::new()),
        startup_applied: Mutex::new(None),
        quitting: AtomicBool::new(false),
        refresh_queued: AtomicBool::new(false),
    });

    let app = tauri::Builder::default()
        .plugin(
            tauri_plugin_window_state::Builder::default()
                .with_denylist(&["overlay"])
                .with_state_flags(StateFlags::all() - StateFlags::VISIBLE)
                .build(),
        )
        .plugin(tauri_plugin_global_shortcut::Builder::new().build())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_notification::init())
        .manage(shell.clone())
        .invoke_handler(tauri::generate_handler![overlay::overlay_layout, overlay::overlay_scale, overlay::overlay_menu])
        .setup(move |app| {
            let handle = app.handle().clone();
            let shell = handle.state::<ShellState>().inner().clone();

            let menu = build_menu(&handle, &shell.settings())?;
            TrayIconBuilder::with_id("tray")
                .icon(app.default_window_icon().cloned().expect("bundle icon"))
                .tooltip(TITLE)
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                        show_main(tray.app_handle());
                    }
                })
                .build(app)?;
            *shell.menu.lock().unwrap() = Some(menu);

            overlay::create(&handle, &shell)?;
            if !minimized {
                show_main(&handle);
            }

            // Engine events, until it exits.
            let h = handle.clone();
            std::thread::spawn(move || {
                engine::read_events(events, |event| on_engine_event(&h, event));
                let shell = h.state::<ShellState>();
                if !shell.quitting.load(Ordering::SeqCst) {
                    win::error_box(TITLE, "The Chest Counter engine stopped unexpectedly. The application will close.");
                    h.exit(1);
                }
            });

            // A second launch (desktop shortcut, the standby) only asks this one to show itself.
            if let Some(show) = win::event(&format!(r"Local\{}-show", shell.key)) {
                let h = handle.clone();
                std::thread::spawn(move || loop {
                    if show.wait(None) {
                        let h2 = h.clone();
                        let _ = h.run_on_main_thread(move || show_main(&h2));
                    }
                });
            }

            queue_refresh(&handle);
            Ok(())
        })
        .on_menu_event(|app, event| on_menu(app, event.id().as_ref()))
        .on_window_event(|window, event| match (window.label(), event) {
            ("main", WindowEvent::CloseRequested { api, .. }) => {
                let app = window.app_handle();
                let shell = app.state::<ShellState>();
                if shell.quitting.load(Ordering::SeqCst) {
                    return;
                }
                api.prevent_close();
                let _ = app.save_window_state(StateFlags::all() - StateFlags::VISIBLE);
                if shell.settings()["minimizeToTray"].as_bool().unwrap_or(true) {
                    let _ = window.hide();
                    hint_tray_once(app);
                } else {
                    quit(app);
                }
            }
            ("overlay", WindowEvent::Moved(pos)) => overlay::moved(window.app_handle(), *pos),
            ("overlay", WindowEvent::ScaleFactorChanged { .. }) => overlay::place(window.app_handle()),
            _ => {}
        })
        .build(tauri::generate_context!());

    let app = match app {
        Ok(app) => app,
        Err(e) => {
            shell.quitting.store(true, Ordering::SeqCst);
            shell.engine.stop();
            win::error_box(TITLE, &format!("The window could not be created.\n\n{e}"));
            return 1;
        }
    };

    let code = app.run_return(|app, event| {
        if let RunEvent::ExitRequested { api, code, .. } = event {
            // No window open is not a reason to stop: the app lives in the tray.
            if code.is_none() && !app.state::<ShellState>().quitting.load(Ordering::SeqCst) {
                api.prevent_exit();
            }
        }
    });
    shell.quitting.store(true, Ordering::SeqCst);
    shell.engine.stop();
    code
}

fn on_engine_event(app: &AppHandle, event: EngineEvent) {
    let shell = app.state::<ShellState>().inner().clone();
    match event {
        // Only settings matter to the shell: the pages reload their own data from the engine's event stream.
        EngineEvent::Changed { topic } if topic == "settings" => queue_refresh(app),
        EngineEvent::PickFolder { id, start } => {
            let settings = shell.settings();
            let mut dialog = app.dialog().file().set_title(t(&settings, "Folder where Fortnite saves its replays", "Dossier où Fortnite enregistre ses replays"));
            if let Some(start) = start {
                dialog = dialog.set_directory(start);
            }
            if let Some(main) = app.get_webview_window("main") {
                dialog = dialog.set_parent(&main);
            }
            dialog.pick_folder(move |picked| {
                let path = picked.and_then(|p| p.into_path().ok()).map(|p| p.display().to_string());
                shell.engine.send(json!({ "cmd": "pickedFolder", "id": id, "path": path }));
            });
        }
        _ => {}
    }
}

/// Coalesces bursts (the overlay sliders write settings many times a second).
fn queue_refresh(app: &AppHandle) {
    let shell = app.state::<ShellState>().inner().clone();
    if shell.refresh_queued.swap(true, Ordering::SeqCst) {
        return;
    }
    let app = app.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(80));
        shell.refresh_queued.store(false, Ordering::SeqCst);
        match shell.engine.get("api/settings") {
            Ok(settings) => {
                *shell.settings.lock().unwrap() = settings.clone();
                apply_settings(&app, &shell, &settings);
            }
            Err(e) => eprintln!("settings: {e}"),
        }
    });
}

/// What lives outside the engine: shortcuts, Windows startup entries, the tray menu, the overlay window.
fn apply_settings(app: &AppHandle, shell: &Shell, s: &Value) {
    let o = &s["overlay"];
    set_hotkey(app, shell, "toggle", o["toggleHotkey"].as_str().unwrap_or(""), "enabled");
    set_hotkey(app, shell, "lock", o["lockHotkey"].as_str().unwrap_or(""), "locked");

    // Only when these two changed.
    let wanted = (s["launchWithWindows"].as_bool().unwrap_or(false), s["launchWithFortnite"].as_bool().unwrap_or(false));
    let mut applied = shell.startup_applied.lock().unwrap();
    if *applied != Some(wanted) {
        *applied = Some(wanted);
        if let Some(error) = startup::apply(wanted.0, wanted.1, &shell.data_dir, &shell.key) {
            eprintln!("startup: {error}");
        }
    }
    drop(applied);

    if let Ok(menu) = build_menu(app, s) {
        if let Some(tray) = app.tray_by_id("tray") {
            let _ = tray.set_menu(Some(menu.clone()));
        }
        *shell.menu.lock().unwrap() = Some(menu);
    }

    overlay::apply_settings(app, s);
}

/// Registers (or re-registers when the combination changed) one overlay shortcut, which flips `field`.
fn set_hotkey(app: &AppHandle, shell: &Shell, id: &'static str, combo: &str, field: &'static str) {
    let mut registered = shell.hotkeys.lock().unwrap();
    if registered.get(id).is_some_and(|(c, _)| c == combo) {
        return;
    }
    if let Some((_, old)) = registered.remove(id) {
        let _ = app.global_shortcut().unregister(old);
    }
    let Some(shortcut) = hotkeys::parse(combo) else {
        eprintln!("overlay: shortcut not usable: {combo}");
        return;
    };
    let result = app.global_shortcut().on_shortcut(shortcut, move |app, _, event| {
        if event.state == ShortcutState::Pressed {
            flip_overlay(app, field);
        }
    });
    match result {
        Ok(()) => {
            registered.insert(id, (combo.to_string(), shortcut));
        }
        // Used by another program: the settings page shows the same combination; nothing else to do.
        Err(e) => eprintln!("overlay: shortcut not available ({combo}): {e}"),
    }
}

/// Changes the overlay settings through the engine (which announces the change, and the shell then applies it).
pub fn update_overlay(app: &AppHandle, change: impl FnOnce(&mut serde_json::Map<String, Value>) + Send + 'static) {
    let shell = app.state::<ShellState>().inner().clone();
    std::thread::spawn(move || {
        let Ok(settings) = shell.engine.get("api/settings") else { return };
        let Some(mut overlay) = settings["overlay"].as_object().cloned() else { return };
        change(&mut overlay);
        if let Err(e) = shell.engine.post("api/settings", json!({ "overlay": overlay })) {
            eprintln!("overlay settings: {e}");
        }
    });
}

fn flip_overlay(app: &AppHandle, field: &'static str) {
    update_overlay(app, move |o| {
        let on = o.get(field).and_then(Value::as_bool).unwrap_or(false);
        o.insert(field.into(), Value::Bool(!on));
    });
}

fn build_menu(app: &AppHandle, s: &Value) -> tauri::Result<Menu<Wry>> {
    let o = &s["overlay"];
    let enabled = o["enabled"].as_bool().unwrap_or(false);
    let locked = o["locked"].as_bool().unwrap_or(false);
    // Text after a tab is drawn as the shortcut, right-aligned.
    let with_key = |text: &str, key: &Value| match key.as_str() {
        Some(k) if !k.is_empty() => format!("{text}\t{k}"),
        _ => text.to_string(),
    };
    MenuBuilder::new(app)
        .item(&MenuItemBuilder::with_id("open", t(s, "Open Chest Counter", "Ouvrir Chest Counter")).build(app)?)
        .separator()
        .item(
            &CheckMenuItemBuilder::with_id("overlay", with_key(t(s, "Show the overlay", "Afficher l'overlay"), &o["toggleHotkey"]))
                .checked(enabled)
                .build(app)?,
        )
        .item(
            &CheckMenuItemBuilder::with_id(
                "lock",
                with_key(t(s, "Lock the overlay (click-through)", "Verrouiller l'overlay (clic au travers)"), &o["lockHotkey"]),
            )
            .checked(locked)
            .enabled(enabled)
            .build(app)?,
        )
        .item(&MenuItemBuilder::with_id("scan", t(s, "Scan for new replays", "Chercher de nouveaux replays")).build(app)?)
        .separator()
        .item(&MenuItemBuilder::with_id("quit", t(s, "Quit", "Quitter")).build(app)?)
        .build()
}

fn on_menu(app: &AppHandle, id: &str) {
    match id {
        "open" => show_main(app),
        "overlay" => flip_overlay(app, "enabled"),
        "lock" => flip_overlay(app, "locked"),
        "scan" => {
            let shell = app.state::<ShellState>().inner().clone();
            std::thread::spawn(move || {
                let _ = shell.engine.post("api/actions/scan", json!({}));
            });
        }
        "quit" => quit(app),
        _ => {}
    }
}

/// Shows the main window, creating it on first show only: starting minimized to the tray costs no browser.
pub fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
        return;
    }
    let shell = app.state::<ShellState>().inner().clone();
    let Ok(url) = Url::parse(&shell.engine.url) else { return };
    let nav = shell.clone();
    let created = WebviewWindowBuilder::new(app, "main", WebviewUrl::External(url))
        .title(TITLE)
        .inner_size(1280.0, 820.0)
        .min_inner_size(900.0, 600.0)
        .center()
        .theme(Some(Theme::Dark))
        .background_color(NIGHT)
        .data_directory(shell.webview_dir())
        .initialization_script(&shell.token_script())
        // The interface handles its own file drops (import).
        .disable_drag_drop_handler()
        .on_navigation(move |url| {
            if nav.is_ours(url) {
                return true;
            }
            open_external(url);
            false
        })
        .build();
    match created {
        Ok(window) => {
            let _ = window.set_focus();
        }
        Err(e) => win::error_box(TITLE, &format!("The window could not be created.\n\n{e}")),
    }
}

/// Links outside the interface (server docs, invite pages) go to the default browser.
pub fn open_external(url: &Url) {
    if matches!(url.scheme(), "http" | "https") {
        let _ = tauri_plugin_opener::open_url(url.as_str(), None::<&str>);
    }
}

fn hint_tray_once(app: &AppHandle) {
    let shell = app.state::<ShellState>();
    let marker = shell.data_dir.join("tray-hint-shown");
    if marker.exists() {
        return;
    }
    let _ = std::fs::write(&marker, b"1");
    let s = shell.settings();
    let _ = app
        .notification()
        .builder()
        .title(TITLE)
        .body(t(&s, "Still counting in the background. Right-click the tray icon to quit.", "Le comptage continue en arrière-plan. Clic droit sur l'icône pour quitter."))
        .show();
}

pub fn quit(app: &AppHandle) {
    let shell = app.state::<ShellState>().inner().clone();
    if shell.quitting.swap(true, Ordering::SeqCst) {
        return;
    }
    let _ = app.save_window_state(StateFlags::all() - StateFlags::VISIBLE);
    let app = app.clone();
    std::thread::spawn(move || {
        shell.engine.stop();
        app.exit(0);
    });
}
