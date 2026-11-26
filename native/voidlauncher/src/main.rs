#![windows_subsystem = "windows"]

mod strings;

use std::io::Read;
use std::env;
use std::ffi::{OsStr, OsString};
use std::fs;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};
use std::ptr::null_mut;

use windows_sys::Win32::Foundation::ERROR_SUCCESS;
use windows_sys::Win32::System::Environment::GetCommandLineW;
use windows_sys::Win32::System::LibraryLoader::{FindResourceW, GetModuleHandleW, LoadResource, LockResource, SizeofResource};
use windows_sys::Win32::System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RRF_SUBKEY_WOW6432KEY};
use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, IDYES, MB_ICONERROR, MB_ICONINFORMATION, MB_ICONQUESTION, MB_YESNO, SW_SHOWNORMAL};

const PAYLOAD_ID: u16 = 101;
const RT_RCDATA: u16 = 10;
const PAYLOAD_DIR: &str = env!("VOIDSTRAP_PAYLOAD_DIR");
const DOTNET_MAJOR: &str = env!("VOIDSTRAP_DOTNET_MAJOR");
const PAYLOAD_LEN: &str = env!("VOIDSTRAP_PAYLOAD_LEN");
const APP_VERSION: &str = env!("VOIDSTRAP_APP_VERSION");
const STD_INPUT_HANDLE: u32 = -10i32 as u32;
const STD_OUTPUT_HANDLE: u32 = -11i32 as u32;
const STD_ERROR_HANDLE: u32 = -12i32 as u32;
const ATTACH_PARENT_PROCESS: u32 = -1i32 as u32;
const FILE_TYPE_DISK: u32 = 1;
const FILE_TYPE_PIPE: u32 = 3;

#[link(name = "kernel32")]
extern "system" {
    fn AttachConsole(dwProcessId: u32) -> i32;
    fn GetStdHandle(nStdHandle: u32) -> *mut core::ffi::c_void;
    fn GetFileType(hFile: *mut core::ffi::c_void) -> u32;
}

