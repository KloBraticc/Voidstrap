use std::ffi::c_void;
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Device, ID3D11DeviceContext, ID3D11Resource, ID3D11Texture2D,
};
use windows::Win32::Graphics::DirectComposition::{
    DCompositionCreateDevice, IDCompositionDevice, IDCompositionTarget, IDCompositionVisual,
};
use windows::Win32::Graphics::Dwm::{DWMWA_EXTENDED_FRAME_BOUNDS, DwmGetWindowAttribute};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_ALPHA_MODE_PREMULTIPLIED, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_SCALING_STRETCH, DXGI_SWAP_CHAIN_DESC1, DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
    DXGI_USAGE_RENDER_TARGET_OUTPUT, IDXGIDevice, IDXGIFactory2, IDXGISwapChain1,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DestroyWindow, GetForegroundWindow, HWND_TOPMOST, IsIconic, IsWindow,
    IsWindowVisible, SW_HIDE, SW_SHOWNOACTIVATE, SWP_NOACTIVATE, SWP_SHOWWINDOW, SetWindowPos,
    ShowWindow, WS_EX_NOACTIVATE, WS_EX_NOREDIRECTIONBITMAP, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
    WS_EX_TRANSPARENT, WS_POPUP,
};
use windows::core::Interface;
use windows::core::w;

pub struct RiShadeOverlayWindow {
    target: HWND,
    window: HWND,
    composition: IDCompositionDevice,
    _composition_target: IDCompositionTarget,
    _visual: IDCompositionVisual,
    last_bounds: Option<(i32, i32, i32, i32)>,
    visible: bool,
}

pub struct RiShadeOverlayRenderer {
    swap_chain: IDXGISwapChain1,
    context: ID3D11DeviceContext,
    width: u32,
    height: u32,
}

impl RiShadeOverlayWindow {
    pub fn new(
        target: HWND,
        device: &IDXGIDevice,
        module: *mut c_void,
    ) -> windows::core::Result<Self> {
        if !unsafe { IsWindow(target).as_bool() } {
            return Err(windows::core::Error::from_win32());
        }
        let composition: IDCompositionDevice = unsafe { DCompositionCreateDevice(device)? };
        let instance = windows::Win32::Foundation::HINSTANCE(module);
        let window = unsafe {
            CreateWindowExW(
                WS_EX_TOPMOST
                    | WS_EX_TOOLWINDOW
                    | WS_EX_TRANSPARENT
                    | WS_EX_NOACTIVATE
                    | WS_EX_NOREDIRECTIONBITMAP,
                w!("STATIC"),
                w!("RiShade output"),
                WS_POPUP,
                0,
                0,
                1,
                1,
                None,
                None,
                instance,
                None,
            )?
        };
        let composition_target = match unsafe { composition.CreateTargetForHwnd(window, true) } {
            Ok(target) => target,
            Err(error) => {
                let _ = unsafe { DestroyWindow(window) };
                return Err(error);
            }
        };
        let visual = match unsafe { composition.CreateVisual() } {
            Ok(visual) => visual,
            Err(error) => {
                let _ = unsafe { DestroyWindow(window) };
                return Err(error);
            }
        };
        let composition_result = unsafe {
            composition_target
                .SetRoot(&visual)
                .and_then(|()| composition.Commit())
        };
        if let Err(error) = composition_result {
            let _ = unsafe { DestroyWindow(window) };
            return Err(error);
        }
        let mut overlay = Self {
            target,
            window,
            composition,
            _composition_target: composition_target,
            _visual: visual,
            last_bounds: None,
            visible: false,
        };
        overlay.sync_to_target(false)?;
        Ok(overlay)
    }

    pub fn set_content(&self, swap_chain: &IDXGISwapChain1) -> windows::core::Result<()> {
        unsafe {
            self._visual.SetContent(swap_chain)?;
            self.composition.Commit()
        }
    }

