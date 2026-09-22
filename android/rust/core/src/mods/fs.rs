use crate::java;
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

pub fn list_raw(dir: &Path) -> Vec<PathBuf> {
    match fs::read_dir(dir) {
        Ok(it) => it.flatten().map(|e| e.path()).collect(),
        Err(_) => Vec::new(),
    }
}

pub fn name_of(p: &Path) -> String {
    p.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default()
}

pub fn list(dir: &Path) -> Vec<PathBuf> {
    let mut files = list_raw(dir);
    files.sort_by(|a, b| match (a.is_dir(), b.is_dir()) {
        (true, false) => std::cmp::Ordering::Less,
        (false, true) => std::cmp::Ordering::Greater,
        _ => java::cmp_ignore_case(&name_of(a), &name_of(b)),
    });
    files.retain(|f| !name_of(f).starts_with('.'));
    files
}

pub fn delete(f: &Path) -> bool {
    let Ok(meta) = fs::symlink_metadata(f) else {
        return false;
    };
    if meta.is_dir() {
        for k in list_raw(f) {
            delete(&k);
        }
        fs::remove_dir(f).is_ok()
    } else {
        fs::remove_file(f).is_ok()
    }
}

pub fn count(f: &Path) -> i64 {
    if !f.is_dir() {
        return 1;
    }
    list_raw(f).iter().map(|k| count(k)).sum()
}

pub fn size(f: &Path) -> i64 {
    if !f.is_dir() {
        return fs::metadata(f).map(|m| m.len() as i64).unwrap_or(0);
    }
    list_raw(f).iter().map(|k| size(k)).sum()
}

pub fn length(f: &Path) -> i64 {
    fs::metadata(f).map(|m| if m.is_file() { m.len() as i64 } else { 0 }).unwrap_or(0)
}

pub fn modified_ms(f: &Path) -> i64 {
    fs::metadata(f)
        .and_then(|m| m.modified())
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

pub fn canonical(p: &Path) -> PathBuf {
    if let Ok(c) = fs::canonicalize(p) {
        return c;
    }
    let mut missing = Vec::new();
    let mut cur = p.to_path_buf();
    loop {
        if let Ok(c) = fs::canonicalize(&cur) {
            let mut out = c;
            for m in missing.iter().rev() {
                if m == ".." {
                    out.pop();
                } else if m != "." {
                    out.push(m);
                }
            }
            return out;
        }
        match (cur.file_name().map(|s| s.to_os_string()), cur.parent()) {
            (Some(name), Some(parent)) => {
                missing.push(name);
                cur = parent.to_path_buf();
            }
            _ => return p.to_path_buf(),
        }
    }
}

pub fn inside(root: &Path, f: &Path) -> bool {
    let r = canonical(root);
    let p = canonical(f);
    p.starts_with(&r)
}

pub fn strictly_inside(root: &Path, f: &Path) -> bool {
    let r = canonical(root);
    let p = canonical(f);
    p != r && p.starts_with(&r)
}

pub fn read_atomic(f: &Path) -> io::Result<Vec<u8>> {
    let bak = PathBuf::from(format!("{}.bak", f.display()));
    if bak.exists() {
        let _ = fs::remove_file(f);
        let _ = fs::rename(&bak, f);
    }
    fs::read(f)
}

pub fn write_atomic(f: &Path, data: &[u8]) -> io::Result<()> {
    let tmp = PathBuf::from(format!("{}.new", f.display()));
    let result = (|| {
        let mut out = fs::File::create(&tmp)?;
        out.write_all(data)?;
        out.sync_all()?;
        fs::rename(&tmp, f)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result
}

pub fn same(f: &Path, data: &[u8]) -> bool {
    match fs::metadata(f) {
        Ok(m) if m.is_file() && m.len() == data.len() as u64 => fs::read(f).map(|d| d == data).unwrap_or(false),
        _ => false,
    }
}

pub fn write(f: &Path, data: &[u8]) -> io::Result<()> {
    if same(f, data) {
        return Ok(());
    }
    if let Some(parent) = f.parent()
        && !parent.is_dir()
    {
        fs::create_dir_all(parent).map_err(|_| io::Error::other("mkdir"))?;
    }
    let tmp = PathBuf::from(format!("{}.vs{}", f.display(), java::now_nanos()));
    let written = (|| {
        let mut out = fs::File::create(&tmp)?;
        out.write_all(data)?;
        out.sync_all()
    })();
    if let Err(e) = written {
        let _ = fs::remove_file(&tmp);
        return Err(e);
    }
    if fs::rename(&tmp, f).is_err() {
        let _ = fs::remove_file(&tmp);
        return Err(io::Error::other("move"));
    }
    Ok(())
}

pub fn read_all(f: &Path) -> io::Result<Vec<u8>> {
    let mut out = Vec::new();
    fs::File::open(f)?.read_to_end(&mut out)?;
    Ok(out)
}

pub fn copy(from: &Path, to: &Path) -> io::Result<()> {
    write(to, &read_all(from)?)
}

pub fn delete_and_prune(root: &Path, f: &Path) {
    let _ = fs::remove_file(f).or_else(|_| fs::remove_dir(f));
    let mut d = f.parent().map(Path::to_path_buf);
    while let Some(dir) = d {
        if dir == root || !inside(root, &dir) {
            break;
        }
        let empty = fs::read_dir(&dir).map(|mut it| it.next().is_none()).unwrap_or(false);
        if !empty || fs::remove_dir(&dir).is_err() {
            break;
        }
        d = dir.parent().map(Path::to_path_buf);
    }
}

pub fn unique(dir: &Path, name: &str) -> PathBuf {
    let f = dir.join(name);
    if !f.exists() {
        return f;
    }
    let (base, ext) = match name.rfind('.') {
        Some(dot) if dot > 0 => (&name[..dot], &name[dot..]),
        _ => (name, ""),
    };
    for i in 2..10000 {
        let f = dir.join(format!("{base} ({i}){ext}"));
        if !f.exists() {
            return f;
        }
    }
    dir.join(format!("{base} {}{ext}", java::now_ms()))
}
