use super::managed::{self, Managed, Record};
use super::{fs, paths, placer};
use crate::archive::{self, Error, MAX_ENTRIES, MAX_EXTRACTED, Sink};
use crate::java;
use serde_json::Value;
use std::collections::HashMap;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};

pub const BOMB_FLOOR: u64 = 64 * 1024 * 1024;
pub const MAX_RATIO: u64 = 120;
const MAX_CACHE_ENTRY: usize = 64 * 1024 * 1024;
pub const SLOT_EXTENSIONS: &[&str] = &["png", "jpg", "jpeg", "bmp", "tga", "dds", "ktx", "webp", "tex", "ogg", "mp3", "wav", "flac", "mesh", "ttf", "otf", "gif"];
pub const BACKUP: &str = "AssetCacheBackup";

pub enum Take {
    Skip,
    Count,
    File(PathBuf),
    Memory,
}

pub enum Taken {
    Nothing,
    Counted(u64),
    Written,
    Bytes(Option<Vec<u8>>),
}

trait Visit {
    fn begin(&mut self, name: &str) -> Result<Take, Error>;
    fn done(&mut self, _name: &str, _taken: Taken) -> Result<(), Error> {
        Ok(())
    }
    fn over(&self) -> Error {
        archive::too_big()
    }
}

enum Current {
    None,
    Skip,
    Count(u64),
    File(BufWriter<std::fs::File>),
    Memory(Option<Vec<u8>>),
}

struct Walk<'a, V: Visit> {
    visit: V,
    cancel: &'a mut dyn FnMut() -> bool,
    name: String,
    current: Current,
    total: u64,
}

impl<V: Visit> Walk<'_, V> {
    fn finish(&mut self) -> Result<(), Error> {
        let taken = match std::mem::replace(&mut self.current, Current::None) {
            Current::None => return Ok(()),
            Current::Skip => Taken::Nothing,
            Current::Count(n) => Taken::Counted(n),
            Current::File(mut w) => {
                w.flush()?;
                Taken::Written
            }
            Current::Memory(b) => Taken::Bytes(b),
        };
        let name = std::mem::take(&mut self.name);
        self.visit.done(&name, taken)
    }
}

impl<V: Visit> Sink for Walk<'_, V> {
    fn entry(&mut self, name: &str, _size: i64) -> Result<(), Error> {
        self.finish()?;
        if (self.cancel)() {
            return Err(Error::Io("cancelled".into()));
        }
        self.name = name.to_string();
        self.current = match self.visit.begin(name)? {
            Take::Skip => Current::Skip,
            Take::Count => Current::Count(0),
            Take::File(p) => Current::File(BufWriter::with_capacity(1 << 16, std::fs::File::create(p)?)),
            Take::Memory => Current::Memory(Some(Vec::new())),
        };
        Ok(())
    }

    fn data(&mut self, bytes: &[u8]) -> Result<(), Error> {
        match &mut self.current {
            Current::None | Current::Skip => {}
            Current::Count(n) => {
                *n += bytes.len() as u64;
                self.total += bytes.len() as u64;
                if self.total > MAX_EXTRACTED {
                    return Err(self.visit.over());
                }
            }
            Current::File(w) => {
                self.total += bytes.len() as u64;
                if self.total > MAX_EXTRACTED {
                    return Err(self.visit.over());
                }
                w.write_all(bytes)?;
            }
            Current::Memory(buf) => {
                if let Some(b) = buf {
                    if b.len() + bytes.len() > MAX_CACHE_ENTRY {
                        *buf = None;
                    } else {
                        b.extend_from_slice(bytes);
                    }
                }
            }
        }
        Ok(())
    }

    fn end(&mut self) -> Result<(), Error> {
        self.finish()
    }
}

fn walk<V: Visit>(archive: &Path, visit: V, cancel: &mut dyn FnMut() -> bool) -> Result<V, Error> {
    let mut w = Walk { visit, cancel, name: String::new(), current: Current::None, total: 0 };
    archive::decode(archive, &mut w)?;
    w.finish()?;
    Ok(w.visit)
}

fn outside() -> Error {
    Error::rejected("The package tried to write outside its own folder.")
}

