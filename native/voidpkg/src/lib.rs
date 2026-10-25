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
        if part == ".." || part.contains(':') {
            return EntryName::Unsafe;
        }
        parts.push(part);
    }
    if parts.is_empty() {
        return EntryName::Skip;
    }
    EntryName::Path(parts.join("/"), is_dir)
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
        let (relative, is_dir) = match normalize_entry_name(entry.name()) {
            EntryName::Skip => continue,
            EntryName::Unsafe => return Err(ExtractError::Failed(format!("Unsafe entry path {}", entry.name()))),
            EntryName::Path(relative, is_dir) => (relative, is_dir || entry.is_dir()),
        };
        if let Some(filter) = filter {
            if is_dir || !filter.contains(&relative.to_lowercase()) {
                continue;
            }
        }
        let target = root.join(&relative);
        if is_dir {
            fs::create_dir_all(&target)?;
            continue;
        }
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent)?;
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
    fs::create_dir_all(dest)?;
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
}
