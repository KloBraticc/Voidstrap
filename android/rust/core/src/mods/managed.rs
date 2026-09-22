use super::{fs, paths};
use crate::java;
use serde_json::{Map, Value, json};
use std::collections::HashSet;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub const PACK_FILE: &str = "ModPack.lock";
const MAX_FILES: usize = 100_000;
static SYNC: Mutex<()> = Mutex::new(());

#[derive(Clone, Debug, PartialEq)]
pub struct Record {
    pub id: String,
    pub name: String,
    pub enabled: bool,
    pub created: i64,
}

impl Record {
    pub fn to_json(&self) -> Value {
        json!({"id": self.id, "name": self.name, "enabled": self.enabled, "created": self.created})
    }
}

pub struct Managed {
    pub root: PathBuf,
}

fn fallback(id: &str) -> String {
    format!("Mod {}", &id[..8])
}

impl Managed {
    pub fn new(root: &str) -> Self {
        Managed { root: PathBuf::from(root) }
    }

    fn packages(&self) -> PathBuf {
        self.root.join("Packages")
    }

    fn index(&self) -> PathBuf {
        self.root.join("Index.json")
    }

    pub fn folder(&self, id: &str) -> io::Result<PathBuf> {
        if !paths::valid_id(id) {
            return Err(io::Error::other("id"));
        }
        Ok(self.packages().join(java::lower(id)))
    }

    pub fn sources(&self, id: &str) -> PathBuf {
        self.root.join("Sources").join(java::lower(id))
    }

