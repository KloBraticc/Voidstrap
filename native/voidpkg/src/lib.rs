use std::cell::RefCell;
use std::collections::HashSet;
use std::fs::{self, File};
use std::io::{self, BufReader, BufWriter, Read, Seek};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use zip::ZipArchive;

pub const OK: i32 = 0;
pub const FAILED: i32 = 1;
pub const CANCELLED: i32 = 2;

const PARALLEL_MIN_BYTES: u64 = 8 * 1024 * 1024;
const MAX_THREADS: usize = 4;
const WRITE_BUFFER: usize = 256 * 1024;

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

#[derive(Debug)]
pub enum ExtractError {
    Cancelled,
    Failed(String),
}

impl From<io::Error> for ExtractError {
    fn from(e: io::Error) -> Self {
        ExtractError::Failed(e.to_string())
    }
}

impl From<zip::result::ZipError> for ExtractError {
    fn from(e: zip::result::ZipError) -> Self {
        ExtractError::Failed(e.to_string())
    }
}

pub enum EntryName {
    Skip,
    Unsafe,
    Path(String, bool),
}

pub fn normalize_entry_name(name: &str) -> EntryName {
    let unified = name.replace('\\', "/");
    let is_dir = unified.ends_with('/');
    let mut parts = Vec::new();
    for part in unified.split('/') {
        if part.is_empty() || part == "." {
            continue;
        }
        if part == ".."
            || part.chars().any(|character| character.is_control() || matches!(character, ':' | '<' | '>' | '"' | '|' | '?' | '*'))
            || part.ends_with(['.', ' '])
            || windows_reserved_name(part)
        {
            return EntryName::Unsafe;
        }
        parts.push(part);
    }
    if parts.is_empty() {
        return EntryName::Skip;
    }
    EntryName::Path(parts.join("/"), is_dir)
}

fn windows_reserved_name(component: &str) -> bool {
    let base = component.split('.').next().unwrap_or_default().trim_end_matches(['.', ' ']).to_ascii_uppercase();
    matches!(base.as_str(), "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$")
        || ["COM", "LPT"].iter().any(|prefix| {
            base.strip_prefix(prefix).is_some_and(|suffix| {
                matches!(suffix, "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³")
            })
        })
}

fn ensure_directory(root: &Path, relative: &Path) -> Result<(), ExtractError> {
    let mut current = root.to_path_buf();
    ensure_one_directory(&current)?;
    for component in relative.components() {
        current.push(component);
        ensure_one_directory(&current)?;
    }
    Ok(())
}

fn ensure_one_directory(path: &Path) -> Result<(), ExtractError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => {
            Err(ExtractError::Failed("Extraction path contains a link or file".into()))
        }
        Ok(_) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(fs::create_dir_all(path)?),
        Err(error) => Err(error.into()),
    }
}

fn open_archive(path: &Path) -> Result<ZipArchive<BufReader<File>>, ExtractError> {
    let file = File::open(path)?;
    Ok(ZipArchive::new(BufReader::with_capacity(64 * 1024, file))?)
}

fn extract_range<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
    indices: &[usize],
    root: &Path,
    filter: Option<&HashSet<String>>,
    stop: &dyn Fn() -> bool,
) -> Result<(), ExtractError> {
    for &index in indices {
        if stop() {
            return Err(ExtractError::Cancelled);
        }
        let mut entry = archive.by_index(index)?;
        let name = entry.name()?.into_owned();
        let (relative, is_dir) = match normalize_entry_name(&name) {
            EntryName::Skip => continue,
            EntryName::Unsafe => return Err(ExtractError::Failed(format!("Unsafe entry path {name}"))),
            EntryName::Path(relative, is_dir) => (relative, is_dir || entry.is_dir()),
        };
        if let Some(filter) = filter {
            if is_dir || !filter.contains(&relative.to_lowercase()) {
                continue;
            }
        }
        let target = root.join(&relative);
        if is_dir {
            ensure_directory(root, Path::new(&relative))?;
            continue;
        }
        if let Some(parent) = Path::new(&relative).parent() {
            ensure_directory(root, parent)?;
        }
        if fs::symlink_metadata(&target).is_ok_and(|metadata| metadata.file_type().is_symlink() || !metadata.is_file()) {
            return Err(ExtractError::Failed("Extraction target is not a regular file".into()));
        }
        let out = File::create(&target)?;
        out.set_len(entry.size())?;
        let mut writer = BufWriter::with_capacity(WRITE_BUFFER, out);
        io::copy(&mut entry, &mut writer)?;
        writer.into_inner().map_err(|e| ExtractError::Failed(e.to_string()))?;
    }
    Ok(())
}

