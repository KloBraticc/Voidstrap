#[cfg(windows)]
use rishade_core::{
    RiShadeCaptureIsFinished, RiShadeCaptureLastError, RiShadeCaptureStartWithDevice,
    RiShadeCaptureStop, RiShadeFrameCallback, RiShadeOverlayWindow,
};
#[cfg(windows)]
use std::ffi::c_void;
#[cfg(windows)]
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
#[cfg(windows)]
use std::sync::mpsc::{self, SyncSender};
#[cfg(windows)]
use std::thread;
#[cfg(windows)]
use std::time::{Duration, Instant};
#[cfg(windows)]
use windows::Win32::Foundation::HMODULE;
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL_11_0};
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, D3D11CreateDevice, ID3D11Device,
    ID3D11Texture2D,
};
#[cfg(windows)]
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
#[cfg(windows)]
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
#[cfg(windows)]
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize};
#[cfg(windows)]
use windows::Win32::UI::WindowsAndMessaging::{
    CW_USEDEFAULT, CreateWindowExW, DestroyWindow, DispatchMessageW, MSG, PM_REMOVE, PeekMessageW,
    SWP_NOACTIVATE, SWP_NOZORDER, SetWindowPos, SetWindowTextW, TranslateMessage, WS_EX_NOACTIVATE,
    WS_OVERLAPPEDWINDOW, WS_VISIBLE,
};
#[cfg(windows)]
use windows::core::Interface;
#[cfg(windows)]
use windows::core::{PCWSTR, w};

#[cfg(windows)]
static FRAMES: AtomicU32 = AtomicU32::new(0);
#[cfg(windows)]
static RESIZED: AtomicBool = AtomicBool::new(false);
#[cfg(windows)]
static INVALID_FRAME: AtomicBool = AtomicBool::new(false);

#[cfg(windows)]
struct CapturedFrame {
    texture: ID3D11Texture2D,
    width: u32,
    height: u32,
}

#[cfg(windows)]
unsafe impl Send for CapturedFrame {}

#[cfg(windows)]
struct Apartment;

#[cfg(windows)]
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { RoUninitialize() };
    }
}

#[cfg(windows)]
unsafe extern "system" fn on_frame(
    user_data: *mut c_void,
    texture: *mut c_void,
    width: u32,
    height: u32,
    _timestamp: i64,
) {
    if texture.is_null() || user_data.is_null() || width == 0 || height == 0 {
        INVALID_FRAME.store(true, Ordering::Release);
        return;
    }
    if width > 640 || height > 480 {
        RESIZED.store(true, Ordering::Release);
    }
    let Some(texture) = (unsafe { ID3D11Texture2D::from_raw_borrowed(&texture) }) else {
        INVALID_FRAME.store(true, Ordering::Release);
        return;
    };
    let texture = texture.clone();
    let sender = unsafe { &*user_data.cast::<SyncSender<CapturedFrame>>() };
    let _ = sender.try_send(CapturedFrame {
        texture,
        width,
        height,
    });
    FRAMES.fetch_add(1, Ordering::AcqRel);
}

