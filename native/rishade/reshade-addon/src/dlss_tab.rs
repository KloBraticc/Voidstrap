use std::ffi::{c_char, c_void};
use std::os::windows::ffi::OsStringExt;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicUsize, Ordering};

const IMGUI_VERSION: u32 = 19250;
const IMGUI_TEXT_UNFORMATTED: usize = 103;
const IMGUI_SEPARATOR_TEXT: usize = 110;
const IMGUI_BUTTON: usize = 111;
const IMGUI_SPACING: usize = 83;
const IMGUI_PUSH_TEXT_WRAP_POS: usize = 63;
const IMGUI_POP_TEXT_WRAP_POS: usize = 64;
const IMGUI_SLIDER_FLOAT: usize = 144;
const IMGUI_BEGIN_DISABLED: usize = 279;
const IMGUI_END_DISABLED: usize = 280;
const IMGUI_IS_ITEM_DEACTIVATED_AFTER_EDIT: usize = 295;

const SECTION: &str = "RENODX-DLSS";
const INTENSITY_KEY: &str = "DirectNeuralRenderingIntensity";
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
static VIEW: Mutex<View> = Mutex::new(View {
    refreshed_at: 0,
    installed: false,
    presenting: false,
    adapter: String::new(),
    intensity: 1.0,
    loaded: false,
});

struct View {
    refreshed_at: u64,
    installed: bool,
    presenting: bool,
    adapter: String,
    intensity: f32,
    loaded: bool,
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

fn slider(label: &std::ffi::CStr, value: &mut f32) -> bool {
    let Some(call) = (unsafe {
        function::<unsafe extern "C" fn(*const c_char, *mut f32, f32, f32, *const c_char, i32) -> bool>(IMGUI_SLIDER_FLOAT)
    }) else {
        return false;
    };
    unsafe { call(label.as_ptr(), value, 0.0, 1.0, c"%.2f".as_ptr(), 0) };
    unsafe { function::<unsafe extern "C" fn() -> bool>(IMGUI_IS_ITEM_DEACTIVATED_AFTER_EDIT) }
        .is_some_and(|edited| unsafe { edited() })
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
    if view.loaded && now.saturating_sub(view.refreshed_at) < STATUS_REFRESH_MS {
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
    if !view.loaded {
        let ini = std::fs::read_to_string(root.join("dlss").join("ReShade.ini")).unwrap_or_default();
        if let Some(value) = ini_value(&ini, SECTION, INTENSITY_KEY).and_then(|value| value.parse::<f32>().ok()) {
            view.intensity = value.clamp(0.0, 1.0);
        }
        view.loaded = true;
    }
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
    wrapped("DLSS runs in its own helper with RenoDX, so it does not appear in the Add-ons list.");
    spacing();

    set_disabled(!view.installed);
    let mut intensity = view.intensity;
    if slider(c"Intensity", &mut intensity) {
        view.intensity = intensity;
        if write_ini_value(&root.join("dlss").join("ReShade.ini"), SECTION, INTENSITY_KEY, &format!("{intensity:.2}")).is_ok() {
            RESTART_REQUESTED.store(true, Ordering::Release);
        }
    } else {
        view.intensity = intensity;
    }
    if button(c"Restart DLSS") {
        RESTART_REQUESTED.store(true, Ordering::Release);
    }
    end_disabled();
    spacing();
    wrapped("Changing a setting restarts the DLSS helper, the picture may pause for a moment.");
}

fn ini_value<'a>(text: &'a str, section: &str, key: &str) -> Option<&'a str> {
    let mut in_section = false;
    for line in text.lines() {
        let line = line.trim();
        if let Some(name) = line.strip_prefix('[').and_then(|rest| rest.strip_suffix(']')) {
            in_section = name.eq_ignore_ascii_case(section);
        } else if in_section
            && let Some((name, value)) = line.split_once('=')
            && name.trim().eq_ignore_ascii_case(key)
        {
            return Some(value.trim());
        }
    }
    None
}

fn set_ini_value(text: &str, section: &str, key: &str, value: &str) -> String {
    let newline = if text.contains("\r\n") { "\r\n" } else { "\n" };
    let mut lines: Vec<String> = text.lines().map(str::to_owned).collect();
    let mut section_start = None;
    let mut section_end = lines.len();
    for (index, line) in lines.iter().enumerate() {
        let trimmed = line.trim();
        if let Some(name) = trimmed.strip_prefix('[').and_then(|rest| rest.strip_suffix(']')) {
            if section_start.is_some() {
                section_end = index;
                break;
            }
            if name.eq_ignore_ascii_case(section) {
                section_start = Some(index);
            }
        }
    }
    let entry = format!("{key}={value}");
    match section_start {
        Some(start) => {
            let existing = (start + 1..section_end).find(|&index| {
                lines[index]
                    .split_once('=')
                    .is_some_and(|(name, _)| name.trim().eq_ignore_ascii_case(key))
            });
            match existing {
                Some(index) => lines[index] = entry,
                None => {
                    let mut insert_at = section_end;
                    while insert_at > start + 1 && lines[insert_at - 1].trim().is_empty() {
                        insert_at -= 1;
                    }
                    lines.insert(insert_at, entry);
                }
            }
        }
        None => {
            if lines.last().is_some_and(|line| !line.trim().is_empty()) {
                lines.push(String::new());
            }
            lines.push(format!("[{section}]"));
            lines.push(entry);
        }
    }
    let mut output = lines.join(newline);
    output.push_str(newline);
    output
}

fn write_ini_value(path: &Path, section: &str, key: &str, value: &str) -> std::io::Result<()> {
    let text = std::fs::read_to_string(path).unwrap_or_default();
    let updated = set_ini_value(&text, section, key, value);
    let staged = path.with_extension("ini.tmp");
    std::fs::write(&staged, updated)?;
    std::fs::rename(&staged, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sets_a_key_inside_an_existing_section() {
        let text = "[GENERAL]\r\nA=1\r\n\r\n[RENODX-DLSS]\r\nDLSSQualityMode=0\r\n\r\n[OVERLAY]\r\nB=2\r\n";
        let updated = set_ini_value(text, SECTION, INTENSITY_KEY, "0.50");
        assert_eq!(ini_value(&updated, SECTION, INTENSITY_KEY), Some("0.50"));
        assert_eq!(ini_value(&updated, "OVERLAY", "B"), Some("2"));
        assert!(updated.contains("DLSSQualityMode=0\r\nDirectNeuralRenderingIntensity=0.50\r\n\r\n[OVERLAY]"));
    }

    #[test]
    fn replaces_an_existing_key_and_adds_a_missing_section() {
        let replaced = set_ini_value("[RENODX-DLSS]\nDirectNeuralRenderingIntensity=1\n", SECTION, INTENSITY_KEY, "0.25");
        assert_eq!(replaced, "[RENODX-DLSS]\nDirectNeuralRenderingIntensity=0.25\n");
        let added = set_ini_value("[GENERAL]\nA=1\n", SECTION, INTENSITY_KEY, "0.75");
        assert_eq!(ini_value(&added, SECTION, INTENSITY_KEY), Some("0.75"));
    }

    #[test]
    fn imgui_indices_match_reshade_6_8() {
        assert_eq!(IMGUI_VERSION, 19250);
        assert_eq!(IMGUI_TEXT_UNFORMATTED, 103);
        assert_eq!(IMGUI_SLIDER_FLOAT, 144);
        assert_eq!(IMGUI_IS_ITEM_DEACTIVATED_AFTER_EDIT, 295);
    }
}
