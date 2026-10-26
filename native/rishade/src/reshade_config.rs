use crate::PresetIni;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

const K_TEXT: u32 = 0xEDEDED;
const K_DIM: u32 = 0x8C8C8C;
const K_BACKGROUND: u32 = 0x121318;
const K_CARD: u32 = 0x1E2028;
const K_CARD_HOVER: u32 = 0x2A2D39;
const K_SIDEBAR: u32 = 0x181920;
const K_BORDER: u32 = 0x2E3240;
const K_BORDER_STRONG: u32 = 0x444A5E;
const K_ACCENT: u32 = 0x4E75FF;
const K_ACCENT_HOVER: u32 = 0x6E8FFF;

fn color_rgba(rgb: u32, alpha: f32) -> String {
    format!(
        "{:.6},{:.6},{:.6},{:.6}",
        ((rgb >> 16) & 0xFF) as f32 / 255.0,
        ((rgb >> 8) & 0xFF) as f32 / 255.0,
        (rgb & 0xFF) as f32 / 255.0,
        alpha
    )
}

fn apply_theme(ini: &mut PresetIni) {
    let colors: &[(&str, String)] = &[
        ("Text", color_rgba(K_TEXT, 1.0)),
        ("TextDisabled", color_rgba(K_DIM, 1.0)),
        ("WindowBg", color_rgba(K_BACKGROUND, 0.97)),
        ("ChildBg", color_rgba(0, 0.0)),
        ("PopupBg", color_rgba(0x16171D, 0.98)),
        ("Border", color_rgba(K_BORDER, 1.0)),
        ("BorderShadow", color_rgba(0, 0.0)),
        ("FrameBg", color_rgba(K_CARD, 1.0)),
        ("FrameBgHovered", color_rgba(K_CARD_HOVER, 1.0)),
        ("FrameBgActive", color_rgba(K_CARD_HOVER, 1.0)),
        ("TitleBg", color_rgba(K_SIDEBAR, 1.0)),
        ("TitleBgActive", color_rgba(K_SIDEBAR, 1.0)),
        ("TitleBgCollapsed", color_rgba(K_SIDEBAR, 1.0)),
        ("MenuBarBg", color_rgba(K_SIDEBAR, 1.0)),
        ("ScrollbarBg", color_rgba(0, 0.0)),
        ("ScrollbarGrab", color_rgba(K_BORDER, 1.0)),
        ("ScrollbarGrabHovered", color_rgba(K_BORDER_STRONG, 1.0)),
        ("ScrollbarGrabActive", color_rgba(K_BORDER_STRONG, 1.0)),
        ("CheckMark", color_rgba(K_ACCENT_HOVER, 1.0)),
        ("SliderGrab", color_rgba(K_ACCENT, 1.0)),
        ("SliderGrabActive", color_rgba(K_ACCENT_HOVER, 1.0)),
        ("Button", color_rgba(K_CARD, 1.0)),
        ("ButtonHovered", color_rgba(K_CARD_HOVER, 1.0)),
        ("ButtonActive", color_rgba(K_BORDER, 1.0)),
        ("Header", color_rgba(K_ACCENT, 0.35)),
        ("HeaderHovered", color_rgba(K_ACCENT, 0.5)),
        ("HeaderActive", color_rgba(K_ACCENT, 0.65)),
        ("Separator", color_rgba(K_BORDER, 1.0)),
        ("SeparatorHovered", color_rgba(K_BORDER_STRONG, 1.0)),
        ("SeparatorActive", color_rgba(K_ACCENT, 1.0)),
        ("ResizeGrip", color_rgba(K_BORDER, 1.0)),
        ("ResizeGripHovered", color_rgba(K_BORDER_STRONG, 1.0)),
        ("ResizeGripActive", color_rgba(K_ACCENT, 1.0)),
        ("TabHovered", color_rgba(K_CARD_HOVER, 1.0)),
        ("Tab", color_rgba(K_SIDEBAR, 1.0)),
        ("TabSelected", color_rgba(K_CARD, 1.0)),
        ("TabSelectedOverline", color_rgba(K_ACCENT, 1.0)),
        ("TabDimmed", color_rgba(K_SIDEBAR, 1.0)),
        ("TabDimmedSelected", color_rgba(K_CARD, 1.0)),
        ("DockingPreview", color_rgba(K_ACCENT, 0.5)),
        ("DockingEmptyBg", color_rgba(K_BACKGROUND, 1.0)),
        ("TextLink", color_rgba(K_ACCENT_HOVER, 1.0)),
        ("TextSelectedBg", color_rgba(K_ACCENT, 0.35)),
        ("NavCursor", color_rgba(K_ACCENT, 1.0)),
        ("ModalWindowDimBg", color_rgba(0, 0.6)),
    ];

    let _ = ini.set("STYLE", "StyleIndex", "4");
    for (name, value) in colors {
        let _ = ini.set("STYLE", name, value);
    }
    for rounding in &[
        "WindowRounding",
        "ChildRounding",
        "FrameRounding",
        "PopupRounding",
        "ScrollbarRounding",
        "GrabRounding",
        "TabRounding",
    ] {
        let _ = ini.set("STYLE", rounding, "6.000000");
    }
}