#[cfg(windows)]
fn pump_messages() {
    let mut message = MSG::default();
    unsafe {
        while PeekMessageW(&mut message, None, 0, 0, PM_REMOVE).as_bool() {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

#[cfg(windows)]
fn main() -> windows::core::Result<()> {
    unsafe { RoInitialize(RO_INIT_MULTITHREADED)? };
    let _apartment = Apartment;
    let instance = unsafe { GetModuleHandleW(PCWSTR::null())? };
    let window = unsafe {
        CreateWindowExW(
            WS_EX_NOACTIVATE,
            w!("STATIC"),
            w!("RiShade capture probe"),
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            640,
            480,
            None,
            None,
            instance,
            None,
        )?
    };

    let mut device = None;
    unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            None,
        )?;
    }
    let device: ID3D11Device = device.expect("D3D11 device creation returned no device");
    let dxgi_device: IDXGIDevice = device.cast()?;
    let mut overlay = RiShadeOverlayWindow::new(window, &dxgi_device, instance.0)?;
    let (sender, frames) = mpsc::sync_channel::<CapturedFrame>(2);
    let sender = Box::new(sender);
    let user_data = (&*sender as *const SyncSender<CapturedFrame>)
        .cast_mut()
        .cast();

    let capture = unsafe {
        RiShadeCaptureStartWithDevice(
            window.0,
            device.as_raw(),
            60,
            Some(on_frame as RiShadeFrameCallback),
            user_data,
        )
    };
    if capture.is_null() {
        let error = unsafe { std::ffi::CStr::from_ptr(RiShadeCaptureLastError()) };
        let _ = unsafe { DestroyWindow(window) };
        return Err(windows::core::Error::new(
            windows::core::HRESULT(0x80004005u32 as i32),
            error.to_string_lossy(),
        ));
    }

    let started = Instant::now();
    let mut last_title_update = Instant::now();
    let mut renderer = None;
    let mut presented = 0u32;
    let mut presented_resized = false;
    let mut render_error = None;
    while started.elapsed() < Duration::from_secs(4) {
        pump_messages();
        if let Err(error) = overlay.sync_to_target(true) {
            render_error = Some(error);
            break;
        }
        while let Ok(frame) = frames.try_recv() {
            if renderer.is_none() {
                match overlay.create_renderer(&device, frame.width, frame.height) {
                    Ok(value) => renderer = Some(value),
                    Err(error) => {
                        render_error = Some(error);
                        break;
                    }
                }
            }
            if let Err(error) = renderer
                .as_mut()
                .expect("renderer was created")
                .present_frame(&frame.texture, frame.width, frame.height)
            {
                render_error = Some(error);
                break;
            }
            presented += 1;
            if frame.width > 640 || frame.height > 480 {
                presented_resized = true;
            }
        }
        if render_error.is_some() {
            break;
        }
        if last_title_update.elapsed() >= Duration::from_millis(100) {
            let title = format!("RiShade capture probe {}", FRAMES.load(Ordering::Acquire));
            let title: Vec<u16> = title.encode_utf16().chain(std::iter::once(0)).collect();
            unsafe {
                let _ = SetWindowTextW(window, windows::core::PCWSTR(title.as_ptr()));
            }
            last_title_update = Instant::now();
        }
        if started.elapsed() >= Duration::from_secs(1) && !RESIZED.load(Ordering::Acquire) {
            unsafe {
                let _ = SetWindowPos(window, None, 0, 0, 960, 720, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
        thread::sleep(Duration::from_millis(16));
    }
    drop(renderer);
    drop(overlay);
    let _ = unsafe { DestroyWindow(window) };
    let shutdown_started = Instant::now();
    while !unsafe { RiShadeCaptureIsFinished(capture) }
        && shutdown_started.elapsed() < Duration::from_secs(2)
    {
        pump_messages();
        thread::sleep(Duration::from_millis(10));
    }
    let finished = unsafe { RiShadeCaptureIsFinished(capture) };
    let stopped = unsafe { RiShadeCaptureStop(capture) };
    drop(sender);
    if let Some(error) = render_error {
        return Err(error);
    }
    if !finished
        || !stopped
        || FRAMES.load(Ordering::Acquire) == 0
        || !RESIZED.load(Ordering::Acquire)
        || INVALID_FRAME.load(Ordering::Acquire)
        || presented == 0
        || !presented_resized
    {
        return Err(windows::core::Error::new(
            windows::core::HRESULT(0x80004005u32 as i32),
            format!(
                "capture probe failed: finished={finished}, stopped={stopped}, frames={}, presented={presented}, presented_resized={presented_resized}, resized={}, invalid={}",
                FRAMES.load(Ordering::Acquire),
                RESIZED.load(Ordering::Acquire),
                INVALID_FRAME.load(Ordering::Acquire)
            ),
        ));
    }
    println!(
        "Capture probe passed: {} frames; resize and window-close shutdown succeeded.",
        FRAMES.load(Ordering::Acquire)
    );
    Ok(())
}

#[cfg(not(windows))]
fn main() {
    eprintln!("The capture probe is available on Windows only.");
}
