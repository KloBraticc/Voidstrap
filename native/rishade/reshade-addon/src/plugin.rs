use std::ffi::{CStr, CString, c_char, c_void};
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

const RESHADE_API_VERSION: u32 = 20;
const RESHADE_D3D11_API: u32 = 0xB000;
const INIT_EFFECT_RUNTIME: u32 = 9;
const DESTROY_EFFECT_RUNTIME: u32 = 10;
const RESHADE_RELOADED_EFFECTS: u32 = 78;
const RESHADE_SET_CURRENT_PRESET_PATH: u32 = 84;
const RESHADE_OPEN_OVERLAY: u32 = 86;
const INPUT_SOURCE_KEYBOARD: u32 = 2;

const EFFECT_RUNTIME_GET_COMMAND_QUEUE_VTABLE_INDEX: usize = 8;
const EFFECT_RUNTIME_RENDER_EFFECTS_VTABLE_INDEX: usize = 9;
const COMMAND_QUEUE_GET_IMMEDIATE_COMMAND_LIST_VTABLE_INDEX: usize = 7;
const EFFECT_RUNTIME_UPDATE_TEXTURE_BINDINGS_VTABLE_INDEX: usize = 46;
const EFFECT_RUNTIME_GET_CURRENT_PRESET_PATH_VTABLE_INDEX: usize = 62;
const EFFECT_RUNTIME_SET_CURRENT_PRESET_PATH_VTABLE_INDEX: usize = 63;
const EFFECT_RUNTIME_OPEN_OVERLAY_VTABLE_INDEX: usize = 74;
const SWAPCHAIN_GET_DESC_VTABLE_INDEX: usize = 12;
const SWAPCHAIN_GET_HWND_VTABLE_INDEX: usize = 20;
const SWAPCHAIN4_VTABLE_LEN: usize = 41;
const GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS: u32 = 0x00000004;
const GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT: u32 = 0x00000002;

type Module = *mut c_void;
type RegisterAddon = unsafe extern "C" fn(Module, u32) -> bool;
type UnregisterAddon = unsafe extern "C" fn(Module);
type RegisterEvent = unsafe extern "C" fn(Module, u32, *mut c_void);
type UnregisterEvent = unsafe extern "C" fn(Module, u32, *mut c_void);

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetProcAddress(module: Module, name: *const c_char) -> *mut c_void;
    fn LoadLibraryExW(path: *const u16, file: Module, flags: u32) -> Module;
    fn FreeLibrary(module: Module) -> i32;
    fn GetModuleHandleExW(flags: u32, name: *const u16, module: *mut Module) -> i32;
    fn GetCurrentProcess() -> *mut c_void;
    fn GetTickCount64() -> u64;
    fn K32EnumProcessModules(
        process: *mut c_void,
        modules: *mut Module,
        bytes: u32,
        needed: *mut u32,
    ) -> i32;
}

#[repr(transparent)]
pub struct ExportedString(*const c_char);

unsafe impl Sync for ExportedString {}

static ADDON_NAME: &[u8] = b"RiShadeRuntime\0";
static ADDON_DESCRIPTION: &[u8] = b"In-process RiShade runtime for Voidstrap\0";

#[unsafe(no_mangle)]
pub static NAME: ExportedString = ExportedString(ADDON_NAME.as_ptr().cast());

#[unsafe(no_mangle)]
pub static DESCRIPTION: ExportedString = ExportedString(ADDON_DESCRIPTION.as_ptr().cast());

type CreateEffectRuntime = unsafe extern "system" fn(
    u32,
    *mut c_void,
    *mut c_void,
    *mut c_void,
    *const c_char,
    *mut *mut c_void,
) -> bool;
type DestroyEffectRuntime = unsafe extern "system" fn(*mut c_void);
static FOLDER_ADDONS_LOADED: AtomicUsize = AtomicUsize::new(0);
static ADDON_GUARD_PATH: Mutex<Option<PathBuf>> = Mutex::new(None);
static HEALTHY_PRESENTS: AtomicUsize = AtomicUsize::new(0);

pub const ADDON_GUARD_FILE: &str = ".rishade-addon-guard";
const HEALTHY_PRESENT_COUNT: usize = 300;

fn clear_addon_guard() {
    if let Ok(mut guard) = ADDON_GUARD_PATH.lock()
        && let Some(path) = guard.take()
    {
        let _ = std::fs::remove_file(path);
    }
}

const LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR: u32 = 0x00000100;
const LOAD_LIBRARY_SEARCH_DEFAULT_DIRS: u32 = 0x00001000;

unsafe fn load_folder_addons(runtime_path: &std::path::Path) {
    if FOLDER_ADDONS_LOADED.swap(1, Ordering::AcqRel) != 0 {
        return;
    }
    let Some(directory) = runtime_path.parent() else {
        return;
    };
    let guard = directory.join(ADDON_GUARD_FILE);
    if guard.exists() {
        return;
    }
    let Ok(entries) = std::fs::read_dir(directory) else {
        return;
    };
    let mut armed = false;
    for entry in entries.flatten() {
        let path = entry.path();
        let is_addon = path
            .extension()
            .and_then(std::ffi::OsStr::to_str)
            .is_some_and(|extension| extension.eq_ignore_ascii_case("addon64"));
        if !is_addon || !entry.file_type().is_ok_and(|kind| kind.is_file()) {
            continue;
        }
        if !armed {
            if std::fs::write(&guard, b"loading").is_err() {
                return;
            }
            if let Ok(mut slot) = ADDON_GUARD_PATH.lock() {
                *slot = Some(guard.clone());
            }
            armed = true;
        }
        let wide: Vec<u16> = path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
        unsafe {
            LoadLibraryExW(
                wide.as_ptr(),
                std::ptr::null_mut(),
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS,
            )
        };
    }
}
type UpdateAndPresentEffectRuntime = unsafe extern "system" fn(*mut c_void);

