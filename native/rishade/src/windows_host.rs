use std::path::{Path, PathBuf};
use windows::Win32::Foundation::{BOOL, CloseHandle, HWND, LPARAM, RECT, TRUE};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::System::Threading::{
    GetCurrentProcessId, OpenProcess, PROCESS_NAME_FORMAT, PROCESS_QUERY_LIMITED_INFORMATION,
    QueryFullProcessImageNameW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GW_OWNER, GWL_EXSTYLE, GetClientRect, GetWindow, GetWindowLongPtrW,
    GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsWindow, IsWindowVisible,
    WS_EX_TOOLWINDOW,
};
use windows::core::PWSTR;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AutoGame {
    pub executable: PathBuf,
    pub name: String,
    pub enabled: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GameWindow {
    pub hwnd: isize,
    pub title: String,
    pub process_id: u32,
}

impl AutoGame {
    pub fn matches_executable(&self, executable: &Path) -> bool {
        let full_path = self
            .executable
            .parent()
            .is_some_and(|parent| !parent.as_os_str().is_empty());
        let expected = if full_path {
            self.executable.to_string_lossy()
        } else {
            self.executable
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
        };
        let actual = if full_path {
            executable.to_string_lossy()
        } else {
            executable.file_name().unwrap_or_default().to_string_lossy()
        };
        expected.eq_ignore_ascii_case(&actual)
    }
}

pub fn list_game_windows() -> Vec<GameWindow> {
    let mut windows = Vec::<GameWindow>::new();
    unsafe {
        let _ = EnumWindows(
            Some(enumerate_window),
            LPARAM((&mut windows as *mut Vec<GameWindow>) as isize),
        );
    }
    windows.sort_by_key(|window| window.title.to_lowercase());
    windows
}

pub fn match_game_window(hwnd: isize, games: &[AutoGame]) -> Option<GameWindow> {
    if !games.iter().any(|game| game.enabled) {
        return None;
    }
    let window = HWND(hwnd as *mut _);
    if !is_game_window(window) {
        return None;
    }
    let process_id = process_id(window)?;
    let executable = process_executable(process_id).ok()?;
    let game = games
        .iter()
        .find(|game| game.enabled && game.matches_executable(&executable))?;
    let mut window = window_info(window, process_id)?;
    window.title = game.name.clone();
    Some(window)
}

pub fn find_game_target(
    selection: Option<&GameWindow>,
    games: &[AutoGame],
    preferred_window: isize,
) -> Option<GameWindow> {
    if let Some(selection) = selection {
        let hwnd = HWND(selection.hwnd as *mut _);
        return game_window_exists(selection)
            .then(|| window_info(hwnd, selection.process_id))
            .flatten();
    }
    let mut first = None;
    for window in list_game_windows() {
        let Some(target) = match_game_window(window.hwnd, games) else {
            continue;
        };
        if target.hwnd == preferred_window {
            return Some(target);
        }
        first.get_or_insert(target);
    }
    first
}

fn game_window_exists(game: &GameWindow) -> bool {
    let hwnd = HWND(game.hwnd as *mut _);
    (unsafe { IsWindow(hwnd).as_bool() }) && process_id(hwnd) == Some(game.process_id)
}

unsafe extern "system" fn enumerate_window(hwnd: HWND, data: LPARAM) -> BOOL {
    if !is_game_window(hwnd) {
        return TRUE;
    }
    if let Some(window) = process_id(hwnd).and_then(|pid| window_info(hwnd, pid)) {
        unsafe { &mut *(data.0 as *mut Vec<GameWindow>) }.push(window);
    }
    TRUE
}

fn is_game_window(hwnd: HWND) -> bool {
    if hwnd.is_invalid()
        || !unsafe { IsWindowVisible(hwnd).as_bool() }
        || process_id(hwnd) == Some(unsafe { GetCurrentProcessId() })
    {
        return false;
    }
    if unsafe { GetWindow(hwnd, GW_OWNER) }.is_ok_and(|owner| !owner.is_invalid())
        || unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } & WS_EX_TOOLWINDOW.0 as isize != 0
    {
        return false;
    }
    let mut cloaked = 0u32;
    if unsafe {
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_CLOAKED,
            (&mut cloaked as *mut u32).cast(),
            std::mem::size_of::<u32>() as u32,
        )
    }
    .is_ok()
        && cloaked != 0
    {
        return false;
    }
    let mut client = RECT::default();
    if unsafe { GetClientRect(hwnd, &mut client) }.is_err()
        || client.right <= 0
        || client.bottom <= 0
    {
        return false;
    }
    title(hwnd).is_some_and(|title| !title.is_empty())
}

fn process_id(hwnd: HWND) -> Option<u32> {
    let mut process_id = 0;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut process_id)) };
    (process_id != 0).then_some(process_id)
}

fn window_info(hwnd: HWND, process_id: u32) -> Option<GameWindow> {
    Some(GameWindow {
        hwnd: hwnd.0 as isize,
        title: title(hwnd)?,
        process_id,
    })
}

fn title(hwnd: HWND) -> Option<String> {
    let length = unsafe { GetWindowTextLengthW(hwnd) };
    if length <= 0 {
        return None;
    }
    let mut buffer = vec![0u16; length as usize + 1];
    let written = unsafe { GetWindowTextW(hwnd, &mut buffer) };
    if written <= 0 {
        return None;
    }
    String::from_utf16(&buffer[..written as usize]).ok()
}

fn process_executable(process_id: u32) -> windows::core::Result<PathBuf> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process_id)? };
    let mut buffer = vec![0u16; 32768];
    let mut length = buffer.len() as u32;
    let result = unsafe {
        QueryFullProcessImageNameW(
            process,
            PROCESS_NAME_FORMAT(0),
            PWSTR(buffer.as_mut_ptr()),
            &mut length,
        )
    };
    unsafe {
        let _ = CloseHandle(process);
    }
    result?;
    buffer.truncate(length as usize);
    Ok(PathBuf::from(String::from_utf16_lossy(&buffer)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn filename_entries_match_across_directories_case_insensitively() {
        let game = AutoGame {
            executable: PathBuf::from("RobloxPlayerBeta.exe"),
            name: "Roblox".into(),
            enabled: true,
        };
        assert!(game.matches_executable(Path::new(r"C:\Games\ROBLOXPLAYERBETA.EXE")));
        assert!(!game.matches_executable(Path::new(r"C:\Games\Other.exe")));
    }

    #[test]
    fn full_path_entries_require_the_same_path() {
        let game = AutoGame {
            executable: PathBuf::from(r"C:\Games\RobloxPlayerBeta.exe"),
            name: "Roblox".into(),
            enabled: true,
        };
        assert!(game.matches_executable(Path::new(r"c:\games\robloxplayerbeta.EXE")));
        assert!(!game.matches_executable(Path::new(r"D:\Games\RobloxPlayerBeta.exe")));
    }
}