fn prepare(dest: &Path, rel: &str) -> Result<Option<PathBuf>, Error> {
    let out = dest.join(rel);
    if !fs::strictly_inside(dest, &out) {
        return Ok(None);
    }
    if let Some(parent) = out.parent()
        && !parent.is_dir()
        && std::fs::create_dir_all(parent).is_err()
    {
        return Err(Error::Io("mkdir".into()));
    }
    Ok(Some(out))
}

pub struct Inspection {
    pub roblox_content: bool,
    pub files: i64,
    pub bytes: i64,
}

struct Inspect<'a> {
    src: &'a placer::Source,
    r: Inspection,
}

impl Visit for Inspect<'_> {
    fn begin(&mut self, name: &str) -> Result<Take, Error> {
        let n = paths::arch_normalize(name);
        if let Some(bad) = paths::inspect_path(&n, &mut |x| placer::client_copy(self.src, x)) {
            return Err(Error::Rejected(bad));
        }
        self.r.files += 1;
        if self.r.files > MAX_ENTRIES as i64 {
            return Err(archive::too_many());
        }
        Ok(Take::Count)
    }

    fn done(&mut self, name: &str, taken: Taken) -> Result<(), Error> {
        if let Taken::Counted(n) = taken {
            self.r.bytes += n as i64;
        }
        let n = paths::arch_normalize(name);
        if paths::installable(&n) && placer::resolve(self.src, &n).is_some() {
            self.r.roblox_content = true;
        }
        Ok(())
    }
}

pub fn inspect(archive: &Path, src: &placer::Source, cancel: &mut dyn FnMut() -> bool) -> Result<Inspection, Error> {
    let packed = fs::length(archive) as u64;
    let v = walk(archive, Inspect { src, r: Inspection { roblox_content: false, files: 0, bytes: 0 } }, cancel)?;
    let r = v.r;
    if r.files == 0 {
        return Err(Error::rejected("The package is empty."));
    }
    let bytes = r.bytes as u64;
    if packed > 0 && bytes > BOMB_FLOOR && bytes / packed > MAX_RATIO {
        return Err(Error::rejected("The package expands far beyond its download size, which is how zip bombs behave."));
    }
    Ok(r)
}

struct Verified<'a> {
    src: &'a placer::Source,
    dest: PathBuf,
    placed: HashMap<String, String>,
    pending: Option<String>,
    written: i64,
}

impl Visit for Verified<'_> {
    fn begin(&mut self, name: &str) -> Result<Take, Error> {
        let n = paths::arch_normalize(name);
        if let Some(bad) = paths::inspect_path(&n, &mut |x| placer::client_copy(self.src, x)) {
            return Err(Error::Rejected(bad));
        }
        if !paths::installable(&n) {
            return Ok(Take::Skip);
        }
        let Some(target) = placer::resolve(self.src, &n) else {
            return Ok(Take::Skip);
        };
        if paths::ignored(&target) {
            return Ok(Take::Skip);
        }
        let out = prepare(&self.dest, &target)?.ok_or_else(outside)?;
        self.pending = Some(target);
        Ok(Take::File(out))
    }

    fn done(&mut self, _name: &str, taken: Taken) -> Result<(), Error> {
        if let (Taken::Written, Some(target)) = (taken, self.pending.take())
            && self.placed.insert(java::lower(&target), target).is_none()
        {
            self.written += 1;
        }
        Ok(())
    }
}

pub fn extract_verified(archive: &Path, dest: &Path, src: &placer::Source, cancel: &mut dyn FnMut() -> bool) -> Result<i64, Error> {
    let v = walk(archive, Verified { src, dest: dest.to_path_buf(), placed: HashMap::new(), pending: None, written: 0 }, cancel)?;
    if v.written == 0 {
        return Err(Error::rejected("Nothing in the package could be matched to a Roblox client folder, so nothing was installed."));
    }
    Ok(v.written)
}

fn staging(managed: &Managed) -> PathBuf {
    managed.root.join(format!("staging_{}", java::now_nanos()))
}

pub fn install_managed(archive: &Path, name: Option<&str>, pack: Option<&Value>, managed: &Managed, src: &placer::Source, cancel: &mut dyn FnMut() -> bool) -> Result<Record, Error> {
    let i = inspect(archive, src, cancel)?;
    if !i.roblox_content {
        return Err(Error::rejected("Nothing in the package could be matched to a Roblox client folder, so it is not a Roblox mod."));
    }
    let staging = staging(managed);
    let result = (|| {
        std::fs::create_dir_all(&staging).map_err(|_| Error::Io("mkdir".into()))?;
        extract_verified(archive, &staging, src, cancel)?;
        if let Some(p) = pack {
            managed::write_pack(&staging, p);
        }
        Ok(managed.adopt(&staging, name)?)
    })();
    fs::delete(&staging);
    result
}

