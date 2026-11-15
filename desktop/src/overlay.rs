//! The in-game counter window: transparent, always on top, never focused, out of the taskbar and Alt+Tab. It never
//! touches the game process: it is an ordinary desktop window, so it shows over Fortnite in windowed and borderless
//! modes only (not exclusive fullscreen). The page (web/src/overlay.ts, served by the engine) draws the figures and
//! reports its size; this side places the window, shows or hides it, and makes the mouse pass through when locked.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::Duration;

use serde_json::Value;
use tauri::webview::Color;
use tauri::{AppHandle, LogicalSize, Manager, PhysicalPosition, Url, WebviewUrl, WebviewWindow, WebviewWindowBuilder};

use crate::shell::{self, ShellState};
use crate::win;

const LABEL: &str = "overlay";

#[derive(Default)]
pub struct OverlayState {
    enabled: AtomicBool,
    has_content: AtomicBool,
    visible: AtomicBool,
    locked: Mutex<Option<bool>>,
    /// The saved position (screen pixels, top-left corner), when there is one.
    saved: Mutex<Option<(i32, i32)>>,
    /// Where this side last put the window: a "moved" event to that spot is ours, not the player's.
    placed: Mutex<Option<(i32, i32)>>,
    move_generation: AtomicU64,
}

fn window(app: &AppHandle) -> Option<WebviewWindow> {
    app.get_webview_window(LABEL)
}

pub fn create(app: &AppHandle, shell: &ShellState) -> tauri::Result<()> {
    let url = Url::parse(&format!("{}overlay.html", shell.engine.url)).expect("engine url");
    let nav = shell.clone();
    let w = WebviewWindowBuilder::new(app, LABEL, WebviewUrl::External(url))
        .title("Chest Counter overlay")
        .decorations(false)
        .transparent(true)
        .shadow(false)
        .always_on_top(true)
        .skip_taskbar(true)
        .resizable(false)
        .focused(false)
        .visible(false)
        .inner_size(10.0, 10.0)
        .background_color(Color(0, 0, 0, 0))
        .data_directory(shell.webview_dir())
        .initialization_script(&shell.token_script())
        .disable_drag_drop_handler()
        .on_navigation(move |url| nav.is_ours(url))
        .build()?;
    if let Ok(hwnd) = w.hwnd() {
        win::make_no_activate(hwnd.0 as _);
    }
    Ok(())
}

/// Settings changed: on/off, lock, position. (Size, opacity and content are the page's business.)
pub fn apply_settings(app: &AppHandle, settings: &Value) {
    let shell = app.state::<ShellState>();
    let o = &settings["overlay"];
    let state = &shell.overlay;
    state.enabled.store(o["enabled"].as_bool().unwrap_or(false), Ordering::SeqCst);

    let locked = o["locked"].as_bool().unwrap_or(false);
    let mut current = state.locked.lock().unwrap();
    if *current != Some(locked) {
        if let Some(w) = window(app) {
            // Locked: clicks reach the game.
            if w.set_ignore_cursor_events(locked).is_ok() {
                *current = Some(locked);
            }
        }
    }
    drop(current);

    let saved = match (o["x"].as_i64(), o["y"].as_i64()) {
        (Some(x), Some(y)) => Some((x as i32, y as i32)),
        _ => None,
    };
    *state.saved.lock().unwrap() = saved;
    place(app);
    update_visibility(app);
}

fn update_visibility(app: &AppHandle) {
    let shell = app.state::<ShellState>();
    let state = &shell.overlay;
    let wanted = state.enabled.load(Ordering::SeqCst) && state.has_content.load(Ordering::SeqCst);
    let Some(w) = window(app) else { return };
    if wanted == state.visible.load(Ordering::SeqCst) {
        if wanted {
            let _ = w.set_always_on_top(true); // stay above a game that went borderless after us
        }
        return;
    }
    state.visible.store(wanted, Ordering::SeqCst);
    if wanted {
        match w.hwnd() {
            Ok(hwnd) => win::show_no_activate(hwnd.0 as _),
            Err(_) => {
                let _ = w.show();
            }
        }
        let _ = w.set_always_on_top(true);
    } else {
        let _ = w.hide();
    }
}

