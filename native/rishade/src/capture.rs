use std::cell::RefCell;
use std::ffi::{CString, c_void};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;
use windows::Foundation::{EventRegistrationToken, TimeSpan, TypedEventHandler};
use windows::Graphics::Capture::{
    Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Graphics::SizeInt32;
use windows::Win32::Foundation::{HMODULE, HWND, RECT};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, D3D11CreateDevice, ID3D11Device,
    ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::System::Com::{APTTYPE, APTTYPE_MTA, APTTYPEQUALIFIER, CoGetApartmentType};
use windows::Win32::System::WinRT::Direct3D11::{
    CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess,
};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize};
use windows::Win32::UI::WindowsAndMessaging::{
    GWL_EXSTYLE, GetClientRect, GetWindowLongPtrW, GetWindowTextLengthW, IsWindow, IsWindowVisible,
    WS_EX_TOOLWINDOW,
};
use windows::core::{IInspectable, Interface, factory};

pub type RiShadeFrameCallback = unsafe extern "system" fn(*mut c_void, *mut c_void, u32, u32, i64);
type FrameCallback = RiShadeFrameCallback;
const FRAME_BUFFERS: i32 = 2;

thread_local! {
    static LAST_ERROR: RefCell<CString> = RefCell::new(CString::default());
}

struct CaptureState {
    stop: AtomicBool,
    closed: AtomicBool,
    failed: AtomicBool,
    finished: AtomicBool,
    error: Mutex<Option<String>>,
    wake: Condvar,
    wait_lock: Mutex<()>,
    callback_gate: Mutex<CallbackGate>,
    callbacks_drained: Condvar,
}

#[derive(Default)]
struct CallbackGate {
    accepting: bool,
    active: usize,
}

struct CallbackLease(Arc<CaptureState>);

impl Drop for CallbackLease {
    fn drop(&mut self) {
        let mut gate = self
            .0
            .callback_gate
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        gate.active -= 1;
        if gate.active == 0 {
            self.0.callbacks_drained.notify_all();
        }
    }
}

impl CaptureState {
    fn new() -> Self {
        Self {
            stop: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            failed: AtomicBool::new(false),
            finished: AtomicBool::new(false),
            error: Mutex::new(None),
            wake: Condvar::new(),
            wait_lock: Mutex::new(()),
            callback_gate: Mutex::new(CallbackGate {
                accepting: true,
                active: 0,
            }),
            callbacks_drained: Condvar::new(),
        }
    }

    fn fail(&self, error: impl std::fmt::Display) {
        if let Ok(mut current) = self.error.lock() {
            *current = Some(error.to_string());
        }
        self.failed.store(true, Ordering::Release);
        self.wake.notify_all();
    }

    fn begin_callback(self: &Arc<Self>) -> Option<CallbackLease> {
        let mut gate = self.callback_gate.lock().ok()?;
        if !gate.accepting {
            return None;
        }
        gate.active += 1;
        Some(CallbackLease(Arc::clone(self)))
    }

    fn stop_callbacks(&self) {
        let mut gate = self
            .callback_gate
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        gate.accepting = false;
    }

    fn wait_for_callbacks(&self) {
        let mut gate = self
            .callback_gate
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        while gate.active != 0 {
            gate = self
                .callbacks_drained
                .wait(gate)
                .unwrap_or_else(|poisoned| poisoned.into_inner());
        }
    }
}

pub struct RiShadeCapture {
    state: Arc<CaptureState>,
    worker: Option<JoinHandle<()>>,
}

struct WinRtApartment;

struct CaptureResources {
    pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    item: GraphicsCaptureItem,
    _frame_handler: TypedEventHandler<Direct3D11CaptureFramePool, IInspectable>,
    frame_token: EventRegistrationToken,
    _close_handler: TypedEventHandler<GraphicsCaptureItem, IInspectable>,
    close_token: EventRegistrationToken,
}

struct SendCom<T>(T);

impl<T> SendCom<T> {
    fn get(&self) -> &T {
        &self.0
    }
}

unsafe impl Send for SendCom<Direct3D11CaptureFramePool> {}
unsafe impl Send for SendCom<IDirect3DDevice> {}
unsafe impl Send for SendCom<ID3D11Device> {}

impl Drop for WinRtApartment {
    fn drop(&mut self) {
        unsafe { RoUninitialize() };
    }
}

fn set_error(error: impl std::fmt::Display) {
    let error = CString::new(error.to_string().replace('\0', "�")).unwrap_or_default();
    LAST_ERROR.with(|slot| *slot.borrow_mut() = error);
}