fn main() -> ExitCode {
    let launcher = match env::current_exe() {
        Ok(path) => path,
        Err(_) => return fail(strings::unpack_failed()),
    };
    let quiet = has_flag("quiet");
    let nolaunch = has_flag("nolaunch");
    let net11_root = runtime_root("11");
    let net11_app = net11_path();
    let preferred = net11_root.as_ref().and_then(|root| preferred_payload(net11_app.as_deref()).map(|path| (path, root.clone())));
    let (app, root) = match preferred {
        Some(choice) => choice,
        None => {
            let root = match runtime_root(DOTNET_MAJOR).or_else(|| install_runtime(quiet)) {
                Some(root) => root,
                None => return ExitCode::from(1),
            };
            match unpack(PAYLOAD_ID, PAYLOAD_DIR, PAYLOAD_LEN) {
                Some(path) => (path, root),
                None => return fail(strings::unpack_failed()),
            }
        }
    };
    let console_mode = is_console_command();
    if console_mode
        && !is_handle_redirected(STD_INPUT_HANDLE)
        && !is_handle_redirected(STD_OUTPUT_HANDLE)
        && !is_handle_redirected(STD_ERROR_HANDLE)
    {
        unsafe {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }
    let mut command = Command::new(&app);
    let arguments = raw_arguments();
    if !arguments.is_empty() {
        command.raw_arg(arguments);
    }
    command.env("VOIDSTRAP_LAUNCHER", &launcher);
    command.env("DOTNET_ROOT_X64", root);
    command.env("VOIDSTRAP_RUNTIME_CHOOSER", "1");
    command.env("VOIDSTRAP_NET11_AVAILABLE", if net11_root.is_some() { "1" } else { "0" });
    match &net11_app {
        Some(path) => command.env("VOIDSTRAP_NET11_PATH", path),
        None => command.env_remove("VOIDSTRAP_NET11_PATH"),
    };
    if console_mode || quiet {
        return match command.status() {
            Ok(status) => ExitCode::from(status.code().unwrap_or(1) as u8),
            Err(_) => fail(strings::unpack_failed()),
        };
    }
    if nolaunch {
        return ExitCode::SUCCESS;
    }
    match command.spawn() {
        Ok(_) => ExitCode::SUCCESS,
        Err(_) => fail(strings::unpack_failed()),
    }
}

fn has_flag(name: &str) -> bool {
    env::args_os()
        .skip(1)
        .any(|arg| arg.to_string_lossy().trim().to_ascii_lowercase() == format!("-{}", name))
}

fn is_console_command() -> bool {
    let Some(first) = env::args_os().nth(1) else {
        return false;
    };
    let text = first.to_string_lossy();
    let lower = text.trim().to_ascii_lowercase();
    if matches!(lower.as_str(), "cli" | "mcp" | "-mcp" | "--mcp" | "/mcp") {
        return true;
    }
    lower.starts_with('+')
}

fn is_handle_redirected(std_handle: u32) -> bool {
    unsafe {
        let handle = GetStdHandle(std_handle);
        if handle.is_null() || handle == (-1isize as *mut core::ffi::c_void) {
            return false;
        }
        let kind = GetFileType(handle);
        kind == FILE_TYPE_DISK || kind == FILE_TYPE_PIPE
    }
}

fn fail(message: String) -> ExitCode {
    show(&message, MB_ICONERROR);
    ExitCode::from(1)
}

fn wide(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(Some(0)).collect()
}

fn show(message: &str, flags: u32) -> i32 {
    let text = wide(OsStr::new(message));
    let title = wide(OsStr::new("Voidstrap"));
    unsafe { MessageBoxW(null_mut(), text.as_ptr(), title.as_ptr(), flags) }
}

fn raw_arguments() -> OsString {
    let line = unsafe {
        let pointer = GetCommandLineW();
        let mut length = 0;
        while *pointer.add(length) != 0 {
            length += 1;
        }
        std::slice::from_raw_parts(pointer, length).to_vec()
    };
    let mut index = 0;
    if line.first() == Some(&(b'"' as u16)) {
        index = 1;
        while index < line.len() && line[index] != b'"' as u16 {
            index += 1;
        }
        index += 1;
    } else {
        while index < line.len() && line[index] != b' ' as u16 && line[index] != b'\t' as u16 {
            index += 1;
        }
    }
    while index < line.len() && (line[index] == b' ' as u16 || line[index] == b'\t' as u16) {
        index += 1;
    }
    OsString::from_wide(&line[index.min(line.len())..])
}

fn runtime_root(major: &str) -> Option<PathBuf> {
    dotnet_roots().into_iter().find(|root| {
        let shared = root.join("shared");
        has_major(&shared.join("Microsoft.WindowsDesktop.App"), major)
            && has_major(&shared.join("Microsoft.NETCore.App"), major)
    })
}

fn has_major(directory: &Path, major: &str) -> bool {
    let prefix = format!("{}.", major);
    fs::read_dir(directory)
        .map(|entries| {
            entries.filter_map(Result::ok).any(|entry| {
                let name = entry.file_name();
                let name = name.to_string_lossy();
                name.starts_with(&prefix) && !name.contains('-') && entry.path().is_dir()
            })
        })
        .unwrap_or(false)
}

fn dotnet_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    for name in ["DOTNET_ROOT_X64", "DOTNET_ROOT"] {
        if let Some(value) = env::var_os(name).filter(|value| !value.is_empty()) {
            roots.push(PathBuf::from(value));
        }
    }
    if let Some(location) = registered_location() {
        roots.push(location);
    }
    if let Some(program_files) = env::var_os("ProgramFiles") {
        roots.push(Path::new(&program_files).join("dotnet"));
    }
    roots
}

fn registered_location() -> Option<PathBuf> {
    let key = wide(OsStr::new(r"SOFTWARE\dotnet\Setup\InstalledVersions\x64"));
    let value = wide(OsStr::new("InstallLocation"));
    let mut buffer = vec![0u16; 1024];
    let mut size = (buffer.len() * 2) as u32;
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            key.as_ptr(),
            value.as_ptr(),
            RRF_RT_REG_SZ | RRF_SUBKEY_WOW6432KEY,
            null_mut(),
            buffer.as_mut_ptr().cast(),
            &mut size,
        )
    };
    if status != ERROR_SUCCESS {
        return None;
    }
    let length = buffer.iter().position(|unit| *unit == 0).unwrap_or(buffer.len());
    (length > 0).then(|| PathBuf::from(OsString::from_wide(&buffer[..length])))
}

