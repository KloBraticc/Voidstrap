use super::fs;
use super::managed::{self, Managed};
use super::paths;
use crate::java;
use crate::sha256::{self, Sha256};
use serde_json::{Map, Value, json};
use std::collections::{BTreeMap, HashMap};
use std::path::{Path, PathBuf};

pub const FORMAT: i64 = 1;
pub const REMOTE_DIR: &str = "/data/local/tmp/voidstrap_mods";
pub const CACHE_LIST: &str = "/data/local/tmp/voidstrap_cache_";

pub struct Roots {
    pub mods: PathBuf,
    pub managed: Managed,
    pub state: PathBuf,
}

impl Roots {
    pub fn from(args: &Value) -> Roots {
        let s = |k: &str| java::opt_str(args, k, "");
        Roots { mods: PathBuf::from(s("mods")), managed: Managed::new(&s("managed")), state: PathBuf::from(s("state")) }
    }

    pub fn state_dir(&self) -> PathBuf {
        let _ = std::fs::create_dir_all(&self.state);
        self.state.clone()
    }
}

struct Ordered {
    items: Vec<Option<(String, PathBuf)>>,
    at: HashMap<String, usize>,
    keys: HashMap<String, String>,
}

impl Ordered {
    fn put(&mut self, rel: String, f: PathBuf) {
        let key = java::lower(&rel);
        if let Some(old) = self.keys.insert(key, rel.clone())
            && let Some(i) = self.at.remove(&old)
        {
            self.items[i] = None;
        }
        if let Some(i) = self.at.remove(&rel) {
            self.items[i] = None;
        }
        self.at.insert(rel.clone(), self.items.len());
        self.items.push(Some((rel, f)));
    }
}

pub fn collect(roots: &Roots) -> Vec<(String, PathBuf)> {
    let mut out = Ordered { items: Vec::new(), at: HashMap::new(), keys: HashMap::new() };
    for rel in managed::files(&roots.mods) {
        if paths::ignored(&rel) || paths::asset_path(&rel).is_none() {
            continue;
        }
        let f = roots.mods.join(&rel);
        out.put(rel, f);
    }
    for (_, source, rel) in roots.managed.enabled_files() {
        if paths::asset_path(&rel).is_none() {
            continue;
        }
        out.put(rel, source);
    }
    out.items.into_iter().flatten().collect()
}

fn mounts() -> Vec<Vec<String>> {
    match std::fs::read("/proc/self/mountinfo") {
        Ok(b) => String::from_utf8_lossy(&b).split('\n').map(|l| l.split(' ').map(str::to_string).collect()).collect(),
        Err(_) => Vec::new(),
    }
}

pub fn mounted_for(pkg: &str, target: &str) -> bool {
    let tail = format!("/voidstrap_mods/{pkg}.apk");
    mounts().iter().any(|f| f.len() > 4 && f[4] == target && f[3].ends_with(&tail))
}

pub fn original_file(pkg: &str) -> String {
    format!("{REMOTE_DIR}/{pkg}.orig.apk")
}

fn stamp_file(roots: &Roots, pkg: &str) -> PathBuf {
    roots.state_dir().join(format!("applied_{pkg}.json"))
}

pub fn stamp(roots: &Roots, pkg: &str) -> Option<Value> {
    let f = stamp_file(roots, pkg);
    if !f.is_file() {
        return None;
    }
    let v: Value = serde_json::from_slice(&std::fs::read(f).ok()?).ok()?;
    v.is_object().then_some(v)
}

pub fn stamp_exists(roots: &Roots, pkg: &str) -> bool {
    stamp_file(roots, pkg).is_file()
}

pub fn delete_stamp(roots: &Roots, pkg: &str) {
    let _ = std::fs::remove_file(stamp_file(roots, pkg));
}

pub fn save_stamp(roots: &Roots, pkg: &str, fingerprint: &str, target: &str, original: i64, files: i64) {
    let mut o = Map::new();
    o.insert("fingerprint".into(), fingerprint.into());
    o.insert("target".into(), target.into());
    o.insert("original".into(), original.into());
    o.insert("files".into(), files.into());
    o.insert("time".into(), java::now_ms().into());
    let _ = std::fs::write(stamp_file(roots, pkg), java::to_string(&Value::Object(o), 0));
}

