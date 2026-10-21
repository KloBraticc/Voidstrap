#![cfg_attr(not(windows), allow(dead_code))]

#[cfg(windows)]
mod plugin;

#[cfg(windows)]
pub use plugin::{
    RiShadeAddonCopyCurrentPresetPath, RiShadeAddonIsCompilingEffects, RiShadeAddonIsInitialized,
    RiShadeAddonIsLoadingEffects, RiShadeAddonIsOverlayOpen, RiShadeAddonRuntimeIsActive,
    RiShadeRenderEffects, RiShadeRuntimeBindDepthTexture, RiShadeRuntimeCreate,
    RiShadeRuntimeDestroy, RiShadeRuntimeLoad, RiShadeRuntimeOpenOverlay, RiShadeRuntimePresent,
    RiShadeRuntimeSelectPreset,
};
