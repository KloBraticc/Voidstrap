#![cfg_attr(not(windows), allow(dead_code))]

#[cfg(windows)]
mod dlss_tab;
#[cfg(windows)]
mod plugin;

#[cfg(windows)]
pub use plugin::{
    RiShadeAddonCopyCurrentPresetPath, RiShadeAddonIsCompilingEffects, RiShadeAddonIsInitialized,
    RiShadeAddonIsLoadingEffects, RiShadeAddonIsOverlayOpen, RiShadeAddonRuntimeIsActive,
    RiShadeAddonSetDlssState, RiShadeAddonTakeDlssRestart, RiShadeAddonTakeDlssSettingsRequest,
    RiShadeRenderEffects, RiShadeRuntimeBindDepthTexture, RiShadeRuntimeCreate,
    RiShadeRuntimeDestroy, RiShadeRuntimeLoad, RiShadeRuntimeOpenOverlay, RiShadeRuntimePresent,
    RiShadeRuntimeSelectPreset,
};
