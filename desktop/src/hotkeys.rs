//! Global keyboard shortcuts for the overlay. They work while the game has focus.

use std::str::FromStr;

use tauri_plugin_global_shortcut::{Code, Modifiers, Shortcut};

/// Parses "Ctrl+Shift+O", "Alt+F9"... the format the settings store. None when it is not a usable combination.
pub fn parse(combo: &str) -> Option<Shortcut> {
    let mut mods = Modifiers::empty();
    let mut key: Option<Code> = None;
    for raw in combo.split('+').map(str::trim).filter(|s| !s.is_empty()) {
        match raw.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => mods |= Modifiers::CONTROL,
            "shift" => mods |= Modifiers::SHIFT,
            "alt" => mods |= Modifiers::ALT,
            "win" | "windows" => mods |= Modifiers::SUPER,
            _ => {
                if key.is_some() {
                    return None;
                }
                key = Some(code(raw)?);
            }
        }
    }
    let key = key?;
    // A bare letter would steal it from the game: at least one modifier, except for function keys.
    if mods.is_empty() && !is_function_key(key) {
        return None;
    }
    Some(Shortcut::new(Some(mods), key))
}

fn code(raw: &str) -> Option<Code> {
    let lower = raw.to_ascii_lowercase();
    let name = if raw.len() == 1 && raw.chars().all(|c| c.is_ascii_alphabetic()) {
        format!("Key{}", raw.to_ascii_uppercase())
    } else if raw.len() == 1 && raw.chars().all(|c| c.is_ascii_digit()) {
        format!("Digit{raw}")
    } else if let Some(n) = lower.strip_prefix('f').filter(|n| !n.is_empty() && n.chars().all(|c| c.is_ascii_digit())) {
        format!("F{n}")
    } else if let Some(n) = lower.strip_prefix("numpad").filter(|n| n.len() == 1) {
        format!("Numpad{n}")
    } else {
        match lower.as_str() {
            "home" => "Home",
            "end" => "End",
            "insert" | "ins" => "Insert",
            "delete" | "del" => "Delete",
            "pageup" | "prior" => "PageUp",
            "pagedown" | "next" => "PageDown",
            "pause" => "Pause",
            "scroll" | "scrolllock" => "ScrollLock",
            "space" => "Space",
            "up" => "ArrowUp",
            "down" => "ArrowDown",
            "left" => "ArrowLeft",
            "right" => "ArrowRight",
            _ => raw,
        }
        .to_string()
    };
    Code::from_str(&name).ok()
}

fn is_function_key(code: Code) -> bool {
    matches!(
        code,
        Code::F1 | Code::F2 | Code::F3 | Code::F4 | Code::F5 | Code::F6 | Code::F7 | Code::F8 | Code::F9 | Code::F10 | Code::F11
            | Code::F12 | Code::F13 | Code::F14 | Code::F15 | Code::F16 | Code::F17 | Code::F18 | Code::F19 | Code::F20
            | Code::F21 | Code::F22 | Code::F23 | Code::F24
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_defaults() {
        assert_eq!(parse("Ctrl+Shift+O"), Some(Shortcut::new(Some(Modifiers::CONTROL | Modifiers::SHIFT), Code::KeyO)));
        assert_eq!(parse("Alt+F9"), Some(Shortcut::new(Some(Modifiers::ALT), Code::F9)));
        assert_eq!(parse("F8"), Some(Shortcut::new(Some(Modifiers::empty()), Code::F8)));
        assert_eq!(parse("ctrl+1"), Some(Shortcut::new(Some(Modifiers::CONTROL), Code::Digit1)));
    }

    #[test]
    fn refuses_unusable_combinations() {
        assert_eq!(parse("O"), None);
        assert_eq!(parse("Ctrl+O+P"), None);
        assert_eq!(parse("Ctrl+Shift"), None);
        assert_eq!(parse(""), None);
    }
}