pub fn original_apk(roots: &Roots, pkg: &str, target: Option<&str>) -> Option<String> {
    let target = target?;
    if !mounted_for(pkg, target) {
        return Some(target.to_string());
    }
    let o = original_file(pkg);
    let size = stamp(roots, pkg).map(|s| java::opt_long(&s, "original", -1)).unwrap_or(-1);
    let readable = std::fs::File::open(&o).is_ok();
    (readable && fs::length(Path::new(&o)) == size).then_some(o)
}

pub struct Plan {
    pub target: String,
    pub base: Option<String>,
    pub changes: BTreeMap<Key, PathBuf>,
    pub skipped: Vec<String>,
    pub added: i64,
    pub replaced: i64,
    pub fingerprint: String,
    pub log: Vec<String>,
}

#[derive(PartialEq, Eq, Clone)]
pub struct Key(pub String);

impl Ord for Key {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        java::cmp(&self.0, &other.0)
    }
}

impl PartialOrd for Key {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

pub fn plan(roots: &Roots, pkg: &str, identity: &str, target: Option<&str>) -> std::io::Result<Option<Plan>> {
    let Some(target) = target else {
        return Ok(None);
    };
    let original = original_apk(roots, pkg, Some(target));
    let dir = crate::apk::read(Path::new(original.as_deref().unwrap_or(target)))?;
    let mut lower: HashMap<String, String> = HashMap::new();
    for e in &dir.entries {
        lower.insert(java::lower(&e.name), e.name.clone());
    }
    let mut md = Sha256::new();
    md.update(format!("v{FORMAT}\n{pkg}\n{identity}\n").as_bytes());
    let mut plan = Plan {
        target: target.to_string(),
        base: original,
        changes: BTreeMap::new(),
        skipped: Vec::new(),
        added: 0,
        replaced: 0,
        fingerprint: String::new(),
        log: Vec::new(),
    };
    let mut unknown = Vec::new();
    for (rel, f) in collect(roots) {
        let Some(asset) = paths::asset_path(&rel) else {
            plan.skipped.push(rel);
            continue;
        };
        match lower.get(&java::lower(&asset)) {
            Some(existing) => {
                plan.changes.insert(Key(existing.clone()), f);
                plan.replaced += 1;
            }
            None => {
                plan.changes.insert(Key(asset), f);
                plan.added += 1;
                if unknown.len() < 10 {
                    unknown.push(rel);
                }
            }
        }
    }
    for (name, f) in &plan.changes {
        md.update(format!("{}\t{}\t{}\t{}\n", name.0, f.display(), fs::length(f), fs::modified_ms(f)).as_bytes());
    }
    plan.fingerprint = sha256::hex(&md.finish());
    plan.log.push(format!("plan for {pkg}: replaces {}, adds {}, unmapped {}", plan.replaced, plan.added, plan.skipped.len()));
    if !unknown.is_empty() {
        plan.log.push(format!("added, replacing nothing in Roblox, so only useful if something references them: [{}]", unknown.join(", ")));
    }
    Ok(Some(plan))
}

impl Plan {
    pub fn to_json(&self) -> Value {
        let changes: Vec<Value> = self.changes.iter().map(|(k, f)| json!([k.0, f.to_string_lossy()])).collect();
        json!({
            "target": self.target,
            "base": self.base,
            "pristine": self.base.is_some(),
            "changes": changes,
            "skipped": self.skipped,
            "added": self.added,
            "replaced": self.replaced,
            "fingerprint": self.fingerprint,
            "log": self.log,
        })
    }
}

pub fn quote(s: &str) -> String {
    format!("'{}'", s.replace('\'', "'\\''"))
}

pub fn safe_pkg(pkg: &str) -> bool {
    !pkg.is_empty() && pkg.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'.')
}