struct ReShadeApi {
    module: usize,
    unregister_event: UnregisterEvent,
    unregister_addon: UnregisterAddon,
}

static API: Mutex<Option<ReShadeApi>> = Mutex::new(None);
static ACTIVE_RUNTIME: AtomicUsize = AtomicUsize::new(0);
static DEPTH_SRV: AtomicUsize = AtomicUsize::new(0);
static LOADED_RESHADE_MODULE: AtomicUsize = AtomicUsize::new(0);
static UPDATE_AND_PRESENT: AtomicUsize = AtomicUsize::new(0);
static LOADING_STARTED: AtomicUsize = AtomicUsize::new(0);
static COMPILING_EFFECTS: AtomicUsize = AtomicUsize::new(0);
static OVERLAY_OPEN: AtomicUsize = AtomicUsize::new(0);
static CURRENT_PRESET_PATH: Mutex<Option<CString>> = Mutex::new(None);
static PENDING_PRESET_PATH: Mutex<Option<CString>> = Mutex::new(None);
static PRESET_PENDING: AtomicBool = AtomicBool::new(false);
const EFFECT_LOADING_TIMEOUT_MS: u64 = 3_000;
const LOAD_WITH_ALTERED_SEARCH_PATH: u32 = 0x00000008;

unsafe fn symbol<T: Copy>(module: Module, name: &'static [u8]) -> Option<T> {
    let address = unsafe { GetProcAddress(module, name.as_ptr().cast()) };
    if address.is_null() {
        return None;
    }
    Some(unsafe { std::mem::transmute_copy(&address) })
}

unsafe fn find_reshade_module() -> Option<Module> {
    let process = unsafe { GetCurrentProcess() };
    let mut needed = 0;
    if unsafe { K32EnumProcessModules(process, std::ptr::null_mut(), 0, &mut needed) } == 0
        || needed == 0
    {
        return None;
    }

    for _ in 0..2 {
        let count = (needed as usize).div_ceil(std::mem::size_of::<Module>());
        if count > 32_768 {
            return None;
        }
        let mut modules = vec![std::ptr::null_mut(); count];
        if unsafe {
            K32EnumProcessModules(
                process,
                modules.as_mut_ptr(),
                (std::mem::size_of_val(modules.as_slice())).min(u32::MAX as usize) as u32,
                &mut needed,
            )
        } == 0
        {
            return None;
        }
        let loaded = (needed as usize / std::mem::size_of::<Module>()).min(modules.len());
        for module in modules.into_iter().take(loaded) {
            if !module.is_null()
                && !unsafe { GetProcAddress(module, c"ReShadeCreateEffectRuntime".as_ptr()) }
                    .is_null()
                && !unsafe { GetProcAddress(module, c"ReShadeDestroyEffectRuntime".as_ptr()) }
                    .is_null()
                && !unsafe {
                    GetProcAddress(module, c"ReShadeUpdateAndPresentEffectRuntime".as_ptr())
                }
                .is_null()
            {
                return Some(module);
            }
        }
        if needed == 0 {
            return None;
        }
    }
    None
}

unsafe fn initialize_addon() -> bool {
    if RiShadeAddonIsInitialized() {
        return true;
    }
    let Some(reshade_module) = (unsafe { find_reshade_module() }) else {
        return false;
    };
    let mut addon_module = std::ptr::null_mut();
    let address = AddonInit as *const () as *const u16;
    if unsafe {
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            address,
            &mut addon_module,
        )
    } == 0
        || addon_module.is_null()
    {
        return false;
    }
    unsafe { AddonInit(addon_module, reshade_module) }
}

