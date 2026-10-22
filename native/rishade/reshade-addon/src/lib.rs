#![cfg_attr(not(windows), allow(dead_code))]

#[cfg(windows)]
mod plugin;

#[cfg(windows)]
pub use plugin::{
    RiShadeAddonActiveRuntime, RiShadeAddonCopyCurrentPresetPath, RiShadeAddonIsCompilingEffects, RiShadeAddonIsInitialized,
    RiShadeAddonIsLoadingEffects, RiShadeAddonIsOverlayOpen, RiShadeAddonRuntimeIsActive,
    RiShadeRenderEffects, RiShadeRuntimeAttachWindow, RiShadeRuntimeBindDepthTexture, RiShadeRuntimeCreate,
    RiShadeRuntimeDetachWindow,
    RiShadeRuntimeDestroy, RiShadeRuntimeLoad, RiShadeRuntimeOpenOverlay, RiShadeRuntimePresent,
    RiShadeRuntimeSelectPreset,
};
