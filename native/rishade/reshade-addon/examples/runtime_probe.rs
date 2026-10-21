use std::ffi::{CStr, CString, c_void};
use std::os::windows::ffi::OsStrExt;
use std::path::PathBuf;
use std::ptr;
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use windows::Win32::Foundation::{BOOL, HMODULE};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CPU_ACCESS_READ,
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_SDK_VERSION,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING, D3D11CreateDeviceAndSwapChain,
    ID3D11Device, ID3D11DeviceContext, ID3D11RenderTargetView, ID3D11Resource,
    ID3D11ShaderResourceView, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_MODE_DESC, DXGI_MODE_SCALING_UNSPECIFIED,
    DXGI_MODE_SCANLINE_ORDER_UNSPECIFIED, DXGI_RATIONAL, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_PRESENT, DXGI_SWAP_CHAIN_DESC, DXGI_SWAP_EFFECT_DISCARD, DXGI_USAGE_RENDER_TARGET_OUTPUT,
    IDXGISwapChain,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CW_USEDEFAULT, CreateWindowExW, DestroyWindow, WS_OVERLAPPEDWINDOW, WS_VISIBLE,
};
use windows::core::{Interface, PCWSTR, w};

#[link(name = "kernel32")]
unsafe extern "system" {
    fn LoadLibraryW(path: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
    fn FreeLibrary(module: *mut c_void) -> i32;
}

type RuntimeLoad = unsafe extern "system" fn(*const u16) -> bool;
type RuntimeCreate =
    unsafe extern "system" fn(*mut c_void, *mut c_void, *mut c_void, *const i8) -> *mut c_void;
type RuntimeDestroy = unsafe extern "system" fn(*mut c_void);
type RuntimeOpenOverlay = unsafe extern "system" fn(*mut c_void, bool) -> bool;
type RenderEffects = unsafe extern "system" fn(
    *mut c_void,
    *mut c_void,
    *mut c_void,
    *mut c_void,
    *mut c_void,
) -> bool;
type RuntimeStatus = unsafe extern "system" fn() -> bool;
type RuntimeSelectPreset = unsafe extern "system" fn(*mut c_void, *const i8) -> bool;
type CopyPresetPath = unsafe extern "system" fn(*mut u8, usize) -> usize;

struct TechniqueContext {
    set_state: unsafe extern "system" fn(*mut c_void, u64, bool),
    count: usize,
}

unsafe extern "system" fn enable_technique(
    runtime: *mut c_void,
    technique: u64,
    user_data: *mut c_void,
) {
    let context = unsafe { &mut *user_data.cast::<TechniqueContext>() };
    unsafe { (context.set_state)(runtime, technique, true) };
    context.count += 1;
}

struct TempDirectory(PathBuf);

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = std::env::args_os().skip(1);
    let extension_dir = PathBuf::from(
        args.next()
            .ok_or("pass the isolated RiShade extension directory containing dxgi.dll")?,
    );
    let addon_dll = PathBuf::from(args.next().ok_or("pass the bundled Rust add-on DLL")?);
    let runtime_path = extension_dir.join("dxgi.dll");
    if !runtime_path.is_file() || !addon_dll.is_file() {
        return Err("the runtime and Rust add-on must exist".into());
    }
    let probe_root = std::env::temp_dir().join(format!(
        "Voidstrap-RiShade-Probe-{}-{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos()
    ));
    std::fs::create_dir(&probe_root)?;
    let _probe_cleanup = TempDirectory(probe_root.clone());
    let probe_extension_dir = probe_root.join("Extensions").join("RiShade");
    std::fs::create_dir_all(&probe_extension_dir)?;
    let shader_dir = probe_extension_dir.join("reshade-shaders").join("Shaders");
    std::fs::create_dir_all(&shader_dir)?;
    std::fs::write(
        shader_dir.join("RiShadeProbe.fx"),
        "uniform float probe_color = 1.0;\nfloat4 ProbeVS(uint id : SV_VertexID, out float2 uv : TEXCOORD0) : SV_Position { uv = float2((id << 1) & 2, id & 2); return float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0); }\nfloat4 ProbePS(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return float4(probe_color, 0.0, 1.0 - probe_color, 1.0); }\ntechnique RiShadeProbe { pass { VertexShader = ProbeVS; PixelShader = ProbePS; } }\n",
    )?;
    let preset_path = probe_extension_dir.join("ReShadePreset.ini");
    std::fs::write(
        &preset_path,
        "Techniques=RiShadeProbe@RiShadeProbe.fx\nTechniqueSorting=RiShadeProbe@RiShadeProbe.fx\n\n[RiShadeProbe.fx]\nprobe_color=1.0\n",
    )?;
    let neutral_preset_path = probe_extension_dir.join("NeutralPreset.ini");
    std::fs::write(
        &neutral_preset_path,
        "Techniques=RiShadeProbe@RiShadeProbe.fx\nTechniqueSorting=RiShadeProbe@RiShadeProbe.fx\n\n[RiShadeProbe.fx]\nprobe_color=0.0\n",
    )?;
    let config_path = probe_extension_dir.join("ReShade.ini");
    std::fs::write(
        &config_path,
        format!(
            "[GENERAL]\nEffectSearchPaths={}\nTextureSearchPaths={}\nPresetPath={}\nStartupPresetPath={}\nNoDebugInfo=0\nNoReloadOnInit=0\n",
            shader_dir.display(),
            probe_extension_dir
                .join("reshade-shaders")
                .join("Textures")
                .display(),
            preset_path.display(),
            preset_path.display()
        ),
    )?;
    let addon_wide = addon_dll
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let addon_module = unsafe { LoadLibraryW(addon_wide.as_ptr()) };
    if addon_module.is_null() {
        return Err("could not load the isolated ReShade add-on for lifecycle inspection".into());
    }
    let runtime_load_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeRuntimeLoad".as_ptr().cast()) };
    let runtime_create_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeRuntimeCreate".as_ptr().cast()) };
    let runtime_destroy_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeRuntimeDestroy".as_ptr().cast()) };
    let overlay_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeRuntimeOpenOverlay".as_ptr().cast()) };
    let render_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeRenderEffects".as_ptr().cast()) };
    let initialized_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeAddonIsInitialized".as_ptr().cast()) };
    let active_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeAddonRuntimeIsActive".as_ptr().cast()) };
    let compiling_address = unsafe {
        GetProcAddress(
            addon_module,
            c"RiShadeAddonIsCompilingEffects".as_ptr().cast(),
        )
    };
    let loading_address = unsafe {
        GetProcAddress(
            addon_module,
            c"RiShadeAddonIsLoadingEffects".as_ptr().cast(),
        )
    };
    let overlay_open_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeAddonIsOverlayOpen".as_ptr().cast()) };
    let select_preset_address =
        unsafe { GetProcAddress(addon_module, c"RiShadeRuntimeSelectPreset".as_ptr().cast()) };
    let copy_preset_path_address = unsafe {
        GetProcAddress(
            addon_module,
            c"RiShadeAddonCopyCurrentPresetPath".as_ptr().cast(),
        )
    };
    if [
        runtime_load_address,
        runtime_create_address,
        runtime_destroy_address,
        overlay_address,
        render_address,
        initialized_address,
        active_address,
        compiling_address,
        loading_address,
        overlay_open_address,
        select_preset_address,
        copy_preset_path_address,
    ]
    .contains(&ptr::null_mut())
    {
        unsafe { FreeLibrary(addon_module) };
        return Err("required exports are missing from the Rust add-on".into());
    }
    let runtime_load: RuntimeLoad = unsafe { std::mem::transmute(runtime_load_address) };
    let runtime_create: RuntimeCreate = unsafe { std::mem::transmute(runtime_create_address) };
    let runtime_destroy: RuntimeDestroy = unsafe { std::mem::transmute(runtime_destroy_address) };
    let runtime_open_overlay: RuntimeOpenOverlay = unsafe { std::mem::transmute(overlay_address) };
    let render_effects: RenderEffects = unsafe { std::mem::transmute(render_address) };
    let addon_initialized: RuntimeStatus = unsafe { std::mem::transmute(initialized_address) };
    let addon_runtime_active: RuntimeStatus = unsafe { std::mem::transmute(active_address) };
    let addon_compiling: RuntimeStatus = unsafe { std::mem::transmute(compiling_address) };
    let addon_loading: RuntimeStatus = unsafe { std::mem::transmute(loading_address) };
    let addon_overlay_open: RuntimeStatus = unsafe { std::mem::transmute(overlay_open_address) };
    let runtime_select_preset: RuntimeSelectPreset =
        unsafe { std::mem::transmute(select_preset_address) };
    let copy_preset_path: CopyPresetPath = unsafe { std::mem::transmute(copy_preset_path_address) };

    let instance = unsafe { GetModuleHandleW(PCWSTR::null())? };
    let window = unsafe {
        CreateWindowExW(
            Default::default(),
            w!("STATIC"),
            w!("RiShade harmless runtime probe"),
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            320,
            240,
            None,
            None,
            instance,
            None,
        )?
    };

    let swap_desc = DXGI_SWAP_CHAIN_DESC {
        BufferDesc: DXGI_MODE_DESC {
            Width: 320,
            Height: 240,
            RefreshRate: DXGI_RATIONAL {
                Numerator: 60,
                Denominator: 1,
            },
            Format: DXGI_FORMAT_R8G8B8A8_UNORM,
            ScanlineOrdering: DXGI_MODE_SCANLINE_ORDER_UNSPECIFIED,
            Scaling: DXGI_MODE_SCALING_UNSPECIFIED,
        },
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
        BufferCount: 2,
        OutputWindow: window,
        Windowed: BOOL(1),
        SwapEffect: DXGI_SWAP_EFFECT_DISCARD,
        Flags: 0,
    };

    let mut swapchain = None;
    let mut device = None;
    let mut context = None;
    unsafe {
        D3D11CreateDeviceAndSwapChain(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&swap_desc),
            Some(&mut swapchain),
            Some(&mut device),
            None,
            Some(&mut context),
        )?;
    }
    let swapchain: IDXGISwapChain = swapchain.ok_or("D3D11 did not create a swap chain")?;
    let device: ID3D11Device = device.ok_or("D3D11 did not create a device")?;
    let context: ID3D11DeviceContext =
        context.ok_or("D3D11 did not create an immediate context")?;

    let runtime_wide = runtime_path
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    if !unsafe { runtime_load(runtime_wide.as_ptr()) } {
        unsafe { DestroyWindow(window) }?;
        return Err("ReShade runtime load failed".into());
    }

    let runtime_config = CString::new(config_path.to_string_lossy().as_bytes())?;
    let runtime = unsafe {
        runtime_create(
            device.as_raw(),
            context.as_raw(),
            swapchain.as_raw(),
            runtime_config.as_ptr().cast::<i8>(),
        )
    };
    if runtime.is_null() {
        unsafe { DestroyWindow(window) }?;
        return Err("ReShade runtime creation failed".into());
    }
    if !unsafe { addon_initialized() } || !unsafe { addon_runtime_active() } {
        unsafe { runtime_destroy(runtime) };
        unsafe { DestroyWindow(window) }?;
        return Err(
            "ReShade did not initialize the in-process add-on or deliver runtime lifecycle events"
                .into(),
        );
    }
    if !unsafe { addon_compiling() } || !unsafe { addon_loading() } {
        unsafe { runtime_destroy(runtime) };
        unsafe { DestroyWindow(window) }?;
        return Err("Rust add-on did not report the runtime's initial effect-loading state".into());
    }
    println!("Add-on initialization and runtime callbacks are active");
    let runtime_vtable = unsafe { *(runtime.cast::<*const *const c_void>()) };
    let neutral_preset_path_c = CString::new(neutral_preset_path.to_string_lossy().as_bytes())?;
    println!("Using the configured ReShade probe preset");
    let texture_desc = D3D11_TEXTURE2D_DESC {
        Width: 320,
        Height: 240,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_R8G8B8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_RENDER_TARGET.0) as u32,
        CPUAccessFlags: 0,
        MiscFlags: 0,
    };
    let mut input_texture: Option<ID3D11Texture2D> = None;
    let mut target_texture: Option<ID3D11Texture2D> = None;
    unsafe {
        device.CreateTexture2D(&texture_desc, None, Some(&mut input_texture))?;
        device.CreateTexture2D(&texture_desc, None, Some(&mut target_texture))?;
    }
    let input_texture = input_texture.ok_or("D3D11 input texture was not created")?;
    let target_texture = target_texture.ok_or("D3D11 target texture was not created")?;
    let mut staging_desc = texture_desc;
    staging_desc.Usage = D3D11_USAGE_STAGING;
    staging_desc.BindFlags = 0;
    staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    let mut staging_texture: Option<ID3D11Texture2D> = None;
    unsafe { device.CreateTexture2D(&staging_desc, None, Some(&mut staging_texture))? };
    let staging_texture = staging_texture.ok_or("D3D11 staging texture was not created")?;
    let mut input_srv: Option<ID3D11ShaderResourceView> = None;
    let mut input_rtv: Option<ID3D11RenderTargetView> = None;
    let mut target_rtv: Option<ID3D11RenderTargetView> = None;
    unsafe {
        device.CreateShaderResourceView(&input_texture, None, Some(&mut input_srv))?;
        device.CreateRenderTargetView(&input_texture, None, Some(&mut input_rtv))?;
        device.CreateRenderTargetView(&target_texture, None, Some(&mut target_rtv))?;
    }
    let input_srv = input_srv.ok_or("D3D11 input view was not created")?;
    let input_rtv = input_rtv.ok_or("D3D11 input target view was not created")?;
    let target_rtv = target_rtv.ok_or("D3D11 output view was not created")?;
    unsafe {
        context.ClearRenderTargetView(&input_rtv, &[0.25, 0.5, 0.75, 1.0]);
        context.ClearRenderTargetView(&target_rtv, &[0.0, 0.0, 0.0, 1.0]);
        for _ in 0..4 {
            println!("Rendering harmless frame");
            if !render_effects(
                runtime,
                input_srv.as_raw(),
                ptr::null_mut(),
                target_rtv.as_raw(),
                ptr::null_mut(),
            ) {
                runtime_destroy(runtime);
                DestroyWindow(window)?;
                return Err("ReShade render_effects failed for the harmless D3D11 frame".into());
            }
            println!("ReShade rendered a harmless frame");
            swapchain.Present(0, DXGI_PRESENT(0)).ok()?;
        }
        let set_technique_address = *runtime_vtable.add(56);
        let set_technique: unsafe extern "system" fn(*mut c_void, u64, bool) =
            std::mem::transmute(set_technique_address);
        let mut technique_context = TechniqueContext {
            set_state: set_technique,
            count: 0,
        };
        let enumerate_address = *runtime_vtable.add(47);
        let enumerate: unsafe extern "system" fn(
            *mut c_void,
            *const i8,
            Option<unsafe extern "system" fn(*mut c_void, u64, *mut c_void)>,
            *mut c_void,
        ) = std::mem::transmute(enumerate_address);
        for _ in 0..100 {
            if !render_effects(
                runtime,
                input_srv.as_raw(),
                ptr::null_mut(),
                target_rtv.as_raw(),
                ptr::null_mut(),
            ) {
                runtime_destroy(runtime);
                DestroyWindow(window)?;
                return Err(
                    "ReShade render_effects failed while waiting for effect loading".into(),
                );
            }
            swapchain.Present(0, DXGI_PRESENT(0)).ok()?;
            enumerate(
                runtime,
                c"RiShadeProbe.fx".as_ptr(),
                Some(enable_technique),
                (&mut technique_context as *mut TechniqueContext).cast(),
            );
            if technique_context.count != 0 {
                break;
            }
            thread::sleep(std::time::Duration::from_millis(50));
        }
        if technique_context.count == 0 {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err("ReShade compiled no techniques from the configured probe effect".into());
        }
        println!(
            "Enabled {} compiled probe technique(s)",
            technique_context.count
        );
        if addon_compiling() || addon_loading() {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err(
                "Rust add-on did not clear its effect-loading state after technique load".into(),
            );
        }
        let staging_resource: ID3D11Resource = staging_texture.cast()?;
        let target_resource: ID3D11Resource = target_texture.cast()?;
        let mut pixel = [0; 4];
        for _ in 0..8 {
            if !render_effects(
                runtime,
                input_srv.as_raw(),
                ptr::null_mut(),
                target_rtv.as_raw(),
                ptr::null_mut(),
            ) {
                runtime_destroy(runtime);
                DestroyWindow(window)?;
                return Err(
                    "ReShade render_effects failed after enabling the probe technique".into(),
                );
            }
            swapchain.Present(0, DXGI_PRESENT(0)).ok()?;
            context.CopyResource(&staging_resource, &target_resource);
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            context.Map(&staging_resource, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
            let output = std::slice::from_raw_parts(mapped.pData.cast::<u8>(), 4);
            pixel = [output[0], output[1], output[2], output[3]];
            context.Unmap(&staging_resource, 0);
            if pixel
                .iter()
                .zip([255, 0, 0])
                .all(|(output, expected)| output.abs_diff(expected) < 4)
            {
                break;
            }
            thread::sleep(std::time::Duration::from_millis(25));
        }
        if !pixel
            .iter()
            .zip([255, 0, 0])
            .all(|(output, expected)| output.abs_diff(expected) < 4)
        {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err(format!(
                "ReShade probe effect did not render its expected red pixel: {pixel:?}"
            )
            .into());
        }
        println!("The probe effect rendered its configured pixel {pixel:?}");

        if !runtime_select_preset(runtime, neutral_preset_path_c.as_ptr()) {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err("Rust add-on rejected the harmless neutral preset selection".into());
        }
        let mut selected_path = vec![0u8; 32_768];
        let mut neutral_pixel = pixel;
        let mut selected = false;
        let mut consecutive_selected_frames = 0;
        for _ in 0..100 {
            if !render_effects(
                runtime,
                input_srv.as_raw(),
                ptr::null_mut(),
                target_rtv.as_raw(),
                ptr::null_mut(),
            ) {
                runtime_destroy(runtime);
                DestroyWindow(window)?;
                return Err(
                    "ReShade render_effects failed while applying the selected preset".into(),
                );
            }
            swapchain.Present(0, DXGI_PRESENT(0)).ok()?;

            selected_path.fill(0);
            let selected_path_len =
                copy_preset_path(selected_path.as_mut_ptr(), selected_path.len());
            let current_path = if selected_path_len < selected_path.len() {
                CStr::from_bytes_until_nul(&selected_path)
                    .ok()
                    .and_then(|path| path.to_str().ok())
            } else {
                None
            };

            context.CopyResource(&staging_resource, &target_resource);
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            context.Map(&staging_resource, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
            let output = std::slice::from_raw_parts(mapped.pData.cast::<u8>(), 4);
            neutral_pixel = [output[0], output[1], output[2], output[3]];
            context.Unmap(&staging_resource, 0);

            let path_matches = current_path.is_some_and(|path| {
                path.eq_ignore_ascii_case(&neutral_preset_path.to_string_lossy())
            });
            let pixel_is_neutral = neutral_pixel
                .iter()
                .zip([0, 0, 255])
                .all(|(output, expected)| output.abs_diff(expected) < 4);
            if path_matches && pixel_is_neutral {
                consecutive_selected_frames += 1;
                if consecutive_selected_frames >= 5 {
                    selected = true;
                    break;
                }
            } else {
                consecutive_selected_frames = 0;
            }
            thread::sleep(std::time::Duration::from_millis(50));
        }
        if !selected {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err(format!(
                "Rust preset selection did not reach the neutral output (path: {:?}, pixel: {neutral_pixel:?})",
                CStr::from_bytes_until_nul(&selected_path)
                    .ok()
                    .and_then(|path| path.to_str().ok())
            )
            .into());
        }
        println!(
            "Rust preset selection loaded the neutral preset and produced pixel {neutral_pixel:?}"
        );
        println!("Opening ReShade overlay");
        if !runtime_open_overlay(runtime, true) {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err("ReShade overlay open failed".into());
        }
        if !addon_overlay_open() {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err("Rust add-on did not observe ReShade's overlay-open event".into());
        }
        println!("Closing ReShade overlay");
        if !runtime_open_overlay(runtime, false) {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err("ReShade overlay close failed".into());
        }
        if addon_overlay_open() {
            runtime_destroy(runtime);
            DestroyWindow(window)?;
            return Err("Rust add-on did not observe ReShade's overlay-close event".into());
        }
        println!("Destroying ReShade runtime");
        runtime_destroy(runtime);
        println!("Destroyed ReShade runtime");
        DestroyWindow(window)?;
    }

    println!(
        "ReShade add-on loaded, runtime lifecycle events fired, harmless frames rendered, overlay toggled, and runtime destroyed"
    );
    Ok(())
}
