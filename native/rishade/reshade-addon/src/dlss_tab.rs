use std::ffi::{c_char, c_void};
use std::os::windows::ffi::OsStringExt;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicUsize, Ordering};

const IMGUI_VERSION: u32 = 19250;
const IMGUI_TEXT_UNFORMATTED: usize = 103;
const IMGUI_SEPARATOR_TEXT: usize = 110;
const IMGUI_BUTTON: usize = 111;
const IMGUI_SAME_LINE: usize = 81;
const IMGUI_SPACING: usize = 83;
const IMGUI_PUSH_TEXT_WRAP_POS: usize = 63;
const IMGUI_POP_TEXT_WRAP_POS: usize = 64;
const IMGUI_BEGIN_DISABLED: usize = 279;
const IMGUI_END_DISABLED: usize = 280;

const ADDON_FILE: &str = "renodx-dlss.addon64";
const STATUS_REFRESH_MS: u64 = 1_000;

pub const STATE_OFF: u32 = 0;
pub const STATE_ALIVE: u32 = 1;
pub const STATE_STOPPED: u32 = 2;

type Module = *mut c_void;
type GetFunctionTable = unsafe extern "C" fn(u32) -> *const *const c_void;
type RegisterOverlay = unsafe extern "C" fn(Module, *const c_char, *mut c_void);

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetModuleFileNameW(module: Module, buffer: *mut u16, size: u32) -> u32;
    fn GetTickCount64() -> u64;
}

static TABLE: AtomicUsize = AtomicUsize::new(0);
static ROOT: Mutex<Option<PathBuf>> = Mutex::new(None);
static HOST_STATE: AtomicU32 = AtomicU32::new(STATE_OFF);
static RESTART_REQUESTED: AtomicBool = AtomicBool::new(false);
static SETTINGS_REQUESTED: AtomicBool = AtomicBool::new(false);
static VIEW: Mutex<View> = Mutex::new(View {
    refreshed_at: 0,
    installed: false,
    presenting: false,
    adapter: String::new(),
});

struct View {
    refreshed_at: u64,
    installed: bool,
    presenting: bool,
    adapter: String,
}