    pub fn load(&self) -> Vec<Record> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        self.load_core()
    }

    fn new_record(name: Option<&str>) -> Record {
        let id = java::uuid_hex();
        let name = paths::normalize_name(name, Some(&fallback(&id))).unwrap_or_default();
        Record { id, name, enabled: true, created: java::now_ms() }
    }

    pub fn create(&self, name: Option<&str>) -> io::Result<Record> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        let mut records = self.load_core();
        let r = Self::new_record(name);
        let f = self.folder(&r.id)?;
        if !f.is_dir() {
            std::fs::create_dir_all(&f).map_err(|_| io::Error::other("mkdir"))?;
        }
        records.insert(0, r.clone());
        self.save(&records);
        Ok(r)
    }

    pub fn adopt(&self, staged: &Path, name: Option<&str>) -> io::Result<Record> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        let mut records = self.load_core();
        let r = Self::new_record(name);
        let f = self.folder(&r.id)?;
        if let Some(parent) = f.parent()
            && !parent.is_dir()
        {
            let _ = std::fs::create_dir_all(parent);
        }
        std::fs::rename(staged, &f).map_err(|_| io::Error::other("move"))?;
        records.insert(0, r.clone());
        self.save(&records);
        Ok(r)
    }

    fn mutate(&self, id: &str, m: impl FnOnce(&mut Record)) -> io::Result<()> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        let mut records = self.load_core();
        let at = index_of(&records, id).ok_or_else(|| io::Error::other("missing"))?;
        m(&mut records[at]);
        self.save(&records);
        Ok(())
    }

    pub fn rename(&self, id: &str, name: Option<&str>) -> io::Result<()> {
        let n = paths::normalize_name(name, None).ok_or_else(|| io::Error::other("name"))?;
        self.mutate(id, |r| r.name = n)
    }

    pub fn set_enabled(&self, id: &str, enabled: bool) -> io::Result<()> {
        self.mutate(id, |r| r.enabled = enabled)
    }

    pub fn move_to(&self, id: &str, to: Option<i64>, delta: i64) -> io::Result<()> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        let mut records = self.load_core();
        let from = index_of(&records, id).ok_or_else(|| io::Error::other("missing"))?;
        let last = records.len() as i64 - 1;
        let to = to.unwrap_or(from as i64 + delta).clamp(0, last.max(0)) as usize;
        if to == from {
            return Ok(());
        }
        let r = records.remove(from);
        records.insert(to, r);
        self.save(&records);
        Ok(())
    }

    pub fn delete(&self, id: &str) -> io::Result<()> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        let mut records = self.load_core();
        let Some(at) = index_of(&records, id) else {
            return Ok(());
        };
        records.remove(at);
        let f = self.folder(id)?;
        let trash = self.root.join("Trash");
        let _ = std::fs::create_dir_all(&trash);
        let moved = trash.join(java::uuid_hex());
        if f.is_dir() && std::fs::rename(&f, &moved).is_err() {
            fs::delete(&f);
        }
        self.save(&records);
        fs::delete(&self.sources(id));
        fs::delete(&moved);
        if let Ok(left) = std::fs::read_dir(&trash) {
            for x in left.flatten() {
                fs::delete(&x.path());
            }
        }
        Ok(())
    }

    pub fn scan(&self) -> Vec<Value> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        struct Entry {
            record: Record,
            files: i64,
            bytes: i64,
            pack: Option<Value>,
            paths: HashSet<String>,
            conflicts: i64,
            overridden: i64,
        }
        let mut out: Vec<Entry> = Vec::new();
        for r in self.load_core() {
            let f = self.folder(&r.id).unwrap_or_default();
            let mut e = Entry { pack: read_pack(&f), record: r, files: 0, bytes: 0, paths: HashSet::new(), conflicts: 0, overridden: 0 };
            for rel in files(&f) {
                e.files += 1;
                e.bytes += std::fs::metadata(f.join(&rel)).map(|m| m.len() as i64).unwrap_or(0);
                if !paths::ignored(&rel) {
                    e.paths.insert(java::lower(&rel));
                }
            }
            out.push(e);
        }
        let mut claimed = HashSet::new();
        for e in out.iter_mut() {
            if !e.record.enabled {
                continue;
            }
            for p in &e.paths {
                if !claimed.insert(p.clone()) {
                    e.overridden += 1;
                }
            }
        }
        let conflicts: Vec<i64> = (0..out.len())
            .map(|i| {
                let mut n = 0;
                for (j, other) in out.iter().enumerate() {
                    if j == i || !other.record.enabled {
                        continue;
                    }
                    if out[i].paths.iter().any(|p| other.paths.contains(p)) {
                        n += 1;
                    }
                }
                n
            })
            .collect();
        for (e, c) in out.iter_mut().zip(conflicts) {
            e.conflicts = c;
        }
        out.into_iter()
            .map(|e| {
                json!({"record": e.record.to_json(), "files": e.files, "bytes": e.bytes, "pack": e.pack, "paths": e.paths.into_iter().collect::<Vec<_>>(), "conflicts": e.conflicts, "overridden": e.overridden})
            })
            .collect()
    }

    pub fn enabled_files(&self) -> Vec<(Record, PathBuf, String)> {
        let _g = SYNC.lock().unwrap_or_else(|e| e.into_inner());
        let mut out = Vec::new();
        let mut claimed = HashSet::new();
        for r in self.load_core() {
            if !r.enabled {
                continue;
            }
            let f = self.folder(&r.id).unwrap_or_default();
            for rel in files(&f) {
                if paths::ignored(&rel) {
                    continue;
                }
                if claimed.insert(java::lower(&rel)) {
                    out.push((r.clone(), f.join(&rel), rel));
                }
            }
        }
        out
    }

    fn load_core(&self) -> Vec<Record> {
        let pk = self.packages();
        if !pk.is_dir() {
            let _ = std::fs::create_dir_all(&pk);
        }
        let mut records = self.read_index();
        let mut changed = false;
        let mut indexed: HashSet<String> = records.iter().map(|r| java::lower(&r.id)).collect();
        for d in fs::list_raw(&pk) {
            if !d.is_dir() {
                continue;
            }
            let n = d.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            if paths::valid_id(&n) {
                if indexed.insert(java::lower(&n)) {
                    records.push(Record { id: java::lower(&n), name: fallback(&n), enabled: true, created: fs::modified_ms(&d) });
                    changed = true;
                }
                continue;
            }
            let r = Self::new_record(Some(&n));
            if std::fs::rename(&d, pk.join(&r.id)).is_ok() {
                indexed.insert(r.id.clone());
                records.push(r);
                changed = true;
            }
        }
        let before = records.len();
        records.retain(|r| self.folder(&r.id).map(|f| f.is_dir()).unwrap_or(false));
        changed |= records.len() != before;
        if changed || !self.index().is_file() {
            self.save(&records);
        }
        records
    }

    fn read_index(&self) -> Vec<Record> {
        let mut out = Vec::new();
        let f = self.index();
        let len = std::fs::metadata(&f).map(|m| m.len()).unwrap_or(0);
        if !f.is_file() || len == 0 || len > 2 * 1024 * 1024 {
            return out;
        }
        let Ok(text) = fs::read_atomic(&f) else {
            return out;
        };
        let Ok(o) = serde_json::from_slice::<Value>(&text) else {
            return out;
        };
        let mut ids = HashSet::new();
        if let Some(mods) = o.get("Mods").and_then(Value::as_array) {
            for m in mods {
                if !m.is_object() {
                    continue;
                }
                let id = java::opt_str(m, "Id", "");
                if !paths::valid_id(&id) || !ids.insert(java::lower(&id)) {
                    continue;
                }
                let id = java::lower(&id);
                let name = paths::normalize_name(Some(&java::opt_str(m, "Name", "")), Some(&fallback(&id))).unwrap_or_default();
                out.push(Record { name, enabled: java::opt_bool(m, "Enabled", true), created: java::parse_iso_utc(&java::opt_str(m, "CreatedUtc", "")), id });
            }
        }
        out
    }

    fn save(&self, records: &[Record]) {
        let _ = std::fs::create_dir_all(&self.root);
        let mods: Vec<Value> = records
            .iter()
            .map(|r| {
                let mut o = Map::new();
                o.insert("Id".into(), r.id.clone().into());
                o.insert("Name".into(), r.name.clone().into());
                o.insert("Enabled".into(), r.enabled.into());
                o.insert("CreatedUtc".into(), java::iso_utc(r.created).into());
                Value::Object(o)
            })
            .collect();
        let mut root = Map::new();
        root.insert("Version".into(), 1.into());
        root.insert("Mods".into(), Value::Array(mods));
        let _ = fs::write_atomic(&self.index(), java::to_string(&Value::Object(root), 2).as_bytes());
    }
}