pub fn extract(
    zip_path: &Path,
    dest: &Path,
    filter: Option<&HashSet<String>>,
    cancel: &(dyn Fn() -> bool + Sync),
) -> Result<(), ExtractError> {
    ensure_directory(dest, Path::new(""))?;
    let mut archive = open_archive(zip_path)?;
    let count = archive.len();
    let size = fs::metadata(zip_path)?.len();
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1).min(MAX_THREADS);
    let indices: Vec<usize> = (0..count).collect();
    if threads <= 1 || size < PARALLEL_MIN_BYTES || count < threads * 2 {
        return extract_range(&mut archive, &indices, dest, filter, cancel);
    }
    drop(archive);
    let failed = AtomicBool::new(false);
    let stop = || failed.load(Ordering::Relaxed) || cancel();
    let chunks: Vec<Vec<usize>> = (0..threads)
        .map(|t| indices.iter().copied().filter(|i| i % threads == t).collect())
        .collect();
    let results: Vec<Result<(), ExtractError>> = std::thread::scope(|scope| {
        let handles: Vec<_> = chunks
            .iter()
            .map(|chunk| {
                let stop = &stop;
                let failed = &failed;
                scope.spawn(move || {
                    let result = open_archive(zip_path).and_then(|mut a| extract_range(&mut a, chunk, dest, filter, stop));
                    if result.is_err() {
                        failed.store(true, Ordering::Relaxed);
                    }
                    result
                })
            })
            .collect();
        handles
            .into_iter()
            .map(|h| h.join().unwrap_or_else(|_| Err(ExtractError::Failed("Extraction thread panicked".into()))))
            .collect()
    });
    for result in &results {
        if let Err(ExtractError::Failed(message)) = result {
            return Err(ExtractError::Failed(message.clone()));
        }
    }
    if cancel() {
        return Err(ExtractError::Cancelled);
    }
    Ok(())
}

unsafe fn utf16_string(ptr: *const u16, len: usize) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    String::from_utf16(std::slice::from_raw_parts(ptr, len)).ok()
}

fn set_error(message: String) {
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
}

#[no_mangle]
pub extern "C" fn vp_version() -> u32 {
    1
}

