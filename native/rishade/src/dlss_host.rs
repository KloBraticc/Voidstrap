#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(not(windows))]
fn main() {}

#[cfg(windows)]
fn main() {
    std::process::exit(match host::run() {
        Ok(()) => 0,
        Err(_) => 1,
    });
}

#[cfg(windows)]
mod host {
    use std::ffi::c_void;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use windows::Win32::Foundation::{
        CloseHandle, FreeLibrary, HANDLE, HMODULE, HWND, LUID, S_OK, WAIT_OBJECT_0,
    };
    use windows::Win32::Graphics::Direct3D::{
        D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1,
    };
    use windows::Win32::Graphics::Direct3D11::{
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, ID3D11Device, ID3D11Device1,
        ID3D11DeviceContext, ID3D11Texture2D,
    };
    use windows::Win32::Graphics::Dxgi::Common::{
        DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_MODE_DESC, DXGI_SAMPLE_DESC,
    };
    use windows::Win32::Graphics::Dxgi::{
        DXGI_SHARED_RESOURCE_READ, DXGI_SHARED_RESOURCE_WRITE, DXGI_SWAP_CHAIN_DESC,
        DXGI_SWAP_CHAIN_FLAG, DXGI_SWAP_EFFECT_SEQUENTIAL, DXGI_USAGE_RENDER_TARGET_OUTPUT,
        IDXGIAdapter1, IDXGIFactory1, IDXGIKeyedMutex, IDXGISwapChain, DXGI_PRESENT,
    };
    use windows::Win32::System::LibraryLoader::{
        GetModuleHandleW, GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW, LoadLibraryW,
    };
    use windows::Win32::System::Memory::{
        FILE_MAP_ALL_ACCESS, MEMORY_MAPPED_VIEW_ADDRESS, MapViewOfFile, OpenFileMappingW,
        UnmapViewOfFile,
    };
    use windows::Win32::System::Threading::{
        OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DestroyWindow, DispatchMessageW, MSG, PM_REMOVE, PeekMessageW,
        TranslateMessage, WINDOW_EX_STYLE, WS_POPUP,
    };
    use windows::Win32::Foundation::{BOOL, HINSTANCE};
    use windows::core::{HSTRING, Interface, PCWSTR, s, w};

    const RESHADE_API_VERSION: u32 = 20;
    const PRESENT_EVENT: u32 = 74;
    const LOCK_TIMEOUT_MS: u32 = 250;
    const STATE_RUNNING: u32 = 1;
    const STATE_FAILED: u32 = 2;

    #[repr(C)]
    struct Header {
        generation: u32,
        width: u32,
        height: u32,
        luid_low: u32,
        luid_high: i32,
        state: u32,
    }

    type CreateFactory = unsafe extern "system" fn(*const windows::core::GUID, *mut *mut c_void) -> windows::core::HRESULT;
    type CreateDeviceAndSwapChain = unsafe extern "system" fn(
        *mut c_void,
        i32,
        HMODULE,
        u32,
        *const D3D_FEATURE_LEVEL,
        u32,
        u32,
        *const DXGI_SWAP_CHAIN_DESC,
        *mut *mut c_void,
        *mut *mut c_void,
        *mut D3D_FEATURE_LEVEL,
        *mut *mut c_void,
    ) -> windows::core::HRESULT;
    type RegisterAddon = unsafe extern "C" fn(HMODULE, u32) -> bool;
    type UnregisterAddon = unsafe extern "C" fn(HMODULE);
    type RegisterEvent = unsafe extern "C" fn(HMODULE, u32, *mut c_void);

    static COPY_CONTEXT: AtomicUsize = AtomicUsize::new(0);
    static COPY_SOURCE: AtomicUsize = AtomicUsize::new(0);
    static COPY_TARGET: AtomicUsize = AtomicUsize::new(0);
    static COPIED: AtomicBool = AtomicBool::new(false);