fn is_allowed_runtime_path(path: &std::path::Path) -> bool {
    let component_matches = |path: Option<&std::ffi::OsStr>, expected: &str| {
        path.is_some_and(|value| value.to_string_lossy().eq_ignore_ascii_case(expected))
    };
    path.is_absolute()
        && component_matches(path.file_name(), "dxgi.dll")
        && component_matches(
            path.parent().and_then(std::path::Path::file_name),
            "RiShade",
        )
        && component_matches(
            path.parent()
                .and_then(std::path::Path::parent)
                .and_then(std::path::Path::file_name),
            "Extensions",
        )
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimeLoad(dxgi_path: *const u16) -> bool {
    if dxgi_path.is_null() {
        return false;
    }
    if LOADED_RESHADE_MODULE.load(Ordering::Acquire) != 0 {
        return unsafe { initialize_addon() };
    }

    let mut path_len = 0usize;
    while path_len < 32_768 && unsafe { *dxgi_path.add(path_len) } != 0 {
        path_len += 1;
    }
    if path_len == 0 || path_len == 32_768 {
        return false;
    }
    let path = PathBuf::from(std::ffi::OsString::from_wide(unsafe {
        std::slice::from_raw_parts(dxgi_path, path_len)
    }));
    if !is_allowed_runtime_path(&path) {
        return false;
    }

    let module = unsafe {
        LoadLibraryExW(
            dxgi_path,
            std::ptr::null_mut(),
            LOAD_WITH_ALTERED_SEARCH_PATH,
        )
    };
    if module.is_null()
        || unsafe { GetProcAddress(module, c"ReShadeCreateEffectRuntime".as_ptr()) }.is_null()
        || unsafe { GetProcAddress(module, c"ReShadeDestroyEffectRuntime".as_ptr()) }.is_null()
        || unsafe { GetProcAddress(module, c"ReShadeUpdateAndPresentEffectRuntime".as_ptr()) }
            .is_null()
    {
        if !module.is_null() {
            unsafe { FreeLibrary(module) };
        }
        return false;
    }

    let Some(update_and_present) = (unsafe {
        symbol::<UpdateAndPresentEffectRuntime>(module, b"ReShadeUpdateAndPresentEffectRuntime\0")
    }) else {
        unsafe { FreeLibrary(module) };
        return false;
    };

    match LOADED_RESHADE_MODULE.compare_exchange(
        0,
        module as usize,
        Ordering::AcqRel,
        Ordering::Acquire,
    ) {
        Ok(_) => {
            UPDATE_AND_PRESENT.store(update_and_present as usize, Ordering::Release);
            let ready = unsafe { initialize_addon() };
            if ready {
                unsafe { load_folder_addons(&path) };
            }
            ready
        }
        Err(_) => {
            unsafe { FreeLibrary(module) };
            unsafe { initialize_addon() }
        }
    }
}

#[repr(C)]
struct DxgiModeDesc {
    width: u32,
    height: u32,
    refresh_rate_numerator: u32,
    refresh_rate_denominator: u32,
    format: u32,
    scanline_ordering: u32,
    scaling: u32,
}

#[repr(C)]
struct DxgiSampleDesc {
    count: u32,
    quality: u32,
}

#[repr(C)]
struct DxgiSwapChainDesc {
    buffer_desc: DxgiModeDesc,
    sample_desc: DxgiSampleDesc,
    buffer_usage: u32,
    buffer_count: u32,
    output_window: *mut c_void,
    windowed: i32,
    swap_effect: u32,
    flags: u32,
}

type GetDescFn = unsafe extern "system" fn(*mut c_void, *mut DxgiSwapChainDesc) -> i32;
type GetHwndFn = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> i32;

static SWAPCHAIN_TARGET_WINDOW: AtomicUsize = AtomicUsize::new(0);
static ORIGINAL_GET_DESC: AtomicUsize = AtomicUsize::new(0);
static ORIGINAL_GET_HWND: AtomicUsize = AtomicUsize::new(0);

unsafe extern "system" fn swapchain_hook_get_desc(
    this: *mut c_void,
    desc: *mut DxgiSwapChainDesc,
) -> i32 {
    let original = ORIGINAL_GET_DESC.load(Ordering::Relaxed);
    let hr = if original != 0 {
        let orig_fn: GetDescFn = unsafe { std::mem::transmute(original) };
        unsafe { orig_fn(this, desc) }
    } else {
        0
    };
    if !desc.is_null() {
        let target = SWAPCHAIN_TARGET_WINDOW.load(Ordering::Relaxed) as *mut c_void;
        if !target.is_null() {
            unsafe {
                (*desc).output_window = target;
            }
        }
    }
    hr
}

unsafe extern "system" fn swapchain_hook_get_hwnd(
    this: *mut c_void,
    hwnd: *mut *mut c_void,
) -> i32 {
    let target = SWAPCHAIN_TARGET_WINDOW.load(Ordering::Relaxed) as *mut c_void;
    if !hwnd.is_null() && !target.is_null() {
        unsafe {
            *hwnd = target;
        }
        0
    } else {
        let original = ORIGINAL_GET_HWND.load(Ordering::Relaxed);
        if original != 0 {
            let orig_fn: GetHwndFn = unsafe { std::mem::transmute(original) };
            unsafe { orig_fn(this, hwnd) }
        } else {
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimeCreate(
    device: *mut c_void,
    immediate_context: *mut c_void,
    swapchain: *mut c_void,
    window: *mut c_void,
    config_path: *const c_char,
) -> *mut c_void {
    if device.is_null()
        || immediate_context.is_null()
        || swapchain.is_null()
        || config_path.is_null()
    {
        return std::ptr::null_mut();
    }
    let Some(module) = (unsafe { find_reshade_module() }) else {
        return std::ptr::null_mut();
    };
    let Some(create) =
        (unsafe { symbol::<CreateEffectRuntime>(module, b"ReShadeCreateEffectRuntime\0") })
    else {
        return std::ptr::null_mut();
    };

    let mut runtime = std::ptr::null_mut();
    let created = if !window.is_null() {
        let vtable_ptr = swapchain.cast::<*const *const c_void>();
        let original_vtable = unsafe { *vtable_ptr };
        let mut shadow_vtable = [std::ptr::null::<c_void>(); SWAPCHAIN4_VTABLE_LEN];
        unsafe {
            std::ptr::copy_nonoverlapping(
                original_vtable,
                shadow_vtable.as_mut_ptr(),
                SWAPCHAIN4_VTABLE_LEN,
            );
        }

        ORIGINAL_GET_DESC.store(
            shadow_vtable[SWAPCHAIN_GET_DESC_VTABLE_INDEX] as usize,
            Ordering::Relaxed,
        );
        ORIGINAL_GET_HWND.store(
            shadow_vtable[SWAPCHAIN_GET_HWND_VTABLE_INDEX] as usize,
            Ordering::Relaxed,
        );
        SWAPCHAIN_TARGET_WINDOW.store(window as usize, Ordering::Relaxed);

        shadow_vtable[SWAPCHAIN_GET_DESC_VTABLE_INDEX] = swapchain_hook_get_desc as *const c_void;
        shadow_vtable[SWAPCHAIN_GET_HWND_VTABLE_INDEX] = swapchain_hook_get_hwnd as *const c_void;

        unsafe {
            *(swapchain.cast::<*const *const c_void>()) = shadow_vtable.as_ptr();
        }

        let res = unsafe {
            create(
                RESHADE_D3D11_API,
                device,
                immediate_context,
                swapchain,
                config_path,
                &mut runtime,
            )
        };

        unsafe {
            *(swapchain.cast::<*const *const c_void>()) = original_vtable;
        }
        SWAPCHAIN_TARGET_WINDOW.store(0, Ordering::Relaxed);
        ORIGINAL_GET_DESC.store(0, Ordering::Relaxed);
        ORIGINAL_GET_HWND.store(0, Ordering::Relaxed);

        res
    } else {
        unsafe {
            create(
                RESHADE_D3D11_API,
                device,
                immediate_context,
                swapchain,
                config_path,
                &mut runtime,
            )
        }
    };
    if created {
        runtime
    } else {
        std::ptr::null_mut()
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimeDestroy(runtime: *mut c_void) {
    if runtime.is_null() {
        return;
    }
    let Some(module) = (unsafe { find_reshade_module() }) else {
        return;
    };
    if let Some(destroy) =
        unsafe { symbol::<DestroyEffectRuntime>(module, b"ReShadeDestroyEffectRuntime\0") }
    {
        unsafe { destroy(runtime) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimeOpenOverlay(runtime: *mut c_void, open: bool) -> bool {
    if runtime.is_null() {
        return false;
    }
    let vtable = unsafe { *(runtime.cast::<*const *const c_void>()) };
    if vtable.is_null() {
        return false;
    }
    let open_overlay = unsafe { *vtable.add(EFFECT_RUNTIME_OPEN_OVERLAY_VTABLE_INDEX) };
    if open_overlay.is_null() {
        return false;
    }
    let open_overlay: unsafe extern "system" fn(*mut c_void, bool, u32) -> bool =
        unsafe { std::mem::transmute(open_overlay) };
    let success = unsafe { open_overlay(runtime, open, INPUT_SOURCE_KEYBOARD) };
    if success {
        OVERLAY_OPEN.store(usize::from(open), Ordering::Release);
    }
    success
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimePresent(runtime: *mut c_void) -> bool {
    if runtime.is_null() {
        return false;
    }
    let vtable = unsafe { *(runtime.cast::<*const *const c_void>()) };
    if vtable.is_null() {
        return false;
    }

    if HEALTHY_PRESENTS.load(Ordering::Relaxed) < HEALTHY_PRESENT_COUNT
        && HEALTHY_PRESENTS.fetch_add(1, Ordering::Relaxed) + 1 == HEALTHY_PRESENT_COUNT
    {
        clear_addon_guard();
    }

    if PRESET_PENDING.swap(false, Ordering::AcqRel) {
        let pending_preset = PENDING_PRESET_PATH
            .lock()
            .ok()
            .and_then(|mut pending| pending.take());
        if let Some(preset) = pending_preset {
            let set_path =
                unsafe { *vtable.add(EFFECT_RUNTIME_SET_CURRENT_PRESET_PATH_VTABLE_INDEX) };
            if !set_path.is_null() {
                let set_path: unsafe extern "system" fn(*mut c_void, *const c_char) =
                    unsafe { std::mem::transmute(set_path) };
                unsafe { set_path(runtime, preset.as_ptr()) };
                cache_preset_path(preset);
            }
        }
    }

    let update_and_present = UPDATE_AND_PRESENT.load(Ordering::Acquire);
    if update_and_present == 0 {
        return false;
    }
    let update_and_present: UpdateAndPresentEffectRuntime =
        unsafe { std::mem::transmute(update_and_present) };
    unsafe { update_and_present(runtime) };
    true
}

unsafe extern "system" fn on_runtime_created(runtime: *mut c_void) {
    ACTIVE_RUNTIME.store(runtime as usize, Ordering::Release);
    let depth_srv = DEPTH_SRV.load(Ordering::Acquire) as *mut c_void;
    if !depth_srv.is_null() {
        unsafe { RiShadeRuntimeBindDepthTexture(runtime, depth_srv) };
    }
    LOADING_STARTED.store(unsafe { GetTickCount64() as usize }, Ordering::Release);
    COMPILING_EFFECTS.store(1, Ordering::Release);
    unsafe { refresh_current_preset_path(runtime) };
}

unsafe extern "system" fn on_runtime_destroyed(runtime: *mut c_void) {
    let _ =
        ACTIVE_RUNTIME.compare_exchange(runtime as usize, 0, Ordering::AcqRel, Ordering::Acquire);
    if ACTIVE_RUNTIME.load(Ordering::Acquire) == 0 {
        LOADING_STARTED.store(0, Ordering::Release);
        COMPILING_EFFECTS.store(0, Ordering::Release);
        OVERLAY_OPEN.store(0, Ordering::Release);
        if let Ok(mut path) = CURRENT_PRESET_PATH.lock() {
            *path = None;
        }
        if let Ok(mut path) = PENDING_PRESET_PATH.lock() {
            *path = None;
        }
        PRESET_PENDING.store(false, Ordering::Release);
    }
}

unsafe fn refresh_current_preset_path(runtime: *mut c_void) {
    if runtime.is_null() {
        return;
    }
    let vtable = unsafe { *(runtime.cast::<*const *const c_void>()) };
    if vtable.is_null() {
        return;
    }
    let get_path = unsafe { *vtable.add(EFFECT_RUNTIME_GET_CURRENT_PRESET_PATH_VTABLE_INDEX) };
    if get_path.is_null() {
        return;
    }
    let get_path: unsafe extern "system" fn(*mut c_void, *mut c_char, *mut usize) =
        unsafe { std::mem::transmute(get_path) };
    let mut length = 0usize;
    unsafe { get_path(runtime, std::ptr::null_mut(), &mut length) };
    if length == 0 || length > 32_768 {
        return;
    }
    let mut bytes = vec![0u8; length];
    unsafe { get_path(runtime, bytes.as_mut_ptr().cast(), &mut length) };
    let Some(nul) = bytes.iter().position(|byte| *byte == 0) else {
        return;
    };
    bytes.truncate(nul);
    let Ok(path) = CString::new(bytes) else {
        return;
    };
    cache_preset_path(path);
}

fn cache_preset_path(path: CString) {
    if let Ok(mut current) = CURRENT_PRESET_PATH.lock() {
        *current = Some(path);
    }
}

unsafe extern "system" fn on_effects_reloaded(runtime: *mut c_void) {
    if runtime as usize != ACTIVE_RUNTIME.load(Ordering::Acquire) {
        return;
    }
    COMPILING_EFFECTS.store(0, Ordering::Release);
    LOADING_STARTED.store(0, Ordering::Release);
    let depth_srv = DEPTH_SRV.load(Ordering::Acquire) as *mut c_void;
    if !depth_srv.is_null() {
        unsafe { RiShadeRuntimeBindDepthTexture(runtime, depth_srv) };
    }
}

unsafe extern "system" fn on_preset_path_changed(runtime: *mut c_void, path: *const c_char) {
    if runtime as usize == ACTIVE_RUNTIME.load(Ordering::Acquire) {
        LOADING_STARTED.store(unsafe { GetTickCount64() as usize }, Ordering::Release);
        COMPILING_EFFECTS.store(1, Ordering::Release);
        if path.is_null() {
            if let Ok(mut current) = CURRENT_PRESET_PATH.lock() {
                *current = None;
            }
        } else {
            let path = unsafe { CStr::from_ptr(path) }.to_bytes();
            if path.len() <= 32_767
                && let Ok(path) = CString::new(path)
            {
                cache_preset_path(path);
            }
        }
    }
}

unsafe extern "system" fn on_overlay_state_changed(
    runtime: *mut c_void,
    open: bool,
    _source: u32,
) -> bool {
    if !open && runtime as usize == ACTIVE_RUNTIME.load(Ordering::Acquire) {
        OVERLAY_OPEN.store(0, Ordering::Release);
    }
    false
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimeBindDepthTexture(
    runtime: *mut c_void,
    depth_srv: *mut c_void,
) -> bool {
    if runtime.is_null() {
        return false;
    }
    DEPTH_SRV.store(depth_srv as usize, Ordering::Release);
    let vtable = unsafe { *(runtime.cast::<*const *const c_void>()) };
    if vtable.is_null() {
        return false;
    }
    let update = unsafe { *vtable.add(EFFECT_RUNTIME_UPDATE_TEXTURE_BINDINGS_VTABLE_INDEX) };
    if update.is_null() {
        return false;
    }
    let update: unsafe extern "system" fn(*mut c_void, *const c_char, u64, u64) =
        unsafe { std::mem::transmute(update) };
    let handle = depth_srv as usize as u64;
    unsafe { update(runtime, c"DEPTH".as_ptr(), handle, handle) };
    true
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRenderEffects(
    runtime: *mut c_void,
    render_target: *mut c_void,
    render_target_srgb: *mut c_void,
) -> bool {
    if runtime.is_null() || render_target.is_null() {
        return false;
    }
    let runtime_vtable = unsafe { *(runtime.cast::<*const *const c_void>()) };
    if runtime_vtable.is_null() {
        return false;
    }
    let get_queue = unsafe { *runtime_vtable.add(EFFECT_RUNTIME_GET_COMMAND_QUEUE_VTABLE_INDEX) };
    if get_queue.is_null() {
        return false;
    }
    let get_queue: unsafe extern "system" fn(*mut c_void) -> *mut c_void =
        unsafe { std::mem::transmute(get_queue) };
    let queue = unsafe { get_queue(runtime) };
    if queue.is_null() {
        return false;
    }
    let queue_vtable = unsafe { *(queue.cast::<*const *const c_void>()) };
    if queue_vtable.is_null() {
        return false;
    }
    let get_command_list =
        unsafe { *queue_vtable.add(COMMAND_QUEUE_GET_IMMEDIATE_COMMAND_LIST_VTABLE_INDEX) };
    if get_command_list.is_null() {
        return false;
    }
    let get_command_list: unsafe extern "system" fn(*mut c_void) -> *mut c_void =
        unsafe { std::mem::transmute(get_command_list) };
    let command_list = unsafe { get_command_list(queue) };
    if command_list.is_null() {
        return false;
    }
    let render_effects = unsafe { *runtime_vtable.add(EFFECT_RUNTIME_RENDER_EFFECTS_VTABLE_INDEX) };
    if render_effects.is_null() {
        return false;
    }
    let render_effects: unsafe extern "system" fn(*mut c_void, *mut c_void, u64, u64) =
        unsafe { std::mem::transmute(render_effects) };
    unsafe {
        render_effects(
            runtime,
            command_list,
            render_target as usize as u64,
            render_target_srgb as usize as u64,
        )
    };
    true
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn AddonInit(module: Module, reshade_module: Module) -> bool {
    if module.is_null() || reshade_module.is_null() {
        return false;
    }

    let mut api_slot = match API.lock() {
        Ok(api) => api,
        Err(_) => return false,
    };
    if api_slot.is_some() {
        return true;
    }

    let Some(register_addon) =
        (unsafe { symbol::<RegisterAddon>(reshade_module, b"ReShadeRegisterAddon\0") })
    else {
        return false;
    };
    let Some(unregister_addon) =
        (unsafe { symbol::<UnregisterAddon>(reshade_module, b"ReShadeUnregisterAddon\0") })
    else {
        return false;
    };
    let Some(register_event) =
        (unsafe { symbol::<RegisterEvent>(reshade_module, b"ReShadeRegisterEventForAddon\0") })
    else {
        return false;
    };
    let Some(unregister_event) =
        (unsafe { symbol::<UnregisterEvent>(reshade_module, b"ReShadeUnregisterEventForAddon\0") })
    else {
        return false;
    };
    if !unsafe { register_addon(module, RESHADE_API_VERSION) } {
        return false;
    }

    unsafe {
        register_event(
            module,
            INIT_EFFECT_RUNTIME,
            on_runtime_created as *mut c_void,
        );
        register_event(
            module,
            DESTROY_EFFECT_RUNTIME,
            on_runtime_destroyed as *mut c_void,
        );
        register_event(
            module,
            RESHADE_RELOADED_EFFECTS,
            on_effects_reloaded as *mut c_void,
        );
        register_event(
            module,
            RESHADE_SET_CURRENT_PRESET_PATH,
            on_preset_path_changed as *mut c_void,
        );
        register_event(
            module,
            RESHADE_OPEN_OVERLAY,
            on_overlay_state_changed as *mut c_void,
        );
    }
    *api_slot = Some(ReShadeApi {
        module: module as usize,
        unregister_event,
        unregister_addon,
    });
    true
}

#[unsafe(no_mangle)]
pub extern "system" fn RiShadeAddonIsInitialized() -> bool {
    API.lock().is_ok_and(|api| api.is_some())
}

#[unsafe(no_mangle)]
pub extern "system" fn RiShadeAddonRuntimeIsActive() -> bool {
    ACTIVE_RUNTIME.load(Ordering::Acquire) != 0
}

#[unsafe(no_mangle)]
pub extern "system" fn RiShadeAddonIsCompilingEffects() -> bool {
    let started = LOADING_STARTED.load(Ordering::Acquire) as u64;
    if started != 0
        && unsafe { GetTickCount64() }.saturating_sub(started) >= EFFECT_LOADING_TIMEOUT_MS
    {
        COMPILING_EFFECTS.store(0, Ordering::Release);
        LOADING_STARTED.store(0, Ordering::Release);
        return false;
    }
    COMPILING_EFFECTS.load(Ordering::Acquire) != 0
}

#[unsafe(no_mangle)]
pub extern "system" fn RiShadeAddonIsLoadingEffects() -> bool {
    let started = LOADING_STARTED.load(Ordering::Acquire) as u64;
    if started != 0
        && unsafe { GetTickCount64() }.saturating_sub(started) >= EFFECT_LOADING_TIMEOUT_MS
    {
        LOADING_STARTED.store(0, Ordering::Release);
        return false;
    }
    started != 0 && COMPILING_EFFECTS.load(Ordering::Acquire) != 0
}

#[unsafe(no_mangle)]
pub extern "system" fn RiShadeAddonIsOverlayOpen() -> bool {
    OVERLAY_OPEN.load(Ordering::Acquire) != 0
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeRuntimeSelectPreset(
    runtime: *mut c_void,
    path: *const c_char,
) -> bool {
    if runtime.is_null()
        || runtime as usize != ACTIVE_RUNTIME.load(Ordering::Acquire)
        || path.is_null()
    {
        return false;
    }
    let Ok(path_text) = unsafe { CStr::from_ptr(path) }.to_str() else {
        return false;
    };
    let path = Path::new(path_text);
    if !path.is_absolute()
        || !matches!(
            path.extension().and_then(std::ffi::OsStr::to_str),
            Some(extension) if extension.eq_ignore_ascii_case("ini") || extension.eq_ignore_ascii_case("txt")
        )
        || !path.is_file()
        || path_text.len() > 32_767
    {
        return false;
    }
    let Ok(path) = CString::new(path_text) else {
        return false;
    };
    let Ok(mut pending) = PENDING_PRESET_PATH.lock() else {
        return false;
    };
    *pending = Some(path);
    PRESET_PENDING.store(true, Ordering::Release);
    true
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn RiShadeAddonCopyCurrentPresetPath(
    buffer: *mut u8,
    capacity: usize,
) -> usize {
    let Ok(current) = CURRENT_PRESET_PATH.lock() else {
        return 0;
    };
    let Some(path) = current.as_ref() else {
        return 0;
    };
    let bytes = path.as_bytes();
    if !buffer.is_null() && capacity > 0 {
        let copied = bytes.len().min(capacity - 1);
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, copied);
            *buffer.add(copied) = 0;
        }
    }
    bytes.len()
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn AddonUninit(module: Module, _reshade_module: Module) {
    let Ok(mut api_slot) = API.lock() else {
        return;
    };
    let Some(api) = api_slot.take() else {
        return;
    };
    let registered_module = api.module as Module;
    if registered_module != module {
        *api_slot = Some(api);
        return;
    }

    unsafe {
        (api.unregister_event)(
            module,
            INIT_EFFECT_RUNTIME,
            on_runtime_created as *mut c_void,
        );
        (api.unregister_event)(
            module,
            DESTROY_EFFECT_RUNTIME,
            on_runtime_destroyed as *mut c_void,
        );
        (api.unregister_event)(
            module,
            RESHADE_RELOADED_EFFECTS,
            on_effects_reloaded as *mut c_void,
        );
        (api.unregister_event)(
            module,
            RESHADE_SET_CURRENT_PRESET_PATH,
            on_preset_path_changed as *mut c_void,
        );
        (api.unregister_event)(
            module,
            RESHADE_OPEN_OVERLAY,
            on_overlay_state_changed as *mut c_void,
        );
        (api.unregister_addon)(module);
    }
    ACTIVE_RUNTIME.store(0, Ordering::Release);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pins_the_reshade_api_and_runtime_lifecycle_constants() {
        assert_eq!(RESHADE_API_VERSION, 20);
        assert_eq!(RESHADE_D3D11_API, 0xB000);
        assert_eq!(INIT_EFFECT_RUNTIME, 9);
        assert_eq!(DESTROY_EFFECT_RUNTIME, 10);
        assert_eq!(EFFECT_RUNTIME_GET_COMMAND_QUEUE_VTABLE_INDEX, 8);
        assert_eq!(EFFECT_RUNTIME_RENDER_EFFECTS_VTABLE_INDEX, 9);
        assert_eq!(COMMAND_QUEUE_GET_IMMEDIATE_COMMAND_LIST_VTABLE_INDEX, 7);
        assert_eq!(EFFECT_RUNTIME_UPDATE_TEXTURE_BINDINGS_VTABLE_INDEX, 46);
        assert_eq!(SWAPCHAIN_GET_DESC_VTABLE_INDEX, 12);
        assert_eq!(SWAPCHAIN_GET_HWND_VTABLE_INDEX, 20);
        assert_eq!(SWAPCHAIN4_VTABLE_LEN, 41);
        assert_eq!(EFFECT_RUNTIME_GET_CURRENT_PRESET_PATH_VTABLE_INDEX, 62);
        assert_eq!(EFFECT_RUNTIME_SET_CURRENT_PRESET_PATH_VTABLE_INDEX, 63);
        assert_eq!(EFFECT_RUNTIME_OPEN_OVERLAY_VTABLE_INDEX, 74);
        assert_eq!(RESHADE_RELOADED_EFFECTS, 78);
        assert_eq!(RESHADE_SET_CURRENT_PRESET_PATH, 84);
        assert_eq!(RESHADE_OPEN_OVERLAY, 86);
    }

    #[test]
    fn runtime_loader_only_accepts_the_extensions_rishade_runtime_path() {
        assert!(is_allowed_runtime_path(std::path::Path::new(
            r"C:\Voidstrap\Extensions\RiShade\dxgi.dll"
        )));
        assert!(!is_allowed_runtime_path(std::path::Path::new(
            r"C:\Roblox\Versions\version-123\dxgi.dll"
        )));
        assert!(!is_allowed_runtime_path(std::path::Path::new("dxgi.dll")));
    }

    #[test]
    fn runtime_overlay_open_forwards_keyboard_source_and_requested_state() {
        use std::sync::atomic::{AtomicBool, AtomicU32};

        static OPEN_STATE: AtomicBool = AtomicBool::new(false);
        static INPUT_SOURCE: AtomicU32 = AtomicU32::new(0);
        unsafe extern "system" fn fake_open_overlay(
            _runtime: *mut c_void,
            open: bool,
            source: u32,
        ) -> bool {
            OPEN_STATE.store(open, Ordering::Release);
            INPUT_SOURCE.store(source, Ordering::Release);
            true
        }

        #[repr(C)]
        struct FakeRuntime {
            vtable: *const *const c_void,
        }

        let mut vtable = [std::ptr::null(); EFFECT_RUNTIME_OPEN_OVERLAY_VTABLE_INDEX + 1];
        vtable[EFFECT_RUNTIME_OPEN_OVERLAY_VTABLE_INDEX] = fake_open_overlay as *const c_void;
        let runtime = FakeRuntime {
            vtable: vtable.as_ptr(),
        };

        assert!(unsafe {
            RiShadeRuntimeOpenOverlay((&runtime as *const FakeRuntime).cast_mut().cast(), true)
        });
        assert!(OPEN_STATE.load(Ordering::Acquire));
        assert_eq!(INPUT_SOURCE.load(Ordering::Acquire), 2);
    }

    #[test]
    fn depth_binding_forwards_srv_and_can_clear_it() {
        use std::sync::Mutex;

        static BINDINGS: Mutex<Vec<(String, u64, u64)>> = Mutex::new(Vec::new());
        unsafe extern "system" fn fake_update(
            _runtime: *mut c_void,
            semantic: *const c_char,
            srv: u64,
            srv_srgb: u64,
        ) {
            let semantic = unsafe { CStr::from_ptr(semantic) }
                .to_string_lossy()
                .into_owned();
            BINDINGS.lock().unwrap().push((semantic, srv, srv_srgb));
        }

        #[repr(C)]
        struct FakeRuntime {
            vtable: *const *const c_void,
        }

        let mut vtable =
            [std::ptr::null(); EFFECT_RUNTIME_UPDATE_TEXTURE_BINDINGS_VTABLE_INDEX + 1];
        vtable[EFFECT_RUNTIME_UPDATE_TEXTURE_BINDINGS_VTABLE_INDEX] = fake_update as *const c_void;
        let runtime = FakeRuntime {
            vtable: vtable.as_ptr(),
        };

        assert!(unsafe {
            RiShadeRuntimeBindDepthTexture(
                (&runtime as *const FakeRuntime).cast_mut().cast(),
                0x1234usize as *mut c_void,
            )
        });
        assert!(unsafe {
            RiShadeRuntimeBindDepthTexture(
                (&runtime as *const FakeRuntime).cast_mut().cast(),
                std::ptr::null_mut(),
            )
        });
        assert_eq!(
            *BINDINGS.lock().unwrap(),
            [
                ("DEPTH".to_owned(), 0x1234, 0x1234),
                ("DEPTH".to_owned(), 0, 0)
            ]
        );
        BINDINGS.lock().unwrap().clear();
    }

    #[test]
    fn runtime_destroy_only_clears_the_matching_instance() {
        ACTIVE_RUNTIME.store(0x1234, Ordering::Release);
        unsafe { on_runtime_destroyed(0x5678usize as *mut c_void) };
        assert_eq!(ACTIVE_RUNTIME.load(Ordering::Acquire), 0x1234);
        unsafe { on_runtime_destroyed(0x1234usize as *mut c_void) };
        assert_eq!(ACTIVE_RUNTIME.load(Ordering::Acquire), 0);
    }

    #[test]
    fn render_effects_rejects_null_target_or_missing_runtime() {
        assert!(!unsafe {
            RiShadeRenderEffects(
                std::ptr::null_mut(),
                std::ptr::dangling_mut(),
                std::ptr::null_mut(),
            )
        });
        assert!(!unsafe {
            RiShadeRenderEffects(
                std::ptr::dangling_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            )
        });
    }

    #[test]
    fn render_then_present_keeps_effect_state_untouched() {
        static CALL_SEQUENCE: Mutex<Vec<&'static str>> = Mutex::new(Vec::new());

        unsafe extern "system" fn fake_update_and_present(_runtime: *mut c_void) {
            CALL_SEQUENCE.lock().unwrap().push("present");
        }

        unsafe extern "system" fn fake_get_queue(runtime: *mut c_void) -> *mut c_void {
            unsafe { (*runtime.cast::<FakeRuntime>()).queue }
        }

        unsafe extern "system" fn fake_get_command_list(_queue: *mut c_void) -> *mut c_void {
            0x3000usize as *mut c_void
        }

        unsafe extern "system" fn fake_render_effects(
            _runtime: *mut c_void,
            cmd_list: *mut c_void,
            rtv: u64,
            rtv_srgb: u64,
        ) {
            assert_eq!(cmd_list as usize, 0x3000);
            assert_eq!(rtv, 0x4000);
            assert_eq!(rtv_srgb, 0x5000);
            CALL_SEQUENCE.lock().unwrap().push("render");
        }

        unsafe extern "system" fn fake_update_texture_bindings(
            _runtime: *mut c_void,
            _semantic: *const c_char,
            _srv: u64,
            _srv_srgb: u64,
        ) {
            CALL_SEQUENCE.lock().unwrap().push("bind");
        }

        #[repr(C)]
        struct FakeQueue {
            vtable: *const *const c_void,
        }

        #[repr(C)]
        struct FakeRuntime {
            vtable: *const *const c_void,
            queue: *mut c_void,
        }

        let mut queue_vtable = [std::ptr::null(); 8];
        queue_vtable[COMMAND_QUEUE_GET_IMMEDIATE_COMMAND_LIST_VTABLE_INDEX] =
            fake_get_command_list as *const c_void;
        let fake_queue = FakeQueue {
            vtable: queue_vtable.as_ptr(),
        };

        let mut runtime_vtable = [std::ptr::null(); EFFECT_RUNTIME_OPEN_OVERLAY_VTABLE_INDEX + 1];
        runtime_vtable[EFFECT_RUNTIME_GET_COMMAND_QUEUE_VTABLE_INDEX] =
            fake_get_queue as *const c_void;
        runtime_vtable[EFFECT_RUNTIME_RENDER_EFFECTS_VTABLE_INDEX] =
            fake_render_effects as *const c_void;
        runtime_vtable[EFFECT_RUNTIME_UPDATE_TEXTURE_BINDINGS_VTABLE_INDEX] =
            fake_update_texture_bindings as *const c_void;

        let fake_runtime = FakeRuntime {
            vtable: runtime_vtable.as_ptr(),
            queue: (&fake_queue as *const FakeQueue).cast_mut().cast(),
        };
        let runtime = (&fake_runtime as *const FakeRuntime).cast_mut().cast();

        UPDATE_AND_PRESENT.store(
            fake_update_and_present as *const () as usize,
            Ordering::Release,
        );

        assert!(unsafe {
            RiShadeRenderEffects(
                runtime,
                0x4000usize as *mut c_void,
                0x5000usize as *mut c_void,
            )
        });
        assert!(unsafe { RiShadeRuntimePresent(runtime) });
        assert_eq!(*CALL_SEQUENCE.lock().unwrap(), ["render", "present"]);
        UPDATE_AND_PRESENT.store(0, Ordering::Release);
    }

    #[test]
    fn runtime_create_rejects_null_arguments() {
        assert!(
            unsafe {
                RiShadeRuntimeCreate(
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null(),
                )
            }
            .is_null()
        );
    }

    #[test]
    fn overlay_state_tracks_open_state() {
        let fake_runtime = 0x5555usize as *mut c_void;
        ACTIVE_RUNTIME.store(fake_runtime as usize, Ordering::Release);
        OVERLAY_OPEN.store(0, Ordering::Release);

        assert!(!unsafe { on_overlay_state_changed(fake_runtime, true, 0) });
        assert_eq!(OVERLAY_OPEN.load(Ordering::Acquire), 0);

        OVERLAY_OPEN.store(1, Ordering::Release);
        assert!(!unsafe { on_overlay_state_changed(fake_runtime, false, 0) });
        assert_eq!(OVERLAY_OPEN.load(Ordering::Acquire), 0);

        ACTIVE_RUNTIME.store(0, Ordering::Release);
    }
}
