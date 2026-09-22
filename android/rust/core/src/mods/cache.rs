use super::engine::{self, CACHE_LIST, Roots};
use super::extract::BACKUP;
use super::{fs, paths};
use crate::java;
use serde_json::{Map, Value};
use std::path::{Path, PathBuf};

pub fn is_cache_mod(folder: &Path) -> bool {
    std::fs::read_dir(folder.join(BACKUP)).map(|mut it| it.next().is_some()).unwrap_or(false)
}

pub fn wanted(roots: &Roots) -> Vec<(String, PathBuf)> {
    let mut out: Vec<(String, PathBuf)> = Vec::new();
    for r in roots.managed.load() {
        if !r.enabled {
            continue;
        }
        let Ok(folder) = roots.managed.folder(&r.id) else {
            continue;
        };
        for f in fs::list_raw(&folder.join(BACKUP)) {
            let n = fs::name_of(&f);
            let Some(hash) = n.strip_suffix(".mod.lock") else {
                continue;
            };
            if paths::lower_hex32(hash) && !out.iter().any(|(h, _)| h == hash) {
                out.push((hash.to_string(), f));
            }
        }
    }
    out
}

fn state_file(roots: &Roots, pkg: &str) -> PathBuf {
    roots.state_dir().join(format!("cache_{pkg}.json"))
}

fn installed(roots: &Roots, pkg: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let Ok(text) = std::fs::read(state_file(roots, pkg)) else {
        return out;
    };
    let Ok(v) = serde_json::from_slice::<Value>(&text) else {
        return out;
    };
    if let Some(a) = v.get("installed").and_then(Value::as_array) {
        for h in a {
            if h.is_null() {
                return out;
            }
            let Some(h) = h.as_str() else {
                continue;
            };
            if paths::lower_hex32(h) && !out.iter().any(|x| x == h) {
                out.push(h.to_string());
            }
        }
    }
    out
}

fn save_installed(roots: &Roots, pkg: &str, hashes: &[String]) {
    let mut o = Map::new();
    o.insert("installed".into(), Value::Array(hashes.iter().map(|h| Value::String(h.clone())).collect()));
    let _ = std::fs::write(state_file(roots, pkg), java::to_string(&Value::Object(o), 0));
}

pub fn pending(roots: &Roots, pkg: &str, enabled: bool) -> bool {
    let want: Vec<String> = if enabled { wanted(roots).into_iter().map(|(h, _)| h).collect() } else { Vec::new() };
    let have = installed(roots, pkg);
    want.len() != have.len() || want.iter().any(|h| !have.contains(h))
}

pub fn sync_script(roots: &Roots, pkg: &str, enabled: bool, restore_all: bool) -> Option<(String, Vec<String>)> {
    if !engine::safe_pkg(pkg) {
        return None;
    }
    let want = if enabled && !restore_all { wanted(roots) } else { Vec::new() };
    let have = installed(roots, pkg);
    if want.is_empty() && have.is_empty() {
        return None;
    }
    let originals = roots.state_dir().join("cache_originals").join(pkg);
    if !originals.is_dir() {
        let _ = std::fs::create_dir_all(&originals);
    }
    let mut sb = String::new();
    sb.push_str(&format!("S=/data/data/{pkg}/cache/rbx-storage\n"));
    sb.push_str(&format!("[ -d /data/data/{pkg} ] || exit 4\n"));
    sb.push_str("mkdir -p \"$S\"\n");
    sb.push_str("O=$(stat -c %u:%g \"$S\")\n");
    sb.push_str("X=$(stat -c %C \"$S\")\n");
    sb.push_str(&format!("B={}\n", engine::quote(&originals.to_string_lossy())));
    sb.push_str("BO=$(stat -c %u:%g \"$B\")\n");
    sb.push_str("BX=$(stat -c %C \"$B\")\n");
    sb.push_str("put() { d=\"$S/${2%${2#??}}\"; mkdir -p \"$d\"; chown \"$O\" \"$d\"; chmod 700 \"$d\"; chcon \"$X\" \"$d\" 2>/dev/null; t=\"$d/$2\"; if [ -f \"$t\" ] && [ ! -f \"$B/$2\" ]; then cp -f \"$t\" \"$B/$2\" && chown \"$BO\" \"$B/$2\" && chcon \"$BX\" \"$B/$2\" 2>/dev/null; fi; cp -f \"$1\" \"$t\" || return 1; chown \"$O\" \"$t\"; chmod 600 \"$t\"; chcon \"$X\" \"$t\" 2>/dev/null; }\n");
    sb.push_str("back() { t=\"$S/${1%${1#??}}/$1\"; if [ -f \"$B/$1\" ]; then cp -f \"$B/$1\" \"$t\" && chown \"$O\" \"$t\" && chmod 600 \"$t\" && chcon \"$X\" \"$t\" 2>/dev/null; rm -f \"$B/$1\"; else rm -f \"$t\"; fi; }\n");
    sb.push_str("R=0\n");
    for (h, f) in &want {
        sb.push_str(&format!("put {} {h} || R=2\n", engine::quote(&f.to_string_lossy())));
    }
    for h in &have {
        if !want.iter().any(|(w, _)| w == h) {
            sb.push_str(&format!("back {h}\n"));
        }
    }
    let list = format!("{CACHE_LIST}{pkg}");
    if want.is_empty() {
        sb.push_str(&format!("rm -f {list}\n"));
    } else {
        let keys: Vec<&str> = want.iter().map(|(h, _)| h.as_str()).collect();
        sb.push_str(&format!("printf '%s\\n' {} > {list}\n", keys.join(" ")));
    }
    sb.push_str("exit $R\n");
    Some((sb, want.into_iter().map(|(h, _)| h).collect()))
}

pub fn synced(roots: &Roots, pkg: &str, hashes: &[String]) {
    save_installed(roots, pkg, hashes);
}
