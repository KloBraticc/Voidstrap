use std::ffi::{CStr, c_char};
use std::path::Path;

unsafe fn copy_str_to_buffer(src: &str, out_buffer: *mut u8, buffer_len: usize) -> usize {
    let bytes = src.as_bytes();
    if !out_buffer.is_null() && buffer_len > 0 {
        let copy_len = bytes.len().min(buffer_len.saturating_sub(1));
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buffer, copy_len);
            *out_buffer.add(copy_len) = 0;
        }
    }
    bytes.len()
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeHdrWhiteLevel(hwnd: isize) -> f32 {
    #[cfg(windows)]
    {
        use windows::Win32::Foundation::HWND;
        crate::hdr::hdr_white_level(HWND(hwnd as *mut _)).unwrap_or(0.0)
    }
    #[cfg(not(windows))]
    {
        let _ = hwnd;
        0.0
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeParseHotkey(
    text: *const c_char,
    out_key: *mut u32,
    out_ctrl: *mut bool,
    out_shift: *mut bool,
    out_alt: *mut bool,
    out_win: *mut bool,
) -> bool {
    if text.is_null()
        || out_key.is_null()
        || out_ctrl.is_null()
        || out_shift.is_null()
        || out_alt.is_null()
        || out_win.is_null()
    {
        return false;
    }
    let c_str = unsafe { CStr::from_ptr(text) };
    let Ok(str_slice) = c_str.to_str() else {
        return false;
    };
    let Some(hotkey) = crate::hotkey::parse_hotkey(str_slice) else {
        return false;
    };
    unsafe {
        *out_key = hotkey.key;
        *out_ctrl = (hotkey.modifiers & crate::hotkey::MOD_CONTROL) != 0;
        *out_shift = (hotkey.modifiers & crate::hotkey::MOD_SHIFT) != 0;
        *out_alt = (hotkey.modifiers & crate::hotkey::MOD_ALT) != 0;
        *out_win = (hotkey.modifiers & crate::hotkey::MOD_WIN) != 0;
    }
    true
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeFormatHotkey(
    key: u32,
    ctrl: bool,
    shift: bool,
    alt: bool,
    win: bool,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    let mut modifiers = 0;
    if ctrl {
        modifiers |= crate::hotkey::MOD_CONTROL;
    }
    if shift {
        modifiers |= crate::hotkey::MOD_SHIFT;
    }
    if alt {
        modifiers |= crate::hotkey::MOD_ALT;
    }
    if win {
        modifiers |= crate::hotkey::MOD_WIN;
    }
    let hotkey = crate::hotkey::Hotkey { modifiers, key };
    let formatted = crate::hotkey::format_hotkey(&hotkey);
    unsafe { copy_str_to_buffer(&formatted, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePrepareConfig(base_dir: *const c_char) -> bool {
    if base_dir.is_null() {
        return false;
    }
    let Ok(base_str) = (unsafe { CStr::from_ptr(base_dir) }).to_str() else {
        return false;
    };
    crate::reshade_config::prepare_reshade_config(Path::new(base_str)).is_ok()
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeInstallEffectsAndPresets(
    base_dir: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> bool {
    if base_dir.is_null() {
        return false;
    }
    let Ok(base_dir) = (unsafe { CStr::from_ptr(base_dir) }).to_str() else {
        return false;
    };
    let result = std::panic::catch_unwind(|| {
        crate::effect_setup::install_effects_and_presets(Path::new(base_dir))
    });
    let (success, message) = match result {
        Ok(Ok(summary)) => (true, summary.describe()),
        Ok(Err(error)) => (false, error.to_string()),
        Err(_) => (false, "RiShade asset setup failed unexpectedly".to_owned()),
    };
    unsafe { copy_str_to_buffer(&message, out_buffer, buffer_len) };
    success
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeInstallDepthAssets(
    base_dir: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> bool {
    if base_dir.is_null() {
        return false;
    }
    let Ok(base_dir) = (unsafe { CStr::from_ptr(base_dir) }).to_str() else {
        return false;
    };
    let result =
        std::panic::catch_unwind(|| crate::effect_setup::install_depth_assets(Path::new(base_dir)));
    let (success, message) = match result {
        Ok(Ok(())) => (
            true,
            "Depth assets are installed and match their pinned checksums".to_owned(),
        ),
        Ok(Err(error)) => (false, error.to_string()),
        Err(_) => (
            false,
            "RiShade depth asset setup failed unexpectedly".to_owned(),
        ),
    };
    unsafe { copy_str_to_buffer(&message, out_buffer, buffer_len) };
    success
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeScanEffectsCount(effects_dir: *const c_char) -> usize {
    if effects_dir.is_null() {
        return 0;
    }
    let Ok(dir_str) = (unsafe { CStr::from_ptr(effects_dir) }).to_str() else {
        return 0;
    };
    crate::effect_catalog::scan_effects(Path::new(dir_str))
        .map(|v| v.len())
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeScanEffectsList(
    effects_dir: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    if effects_dir.is_null() {
        return 0;
    }
    let Ok(dir_str) = (unsafe { CStr::from_ptr(effects_dir) }).to_str() else {
        return 0;
    };
    let Ok(effects) = crate::effect_catalog::scan_effects(Path::new(dir_str)) else {
        return 0;
    };
    let mut joined = String::new();
    for (i, effect) in effects.iter().enumerate() {
        if i > 0 {
            joined.push('\n');
        }
        joined.push_str(&effect.relative_path.to_string_lossy().replace('\\', "/"));
    }
    unsafe { copy_str_to_buffer(&joined, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePresetGetTechniques(
    preset_text: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    if preset_text.is_null() {
        return 0;
    }
    let Ok(text) = (unsafe { CStr::from_ptr(preset_text) }).to_str() else {
        return 0;
    };
    let preset = crate::preset_ini::PresetIni::parse(text);
    let techniques = preset.get("", "Techniques").unwrap_or("");
    unsafe { copy_str_to_buffer(techniques, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePresetGetEffectFiles(
    preset_text: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    if preset_text.is_null() {
        return 0;
    }
    let Ok(text) = (unsafe { CStr::from_ptr(preset_text) }).to_str() else {
        return 0;
    };
    let preset = crate::preset_ini::PresetIni::parse(text);
    let files = crate::preset_ini::preset_effect_files(&preset);
    let joined = files.join("\n");
    unsafe { copy_str_to_buffer(&joined, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePresetGetValue(
    preset_text: *const c_char,
    section: *const c_char,
    key: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    if preset_text.is_null() || section.is_null() || key.is_null() {
        return 0;
    }
    let (Ok(text), Ok(sec), Ok(k)) = (
        (unsafe { CStr::from_ptr(preset_text) }).to_str(),
        (unsafe { CStr::from_ptr(section) }).to_str(),
        (unsafe { CStr::from_ptr(key) }).to_str(),
    ) else {
        return 0;
    };
    let preset = crate::preset_ini::PresetIni::parse(text);
    let value = preset.get(sec, k).unwrap_or("");
    unsafe { copy_str_to_buffer(value, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePresetSetValue(
    preset_text: *const c_char,
    section: *const c_char,
    key: *const c_char,
    value: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    if preset_text.is_null() || section.is_null() || key.is_null() || value.is_null() {
        return 0;
    }
    let (Ok(text), Ok(sec), Ok(k), Ok(v)) = (
        (unsafe { CStr::from_ptr(preset_text) }).to_str(),
        (unsafe { CStr::from_ptr(section) }).to_str(),
        (unsafe { CStr::from_ptr(key) }).to_str(),
        (unsafe { CStr::from_ptr(value) }).to_str(),
    ) else {
        return 0;
    };
    let mut preset = crate::preset_ini::PresetIni::parse(text);
    if preset.set(sec, k, v).is_err() {
        return 0;
    }
    let result = preset.text();
    unsafe { copy_str_to_buffer(&result, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePresetSectionNames(
    preset_text: *const c_char,
    out_buffer: *mut u8,
    buffer_len: usize,
) -> usize {
    if preset_text.is_null() {
        return 0;
    }
    let Ok(text) = (unsafe { CStr::from_ptr(preset_text) }).to_str() else {
        return 0;
    };
    let preset = crate::preset_ini::PresetIni::parse(text);
    let names: Vec<&str> = preset.section_names().collect();
    let joined = names.join("\n");
    unsafe { copy_str_to_buffer(&joined, out_buffer, buffer_len) }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadePresetFileIsLoadable(preset_path: *const c_char) -> bool {
    if preset_path.is_null() {
        return false;
    }
    let Ok(path) = (unsafe { CStr::from_ptr(preset_path) }).to_str() else {
        return false;
    };
    let path = Path::new(path);
    if !path
        .extension()
        .and_then(std::ffi::OsStr::to_str)
        .is_some_and(|extension| extension.eq_ignore_ascii_case("ini"))
    {
        return false;
    }
    let Ok(metadata) = std::fs::metadata(path) else {
        return false;
    };
    if !metadata.is_file() || metadata.len() > 16 * 1024 * 1024 {
        return false;
    }
    let Ok(text) = std::fs::read_to_string(path) else {
        return false;
    };
    let preset = crate::preset_ini::PresetIni::parse(&text);
    preset.get("", "Techniques").is_some()
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeGameListParseCount(text: *const c_char) -> usize {
    if text.is_null() {
        return 0;
    }
    let Ok(str_slice) = (unsafe { CStr::from_ptr(text) }).to_str() else {
        return 0;
    };
    crate::game_list::parse_game_list(str_slice)
        .map(|games| games.len())
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeGameListGetEntry(
    text: *const c_char,
    index: usize,
    out_exe: *mut u8,
    exe_len: usize,
    out_name: *mut u8,
    name_len: usize,
    out_enabled: *mut bool,
) -> bool {
    if text.is_null() || out_enabled.is_null() {
        return false;
    }
    let Ok(str_slice) = (unsafe { CStr::from_ptr(text) }).to_str() else {
        return false;
    };
    let Ok(games) = crate::game_list::parse_game_list(str_slice) else {
        return false;
    };
    let Some(entry) = games.get(index) else {
        return false;
    };
    unsafe {
        copy_str_to_buffer(&entry.executable, out_exe, exe_len);
        copy_str_to_buffer(&entry.name, out_name, name_len);
        *out_enabled = entry.enabled;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CString;

    #[test]
    fn parse_and_format_hotkey_ffi() {
        let input = CString::new("Ctrl+Shift+F2").unwrap();
        let mut key = 0u32;
        let mut ctrl = false;
        let mut shift = false;
        let mut alt = false;
        let mut win = false;
        assert!(unsafe {
            RiShadeParseHotkey(
                input.as_ptr(),
                &mut key,
                &mut ctrl,
                &mut shift,
                &mut alt,
                &mut win,
            )
        });
        assert_eq!(key, 0x71);
        assert!(ctrl);
        assert!(shift);
        assert!(!alt);
        assert!(!win);

        let mut buf = [0u8; 32];
        let len =
            unsafe { RiShadeFormatHotkey(key, ctrl, shift, alt, win, buf.as_mut_ptr(), buf.len()) };
        assert_eq!(len, 13);
        let formatted = unsafe { CStr::from_ptr(buf.as_ptr().cast()) }
            .to_str()
            .unwrap();
        assert_eq!(formatted, "Ctrl+Shift+F2");
    }

    #[test]
    fn scan_effects_count_and_list_handles_null_or_empty() {
        assert_eq!(unsafe { RiShadeScanEffectsCount(std::ptr::null()) }, 0);
        assert_eq!(
            unsafe { RiShadeScanEffectsList(std::ptr::null(), std::ptr::null_mut(), 0) },
            0
        );
    }

    #[test]
    fn preset_ini_ffi_inspection_and_mutation() {
        let preset_text = CString::new(
            "Techniques=Bloom@bloom.fx,Tone@tone.fx\n\n[bloom.fx]\nIntensity=1.5\n\n[tone.fx]\nGamma=2.2\n",
        )
        .unwrap();

        let mut buf = [0u8; 128];
        let len = unsafe {
            RiShadePresetGetTechniques(preset_text.as_ptr(), buf.as_mut_ptr(), buf.len())
        };
        assert!(len > 0);
        let tech = unsafe { CStr::from_ptr(buf.as_ptr().cast()) }
            .to_str()
            .unwrap();
        assert_eq!(tech, "Bloom@bloom.fx,Tone@tone.fx");

        let len = unsafe {
            RiShadePresetGetEffectFiles(preset_text.as_ptr(), buf.as_mut_ptr(), buf.len())
        };
        assert!(len > 0);
        let files = unsafe { CStr::from_ptr(buf.as_ptr().cast()) }
            .to_str()
            .unwrap();
        assert_eq!(files, "bloom.fx\ntone.fx");

        let sec = CString::new("bloom.fx").unwrap();
        let key = CString::new("Intensity").unwrap();
        let len = unsafe {
            RiShadePresetGetValue(
                preset_text.as_ptr(),
                sec.as_ptr(),
                key.as_ptr(),
                buf.as_mut_ptr(),
                buf.len(),
            )
        };
        assert!(len > 0);
        let val = unsafe { CStr::from_ptr(buf.as_ptr().cast()) }
            .to_str()
            .unwrap();
        assert_eq!(val, "1.5");

        let new_val = CString::new("2.0").unwrap();
        let mut out_ini = [0u8; 256];
        let len = unsafe {
            RiShadePresetSetValue(
                preset_text.as_ptr(),
                sec.as_ptr(),
                key.as_ptr(),
                new_val.as_ptr(),
                out_ini.as_mut_ptr(),
                out_ini.len(),
            )
        };
        assert!(len > 0);
        let updated = unsafe { CStr::from_ptr(out_ini.as_ptr().cast()) }
            .to_str()
            .unwrap();
        assert!(updated.contains("Intensity=2.0"));
    }

    #[test]
    fn game_list_ffi_parsing() {
        let text = CString::new(
            "[games]\ncount=1\n[game0]\nexecutable=RobloxPlayerBeta.exe\nname=Roblox\nenabled=1\n",
        )
        .unwrap();

        assert_eq!(unsafe { RiShadeGameListParseCount(text.as_ptr()) }, 1);

        let mut exe_buf = [0u8; 64];
        let mut name_buf = [0u8; 64];
        let mut enabled = false;

        assert!(unsafe {
            RiShadeGameListGetEntry(
                text.as_ptr(),
                0,
                exe_buf.as_mut_ptr(),
                exe_buf.len(),
                name_buf.as_mut_ptr(),
                name_buf.len(),
                &mut enabled,
            )
        });

        let exe = unsafe { CStr::from_ptr(exe_buf.as_ptr().cast()) }
            .to_str()
            .unwrap();
        let name = unsafe { CStr::from_ptr(name_buf.as_ptr().cast()) }
            .to_str()
            .unwrap();
        assert_eq!(exe, "RobloxPlayerBeta.exe");
        assert_eq!(name, "Roblox");
        assert!(enabled);
    }

    #[test]
    fn preset_file_loadability_requires_an_ini_with_a_technique_key() {
        let dir = tempfile::tempdir().unwrap();
        let valid = dir.path().join("valid.ini");
        let missing = dir.path().join("missing.ini");
        let wrong_extension = dir.path().join("valid.txt");
        let uppercase_extension = dir.path().join("uppercase.INI");
        std::fs::write(&valid, "Techniques=Bloom@Bloom.fx\n").unwrap();
        std::fs::write(&missing, "Name=not a preset\n").unwrap();
        std::fs::write(&wrong_extension, "Techniques=Bloom@Bloom.fx\n").unwrap();
        std::fs::write(&uppercase_extension, "Techniques=Bloom@Bloom.fx\n").unwrap();

        let valid = std::ffi::CString::new(valid.to_string_lossy().as_bytes()).unwrap();
        let missing = std::ffi::CString::new(missing.to_string_lossy().as_bytes()).unwrap();
        let wrong_extension =
            std::ffi::CString::new(wrong_extension.to_string_lossy().as_bytes()).unwrap();
        let uppercase_extension =
            std::ffi::CString::new(uppercase_extension.to_string_lossy().as_bytes()).unwrap();
        assert!(unsafe { RiShadePresetFileIsLoadable(valid.as_ptr()) });
        assert!(unsafe { RiShadePresetFileIsLoadable(uppercase_extension.as_ptr()) });
        assert!(!unsafe { RiShadePresetFileIsLoadable(missing.as_ptr()) });
        assert!(!unsafe { RiShadePresetFileIsLoadable(wrong_extension.as_ptr()) });
        assert!(!unsafe { RiShadePresetFileIsLoadable(std::ptr::null()) });
    }
}
