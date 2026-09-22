use super::extract::SLOT_EXTENSIONS;
use super::managed::{self, Managed};
use super::{fs, paths, placer};
use crate::java;
use crate::sha256;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

const INSTALLED: &str = "Installed";

fn ws(c: char) -> bool {
    java::regex_space(c)
}

fn digits(s: &str) -> bool {
    !s.is_empty() && s.chars().all(|c| java::digit(c).is_some())
}

fn suffix(rest: &str) -> bool {
    let a = rest.trim_start_matches(ws);
    if let Some(inner) = a.strip_prefix('(').and_then(|x| x.strip_suffix(')'))
        && digits(inner)
    {
        return true;
    }
    if a.len() < rest.len() {
        let l = a.to_ascii_lowercase();
        if let Some(after) = l.strip_prefix("copy") {
            let t = after.trim_start_matches(ws);
            if after.is_empty() || digits(t) {
                return true;
            }
        }
    }
    let b = rest.trim_start_matches(|c: char| ws(c) || c == '_' || c == '-');
    if b.len() == rest.len() {
        return false;
    }
    let l = b.to_ascii_lowercase();
    let v = l.strip_prefix('v').unwrap_or(&l);
    if digits(v) && v.chars().count() <= 2 {
        return true;
    }
    ["alt", "variant", "version"].iter().any(|w| l.strip_prefix(w).is_some_and(|d| d.is_empty() || digits(d)))
}

fn base(stem: &str) -> Option<&str> {
    for (i, c) in stem.char_indices().skip(1) {
        let head = &stem[..i];
        if suffix(&stem[i..]) {
            return Some(head);
        }
        if java::line_terminator(c) {
            return None;
        }
    }
    None
}

pub fn resolve_slot(src: &placer::Source, relative: &str) -> Option<String> {
    let ext = paths::extension(relative);
    if !SLOT_EXTENSIONS.contains(&ext.as_str()) {
        return None;
    }
    if let Some(direct) = placer::resolve(src, relative) {
        return Some(direct);
    }
    let file = &relative[relative.rfind('/').map_or(0, |i| i + 1)..];
    let stem = file.rfind('.').map_or(file, |d| &file[..d]);
    if stem.starts_with(java::line_terminator) {
        return None;
    }
    let b = base(stem)?;
    let dir = relative.rfind('/').map_or("", |i| &relative[..i + 1]);
    placer::resolve(src, &format!("{dir}{}.{ext}", java::trim(b)))
}

fn hash(f: &Path) -> String {
    sha256::file(f).map(|h| sha256::hex_upper(&h)).unwrap_or_default()
}

fn installed_copy(src: &Path, f: &Path) -> bool {
    f.starts_with(src.join(INSTALLED)) && f != src.join(INSTALLED)
}

pub fn has_slots(managed: &Managed, id: &str) -> bool {
    let s = managed.sources(id);
    if managed::files(&s).iter().any(|rel| !rel.starts_with("Installed/")) {
        return true;
    }
    let Ok(folder) = managed.folder(id) else {
        return false;
    };
    managed::files(&folder).iter().any(|rel| SLOT_EXTENSIONS.contains(&paths::extension(rel).as_str()))
}

fn capture_installed(module: &Path, src: &Path) {
    let mut known: Vec<String> = managed::files(src).iter().map(|rel| hash(&src.join(rel))).collect();
    for rel in managed::files(module) {
        if !SLOT_EXTENSIONS.contains(&paths::extension(&rel).as_str()) {
            continue;
        }
        let h = hash(&module.join(&rel));
        if h.is_empty() || known.contains(&h) {
            continue;
        }
        if fs::copy(&module.join(&rel), &src.join(INSTALLED).join(&rel)).is_ok() {
            known.push(h);
        }
    }
}

pub fn build(managed: &Managed, id: &str, src_index: &placer::Source) -> std::io::Result<Vec<Value>> {
    let module = managed.folder(id)?;
    let src = managed.sources(id);
    capture_installed(&module, &src);
    let mut groups: Vec<(String, Vec<PathBuf>)> = Vec::new();
    for rel in managed::files(&src) {
        let slot = match rel.strip_prefix("Installed/") {
            Some(r) => Some(r.to_string()),
            None => resolve_slot(src_index, &rel),
        };
        let Some(slot) = slot.filter(|s| SLOT_EXTENSIONS.contains(&paths::extension(s).as_str())) else {
            continue;
        };
        match groups.iter_mut().find(|(k, _)| java::eq_ignore_case(k, &slot)) {
            Some((_, files)) => files.push(src.join(&rel)),
            None => groups.push((slot, vec![src.join(&rel)])),
        }
    }
    groups.sort_by(|a, b| java::cmp_ignore_case(&a.0, &b.0));
    let mut out = Vec::new();
    for (target, files) in groups {
        let mut labels: Vec<Option<String>> = vec![None];
        let mut sources: Vec<Option<PathBuf>> = vec![None];
        let mut seen: HashMap<String, PathBuf> = HashMap::new();
        let mut ordered = files;
        ordered.sort_by(|a, b| {
            let ia = installed_copy(&src, a) as i32;
            let ib = installed_copy(&src, b) as i32;
            ia.cmp(&ib).then_with(|| java::cmp_ignore_case(&fs::name_of(a), &fs::name_of(b)))
        });
        for f in ordered {
            let h = hash(&f);
            if h.is_empty() || seen.contains_key(&h) {
                continue;
            }
            seen.insert(h, f.clone());
            let mut label = if installed_copy(&src, &f) { String::new() } else { fs::name_of(&f) };
            if labels.iter().any(|l| l.as_deref() == Some(label.as_str())) {
                let parent = f.parent().map(fs::name_of).unwrap_or_default();
                label = format!("{label} ({parent})");
            }
            labels.push(Some(label));
            sources.push(Some(f));
        }
        let mut selected = 0;
        let installed = module.join(&target);
        if installed.is_file()
            && let Some(m) = seen.get(&hash(&installed))
        {
            for (i, s) in sources.iter().enumerate() {
                if s.as_ref() == Some(m) {
                    selected = i;
                }
            }
        }
        let options: Vec<Value> = labels.into_iter().zip(sources).map(|(l, s)| json!({"label": l, "source": s.map(|p| p.to_string_lossy().into_owned())})).collect();
        out.push(json!({"target": target, "options": options, "selected": selected}));
    }
    Ok(out)
}

pub fn apply(managed: &Managed, id: &str, slots: &[Value]) -> std::io::Result<i64> {
    let module = managed.folder(id)?;
    let mut changed = 0;
    for s in slots {
        let target = module.join(java::opt_str(s, "target", ""));
        if !fs::inside(&module, &target) {
            continue;
        }
        match s.get("source").and_then(Value::as_str) {
            None => {
                if target.is_file() && std::fs::remove_file(&target).is_ok() {
                    changed += 1;
                }
            }
            Some(source) => {
                let source = Path::new(source);
                if target.is_file() && hash(&target) == hash(source) {
                    continue;
                }
                if fs::copy(source, &target).is_ok() {
                    changed += 1;
                }
            }
        }
    }
    Ok(changed)
}