fn get_pictures_dir() -> PathBuf {
    if let Ok(profile) = std::env::var("USERPROFILE") {
        PathBuf::from(profile).join("Pictures").join("RiShade")
    } else {
        PathBuf::from(r"C:\Pictures\RiShade")
    }
}

fn renamed_preset_path(directory: &Path, preset_path: &str) -> Option<String> {
    let split = preset_path.rfind(['\\', '/']).map_or(0, |index| index + 1);
    let (folder, name) = preset_path.split_at(split);
    let (_, new, _) = crate::effect_setup::RENAMED_PRESETS
        .iter()
        .find(|(old, _, _)| old.eq_ignore_ascii_case(name))?;
    let resolve = |file: &str| {
        let path = Path::new(folder).join(file);
        if path.is_absolute() {
            path
        } else {
            directory.join(folder.replace('\\', "/")).join(file)
        }
    };
    (!resolve(name).exists() && resolve(new).exists()).then(|| format!("{folder}{new}"))
}

pub fn prepare_reshade_config(directory: &Path) -> io::Result<()> {
    crate::effect_setup::remove_retired_assets(directory);
    crate::effect_setup::migrate_renamed_presets(directory);
    crate::effect_setup::upgrade_unmodified_presets(directory);
    crate::effect_setup::migrate_ssr_technique(directory);
    crate::effect_setup::patch_quint_ssr(directory);
    let path = directory.join("ReShade.ini");
    let original_text = if path.is_file() {
        fs::read_to_string(&path)?
    } else {
        String::new()
    };

    let mut ini = PresetIni::parse(&original_text);

    if directory.join(crate::dlss::ADDON_FILE).exists()
        && ini
            .get("RENODX-DLSS", "DirectNeuralRenderingRequireDlss")
            .is_none()
    {
        let _ = ini.set("RENODX-DLSS", "DirectNeuralRenderingRequireDlss", "0");
    }

    for key in ["EffectSearchPaths", "TextureSearchPaths"] {
        let default_val = if key == "EffectSearchPaths" {
            r".\reshade-shaders\Shaders\**"
        } else {
            r".\reshade-shaders\Textures\**"
        };
        let value = ini.get("GENERAL", key).unwrap_or(default_val);
        let fixed_paths = value
            .split(';')
            .filter(|search_path| !search_path.trim().is_empty())
            .map(|search_path| {
                let mut path = search_path.trim().trim_end_matches(['\\', '/']);
                while let Some(parent) = path.strip_suffix(r"\**\**") {
                    path = parent.trim_end_matches(['\\', '/']);
                }
                while let Some(parent) = path.strip_suffix("/**/**") {
                    path = parent.trim_end_matches(['\\', '/']);
                }
                if !path.ends_with(r"\**") && !path.ends_with("/**") {
                    format!(r"{}\**", path)
                } else {
                    path.to_owned()
                }
            })
            .collect::<Vec<_>>()
            .join(";");
        let effective = if fixed_paths.is_empty() {
            default_val.to_owned()
        } else {
            fixed_paths
        };
        if ini.get("GENERAL", key) != Some(&effective) {
            let _ = ini.set("GENERAL", key, &effective);
        }
    }

    let preset_path = ini.get("GENERAL", "PresetPath").unwrap_or_default().trim();
    let preset_is_uninitialized = preset_path.is_empty()
        || Path::new(preset_path)
            .file_name()
            .is_some_and(|name| name.eq_ignore_ascii_case("ReShadePreset.ini"))
            && fs::read_to_string(directory.join(preset_path.replace('\\', "/")))
                .ok()
                .is_none_or(|contents| {
                    PresetIni::parse(&contents)
                        .get("", "Techniques")
                        .is_none_or(str::is_empty)
                });
    if preset_is_uninitialized {
        let default_preset = format!(r".\presets\{}", crate::effect_setup::DEFAULT_PRESET);
        let _ = ini.set("GENERAL", "PresetPath", &default_preset);
    } else if let Some(renamed) = renamed_preset_path(directory, preset_path) {
        let _ = ini.set("GENERAL", "PresetPath", &renamed);
    }

    let dlss_installed = crate::dlss::installed(directory);
    let disabled = ini.get("ADDON", "DisabledAddons").unwrap_or_default();
    let mut kept = Vec::new();
    for item in disabled.split([',', ';']) {
        let item = item.trim();
        if !item.is_empty()
            && !item.eq_ignore_ascii_case("RiShade")
            && !item.to_ascii_lowercase().starts_with("rishade@")
            && !item.to_ascii_lowercase().starts_with("rishade-")
            && !item.eq_ignore_ascii_case("RiShadeRuntime")
            && !item.to_ascii_lowercase().starts_with("rishaderuntime@")
            && !item.to_ascii_lowercase().starts_with("rishaderuntime-")
            && !item.eq_ignore_ascii_case("Unishade")
            && !item.to_ascii_lowercase().starts_with("unishade@")
            && !item.eq_ignore_ascii_case("RobloxShadeHost")
            && !item.to_ascii_lowercase().starts_with("robloxshadehost@")
            && !(dlss_installed && is_dlss_addon(item))
        {
            kept.push(item);
        }
    }
    if !kept
        .iter()
        .any(|&item| item.eq_ignore_ascii_case("Generic Depth"))
    {
        kept.push("Generic Depth");
    }
    let kept_str = kept.join(",");
    let _ = ini.set("ADDON", "DisabledAddons", &kept_str);

    let wrapped = directory.join(crate::dlss::ADDON_FILE).exists();
    let _ = ini.set("GENERAL", "NoLogging", if wrapped { "0" } else { "1" });
    let _ = ini.set("OVERLAY", "TutorialProgress", "4");
    let _ = ini.set("OVERLAY", "ShowPresetTransitionMessage", "0");
    let _ = ini.set("INSTALL", "Logging", if wrapped { "1" } else { "0" });
    let _ = ini.set("INSTALL", "EnableLogging", "0");

    if ini.get("STYLE", "StyleIndex").is_none() {
        apply_theme(&mut ini);
    }

    let font = ini.get("STYLE", "Font");
    if font.is_none() || font == Some("") || font == Some("ProggyClean.ttf") {
        let _ = ini.set("STYLE", "Font", r"C:\Windows\Fonts\segoeui.ttf");
    }

    let screenshot_path = ini.get("SCREENSHOT", "SavePath");
    if screenshot_path.is_none() || screenshot_path == Some("") || screenshot_path == Some(".\\") {
        let pictures = get_pictures_dir();
        let mut path_str = pictures.to_string_lossy().to_string();
        if !path_str.ends_with('\\') {
            path_str.push('\\');
        }
        let _ = ini.set("SCREENSHOT", "SavePath", &path_str);
    }

    let key_overlay_empty = ini.get("INPUT", "KeyOverlay").map_or(true, |v| {
        let trimmed = v.trim();
        trimmed.is_empty()
            || trimmed == "0"
            || trimmed == "0,0,0,0"
            || trimmed.split(',').all(|part| part.trim() == "0")
    });
    if key_overlay_empty {
        let _ = ini.set("INPUT", "KeyOverlay", "36,0,0,0");
    }

    let key_menu_empty = ini.get("INPUT", "KeyMenu").map_or(true, |v| {
        let trimmed = v.trim();
        trimmed.is_empty()
            || trimmed == "0"
            || trimmed == "0,0,0,0"
            || trimmed.split(',').all(|part| part.trim() == "0")
    });
    if key_menu_empty {
        let _ = ini.set("INPUT", "KeyMenu", "36,0,0,0");
    }

    if ini.get("INPUT", "InputProcessing").is_none() {
        let _ = ini.set("INPUT", "InputProcessing", "2");
    }

    let _ = ini.set("GENERAL", "SkipLoadingDisabledEffects", "1");
    let _ = ini.set("GENERAL", "NoEffectCache", "0");
    let _ = ini.set("GENERAL", "NoReloadOnInit", "0");
    let _ = ini.set("GENERAL", "NoDebugInfo", "1");
    let preprocessor_defs = "RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0,RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=0,RESHADE_DEPTH_INPUT_IS_REVERSED=0,RESHADE_DEPTH_INPUT_IS_LOGARITHMIC=0,RESHADE_DEPTH_INPUT_X_SCALE=1.0,RESHADE_DEPTH_INPUT_Y_SCALE=1.0,BABA_USE_LEGACY_PIPELINE=1";
    let cur_defs = ini
        .get("GENERAL", "PreprocessorDefinitions")
        .unwrap_or_default();
    if cur_defs.trim().is_empty()
        || !cur_defs.contains("RESHADE_DEPTH_INPUT_IS_REVERSED")
        || !cur_defs.contains("BABA_USE_LEGACY_PIPELINE")
        || !cur_defs.contains("RESHADE_DEPTH_INPUT_X_SCALE")
    {
        let _ = ini.set("GENERAL", "PreprocessorDefinitions", preprocessor_defs);
    }
    let cache_dir = directory.join("Cache");
    let _ = fs::create_dir_all(&cache_dir);
    if let Some(cache_str) = cache_dir.to_str() {
        let _ = ini.set("GENERAL", "IntermediateCachePath", cache_str);
    }

    let updated_text = ini.text();
    if updated_text == original_text {
        return Ok(());
    }

    let temp_path = directory.join("ReShade.ini.tmp");
    fs::write(&temp_path, updated_text.as_bytes())?;
    fs::rename(&temp_path, &path)?;

    Ok(())
}