struct Capture<'a> {
    src: &'a placer::Source,
    root: PathBuf,
    written: i64,
}

impl Visit for Capture<'_> {
    fn begin(&mut self, name: &str) -> Result<Take, Error> {
        let n = paths::arch_normalize(name);
        if !SLOT_EXTENSIONS.contains(&paths::extension(&n).as_str()) || paths::inspect_path(&n, &mut |x| placer::client_copy(self.src, x)).is_some() {
            return Ok(Take::Skip);
        }
        let out = self.root.join(&n);
        if !fs::strictly_inside(&self.root, &out) {
            return Ok(Take::Skip);
        }
        if let Some(parent) = out.parent()
            && !parent.is_dir()
            && std::fs::create_dir_all(parent).is_err()
        {
            return Ok(Take::Skip);
        }
        Ok(Take::File(out))
    }

    fn done(&mut self, _name: &str, taken: Taken) -> Result<(), Error> {
        if let Taken::Written = taken {
            self.written += 1;
        }
        Ok(())
    }

    fn over(&self) -> Error {
        Error::Io("too large".into())
    }
}

pub fn capture(archive: &Path, root: &Path, src: &placer::Source, cancel: &mut dyn FnMut() -> bool) -> Result<i64, Error> {
    Ok(walk(archive, Capture { src, root: root.to_path_buf(), written: 0 }, cancel)?.written)
}

fn restore(name: &str) -> bool {
    name.to_uppercase().contains("RESTORE")
}

struct HasEntries {
    found: bool,
}

impl Visit for HasEntries {
    fn begin(&mut self, name: &str) -> Result<Take, Error> {
        if self.found || restore(name) || paths::hash_of(name).is_none() {
            return Ok(Take::Skip);
        }
        Ok(Take::Memory)
    }

    fn done(&mut self, _name: &str, taken: Taken) -> Result<(), Error> {
        if let Taken::Bytes(Some(all)) = taken
            && paths::cache_entry(&all)
        {
            self.found = true;
        }
        Ok(())
    }
}

pub fn cache_has_entries(archive: &Path, cancel: &mut dyn FnMut() -> bool) -> Result<bool, Error> {
    Ok(walk(archive, HasEntries { found: false }, cancel)?.found)
}

struct CachePack {
    backup: PathBuf,
    seen: Vec<String>,
    count: i64,
}

impl Visit for CachePack {
    fn begin(&mut self, name: &str) -> Result<Take, Error> {
        if restore(name) {
            return Ok(Take::Skip);
        }
        match paths::hash_of(name) {
            Some(h) if !self.seen.contains(&h) => Ok(Take::Memory),
            _ => Ok(Take::Skip),
        }
    }

    fn done(&mut self, name: &str, taken: Taken) -> Result<(), Error> {
        let (Taken::Bytes(Some(all)), Some(hash)) = (taken, paths::hash_of(name)) else {
            return Ok(());
        };
        if self.seen.contains(&hash) || !paths::cache_entry(&all) {
            return Ok(());
        }
        std::fs::write(self.backup.join(format!("{hash}.mod.lock")), &all)?;
        self.seen.push(hash);
        self.count += 1;
        Ok(())
    }
}

pub fn cache_install(archive: &Path, name: Option<&str>, pack: Option<&Value>, managed: &Managed, cancel: &mut dyn FnMut() -> bool) -> Result<Record, Error> {
    let staging = staging(managed);
    let backup = staging.join(BACKUP);
    if std::fs::create_dir_all(&backup).is_err() {
        return Err(Error::Io("mkdir".into()));
    }
    let result = (|| {
        let v = walk(archive, CachePack { backup: backup.clone(), seen: Vec::new(), count: 0 }, cancel)?;
        if v.count == 0 {
            return Err(Error::rejected("This package does not contain any usable Roblox asset cache entries."));
        }
        if let Some(p) = pack {
            managed::write_pack(&staging, p);
        }
        Ok(managed.adopt(&staging, name)?)
    })();
    fs::delete(&staging);
    result
}