fn index_of(records: &[Record], id: &str) -> Option<usize> {
    records.iter().position(|r| java::eq_ignore_case(&r.id, id))
}

pub fn files(root: &Path) -> Vec<String> {
    let mut out = Vec::new();
    if !root.is_dir() {
        return out;
    }
    let mut pending = vec![root.to_path_buf()];
    while let Some(d) = pending.pop() {
        if out.len() >= MAX_FILES {
            break;
        }
        for k in fs::list_raw(&d) {
            let Ok(meta) = std::fs::symlink_metadata(&k) else {
                continue;
            };
            if meta.file_type().is_symlink() {
                continue;
            }
            if meta.is_dir() {
                pending.push(k);
            } else if meta.is_file() {
                let Ok(rel) = k.strip_prefix(root) else {
                    continue;
                };
                let rel = rel.to_string_lossy().replace('\\', "/");
                if rel.ends_with(".lock") || rel.starts_with('.') || rel.contains("/.") {
                    continue;
                }
                out.push(rel);
            }
        }
    }
    out
}

pub fn read_pack(folder: &Path) -> Option<Value> {
    let f = folder.join(PACK_FILE);
    let meta = std::fs::metadata(&f).ok()?;
    if !meta.is_file() || meta.len() > 256 * 1024 {
        return None;
    }
    let o: Value = serde_json::from_slice(&std::fs::read(&f).ok()?).ok()?;
    if !o.is_object() {
        return None;
    }
    let pick = |a: &str, b: &str| java::opt_str(&o, a, &java::opt_str(&o, b, ""));
    Some(json!({
        "source": pick("Source", "source"),
        "id": java::opt_long(&o, "Id", java::opt_long(&o, "id", 0)),
        "name": pick("Name", "name"),
        "author": pick("Author", "author"),
        "iconUrl": pick("IconUrl", "iconUrl"),
        "profileUrl": pick("ProfileUrl", "profileUrl"),
        "category": pick("Category", "category"),
        "installKind": pick("InstallKind", "installKind"),
    }))
}

pub fn write_pack(folder: &Path, pack: &Value) {
    let s = |k: &str| Value::String(java::opt_str(pack, k, ""));
    let mut o = Map::new();
    o.insert("Source".into(), s("source"));
    o.insert("Id".into(), java::opt_long(pack, "id", 0).into());
    o.insert("Name".into(), s("name"));
    o.insert("Author".into(), s("author"));
    o.insert("IconUrl".into(), s("iconUrl"));
    o.insert("ProfileUrl".into(), s("profileUrl"));
    o.insert("Category".into(), s("category"));
    o.insert("InstallKind".into(), s("installKind"));
    o.insert("InstalledUtc".into(), java::iso_utc(java::now_ms()).into());
    let _ = std::fs::write(folder.join(PACK_FILE), java::to_string(&Value::Object(o), 2));
}
