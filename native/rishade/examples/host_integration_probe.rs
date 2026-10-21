#[cfg(windows)]
use std::process::{Command, Stdio};
#[cfg(windows)]
use std::thread;
#[cfg(windows)]
use std::time::{Duration, Instant};
#[cfg(windows)]
use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND};
#[cfg(windows)]
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
#[cfg(windows)]
use windows::Win32::System::Threading::{CreateEventW, SetEvent};
#[cfg(windows)]
use windows::Win32::UI::WindowsAndMessaging::{
    CW_USEDEFAULT, CreateWindowExW, DestroyWindow, DispatchMessageW, MSG, PM_REMOVE, PeekMessageW,
    TranslateMessage, WS_OVERLAPPEDWINDOW, WS_VISIBLE,
};
#[cfg(windows)]
use windows::core::{PCWSTR, w};

#[cfg(windows)]
struct ProbeWindow(HWND);

#[cfg(windows)]
impl Drop for ProbeWindow {
    fn drop(&mut self) {
        let _ = unsafe { DestroyWindow(self.0) };
    }
}

#[cfg(windows)]
struct StopEvent(HANDLE);

#[cfg(windows)]
impl Drop for StopEvent {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
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
fn main() -> Result<(), Box<dyn std::error::Error>> {
    use std::os::windows::process::CommandExt;
    use windows::Win32::System::Threading::CREATE_NO_WINDOW;

    let instance = unsafe { GetModuleHandleW(PCWSTR::null())? };
    let window = unsafe {
        CreateWindowExW(
            Default::default(),
            w!("STATIC"),
            w!("RiShade host integration probe"),
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
    let window = ProbeWindow(window);
    let host = std::env::current_exe()?
        .parent()
        .and_then(std::path::Path::parent)
        .ok_or("probe executable has no parent folder")?
        .join("rishade-host.exe");
    if !host.is_file() {
        return Err(format!("Rust host executable is missing: {}", host.display()).into());
    }
    let stop_event_name = format!("Local\\RiShadeHostProbe.{}", std::process::id());
    let stop_event_name_wide = stop_event_name
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    let stop_event = StopEvent(unsafe {
        CreateEventW(None, true, false, PCWSTR(stop_event_name_wide.as_ptr()))?
    });
    let mut child = Command::new(&host)
        .arg("--window")
        .arg(format!("0x{:X}", window.0.0 as usize))
        .arg("--fps")
        .arg("30")
        .arg("--stop-event")
        .arg(&stop_event_name)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW.0)
        .spawn()?;

    let started = Instant::now();
    let mut stop_signaled_at = None;
    loop {
        pump_messages();
        if stop_signaled_at.is_none() && started.elapsed() >= Duration::from_secs(2) {
            unsafe { SetEvent(stop_event.0)? };
            stop_signaled_at = Some(Instant::now());
        }
        if let Some(status) = child.try_wait()? {
            if !status.success() {
                let output = child.wait_with_output()?;
                return Err(format!(
                    "Rust host exited with status {status}: {}",
                    String::from_utf8_lossy(&output.stderr).trim()
                )
                .into());
            }
            if stop_signaled_at.is_none() {
                return Err("Rust host exited before the parent signaled shutdown".into());
            }
            break;
        }
        if stop_signaled_at.is_some_and(|signaled| signaled.elapsed() >= Duration::from_secs(4)) {
            let _ = child.kill();
            let _ = child.wait();
            return Err("Rust host did not exit after the parent signaled shutdown".into());
        }
        thread::sleep(Duration::from_millis(10));
    }

    let mut child = Command::new(host)
        .arg("--window")
        .arg(format!("0x{:X}", window.0.0 as usize))
        .arg("--fps")
        .arg("30")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW.0)
        .spawn()?;
    let started = Instant::now();
    let mut closed_at = None;
    loop {
        pump_messages();
        if closed_at.is_none() && started.elapsed() >= Duration::from_secs(2) {
            let _ = unsafe { DestroyWindow(window.0) };
            closed_at = Some(Instant::now());
        }
        if let Some(status) = child.try_wait()? {
            if !status.success() {
                let output = child.wait_with_output()?;
                return Err(format!(
                    "Rust host exited with status {status}: {}",
                    String::from_utf8_lossy(&output.stderr).trim()
                )
                .into());
            }
            if closed_at.is_none() {
                return Err("Rust host exited before the probe window was closed".into());
            }
            println!(
                "Rust host passed capture, presentation, shutdown-event, and target-close checks."
            );
            return Ok(());
        }
        if closed_at.is_some_and(|closed| closed.elapsed() >= Duration::from_secs(4)) {
            let _ = child.kill();
            let _ = child.wait();
            return Err("Rust host did not exit after its target window closed".into());
        }
        thread::sleep(Duration::from_millis(10));
    }
}

#[cfg(not(windows))]
fn main() {
    eprintln!("The Rust host integration probe is available on Windows only.");
}