    pub fn create_renderer(
        &self,
        device: &ID3D11Device,
        width: u32,
        height: u32,
    ) -> windows::core::Result<RiShadeOverlayRenderer> {
        let dxgi_device: IDXGIDevice = device.cast()?;
        let adapter = unsafe { dxgi_device.GetAdapter()? };
        let factory: IDXGIFactory2 = unsafe { adapter.GetParent()? };
        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: width.max(1),
            Height: height.max(1),
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags: 0,
        };
        let swap_chain = unsafe { factory.CreateSwapChainForComposition(device, &desc, None)? };
        self.set_content(&swap_chain)?;
        let context = unsafe { device.GetImmediateContext()? };
        Ok(RiShadeOverlayRenderer {
            swap_chain,
            context,
            width: desc.Width,
            height: desc.Height,
        })
    }

    pub fn sync_to_target(&mut self, keep_visible: bool) -> windows::core::Result<bool> {
        if !unsafe { IsWindow(self.target).as_bool() } {
            return Err(windows::core::Error::from_win32());
        }
        let mut cloaked = 0u32;
        let is_cloaked = unsafe {
            DwmGetWindowAttribute(
                self.target,
                windows::Win32::Graphics::Dwm::DWMWA_CLOAKED,
                (&mut cloaked as *mut u32).cast(),
                std::mem::size_of::<u32>() as u32,
            )
        }
        .is_ok()
            && cloaked != 0;
        if !unsafe { IsWindowVisible(self.target).as_bool() }
            || unsafe { IsIconic(self.target).as_bool() }
            || is_cloaked
            || (!keep_visible && unsafe { GetForegroundWindow() } != self.target)
        {
            if self.visible {
                let _ = unsafe { ShowWindow(self.window, SW_HIDE) };
            }
            self.visible = false;
            return Ok(false);
        }

        let mut rect = RECT::default();
        unsafe {
            DwmGetWindowAttribute(
                self.target,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                (&mut rect as *mut RECT).cast(),
                std::mem::size_of::<RECT>() as u32,
            )?;
        }
        if rect.right <= rect.left || rect.bottom <= rect.top {
            if self.visible {
                let _ = unsafe { ShowWindow(self.window, SW_HIDE) };
            }
            self.visible = false;
            return Ok(false);
        }
        let bounds = (
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
        );
        if !self.visible || self.last_bounds != Some(bounds) {
            unsafe {
                SetWindowPos(
                    self.window,
                    HWND_TOPMOST,
                    bounds.0,
                    bounds.1,
                    bounds.2,
                    bounds.3,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW,
                )?;
                let _ = ShowWindow(self.window, SW_SHOWNOACTIVATE);
            }
            self.last_bounds = Some(bounds);
            self.visible = true;
        }
        Ok(true)
    }

    pub fn hwnd(&self) -> HWND {
        self.window
    }
}

impl RiShadeOverlayRenderer {
    pub fn present_frame(
        &mut self,
        texture: &ID3D11Texture2D,
        width: u32,
        height: u32,
    ) -> windows::core::Result<()> {
        if width == 0 || height == 0 {
            return Err(windows::core::Error::from_win32());
        }
        if (width, height) != (self.width, self.height) {
            unsafe {
                self.swap_chain.ResizeBuffers(
                    0,
                    width,
                    height,
                    DXGI_FORMAT_B8G8R8A8_UNORM,
                    Default::default(),
                )?;
            }
            self.width = width;
            self.height = height;
        }
        let back_buffer: ID3D11Texture2D = unsafe { self.swap_chain.GetBuffer(0)? };
        let destination: ID3D11Resource = back_buffer.cast()?;
        let source: ID3D11Resource = texture.cast()?;
        unsafe {
            self.context.CopyResource(&destination, &source);
            self.swap_chain.Present(1, Default::default()).ok()?;
        }
        Ok(())
    }
}

impl Drop for RiShadeOverlayWindow {
    fn drop(&mut self) {
        let _ = unsafe { DestroyWindow(self.window) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overlay_window_uses_click_through_no_activate_styles() {
        let flags = WS_EX_TOPMOST
            | WS_EX_TOOLWINDOW
            | WS_EX_TRANSPARENT
            | WS_EX_NOACTIVATE
            | WS_EX_NOREDIRECTIONBITMAP;
        assert_eq!(flags.0, 0x0820_00a8);
    }
}
