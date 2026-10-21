use std::collections::HashMap;
use std::fmt;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};

const MAX_GAMES: usize = 4096;
static TEMP_FILE_ID: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GameListEntry {
    pub executable: String,
    pub name: String,
    pub enabled: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum GameListError {
    MissingCount,
    InvalidCount,
    TooManyGames,
    InvalidEntry(usize),
    UnsafeValue(usize),
}

impl fmt::Display for GameListError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingCount => formatter.write_str("the saved game list has no game count"),
            Self::InvalidCount => formatter.write_str("the saved game count is invalid"),
            Self::TooManyGames => {
                formatter.write_str("the saved game list exceeds the supported limit")
            }
            Self::InvalidEntry(index) => write!(formatter, "saved game entry {index} is invalid"),
            Self::UnsafeValue(index) => write!(
                formatter,
                "saved game entry {index} contains an invalid value"
            ),
        }
    }
}

impl std::error::Error for GameListError {}

pub fn parse_game_list(text: &str) -> Result<Vec<GameListEntry>, GameListError> {
    let mut sections = HashMap::<String, HashMap<String, String>>::new();
    let mut section = String::new();

    for line in text.trim_start_matches('\u{feff}').lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with(';') || line.starts_with('#') {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') {
            section = line[1..line.len() - 1].trim().to_ascii_lowercase();
            continue;
        }
        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        if section.is_empty() || key.trim().is_empty() {
            continue;
        }
        sections
            .entry(section.clone())
            .or_default()
            .insert(key.trim().to_ascii_lowercase(), value.trim().to_owned());
    }

    let count = sections
        .get("games")
        .and_then(|values| values.get("count"))
        .ok_or(GameListError::MissingCount)?
        .parse::<usize>()
        .map_err(|_| GameListError::InvalidCount)?;
    if count > MAX_GAMES {
        return Err(GameListError::TooManyGames);
    }

    (0..count)
        .map(|index| {
            let values = sections
                .get(&format!("game{index}"))
                .ok_or(GameListError::InvalidEntry(index))?;
            let executable = values
                .get("executable")
                .filter(|value| !value.is_empty())
                .ok_or(GameListError::InvalidEntry(index))?;
            let name = values
                .get("name")
                .filter(|value| !value.is_empty())
                .ok_or(GameListError::InvalidEntry(index))?;
            let enabled = match values.get("enabled").map(String::as_str) {
                Some("0") => false,
                Some("1") => true,
                _ => return Err(GameListError::InvalidEntry(index)),
            };
            if !valid_ini_value(executable) || !valid_ini_value(name) {
                return Err(GameListError::UnsafeValue(index));
            }
            Ok(GameListEntry {
                executable: executable.clone(),
                name: name.clone(),
                enabled,
            })
        })
        .collect()
}

pub fn format_game_list(games: &[GameListEntry]) -> Result<String, GameListError> {
    if games.len() > MAX_GAMES {
        return Err(GameListError::TooManyGames);
    }

    let mut text = format!("[Games]\nCount={}\n", games.len());
    for (index, game) in games.iter().enumerate() {
        if game.executable.is_empty() || game.name.is_empty() {
            return Err(GameListError::InvalidEntry(index));
        }
        if !valid_ini_value(&game.executable) || !valid_ini_value(&game.name) {
            return Err(GameListError::UnsafeValue(index));
        }
        text.push_str(&format!(
            "\n[Game{index}]\nExecutable={}\nName={}\nEnabled={}\n",
            game.executable,
            game.name,
            u8::from(game.enabled)
        ));
    }
    Ok(text)
}

pub fn load_game_list_file(path: &Path) -> io::Result<Vec<GameListEntry>> {
    let text = match fs::read_to_string(path) {
        Ok(text) => text,
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            return Ok(vec![GameListEntry {
                executable: "RobloxPlayerBeta.exe".into(),
                name: "Roblox".into(),
                enabled: true,
            }]);
        }
        Err(error) => return Err(error),
    };
    parse_game_list(&text).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

