#![windows_subsystem = "windows"]

mod strings;

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

fn main() -> ExitCode {
    let launcher = match env::current_exe() {
        Ok(path) => path,
        Err(_) => return fail(strings::unpack_failed()),
    };
    if !runtime_installed() && !install_runtime() {
        return ExitCode::from(1);
    }
    let app = match unpack() {
        Some(path) => path,
        None => return fail(strings::unpack_failed()),
    };
    let mut command = Command::new(&app);
    let arguments = raw_arguments();
    if !arguments.is_empty() {
        command.raw_arg(arguments);
    }
    command.env("VOIDSTRAP_LAUNCHER", &launcher);
    match command.spawn() {
        Ok(_) => ExitCode::SUCCESS,
        Err(_) => fail(strings::unpack_failed()),
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

fn runtime_installed() -> bool {
    dotnet_roots().iter().any(|root| {
        let shared = root.join("shared");
        has_major(&shared.join("Microsoft.WindowsDesktop.App")) && has_major(&shared.join("Microsoft.NETCore.App"))
    })
}

fn has_major(directory: &Path) -> bool {
    let prefix = format!("{}.", DOTNET_MAJOR);
    fs::read_dir(directory)
        .map(|entries| {
            entries.filter_map(Result::ok).any(|entry| {
                entry.file_name().to_string_lossy().starts_with(&prefix) && entry.path().is_dir()
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

fn install_runtime() -> bool {
    if show(&strings::ask(), MB_YESNO | MB_ICONQUESTION) != IDYES {
        return false;
    }
    let package = format!("Microsoft.DotNet.DesktopRuntime.{}", DOTNET_MAJOR);
    let installed = Command::new("winget")
        .args(["install", "--id", &package, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements"])
        .status()
        .is_ok();
    if installed && runtime_installed() {
        return true;
    }
    show(&strings::failed(), MB_ICONINFORMATION);
    let verb = wide(OsStr::new("open"));
    let page = wide(OsStr::new(&format!("https://dotnet.microsoft.com/download/dotnet/{}.0", DOTNET_MAJOR)));
    unsafe {
        ShellExecuteW(null_mut(), verb.as_ptr(), page.as_ptr(), std::ptr::null(), std::ptr::null(), SW_SHOWNORMAL);
    }
    false
}

fn payload() -> Option<&'static [u8]> {
    unsafe {
        let module = GetModuleHandleW(std::ptr::null());
        let resource = FindResourceW(module, PAYLOAD_ID as usize as *const u16, RT_RCDATA as usize as *const u16);
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

fn unpack() -> Option<PathBuf> {
    let bytes = payload()?;
    let base = Path::new(&env::var_os("LOCALAPPDATA")?).join("Voidstrap").join("Bin");
    let directory = base.join(PAYLOAD_DIR);
    let app = directory.join("Voidstrap.exe");
    if !fs::metadata(&app).map(|meta| meta.len() == bytes.len() as u64).unwrap_or(false) {
        fs::create_dir_all(&directory).ok()?;
        let temporary = directory.join(format!("Voidstrap.{}.tmp", std::process::id()));
        if fs::write(&temporary, bytes).is_err() || fs::rename(&temporary, &app).is_err() {
            let _ = fs::remove_file(&temporary);
            if !fs::metadata(&app).map(|meta| meta.len() == bytes.len() as u64).unwrap_or(false) {
                return None;
            }
        }
    }
    remove_old_versions(&base);
    Some(app)
}

fn remove_old_versions(base: &Path) {
    if let Ok(entries) = fs::read_dir(base) {
        for entry in entries.filter_map(Result::ok) {
            if entry.file_name() != OsStr::new(PAYLOAD_DIR) && entry.path().is_dir() {
                let _ = fs::remove_dir_all(entry.path());
            }
        }
    }
}