fn is_mta_thread() -> bool {
    let mut apartment = APTTYPE(0);
    let mut qualifier = APTTYPEQUALIFIER(0);
    unsafe { CoGetApartmentType(&mut apartment, &mut qualifier) }.is_ok()
        && apartment == APTTYPE_MTA
}

fn utf8_prefix_len(value: &str, capacity: usize) -> usize {
    let mut count = value.len().min(capacity);
    while !value.is_char_boundary(count) {
        count -= 1;
    }
    count
}

#[unsafe(no_mangle)]

pub unsafe extern "system" fn RiShadeCaptureStart(
    hwnd: *mut c_void,
    target_fps: u32,
    callback: Option<FrameCallback>,
    user_data: *mut c_void,
) -> *mut RiShadeCapture {
    start_capture(hwnd, target_fps, callback, user_data, None)
}

#[unsafe(no_mangle)]

pub unsafe extern "system" fn RiShadeCaptureStartWithDevice(
    hwnd: *mut c_void,
    device: *mut c_void,
    target_fps: u32,
    callback: Option<FrameCallback>,
    user_data: *mut c_void,
) -> *mut RiShadeCapture {
    if !is_mta_thread() {
        set_error("the caller thread must be initialized as a multithreaded COM apartment");
        return std::ptr::null_mut();
    }
    if device.is_null() {
        set_error("a D3D11 device is required");
        return std::ptr::null_mut();
    }
    let Some(device) = (unsafe { ID3D11Device::from_raw_borrowed(&device) }) else {
        set_error("the D3D11 device pointer is invalid");
        return std::ptr::null_mut();
    };
    start_capture(hwnd, target_fps, callback, user_data, Some(device.clone()))
}

fn start_capture(
    hwnd: *mut c_void,
    target_fps: u32,
    callback: Option<FrameCallback>,
    user_data: *mut c_void,
    device: Option<ID3D11Device>,
) -> *mut RiShadeCapture {
    let Some(callback) = callback else {
        set_error("a frame callback is required");
        return std::ptr::null_mut();
    };
    if hwnd.is_null() {
        set_error("a target window is required");
        return std::ptr::null_mut();
    }
    if !is_valid_capture_window(hwnd) {
        set_error("the target is not a capturable visible window");
        return std::ptr::null_mut();
    }

    let state = Arc::new(CaptureState::new());
    let worker_state = Arc::clone(&state);
    let hwnd = hwnd as isize;
    let user_data = user_data as usize;
    let (started, result) = std::sync::mpsc::sync_channel(1);
    let worker = thread::spawn(move || {
        let result = capture_window(
            hwnd,
            target_fps,
            callback,
            user_data,
            device.map(SendCom),
            &worker_state,
            &started,
        );
        if let Err(error) = result {
            worker_state.fail(&error);
            let _ = started.send(Err(error));
        }
        worker_state.finished.store(true, Ordering::Release);
        worker_state.wake.notify_all();
    });

    match result.recv() {
        Ok(Ok(())) => {
            LAST_ERROR.with(|slot| *slot.borrow_mut() = CString::default());
            Box::into_raw(Box::new(RiShadeCapture {
                state,
                worker: Some(worker),
            }))
        }
        Ok(Err(error)) => {
            let _ = worker.join();
            set_error(error);
            std::ptr::null_mut()
        }
        Err(error) => {
            let _ = worker.join();
            set_error(error);
            std::ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]

pub unsafe extern "system" fn RiShadeCaptureStop(capture: *mut RiShadeCapture) -> bool {
    if capture.is_null() {
        set_error("capture handle is null");
        return false;
    }
    let mut capture = unsafe { Box::from_raw(capture) };
    capture.state.stop.store(true, Ordering::Release);
    capture.state.wake.notify_all();
    let stopped = capture
        .worker
        .take()
        .is_none_or(|worker| worker.join().is_ok());
    if stopped {
        LAST_ERROR.with(|slot| *slot.borrow_mut() = CString::default());
    } else {
        set_error("the window capture worker failed to stop cleanly");
    }
    stopped
}

#[unsafe(no_mangle)]

pub unsafe extern "system" fn RiShadeCaptureIsFinished(capture: *const RiShadeCapture) -> bool {
    if capture.is_null() {
        set_error("capture handle is null");
        return true;
    }
    unsafe { &*capture }.state.finished.load(Ordering::Acquire)
}

#[unsafe(no_mangle)]

pub unsafe extern "system" fn RiShadeCaptureCopyError(
    capture: *const RiShadeCapture,
    buffer: *mut u8,
    capacity: usize,
) -> usize {
    if capture.is_null() {
        return 0;
    }
    let capture = unsafe { &*capture };
    let Ok(error) = capture.state.error.lock() else {
        return 0;
    };
    let Some(error) = error.as_deref() else {
        return 0;
    };
    let bytes = error.as_bytes();
    if buffer.is_null() || capacity == 0 {
        return bytes.len();
    }
    let count = utf8_prefix_len(error, capacity - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, count);
        *buffer.add(count) = 0;
    }
    bytes.len()
}

#[unsafe(no_mangle)]
pub extern "system" fn RiShadeCaptureLastError() -> *const i8 {
    LAST_ERROR.with(|slot| slot.borrow().as_ptr())
}

fn is_valid_capture_window(hwnd: *mut c_void) -> bool {
    let hwnd = HWND(hwnd);
    if hwnd.is_invalid()
        || !unsafe { IsWindow(hwnd).as_bool() }
        || !unsafe { IsWindowVisible(hwnd).as_bool() }
    {
        return false;
    }
    if unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } & WS_EX_TOOLWINDOW.0 as isize != 0 {
        return false;
    }
    let mut client = RECT::default();
    unsafe { GetClientRect(hwnd, &mut client) }.is_ok()
        && client.right > 0
        && client.bottom > 0
        && unsafe { GetWindowTextLengthW(hwnd) } > 0
}