pub fn save_game_list_file(path: &Path, games: &[GameListEntry]) -> io::Result<()> {
    let text = format_game_list(games)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidInput, error))?;
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    fs::create_dir_all(parent)?;
    let file_name = path.file_name().unwrap_or_default().to_string_lossy();
    let (temporary, mut file) = loop {
        let id = TEMP_FILE_ID.fetch_add(1, Ordering::Relaxed);
        let temporary = parent.join(format!(".{file_name}.{}.{}.tmp", std::process::id(), id));
        match OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
        {
            Ok(file) => break (temporary, file),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    };

    let result = (|| {
        file.write_all(text.as_bytes())?;
        file.sync_all()?;
        drop(file);
        replace_file(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

#[cfg(windows)]
fn replace_file(source: &Path, destination: &Path) -> io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };
    use windows::core::PCWSTR;

    let source: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    unsafe {
        MoveFileExW(
            PCWSTR(source.as_ptr()),
            PCWSTR(destination.as_ptr()),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    }
    .map_err(io::Error::other)
}

#[cfg(not(windows))]
fn replace_file(source: &Path, destination: &Path) -> io::Result<()> {
    fs::rename(source, destination)
}

fn valid_ini_value(value: &str) -> bool {
    !value.chars().any(char::is_control)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_entries_and_disabled_games() {
        let games = vec![
            GameListEntry {
                executable: "RobloxPlayerBeta.exe".into(),
                name: "Roblox".into(),
                enabled: true,
            },
            GameListEntry {
                executable: "C:\\Games\\example.exe".into(),
                name: "Example Game".into(),
                enabled: false,
            },
        ];

        let formatted = format_game_list(&games).unwrap();
        assert_eq!(parse_game_list(&formatted).unwrap(), games);
    }

    #[test]
    fn accepts_utf8_bom_crlf_and_case_insensitive_keys() {
        let text = "\u{feff}[games]\r\ncount=1\r\n[GAME0]\r\nexecutable=game.exe\r\nname=Game\r\nenabled=1\r\n";
        assert_eq!(
            parse_game_list(text).unwrap(),
            [GameListEntry {
                executable: "game.exe".into(),
                name: "Game".into(),
                enabled: true,
            }]
        );
    }

    #[test]
    fn rejects_malformed_or_unbounded_lists() {
        assert_eq!(
            parse_game_list("[Games]\n"),
            Err(GameListError::MissingCount)
        );
        assert_eq!(
            parse_game_list("[Games]\nCount=nope"),
            Err(GameListError::InvalidCount)
        );
        assert_eq!(
            parse_game_list("[Games]\nCount=4097"),
            Err(GameListError::TooManyGames)
        );
        assert_eq!(
            parse_game_list(
                "[Games]\nCount=1\n[Game0]\nExecutable=game.exe\nName=Game\nEnabled=yes"
            ),
            Err(GameListError::InvalidEntry(0))
        );
    }

    #[test]
    fn rejects_ini_injection_on_write_and_read() {
        let injected = GameListEntry {
            executable: "game.exe".into(),
            name: "Game\n[Games]\nCount=0".into(),
            enabled: true,
        };
        assert_eq!(
            format_game_list(&[injected]),
            Err(GameListError::UnsafeValue(0))
        );
        assert!(
            parse_game_list(
                "[Games]\nCount=1\n[Game0]\nExecutable=game.exe\nName=Game\n[Games]\nEnabled=1"
            )
            .is_err()
        );
    }

    #[test]
    fn missing_game_file_uses_default_and_saves_atomically() {
        let directory = std::env::temp_dir().join(format!(
            "rishade-game-list-{}-{}",
            std::process::id(),
            TEMP_FILE_ID.fetch_add(1, Ordering::Relaxed)
        ));
        let path = directory.join("games.ini");
        let defaults = load_game_list_file(&path).unwrap();
        assert_eq!(defaults[0].executable, "RobloxPlayerBeta.exe");
        save_game_list_file(&path, &defaults).unwrap();
        assert_eq!(load_game_list_file(&path).unwrap(), defaults);
        let updated = [GameListEntry {
            executable: "Example.exe".into(),
            name: "Example".into(),
            enabled: false,
        }];
        save_game_list_file(&path, &updated).unwrap();
        assert_eq!(load_game_list_file(&path).unwrap(), updated);
        fs::remove_dir_all(directory).unwrap();
    }
}