/// The saved position when it is on a screen (kept inside it), else the top-left corner of the main screen.
pub fn place(app: &AppHandle) {
    let shell = app.state::<ShellState>();
    let Some(w) = window(app) else { return };
    let size = w.outer_size().unwrap_or_default();
    let (width, height) = (size.width as i32, size.height as i32);
    let monitors = w.available_monitors().unwrap_or_default();

    let on_screen = shell.overlay.saved.lock().unwrap().and_then(|(x, y)| {
        monitors.iter().find_map(|m| {
            let (mp, ms) = (m.position(), m.size());
            let (left, top, right, bottom) = (mp.x, mp.y, mp.x + ms.width as i32, mp.y + ms.height as i32);
            let intersects = x < right && x + width > left && y < bottom && y + height > top;
            intersects.then(|| (x.clamp(left, (right - width).max(left)), y.clamp(top, (bottom - height).max(top))))
        })
    });
    let target = on_screen.unwrap_or_else(|| {
        let primary = w.primary_monitor().ok().flatten();
        let (px, py) = primary.map(|m| (m.position().x, m.position().y)).unwrap_or((0, 0));
        let k = w.scale_factor().unwrap_or(1.0);
        (px + (24.0 * k) as i32, py + (24.0 * k) as i32)
    });

    let current = w.outer_position().map(|p| (p.x, p.y)).ok();
    *shell.overlay.placed.lock().unwrap() = Some(target);
    if current != Some(target) {
        let _ = w.set_position(PhysicalPosition::new(target.0, target.1));
    }
}

/// The window moved. When the player dragged it, save the new spot once the drag has settled.
pub fn moved(app: &AppHandle, pos: PhysicalPosition<i32>) {
    let shell = app.state::<ShellState>().inner().clone();
    let at = (pos.x, pos.y);
    if *shell.overlay.placed.lock().unwrap() == Some(at) {
        return;
    }
    let generation = shell.overlay.move_generation.fetch_add(1, Ordering::SeqCst) + 1;
    let app = app.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(400));
        if shell.overlay.move_generation.load(Ordering::SeqCst) != generation {
            return; // still moving
        }
        *shell.overlay.placed.lock().unwrap() = Some(at);
        *shell.overlay.saved.lock().unwrap() = Some(at);
        shell::update_overlay(&app, move |o| {
            o.insert("x".into(), at.0.into());
            o.insert("y".into(), at.1.into());
        });
    });
}

// ---- what the overlay page asks for ----

/// The page laid out its content: size the window to it (CSS pixels), and show it only when there is something.
#[tauri::command]
pub fn overlay_layout(app: AppHandle, width: f64, height: f64, has_content: bool) {
    if let Some(w) = window(&app) {
        let _ = w.set_size(LogicalSize::new(width.max(1.0), height.max(1.0)));
    }
    app.state::<ShellState>().overlay.has_content.store(has_content, Ordering::SeqCst);
    place(&app);
    update_visibility(&app);
}

/// Wheel on the unlocked overlay: +1 bigger, -1 smaller.
#[tauri::command]
pub fn overlay_scale(app: AppHandle, step: i64) {
    shell::update_overlay(&app, move |o| {
        let scale = o.get("scale").and_then(Value::as_i64).unwrap_or(100);
        o.insert("scale".into(), (scale + step.signum() * 10).clamp(60, 250).into());
    });
}

/// Right-click on the overlay: the tray menu, where the pointer is.
#[tauri::command]
pub fn overlay_menu(app: AppHandle) {
    let shell = app.state::<ShellState>();
    if let (Some(w), Some(menu)) = (window(&app), shell.menu()) {
        let _ = w.popup_menu(&menu);
    }
}