#[no_mangle]
pub unsafe extern "C" fn vp_extract(
    zip_ptr: *const u16,
    zip_len: usize,
    dest_ptr: *const u16,
    dest_len: usize,
    filter_ptr: *const u16,
    filter_len: usize,
    cancel_flag: *const AtomicI32,
) -> i32 {
    let outcome = std::panic::catch_unwind(|| {
        let zip_path = utf16_string(zip_ptr, zip_len).ok_or_else(|| ExtractError::Failed("Invalid archive path".into()))?;
        let dest = utf16_string(dest_ptr, dest_len).ok_or_else(|| ExtractError::Failed("Invalid destination path".into()))?;
        let filter: Option<HashSet<String>> = if filter_ptr.is_null() {
            None
        } else {
            let text = utf16_string(filter_ptr, filter_len).ok_or_else(|| ExtractError::Failed("Invalid filter".into()))?;
            Some(
                text.split('\n')
                    .filter_map(|line| match normalize_entry_name(line) {
                        EntryName::Path(name, _) => Some(name.to_lowercase()),
                        _ => None,
                    })
                    .collect(),
            )
        };
        let flag = if cancel_flag.is_null() { None } else { Some(&*cancel_flag) };
        let cancel = move || flag.is_some_and(|f| f.load(Ordering::Relaxed) != 0);
        extract(Path::new(&zip_path), &PathBuf::from(dest), filter.as_ref(), &cancel)
    });
    match outcome {
        Ok(Ok(())) => OK,
        Ok(Err(ExtractError::Cancelled)) => CANCELLED,
        Ok(Err(ExtractError::Failed(message))) => {
            set_error(message);
            FAILED
        }
        Err(_) => {
            set_error("Extraction panicked".into());
            FAILED
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn vp_last_error(buffer: *mut u16, capacity: usize) -> usize {
    LAST_ERROR.with(|e| {
        let encoded: Vec<u16> = e.borrow().encode_utf16().collect();
        if !buffer.is_null() && capacity > 0 {
            let n = encoded.len().min(capacity);
            std::ptr::copy_nonoverlapping(encoded.as_ptr(), buffer, n);
        }
        encoded.len()
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use zip::write::SimpleFileOptions;

    fn write_archive(path: &Path, name: &str) {
        let mut archive = zip::ZipWriter::new(File::create(path).unwrap());
        archive.start_file(name, SimpleFileOptions::default()).unwrap();
        archive.write_all(b"package data").unwrap();
        archive.finish().unwrap();
    }

    fn path(name: &str) -> Option<(String, bool)> {
        match normalize_entry_name(name) {
            EntryName::Path(p, d) => Some((p, d)),
            _ => None,
        }
    }

    #[test]
    fn normalizes_backslash_paths() {
        assert_eq!(path("content\\fonts\\a.ttf"), Some(("content/fonts/a.ttf".into(), false)));
        assert_eq!(path("shaders\\"), Some(("shaders".into(), true)));
        assert!(matches!(normalize_entry_name("\\"), EntryName::Skip));
        assert_eq!(path("\\RobloxPlayerBeta.exe"), Some(("RobloxPlayerBeta.exe".into(), false)));
    }

    #[test]
    fn rejects_escaping_paths() {
        assert!(matches!(normalize_entry_name("..\\evil.dll"), EntryName::Unsafe));
        assert!(matches!(normalize_entry_name("C:\\Windows\\x"), EntryName::Unsafe));
        assert!(matches!(normalize_entry_name("a/../../b"), EntryName::Unsafe));
        assert!(matches!(normalize_entry_name("\\\\server\\share\\..\\x"), EntryName::Unsafe));
    }

    #[test]
    fn rejects_windows_path_aliases_and_device_names() {
        for name in [".. /escape.dll", "a/.. ./escape.dll", "a/CON.txt", "a/CON .txt", "a/nul", "a/CONIN$", "a/COM1.dll", "a/LPT².txt", "a/trailing.", "a/bad?.dll", "a/null\0.dll"] {
            assert!(matches!(normalize_entry_name(name), EntryName::Unsafe), "{name:?}");
        }
        assert_eq!(path("content/console.txt"), Some(("content/console.txt".into(), false)));
    }

    #[test]
    fn extracts_deflated_archives_and_preserves_filters_and_cancellation() {
        let temp = tempfile::tempdir().unwrap();
        let archive = temp.path().join("package.zip");
        let output = temp.path().join("output");
        write_archive(&archive, "content/data.txt");
        let filter = HashSet::from(["other.txt".into()]);
        extract(&archive, &output, Some(&filter), &|| false).unwrap();
        assert!(!output.join("content/data.txt").exists());
        extract(&archive, &output, None, &|| false).unwrap();
        assert_eq!(fs::read(output.join("content/data.txt")).unwrap(), b"package data");
        assert!(matches!(extract(&archive, &output, None, &|| true), Err(ExtractError::Cancelled)));
    }

    #[test]
    fn rejects_archive_paths_before_writing_outside_the_destination() {
        let temp = tempfile::tempdir().unwrap();
        let archive = temp.path().join("package.zip");
        write_archive(&archive, ".. /outside.txt");
        assert!(extract(&archive, &temp.path().join("output"), None, &|| false).is_err());
        assert!(!temp.path().join("outside.txt").exists());
    }

    #[test]
    fn rejects_existing_symlinks_without_overwriting_their_targets() {
        let temp = tempfile::tempdir().unwrap();
        let archive = temp.path().join("package.zip");
        let output = temp.path().join("output");
        let outside = temp.path().join("outside");
        fs::create_dir(&output).unwrap();
        fs::create_dir(&outside).unwrap();
        #[cfg(unix)]
        std::os::unix::fs::symlink(&outside, output.join("content")).unwrap();
        #[cfg(windows)]
        assert!(std::process::Command::new("powershell.exe")
            .args(["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:VOIDPKG_TEST_TARGET -Target $env:VOIDPKG_TEST_SOURCE | Out-Null"])
            .env("VOIDPKG_TEST_TARGET", output.join("content"))
            .env("VOIDPKG_TEST_SOURCE", &outside)
            .status().unwrap().success());
        write_archive(&archive, "content/data.txt");
        assert!(extract(&archive, &output, None, &|| false).is_err());
        assert!(!outside.join("data.txt").exists());
    }
}