fn install_runtime(quiet: bool) -> Option<PathBuf> {
    if !quiet && show(&strings::ask(), MB_YESNO | MB_ICONQUESTION) != IDYES {
        return None;
    }
    let package = format!("Microsoft.DotNet.DesktopRuntime.{}", DOTNET_MAJOR);
    let installed = Command::new("winget")
        .args(["install", "--id", &package, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements"])
        .status()
        .is_ok();
    if installed {
        if let Some(root) = runtime_root(DOTNET_MAJOR) {
            return Some(root);
        }
    }
    if quiet {
        return None;
    }
    show(&strings::failed(), MB_ICONINFORMATION);
    let verb = wide(OsStr::new("open"));
    let page = wide(OsStr::new(&format!("https://dotnet.microsoft.com/download/dotnet/{}.0", DOTNET_MAJOR)));
    unsafe {
        ShellExecuteW(null_mut(), verb.as_ptr(), page.as_ptr(), std::ptr::null(), std::ptr::null(), SW_SHOWNORMAL);
    }
    None
}

fn payload(id: u16) -> Option<&'static [u8]> {
    unsafe {
        let module = GetModuleHandleW(std::ptr::null());
        let resource = FindResourceW(module, id as usize as *const u16, RT_RCDATA as usize as *const u16);
        if resource.is_null() {
            return None;
        }
        let size = SizeofResource(module, resource) as usize;
        let loaded = LoadResource(module, resource);
        if loaded.is_null() || size == 0 {
            return None;
        }
        let data = LockResource(loaded) as *const u8;
        (!data.is_null()).then(|| std::slice::from_raw_parts(data, size))
    }
}

fn net11_directory_name() -> String {
    format!("net11-{}", APP_VERSION)
}

fn net11_path() -> Option<PathBuf> {
    Some(Path::new(&env::var_os("LOCALAPPDATA")?).join("Voidstrap").join("Bin").join(net11_directory_name()).join("Voidstrap.exe"))
}

fn preferred_payload(app: Option<&Path>) -> Option<PathBuf> {
    let app = app?;
    let preference = Path::new(&env::var_os("LOCALAPPDATA")?).join("Voidstrap").join("DotNetRuntime");
    let mut bytes = Vec::with_capacity(3);
    fs::File::open(preference).ok()?.take(3).read_to_end(&mut bytes).ok()?;
    if bytes != b"11" || !app.is_file() {
        return None;
    }
    Some(app.to_path_buf())
}

fn unpack(id: u16, name: &str, length: &str) -> Option<PathBuf> {
    let bytes = payload(id)?;
    let base = Path::new(&env::var_os("LOCALAPPDATA")?).join("Voidstrap").join("Bin");
    let directory = base.join(name);
    let app = directory.join("Voidstrap.exe");
    let expected_len: u64 = length.parse().unwrap_or(0);
    let check_len = if expected_len > 0 { expected_len } else { bytes.len() as u64 };
    if !fs::metadata(&app).map(|meta| meta.len() == check_len).unwrap_or(false) {
        fs::create_dir_all(&directory).ok()?;
        let temporary = directory.join(format!("Voidstrap.{}.tmp", std::process::id()));
        let mut write_bytes = Vec::with_capacity(check_len as usize);
        lzma_rust2::XzReader::new(bytes, false).read_to_end(&mut write_bytes).ok()?;
        if write_bytes.len() as u64 != check_len {
            return None;
        }
        if fs::write(&temporary, &write_bytes).is_err() || fs::rename(&temporary, &app).is_err() {
            let _ = fs::remove_file(&temporary);
            if !fs::metadata(&app).map(|meta| meta.len() == check_len).unwrap_or(false) {
                return None;
            }
        }
    }
    remove_old_versions(&base);
    Some(app)
}

fn remove_old_versions(base: &Path) {
    let net11 = net11_directory_name();
    if let Ok(entries) = fs::read_dir(base) {
        for entry in entries.filter_map(Result::ok) {
            let name = entry.file_name();
            if name != OsStr::new(PAYLOAD_DIR)
                && name != OsStr::new(&net11)
                && entry.path().is_dir()
            {
                let _ = fs::remove_dir_all(entry.path());
            }
        }
    }
}