    unsafe extern "C" fn on_present(
        _queue: *mut c_void,
        _swapchain: *mut c_void,
        _source_rect: *const c_void,
        _dest_rect: *const c_void,
        _dirty_rect_count: u32,
        _dirty_rects: *const c_void,
    ) {
        let context = COPY_CONTEXT.load(Ordering::Acquire) as *mut c_void;
        let source = COPY_SOURCE.load(Ordering::Acquire) as *mut c_void;
        let target = COPY_TARGET.load(Ordering::Acquire) as *mut c_void;
        let (Some(context), Some(source), Some(target)) = (
            unsafe { ID3D11DeviceContext::from_raw_borrowed(&context) },
            unsafe { ID3D11Texture2D::from_raw_borrowed(&source) },
            unsafe { ID3D11Texture2D::from_raw_borrowed(&target) },
        ) else {
            return;
        };
        unsafe { context.CopyResource(target, source) };
        COPIED.store(true, Ordering::Release);
    }

    struct Options {
        root: PathBuf,
        map: String,
        parent: u32,
    }

    fn parse_options() -> Option<Options> {
        let mut root = None;
        let mut map = None;
        let mut parent = None;
        let mut args = std::env::args().skip(1);
        while let Some(name) = args.next() {
            let value = args.next()?;
            match name.as_str() {
                "--root" => root = Some(PathBuf::from(value)),
                "--map" => map = Some(value),
                "--parent" => parent = value.parse().ok(),
                _ => return None,
            }
        }
        Some(Options {
            root: root?,
            map: map?,
            parent: parent?,
        })
    }

    unsafe fn symbol<T: Copy>(module: HMODULE, name: windows::core::PCSTR) -> Option<T> {
        let address = unsafe { GetProcAddress(module, name) }?;
        Some(unsafe { std::mem::transmute_copy(&address) })
    }

    fn acquire(mutex: &IDXGIKeyedMutex, key: u64) -> bool {
        unsafe { (Interface::vtable(mutex).AcquireSync)(Interface::as_raw(mutex), key, LOCK_TIMEOUT_MS) == S_OK }
    }

    struct Shared {
        input: ID3D11Texture2D,
        input_lock: IDXGIKeyedMutex,
        output: ID3D11Texture2D,
        output_lock: IDXGIKeyedMutex,
    }

    fn open_shared(device: &ID3D11Device1, map: &str, generation: u32) -> windows::core::Result<Shared> {
        let access = DXGI_SHARED_RESOURCE_READ.0 | DXGI_SHARED_RESOURCE_WRITE.0;
        let input: ID3D11Texture2D = unsafe {
            device.OpenSharedResourceByName(&HSTRING::from(format!("{map}_in_{generation}")), access)
        }?;
        let output: ID3D11Texture2D = unsafe {
            device.OpenSharedResourceByName(&HSTRING::from(format!("{map}_out_{generation}")), access)
        }?;
        Ok(Shared {
            input_lock: input.cast()?,
            input,
            output_lock: output.cast()?,
            output,
        })
    }

