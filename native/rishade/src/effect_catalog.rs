use std::fs;
use std::io;
use std::path::{Path, PathBuf};

const MAX_EFFECT_FILES: usize = 16_384;
const MAX_DIRECTORY_DEPTH: usize = 64;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EffectFile {
    pub relative_path: PathBuf,
}

pub fn scan_effects(root: &Path) -> io::Result<Vec<EffectFile>> {
    let root = match fs::canonicalize(root) {
        Ok(root) => root,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
    };
    let mut pending = vec![(root.clone(), 0usize)];
    let mut effects = Vec::new();

    while let Some((directory, depth)) = pending.pop() {
        if depth > MAX_DIRECTORY_DEPTH {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "effect directory nesting exceeds the supported limit",
            ));
        }

        let mut entries = fs::read_dir(directory)?.collect::<Result<Vec<_>, _>>()?;
        entries.sort_by_key(|entry| entry.file_name().to_string_lossy().to_lowercase());
        for entry in entries.into_iter().rev() {
            let path = entry.path();
            let file_type = entry.file_type()?;
            if file_type.is_symlink() {
                continue;
            }
            if file_type.is_dir() {
                pending.push((path, depth + 1));
                continue;
            }
            if !file_type.is_file()
                || !path
                    .extension()
                    .is_some_and(|extension| extension.eq_ignore_ascii_case("fx"))
            {
                continue;
            }

            let relative_path = path.strip_prefix(&root).map_err(|_| {
                io::Error::new(
                    io::ErrorKind::InvalidData,
                    "effect file escaped its configured directory",
                )
            })?;
            effects.push(EffectFile {
                relative_path: relative_path.to_owned(),
            });
            if effects.len() > MAX_EFFECT_FILES {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "effect directory contains too many effect files",
                ));
            }
        }
    }

    effects.sort_by_key(|effect| effect.relative_path.to_string_lossy().to_lowercase());
    Ok(effects)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    static TEMP_ID: AtomicUsize = AtomicUsize::new(0);

    fn test_directory() -> PathBuf {
        std::env::temp_dir().join(format!(
            "rishade-effects-{}-{}",
            std::process::id(),
            TEMP_ID.fetch_add(1, Ordering::Relaxed)
        ))
    }

    #[test]
    fn discovers_nested_effects_case_insensitively_in_stable_order() {
        let directory = test_directory();
        fs::create_dir_all(directory.join("Color")).unwrap();
        fs::create_dir_all(directory.join("AA")).unwrap();
        fs::write(directory.join("Color").join("tone.fx"), "").unwrap();
        fs::write(directory.join("AA").join("fxaa.FX"), "").unwrap();
        fs::write(directory.join("readme.txt"), "").unwrap();

        let effects = scan_effects(&directory).unwrap();
        assert_eq!(
            effects
                .iter()
                .map(|effect| effect.relative_path.to_string_lossy().replace('\\', "/"))
                .collect::<Vec<_>>(),
            ["AA/fxaa.FX", "Color/tone.fx"]
        );
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn missing_effect_directory_is_an_empty_catalog() {
        assert!(scan_effects(&test_directory()).unwrap().is_empty());
    }
}