fn common(pkg: &str, target: &str) -> String {
    format!(
        "P={}\nT={}\nR={REMOTE_DIR}\nD=$R/$P.apk\nO=$R/$P.orig.apk\nNS=''\n\
if nsenter -t 1 -m -- true 2>/dev/null; then NS='nsenter -t 1 -m --'; fi\n\
ours() {{ grep \" $T \" /proc/1/mountinfo | grep -q \"/voidstrap_mods/$P.apk \"; }}\n\
any() {{ grep -q \" $T \" /proc/1/mountinfo; }}\n\
unmount_all() {{\n\
  M=$(cat /proc/1/mountinfo)\n\
  echo \"$M\" | while read -r a b c r m rest; do\n\
    case \"$r\" in */voidstrap_mods/\"$P\".apk) $NS umount -l \"$m\" 2>/dev/null;; esac\n\
    if [ \"$m\" = \"$T\" ] || [ \"$m\" = \"$O\" ]; then $NS umount -l \"$m\" 2>/dev/null; fi\n\
  done\n\
}}\n\
stop() {{ if [ \"$K\" = 1 ] && [ -n \"$(pidof $P)\" ]; then am force-stop $P; fi; }}\n",
        quote(pkg),
        quote(target)
    )
}

pub fn script(pkg: &str, target: &str, build: Option<&str>, restart: bool) -> String {
    let mut sb = common(pkg, target);
    sb.push_str(&format!("K={}\n", if restart { 1 } else { 0 }));
    sb.push_str("[ -e \"$T\" ] || exit 4\n");
    match build {
        Some(b) => {
            sb.push_str(&format!("B={}\n", quote(b)));
            sb.push_str("[ -f \"$B\" ] || exit 2\n");
            sb.push_str("mkdir -p $R && chmod 755 $R || exit 2\n");
            sb.push_str("unmount_all\n");
            sb.push_str("rm -f \"$D\"\n");
            sb.push_str("mv -f \"$B\" \"$D\" 2>/dev/null || { cp -f \"$B\" \"$D\" && rm -f \"$B\"; } || exit 2\n");
        }
        None => {
            sb.push_str("[ -f \"$D\" ] || exit 3\n");
            sb.push_str("if ours; then exit 0; fi\n");
            sb.push_str("unmount_all\n");
        }
    }
    sb.push_str("rm -f \"$O\"\n");
    sb.push_str("if ! ln \"$T\" \"$O\" 2>/dev/null; then\n");
    sb.push_str("  touch \"$O\" && chmod 644 \"$O\" || exit 2\n");
    sb.push_str("  $NS mount -o bind \"$T\" \"$O\" || exit 2\n");
    sb.push_str("  $NS mount -o private none \"$O\" 2>/dev/null\n");
    sb.push_str("fi\n");
    sb.push_str("chown 1000:1000 \"$D\"; chmod 644 \"$D\" || exit 2\n");
    sb.push_str("chcon u:object_r:apk_data_file:s0 \"$D\" || exit 2\n");
    sb.push_str("$NS mount -o bind \"$D\" \"$T\" || exit 2\n");
    sb.push_str("ours || exit 5\n");
    sb.push_str("stop\n");
    sb.push_str("exit 0\n");
    sb
}

pub fn remove_script(pkg: &str, target: &str, restart: bool) -> String {
    format!(
        "{}K={}\nW=0\nif [ -n \"$T\" ] && any; then W=1; fi\nunmount_all\nrm -f \"$D\" \"$O\"\nif [ $W = 1 ]; then stop; fi\nexit 0\n",
        common(pkg, target),
        if restart { 1 } else { 0 }
    )
}

pub fn sky_kind(path: &Path) -> &'static str {
    let len = fs::length(path);
    if !(8..=64 * 1024 * 1024).contains(&len) {
        return "reject";
    }
    let mut head = [0u8; 4];
    let ok = std::fs::File::open(path).and_then(|mut f| std::io::Read::read_exact(&mut f, &mut head)).is_ok();
    if !ok {
        return "skip";
    }
    if &head == b"DDS " || head == [0x89, b'P', b'N', b'G'] || head == [0xAB, b'K', b'T', b'X'] {
        "keep"
    } else {
        "convert"
    }
}