    fn pump_messages() {
        let mut message = MSG::default();
        while unsafe { PeekMessageW(&mut message, HWND::default(), 0, 0, PM_REMOVE) }.as_bool() {
            unsafe {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
    }

    pub fn run() -> Result<(), ()> {
        let options = parse_options().ok_or(())?;
        let parent = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, options.parent) }.map_err(|_| ())?;
        let mapping = unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS.0, false, &HSTRING::from(options.map.as_str())) }
            .map_err(|_| ())?;
        let view = unsafe { MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, std::mem::size_of::<Header>()) };
        if view.Value.is_null() {
            return Err(());
        }
        let header = view.Value.cast::<Header>();
        let result = unsafe { serve(&options, parent, header) };
        unsafe {
            std::ptr::write_volatile(&raw mut (*header).state, if result.is_ok() { 0 } else { STATE_FAILED });
            let _ = UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: view.Value });
            let _ = CloseHandle(mapping);
            let _ = CloseHandle(parent);
        }
        result
    }

    unsafe fn serve(options: &Options, parent: HANDLE, header: *mut Header) -> Result<(), ()> {
        let base = rishade_core::reshade_config::prepare_dlss_config(&options.root).map_err(|_| ())?;
        unsafe {
            std::env::set_var("RESHADE_BASE_PATH_OVERRIDE", &base);
            std::env::set_var("RESHADE_DISABLE_INPUT_HOOK", "1");
            std::env::remove_var("RESHADE_DISABLE_GRAPHICS_HOOK");
            std::env::remove_var("RESHADE_DISABLE_LOGGING");
        }
        let system_d3d11 = unsafe { LoadLibraryExW(w!("d3d11.dll"), HANDLE::default(), LOAD_LIBRARY_SEARCH_SYSTEM32) }.map_err(|_| ())?;
        let reshade = unsafe { LoadLibraryW(&HSTRING::from(options.root.join("dxgi.dll").as_os_str())) }.map_err(|_| ())?;
        let outcome = unsafe { serve_with(options, parent, header, reshade) };
        unsafe {
            let _ = FreeLibrary(reshade);
            let _ = FreeLibrary(system_d3d11);
        }
        outcome
    }

    unsafe fn serve_with(options: &Options, parent: HANDLE, header: *mut Header, reshade: HMODULE) -> Result<(), ()> {
        let create_factory: CreateFactory = unsafe { symbol(reshade, s!("CreateDXGIFactory1")) }.ok_or(())?;
        let create_device: CreateDeviceAndSwapChain =
            unsafe { symbol(reshade, s!("D3D11CreateDeviceAndSwapChain")) }.ok_or(())?;
        let register_addon: RegisterAddon = unsafe { symbol(reshade, s!("ReShadeRegisterAddon")) }.ok_or(())?;
        let unregister_addon: UnregisterAddon = unsafe { symbol(reshade, s!("ReShadeUnregisterAddon")) }.ok_or(())?;
        let register_event: RegisterEvent = unsafe { symbol(reshade, s!("ReShadeRegisterEventForAddon")) }.ok_or(())?;

        let read = move |field: fn(*mut Header) -> *mut u32| unsafe { std::ptr::read_volatile(field(header)) };
        while read(|h| unsafe { &raw mut (*h).width }) == 0 || read(|h| unsafe { &raw mut (*h).height }) == 0 {
            if unsafe { WaitForSingleObject(parent, 100) } == WAIT_OBJECT_0 {
                return Ok(());
            }
        }

        let luid = LUID {
            LowPart: read(|h| unsafe { &raw mut (*h).luid_low }),
            HighPart: unsafe { std::ptr::read_volatile(&raw const (*header).luid_high) },
        };
        let mut factory_raw = std::ptr::null_mut();
        unsafe { create_factory(&IDXGIFactory1::IID, &mut factory_raw) }.ok().map_err(|_| ())?;
        let factory = unsafe { IDXGIFactory1::from_raw(factory_raw) };
        let mut adapter: Option<IDXGIAdapter1> = None;
        let mut index = 0;
        while let Ok(candidate) = unsafe { factory.EnumAdapters1(index) } {
            index += 1;
            if unsafe { candidate.GetDesc1() }.is_ok_and(|desc| {
                desc.AdapterLuid.LowPart == luid.LowPart && desc.AdapterLuid.HighPart == luid.HighPart
            }) {
                adapter = Some(candidate);
                break;
            }
        }
        let adapter = adapter.ok_or(())?;

        let window = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE::default(),
                w!("STATIC"),
                w!("RiShade DLSS"),
                WS_POPUP,
                0,
                0,
                1,
                1,
                None,
                None,
                GetModuleHandleW(PCWSTR::null()).map(|module| HINSTANCE(module.0)).unwrap_or_default(),
                None,
            )
        }
        .map_err(|_| ())?;

        let mut generation = read(|h| unsafe { &raw mut (*h).generation });
        let mut width = read(|h| unsafe { &raw mut (*h).width });
        let mut height = read(|h| unsafe { &raw mut (*h).height });
        let swap_desc = DXGI_SWAP_CHAIN_DESC {
            BufferDesc: DXGI_MODE_DESC {
                Width: width,
                Height: height,
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                ..Default::default()
            },
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 1,
            OutputWindow: window,
            Windowed: BOOL::from(true),
            SwapEffect: DXGI_SWAP_EFFECT_SEQUENTIAL,
            Flags: 0,
        };
        let levels = [D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0];
        let mut swapchain_raw = std::ptr::null_mut();
        let mut device_raw = std::ptr::null_mut();
        let mut context_raw = std::ptr::null_mut();
        let created = unsafe {
            create_device(
                adapter.as_raw(),
                D3D_DRIVER_TYPE_UNKNOWN.0,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT.0 as u32,
                levels.as_ptr(),
                levels.len() as u32,
                D3D11_SDK_VERSION,
                &swap_desc,
                &mut swapchain_raw,
                &mut device_raw,
                std::ptr::null_mut(),
                &mut context_raw,
            )
        };
        if created.is_err() || swapchain_raw.is_null() || device_raw.is_null() || context_raw.is_null() {
            unsafe {
                let _ = DestroyWindow(window);
            }
            return Err(());
        }
        let swapchain = unsafe { IDXGISwapChain::from_raw(swapchain_raw) };
        let device = unsafe { ID3D11Device::from_raw(device_raw) };
        let context = unsafe { ID3D11DeviceContext::from_raw(context_raw) };
        let device1: ID3D11Device1 = device.cast().map_err(|_| ())?;

        let module = unsafe { GetModuleHandleW(PCWSTR::null()) }.map_err(|_| ())?;
        let registered = unsafe { register_addon(module, RESHADE_API_VERSION) };
        if registered {
            unsafe { register_event(module, PRESENT_EVENT, on_present as *mut c_void) };
        }
        COPY_CONTEXT.store(context.as_raw() as usize, Ordering::Release);
        unsafe { std::ptr::write_volatile(&raw mut (*header).state, STATE_RUNNING) };

        let mut backbuffer: Option<ID3D11Texture2D> = unsafe { swapchain.GetBuffer(0) }.ok();
        let mut shared = open_shared(&device1, &options.map, generation).ok();
        let outcome = loop {
            pump_messages();
            if unsafe { WaitForSingleObject(parent, 0) } == WAIT_OBJECT_0 {
                break Ok(());
            }
            let current = read(|h| unsafe { &raw mut (*h).generation });
            if current != generation || shared.is_none() {
                shared = None;
                COPY_SOURCE.store(0, Ordering::Release);
                COPY_TARGET.store(0, Ordering::Release);
                let new_width = read(|h| unsafe { &raw mut (*h).width });
                let new_height = read(|h| unsafe { &raw mut (*h).height });
                if new_width != width || new_height != height {
                    backbuffer = None;
                    if unsafe { swapchain.ResizeBuffers(1, new_width, new_height, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SWAP_CHAIN_FLAG(0)) }.is_err() {
                        break Err(());
                    }
                    width = new_width;
                    height = new_height;
                    backbuffer = unsafe { swapchain.GetBuffer(0) }.ok();
                }
                generation = current;
                shared = open_shared(&device1, &options.map, generation).ok();
                if shared.is_none() {
                    unsafe { WaitForSingleObject(parent, 50) };
                    continue;
                }
            }
            let (Some(frame), Some(back)) = (shared.as_ref(), backbuffer.as_ref()) else {
                break Err(());
            };
            if !acquire(&frame.input_lock, 1) {
                continue;
            }
            unsafe {
                context.CopyResource(back, &frame.input);
                let _ = frame.input_lock.ReleaseSync(0);
            }
            if !acquire(&frame.output_lock, 0) {
                continue;
            }
            COPY_SOURCE.store(back.as_raw() as usize, Ordering::Release);
            COPY_TARGET.store(frame.output.as_raw() as usize, Ordering::Release);
            COPIED.store(false, Ordering::Release);
            let presented = unsafe { swapchain.Present(0, DXGI_PRESENT(0)) };
            COPY_SOURCE.store(0, Ordering::Release);
            COPY_TARGET.store(0, Ordering::Release);
            if !COPIED.load(Ordering::Acquire) {
                unsafe { context.CopyResource(&frame.output, back) };
            }
            unsafe {
                let _ = frame.output_lock.ReleaseSync(1);
            }
            if presented.is_err() {
                break Err(());
            }
        };

        COPY_CONTEXT.store(0, Ordering::Release);
        if registered {
            unsafe { unregister_addon(module) };
        }
        drop(shared);
        drop(backbuffer);
        drop(device1);
        drop(context);
        drop(device);
        drop(swapchain);
        unsafe {
            let _ = DestroyWindow(window);
        }
        outcome
    }
}
