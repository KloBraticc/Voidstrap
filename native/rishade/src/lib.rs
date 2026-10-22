#[cfg(windows)]
mod capture;
mod depth_config;
mod effect_catalog;
mod effect_setup;
pub mod ffi;
pub mod frame_stats;
mod game_list;
#[cfg(windows)]
pub mod hdr;
mod hotkey;
#[cfg(windows)]
mod overlay_window;
mod preset_ini;
pub mod reshade_config;
#[cfg(windows)]
mod windows_host;

#[cfg(windows)]
pub use capture::{
    RiShadeCapture, RiShadeCaptureCopyError, RiShadeCaptureIsFinished, RiShadeCaptureLastError,
    RiShadeCaptureStart, RiShadeCaptureStartWithDevice, RiShadeCaptureStop, RiShadeFrameCallback,
};
pub use depth_config::{
    DEFAULT_DEPTH_SIZE, DEPTH_SIZES, depth_model_dimensions, normalize_depth_size,
};
pub use effect_catalog::{EffectFile, scan_effects};
pub use effect_setup::{InstallSummary, install_effects_and_presets};
pub use ffi::{
    RiShadeFormatHotkey, RiShadeGameListGetEntry, RiShadeGameListParseCount, RiShadeHdrWhiteLevel,
    RiShadeInstallEffectsAndPresets, RiShadeParseHotkey,
    RiShadePrepareConfig, RiShadePresetFileIsLoadable, RiShadePresetGetEffectFiles,
    RiShadePresetGetTechniques, RiShadePresetGetValue, RiShadePresetSectionNames,
    RiShadePresetSetValue, RiShadeScanEffectsCount, RiShadeScanEffectsList,
};
pub use frame_stats::{FrameLimit, FrameStatistics, GpuTimings, Timings};
pub use game_list::{
    GameListEntry, format_game_list, load_game_list_file, parse_game_list, save_game_list_file,
};
#[cfg(windows)]
pub use hdr::hdr_white_level;
pub use hotkey::{Hotkey, NAMED_KEYS, format_hotkey, parse_hotkey};
#[cfg(windows)]
pub use overlay_window::{RiShadeOverlayRenderer, RiShadeOverlayWindow};
pub use preset_ini::{PresetIni, PresetIniError, preset_effect_files};
pub use reshade_config::prepare_reshade_config;
#[cfg(windows)]
pub use windows_host::{
    AutoGame, GameWindow, find_game_target, list_game_windows, match_game_window,
};