fn capture_window(
    hwnd: isize,
    target_fps: u32,
    callback: FrameCallback,
    user_data: usize,
    device: Option<SendCom<ID3D11Device>>,
    state: &Arc<CaptureState>,
    started: &std::sync::mpsc::SyncSender<Result<(), String>>,
) -> Result<(), String> {
    unsafe { RoInitialize(RO_INIT_MULTITHREADED) }.map_err(|error| error.to_string())?;
    let _apartment = WinRtApartment;
    let resources = setup_capture(hwnd, target_fps, callback, user_data, device, state)
        .map_err(|error| error.to_string())?;
    let _ = started.send(Ok(()));

    let mut wait_guard = state.wait_lock.lock().map_err(|error| error.to_string())?;
    while !state.stop.load(Ordering::Acquire)
        && !state.closed.load(Ordering::Acquire)
        && !state.failed.load(Ordering::Acquire)
    {
        if !unsafe { IsWindow(HWND(hwnd as *mut _)).as_bool() } {
            state.closed.store(true, Ordering::Release);
            break;
        }
        let (next_guard, _) = state
            .wake
            .wait_timeout(wait_guard, Duration::from_millis(100))
            .map_err(|error| error.to_string())?;
        wait_guard = next_guard;
    }
    drop(wait_guard);

    let _ = resources.pool.RemoveFrameArrived(resources.frame_token);
    let _ = resources.item.RemoveClosed(resources.close_token);
    state.stop_callbacks();
    let _ = resources.session.Close();
    let _ = resources.pool.Close();
    drop(resources);
    state.wait_for_callbacks();
    Ok(())
}

fn create_capture_device(driver_type: D3D_DRIVER_TYPE) -> windows::core::Result<ID3D11Device> {
    let mut device = None;
    unsafe {
        D3D11CreateDevice(
            None,
            driver_type,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            None,
        )?;
    }
    device.ok_or_else(|| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))
}