pub unsafe fn register(module: Module, reshade_module: Module) {
    let Some(get_table) =
        (unsafe { crate::plugin::symbol::<GetFunctionTable>(reshade_module, b"ReShadeGetImGuiFunctionTable\0") })
    else {
        return;
    };
    let Some(register_overlay) = (unsafe {
        crate::plugin::symbol::<RegisterOverlay>(reshade_module, b"ReShadeRegisterOverlayForAddon\0")
    }) else {
        return;
    };
    let table = unsafe { get_table(IMGUI_VERSION) };
    if table.is_null() {
        return;
    }
    let mut buffer = vec![0u16; 32_768];
    let length = unsafe { GetModuleFileNameW(reshade_module, buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
    if length == 0 || length >= buffer.len() {
        return;
    }
    let Some(root) = PathBuf::from(std::ffi::OsString::from_wide(&buffer[..length]))
        .parent()
        .map(Path::to_path_buf)
    else {
        return;
    };
    if let Ok(mut slot) = ROOT.lock() {
        *slot = Some(root);
    }
    TABLE.store(table as usize, Ordering::Release);
    unsafe { register_overlay(module, c"DLSS".as_ptr(), draw as *mut c_void) };
}

pub fn set_host_state(state: u32) {
    HOST_STATE.store(state, Ordering::Release);
}

pub fn take_restart_request() -> bool {
    RESTART_REQUESTED.swap(false, Ordering::AcqRel)
}

pub fn take_settings_request() -> bool {
    SETTINGS_REQUESTED.swap(false, Ordering::AcqRel)
}

unsafe fn function<T: Copy>(index: usize) -> Option<T> {
    let table = TABLE.load(Ordering::Acquire) as *const *const c_void;
    if table.is_null() {
        return None;
    }
    let pointer = unsafe { *table.add(index) };
    if pointer.is_null() {
        return None;
    }
    Some(unsafe { std::mem::transmute_copy(&pointer) })
}

fn text(value: &str) {
    if let Some(call) = unsafe { function::<unsafe extern "C" fn(*const c_char, *const c_char)>(IMGUI_TEXT_UNFORMATTED) } {
        let range = value.as_bytes().as_ptr_range();
        unsafe { call(range.start.cast(), range.end.cast()) };
    }
}

fn wrapped(value: &str) {
    if let (Some(push), Some(pop)) = unsafe {
        (
            function::<unsafe extern "C" fn(f32)>(IMGUI_PUSH_TEXT_WRAP_POS),
            function::<unsafe extern "C" fn()>(IMGUI_POP_TEXT_WRAP_POS),
        )
    } {
        unsafe { push(0.0) };
        text(value);
        unsafe { pop() };
    }
}

fn separator(label: &std::ffi::CStr) {
    if let Some(call) = unsafe { function::<unsafe extern "C" fn(*const c_char)>(IMGUI_SEPARATOR_TEXT) } {
        unsafe { call(label.as_ptr()) };
    }
}

fn spacing() {
    if let Some(call) = unsafe { function::<unsafe extern "C" fn()>(IMGUI_SPACING) } {
        unsafe { call() };
    }
}

fn button(label: &std::ffi::CStr) -> bool {
    let size = [0.0f32; 2];
    unsafe { function::<unsafe extern "C" fn(*const c_char, *const [f32; 2]) -> bool>(IMGUI_BUTTON) }
        .is_some_and(|call| unsafe { call(label.as_ptr(), &size) })
}

fn same_line() {
    if let Some(call) = unsafe { function::<unsafe extern "C" fn(f32, f32)>(IMGUI_SAME_LINE) } {
        unsafe { call(0.0, -1.0) };
    }
}

fn set_disabled(disabled: bool) {
    if let Some(call) = unsafe { function::<unsafe extern "C" fn(bool)>(IMGUI_BEGIN_DISABLED) } {
        unsafe { call(disabled) };
    }
}

fn end_disabled() {
    if let Some(call) = unsafe { function::<unsafe extern "C" fn()>(IMGUI_END_DISABLED) } {
        unsafe { call() };
    }
}

fn root() -> Option<PathBuf> {
    ROOT.lock().ok().and_then(|root| root.clone())
}

fn refresh(view: &mut View, root: &Path) {
    let now = unsafe { GetTickCount64() };
    if view.refreshed_at != 0 && now.saturating_sub(view.refreshed_at) < STATUS_REFRESH_MS {
        return;
    }
    view.refreshed_at = now;
    view.installed = root.join(ADDON_FILE).is_file();
    let log = std::fs::read_to_string(root.join("dlss").join("ReShade.log")).unwrap_or_default();
    view.presenting = log.contains("RenoDX DLSS first present");
    view.adapter = log
        .lines()
        .find_map(|line| line.split_once("| Running on ").map(|(_, rest)| rest.trim().trim_end_matches('.').to_owned()))
        .unwrap_or_default();
}

unsafe extern "C" fn draw(_runtime: *mut c_void) {
    let Some(root) = root() else {
        return;
    };
    let Ok(mut view) = VIEW.lock() else {
        return;
    };
    refresh(&mut view, &root);

    separator(c"NVIDIA DLSS");
    let state = HOST_STATE.load(Ordering::Acquire);
    let status = if !view.installed {
        "Not installed. Turn on NVIDIA DLSS on the RiShade page in Voidstrap.".to_owned()
    } else if state == STATE_STOPPED {
        "Stopped. The DLSS helper closed, use Restart DLSS to try again.".to_owned()
    } else if state != STATE_ALIVE {
        "Starting, DLSS loads once RiShade is applying a preset.".to_owned()
    } else if view.presenting && !view.adapter.is_empty() {
        format!("Running on {}", view.adapter)
    } else if view.presenting {
        "Running".to_owned()
    } else {
        "Starting".to_owned()
    };
    text("Status:");
    wrapped(&status);
    spacing();
    wrapped("DLSS runs in its own helper with RenoDX. Open DLSS settings shows the helper window with the full RenoDX settings in its ReShade menu, changes there apply right away.");
    spacing();
    set_disabled(!view.installed || state != STATE_ALIVE);
    if button(c"Open DLSS settings") {
        SETTINGS_REQUESTED.store(true, Ordering::Release);
    }
    end_disabled();
    same_line();
    set_disabled(!view.installed);
    if button(c"Restart DLSS") {
        RESTART_REQUESTED.store(true, Ordering::Release);
    }
    end_disabled();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn imgui_indices_match_reshade_6_8() {
        assert_eq!(IMGUI_VERSION, 19250);
        assert_eq!(IMGUI_TEXT_UNFORMATTED, 103);
        assert_eq!(IMGUI_BUTTON, 111);
        assert_eq!(IMGUI_SAME_LINE, 81);
    }
}