fn is_dlss_addon(item: &str) -> bool {
    let lower = item.to_ascii_lowercase();
    lower == "renodx dlss" || lower.starts_with("renodx dlss@") || lower.ends_with(&format!("@{}", crate::dlss::ADDON_FILE))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn installed_dlss_is_never_left_disabled() {
        let temp_dir = std::env::temp_dir().join(format!("rishade_cfg_dlss_{}", std::process::id()));
        let _ = fs::create_dir_all(&temp_dir);
        for name in ["nvngx_dlssnr.dll", crate::dlss::ADDON_FILE] {
            fs::write(temp_dir.join(name), b"x").unwrap();
        }
        fs::write(temp_dir.join("ReShade.ini"), "[ADDON]
DisabledAddons=Generic Depth,RenoDX DLSS@renodx-dlss.addon64,SomeMod
").unwrap();
        prepare_reshade_config(&temp_dir).unwrap();
        let parsed = PresetIni::parse(&fs::read_to_string(temp_dir.join("ReShade.ini")).unwrap());
        assert_eq!(parsed.get("ADDON", "DisabledAddons"), Some("Generic Depth,SomeMod"));
        let _ = fs::remove_dir_all(&temp_dir);
    }

    #[test]
    fn dlss_entry_stays_disabled_when_dlss_is_not_installed() {
        let temp_dir = std::env::temp_dir().join(format!("rishade_cfg_nodlss_{}", std::process::id()));
        let _ = fs::create_dir_all(&temp_dir);
        fs::write(temp_dir.join("ReShade.ini"), "[ADDON]
DisabledAddons=RenoDX DLSS@renodx-dlss.addon64
").unwrap();
        prepare_reshade_config(&temp_dir).unwrap();
        let parsed = PresetIni::parse(&fs::read_to_string(temp_dir.join("ReShade.ini")).unwrap());
        assert_eq!(parsed.get("ADDON", "DisabledAddons"), Some("RenoDX DLSS@renodx-dlss.addon64,Generic Depth"));
        let _ = fs::remove_dir_all(&temp_dir);
    }

    #[test]
    fn prepare_reshade_config_applies_all_unishade_parity_settings() {
        let temp_dir =
            std::env::temp_dir().join(format!("rishade_cfg_test_{}", std::process::id()));
        let _ = fs::create_dir_all(&temp_dir);

        let initial_ini = "[ADDON]\nDisabledAddons=OtherAddon,RiShade,Unishade@1.0,SomeMod\n[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\\**\\\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\\**\n[STYLE]\nFont=ProggyClean.ttf\n";
        let ini_path = temp_dir.join("ReShade.ini");
        fs::write(&ini_path, initial_ini).unwrap();

        prepare_reshade_config(&temp_dir).unwrap();

        let result = fs::read_to_string(&ini_path).unwrap();
        let parsed = PresetIni::parse(&result);

        let disabled = parsed.get("ADDON", "DisabledAddons").unwrap();
        assert_eq!(disabled, "OtherAddon,SomeMod,Generic Depth");

        assert_eq!(parsed.get("OVERLAY", "TutorialProgress"), Some("4"));
        assert_eq!(
            parsed.get("GENERAL", "EffectSearchPaths"),
            Some(r".\reshade-shaders\Shaders\**")
        );
        assert_eq!(
            parsed.get("GENERAL", "TextureSearchPaths"),
            Some(r".\reshade-shaders\Textures\**")
        );
        assert_eq!(
            parsed.get("GENERAL", "PresetPath"),
            Some(r".\presets\RiShade Natural.ini")
        );
        assert_eq!(
            parsed.get("GENERAL", "SkipLoadingDisabledEffects"),
            Some("1")
        );
        assert_eq!(parsed.get("GENERAL", "NoEffectCache"), Some("0"));
        assert_eq!(parsed.get("GENERAL", "NoReloadOnInit"), Some("0"));
        assert_eq!(parsed.get("GENERAL", "NoDebugInfo"), Some("1"));

        assert_eq!(
            parsed.get("STYLE", "Font"),
            Some(r"C:\Windows\Fonts\segoeui.ttf")
        );

        assert_eq!(parsed.get("STYLE", "StyleIndex"), Some("4"));
        assert_eq!(parsed.get("STYLE", "WindowRounding"), Some("6.000000"));

        let save_path = parsed.get("SCREENSHOT", "SavePath").unwrap();
        assert!(save_path.contains("RiShade"));

        assert_eq!(parsed.get("INPUT", "KeyOverlay"), Some("36,0,0,0"));
        assert_eq!(parsed.get("INPUT", "KeyMenu"), Some("36,0,0,0"));
        assert_eq!(parsed.get("INPUT", "InputProcessing"), Some("2"));
        assert_eq!(parsed.get("INSTALL", "Logging"), Some("0"));
        assert_eq!(parsed.get("INSTALL", "EnableLogging"), Some("0"));

        let _ = fs::remove_dir_all(&temp_dir);
    }

    #[test]
    fn prepare_reshade_config_follows_a_renamed_bundled_preset() {
        let temp_dir =
            std::env::temp_dir().join(format!("rishade_cfg_rename_{}", std::process::id()));
        let _ = fs::remove_dir_all(&temp_dir);
        fs::create_dir_all(temp_dir.join("presets")).unwrap();
        fs::write(
            temp_dir.join("presets").join("GenericPreset2.ini"),
            include_bytes!("../presets/RiShade Glow.ini"),
        )
        .unwrap();
        fs::write(
            temp_dir.join("ReShade.ini"),
            "[GENERAL]\nPresetPath=.\\presets\\GenericPreset2.ini\n",
        )
        .unwrap();

        prepare_reshade_config(&temp_dir).unwrap();

        let parsed = PresetIni::parse(&fs::read_to_string(temp_dir.join("ReShade.ini")).unwrap());
        assert_eq!(
            parsed.get("GENERAL", "PresetPath"),
            Some(r".\presets\RiShade Glow.ini")
        );
        assert!(temp_dir.join("presets").join("RiShade Glow.ini").is_file());
        assert!(!temp_dir.join("presets").join("GenericPreset2.ini").exists());
        let _ = fs::remove_dir_all(&temp_dir);
    }

    #[test]
    fn prepare_reshade_config_preserves_custom_key_and_replaces_zero() {
        let temp_dir =
            std::env::temp_dir().join(format!("rishade_cfg_test_key_{}", std::process::id()));
        let _ = fs::create_dir_all(&temp_dir);

        let initial_ini = "[INPUT]\nKeyOverlay=0,0,0,0\nKeyMenu=0,0,0,0\n";
        let ini_path = temp_dir.join("ReShade.ini");
        fs::write(&ini_path, initial_ini).unwrap();

        prepare_reshade_config(&temp_dir).unwrap();

        let result = fs::read_to_string(&ini_path).unwrap();
        let parsed = PresetIni::parse(&result);
        assert_eq!(parsed.get("INPUT", "KeyOverlay"), Some("36,0,0,0"));
        assert_eq!(parsed.get("INPUT", "KeyMenu"), Some("36,0,0,0"));

        let custom_ini = "[INPUT]\nKeyOverlay=45,0,0,0\nKeyMenu=45,0,0,0\n";
        fs::write(&ini_path, custom_ini).unwrap();

        prepare_reshade_config(&temp_dir).unwrap();

        let result = fs::read_to_string(&ini_path).unwrap();
        let parsed = PresetIni::parse(&result);
        assert_eq!(parsed.get("INPUT", "KeyOverlay"), Some("45,0,0,0"));
        assert_eq!(parsed.get("INPUT", "KeyMenu"), Some("45,0,0,0"));

        let _ = fs::remove_dir_all(&temp_dir);
    }
}