fn setup_capture(
    hwnd: isize,
    target_fps: u32,
    callback: FrameCallback,
    user_data: usize,
    device: Option<SendCom<ID3D11Device>>,
    state: &Arc<CaptureState>,
) -> windows::core::Result<CaptureResources> {
    if !GraphicsCaptureSession::IsSupported()? {
        return Err(windows::core::Error::new(
            windows::core::HRESULT(0x80004001u32 as i32),
            "Windows Graphics Capture is not supported",
        ));
    }
    let device: ID3D11Device = if let Some(device) = device {
        device.0
    } else {
        create_capture_device(D3D_DRIVER_TYPE_HARDWARE)
            .or_else(|_| create_capture_device(D3D_DRIVER_TYPE_WARP))?
    };
    let dxgi_device: IDXGIDevice = device.cast()?;
    let winrt_device: IDirect3DDevice =
        unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi_device)? }.cast()?;
    let interop: IGraphicsCaptureItemInterop =
        factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()?;
    let item: GraphicsCaptureItem = unsafe { interop.CreateForWindow(HWND(hwnd as *mut _))? };
    let size = item.Size()?;
    let frame_pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
        &winrt_device,
        DirectXPixelFormat::B8G8R8A8UIntNormalized,
        FRAME_BUFFERS,
        SizeInt32 {
            Width: size.Width.max(1),
            Height: size.Height.max(1),
        },
    )?;
    let session = frame_pool.CreateCaptureSession(&item)?;
    let _ = session.SetIsCursorCaptureEnabled(false);
    let _ = session.SetIsBorderRequired(false);
    if target_fps > 0 {
        let fps = target_fps.clamp(30, 500);
        let _ = session.SetMinUpdateInterval(TimeSpan {
            Duration: (10_000_000 / fps as i64).max(1),
        });
    }

    let pool_for_frames = SendCom(frame_pool.clone());
    let device_for_frames = SendCom(winrt_device.clone());
    let dimensions = Arc::new(Mutex::new((size.Width, size.Height)));
    let dimensions_for_frames = Arc::clone(&dimensions);
    let state_for_frames = Arc::clone(state);
    let frame_handler =
        TypedEventHandler::<Direct3D11CaptureFramePool, IInspectable>::new(move |sender, _args| {
            let Some(_callback_lease) = state_for_frames.begin_callback() else {
                return Ok(());
            };
            let result = (|| -> windows::core::Result<()> {
                let Some(sender) = sender.as_ref() else {
                    return Ok(());
                };
                let frame = sender.TryGetNextFrame()?;
                let size = frame.ContentSize()?;
                if size.Width <= 0 || size.Height <= 0 {
                    return Ok(());
                }
                let surface = frame.Surface()?;
                let access: IDirect3DDxgiInterfaceAccess = surface.cast()?;
                let texture: ID3D11Texture2D = unsafe { access.GetInterface()? };
                let timestamp = frame.SystemRelativeTime()?.Duration;

                let mut known_size = dimensions_for_frames
                    .lock()
                    .map_err(|_| windows::core::Error::from_win32())?;
                if *known_size != (size.Width, size.Height) {
                    pool_for_frames.get().Recreate(
                        device_for_frames.get(),
                        DirectXPixelFormat::B8G8R8A8UIntNormalized,
                        FRAME_BUFFERS,
                        size,
                    )?;
                    *known_size = (size.Width, size.Height);
                    return Ok(());
                }
                drop(known_size);

                unsafe {
                    callback(
                        user_data as *mut c_void,
                        texture.as_raw(),
                        size.Width as u32,
                        size.Height as u32,
                        timestamp,
                    );
                }
                Ok(())
            })();
            if let Err(error) = result {
                state_for_frames.fail(error);
            }
            Ok(())
        });
    let state_for_close = Arc::clone(state);
    let close_handler =
        TypedEventHandler::<GraphicsCaptureItem, IInspectable>::new(move |_sender, _args| {
            state_for_close.closed.store(true, Ordering::Release);
            state_for_close.wake.notify_all();
            Ok(())
        });
    let close_token = item.Closed(&close_handler)?;
    let frame_token = match frame_pool.FrameArrived(&frame_handler) {
        Ok(token) => token,
        Err(error) => {
            let _ = item.RemoveClosed(close_token);
            return Err(error);
        }
    };
    if let Err(error) = session.StartCapture() {
        let _ = frame_pool.RemoveFrameArrived(frame_token);
        let _ = item.RemoveClosed(close_token);
        return Err(error);
    }
    Ok(CaptureResources {
        pool: frame_pool,
        session,
        item,
        _frame_handler: frame_handler,
        frame_token,
        _close_handler: close_handler,
        close_token,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;
    use std::time::Duration;

    #[test]
    fn shutdown_rejects_new_callbacks_and_waits_for_active_ones() {
        let state = Arc::new(CaptureState::new());
        let callback = state.begin_callback().expect("callback lease");
        state.stop_callbacks();
        assert!(state.begin_callback().is_none());

        let (drained, wait) = mpsc::channel();
        let worker_state = Arc::clone(&state);
        let worker = thread::spawn(move || {
            worker_state.wait_for_callbacks();
            drained.send(()).unwrap();
        });
        assert!(wait.recv_timeout(Duration::from_millis(25)).is_err());
        drop(callback);
        wait.recv_timeout(Duration::from_secs(1))
            .expect("callback drain completes");
        worker.join().unwrap();
    }

    #[test]
    fn copied_error_prefix_stays_valid_utf8() {
        let error = "capture failed: 🛑";
        let count = utf8_prefix_len(error, 18);
        assert!(std::str::from_utf8(&error.as_bytes()[..count]).is_ok());
    }

    #[test]
    fn shared_device_requires_an_initialized_mta_thread() {
        assert!(
            !thread::spawn(is_mta_thread)
                .join()
                .expect("apartment check thread")
        );
    }
}
