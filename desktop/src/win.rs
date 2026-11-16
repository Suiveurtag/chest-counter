//! The few Win32 calls the shell needs directly: named kernel objects (one instance per data folder, the standby),
//! the process list (is Fortnite running?), message boxes, and showing the overlay without taking the focus.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::time::Duration;

use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HANDLE, HWND, WAIT_ABANDONED, WAIT_OBJECT_0};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, CreateMutexW, GetCurrentProcess, OpenEventW, OpenMutexW, SetEvent, SetPriorityClass, WaitForSingleObject,
    EVENT_MODIFY_STATE, IDLE_PRIORITY_CLASS, NORMAL_PRIORITY_CLASS, SYNCHRONIZATION_SYNCHRONIZE,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetWindowLongPtrW, MessageBoxW, ShowWindow, GWL_EXSTYLE, MB_ICONERROR, MB_OK, SW_HIDE, SW_SHOWNOACTIVATE, WS_EX_NOACTIVATE,
    WS_EX_TOOLWINDOW,
};

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

/// A kernel object handle, closed when dropped.
pub struct Handle(HANDLE);
// Kernel handles may be used from any thread.
unsafe impl Send for Handle {}
unsafe impl Sync for Handle {}

impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

impl Handle {
    /// Waits for the object; true when it was signalled (or, for a mutex, abandoned by a killed owner).
    pub fn wait(&self, timeout: Option<Duration>) -> bool {
        let ms = timeout.map(|t| t.as_millis().min(u32::MAX as u128 - 1) as u32).unwrap_or(u32::MAX);
        let r = unsafe { WaitForSingleObject(self.0, ms) };
        r == WAIT_OBJECT_0 || r == WAIT_ABANDONED
    }

    pub fn set(&self) {
        unsafe { SetEvent(self.0) };
    }
}

/// Creates (and owns) a named mutex. `None` when another process already created it.
pub fn create_owned_mutex(name: &str) -> Option<Handle> {
    let h = unsafe { CreateMutexW(std::ptr::null(), 1, wide(name).as_ptr()) };
    if h.is_null() {
        return None;
    }
    let existed = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
    let handle = Handle(h);
    if existed {
        None
    } else {
        Some(handle)
    }
}

/// Creates or opens a named mutex without taking it.
pub fn mutex(name: &str) -> Option<Handle> {
    let h = unsafe { CreateMutexW(std::ptr::null(), 0, wide(name).as_ptr()) };
    (!h.is_null()).then(|| Handle(h))
}

pub fn mutex_exists(name: &str) -> bool {
    let h = unsafe { OpenMutexW(SYNCHRONIZATION_SYNCHRONIZE, 0, wide(name).as_ptr()) };
    if h.is_null() {
        return false;
    }
    drop(Handle(h));
    true
}

/// An auto-reset named event, created or opened.
pub fn event(name: &str) -> Option<Handle> {
    let h = unsafe { CreateEventW(std::ptr::null(), 0, 0, wide(name).as_ptr()) };
    (!h.is_null()).then(|| Handle(h))
}

/// Signals a named event that another process created. False when it does not exist.
pub fn signal_event(name: &str) -> bool {
    let h = unsafe { OpenEventW(EVENT_MODIFY_STATE, 0, wide(name).as_ptr()) };
    if h.is_null() {
        return false;
    }
    let handle = Handle(h);
    handle.set();
    true
}

/// Whether a process with this executable name (case-insensitive, ".exe" included) is running.
pub fn process_running(exe_name: &str) -> bool {
    unsafe {
        let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snap.is_null() || snap as isize == -1 {
            return false;
        }
        let _guard = Handle(snap);
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        if Process32FirstW(snap, &mut entry) == 0 {
            return false;
        }
        loop {
            let len = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
            let name = String::from_utf16_lossy(&entry.szExeFile[..len]);
            if name.eq_ignore_ascii_case(exe_name) {
                return true;
            }
            if Process32NextW(snap, &mut entry) == 0 {
                return false;
            }
        }
    }
}

pub fn set_idle_priority(idle: bool) {
    unsafe { SetPriorityClass(GetCurrentProcess(), if idle { IDLE_PRIORITY_CLASS } else { NORMAL_PRIORITY_CLASS }) };
}

pub fn error_box(title: &str, text: &str) {
    unsafe { MessageBoxW(std::ptr::null_mut(), wide(text).as_ptr(), wide(title).as_ptr(), MB_OK | MB_ICONERROR) };
}

/// The overlay must never take the focus from the game: no activation on click, not in Alt+Tab.
pub fn make_no_activate(hwnd: HWND) {
    use windows_sys::Win32::UI::WindowsAndMessaging::SetWindowLongPtrW;
    unsafe {
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) as isize);
    }
}

/// Shows a window without activating it (Tauri's show() would take the focus).
pub fn show_no_activate(hwnd: HWND) {
    unsafe { ShowWindow(hwnd, SW_SHOWNOACTIVATE) };
}

/// Rebuilds the transparent window's compositor surface without taking focus.
pub fn refresh_no_activate(hwnd: HWND) {
    unsafe {
        ShowWindow(hwnd, SW_HIDE);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }
}
