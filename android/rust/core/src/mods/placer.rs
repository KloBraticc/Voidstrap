use super::paths;
use crate::java;
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::{Arc, Mutex};

const ROOTS: &[&str] = &["content", "extracontent", "platformcontent", "shaders"];
const ANCHORS: &[(&str, &str)] = &[
    ("keyboardmouse", "content/textures/Cursors/KeyboardMouse"),
    ("dragdetector", "content/textures/Cursors/DragDetector"),
    ("cursors", "content/textures/Cursors"),
    ("families", "content/fonts/families"),
    ("particles", "content/textures/particles"),
    ("sky", "PlatformContent/pc/textures/sky"),
    ("textures", "content/textures"),
    ("sounds", "content/sounds"),
    ("fonts", "content/fonts"),
    ("music", "content/music"),
    ("models", "content/models"),
    ("avatar", "content/avatar"),
];
pub const SKY_FACES: [&str; 6] = ["sky512_bk.tex", "sky512_dn.tex", "sky512_ft.tex", "sky512_lf.tex", "sky512_rt.tex", "sky512_up.tex"];

fn known(name: &str) -> Option<&'static str> {
    match name {
        "arrowcursor.png" | "arrowfarcursor.png" | "ibeamcursor.png" | "arrowcursordecaldrag.png" => Some("content/textures/Cursors/KeyboardMouse"),
        "activatedcursor.png" | "hovercursor.png" => Some("content/textures/Cursors/DragDetector"),
        "mouselockedcursor.png" => Some("content/textures"),
        "oof.ogg" | "ouch.ogg" => Some("content/sounds"),
        n if SKY_FACES.contains(&n) => Some("PlatformContent/pc/textures/sky"),
        _ => None,
    }
}

#[derive(Default)]
pub struct Index {
    key: String,
    pub files: HashSet<String>,
    unique: HashMap<String, Option<String>>,
}

static INDEX: Mutex<Option<Arc<Index>>> = Mutex::new(None);

pub struct Source {
    pub key: String,
    pub apk: Option<String>,
}

impl Source {
    pub fn index(&self) -> Arc<Index> {
        let mut slot = INDEX.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(i) = slot.as_ref()
            && i.key == self.key
        {
            return i.clone();
        }
        let mut idx = Index { key: self.key.clone(), ..Default::default() };
        if let Some(apk) = &self.apk
            && let Ok(dir) = crate::apk::read(Path::new(apk))
        {
            for e in &dir.entries {
                let Some(desktop) = paths::desktop_path(&e.name) else {
                    continue;
                };
                idx.files.insert(java::lower(&desktop));
                let file = java::lower(&desktop[desktop.rfind('/').map_or(0, |i| i + 1)..]);
                let value = if idx.unique.contains_key(&file) { None } else { Some(desktop.clone()) };
                idx.unique.insert(file, value);
            }
        }
        let idx = Arc::new(idx);
        *slot = Some(idx.clone());
        idx
    }
}

impl Index {
    pub fn exists(&self, desktop: &str) -> bool {
        self.files.contains(&java::lower(desktop))
    }
}

fn canonical_root(path: &str) -> String {
    let Some(slash) = path.find('/').filter(|&s| s > 0) else {
        return path.to_string();
    };
    let rest = &path[slash + 1..];
    match java::lower(&path[..slash]).as_str() {
        "content" => format!("content/{rest}"),
        "extracontent" => format!("ExtraContent/{rest}"),
        "platformcontent" => format!("PlatformContent/{rest}"),
        _ => path.to_string(),
    }
}

pub fn resolve(src: &Source, relative: &str) -> Option<String> {
    let n = paths::arch_normalize(relative);
    let seg: Vec<&str> = n.split('/').filter(|p| !p.is_empty()).collect();
    if seg.is_empty() {
        return None;
    }
    for i in 0..seg.len() - 1 {
        if ROOTS.contains(&java::lower(seg[i]).as_str()) {
            return Some(canonical_root(&seg[i..].join("/")));
        }
    }
    let mut candidate = None;
    for i in (0..seg.len() - 1).rev() {
        let l = java::lower(seg[i]);
        if let Some((_, anchor)) = ANCHORS.iter().find(|(k, _)| *k == l) {
            candidate = Some(format!("{anchor}/{}", seg[i + 1..].join("/")));
            break;
        }
    }
    let name = seg[seg.len() - 1];
    let lname = java::lower(name);
    if candidate.is_none()
        && let Some(folder) = known(&lname)
    {
        candidate = Some(format!("{folder}/{name}"));
    }
    if candidate.is_none() && lname.starts_with("img_set_") {
        candidate = Some(format!("ExtraContent/LuaPackages/Packages/_Index/FoundationImages/FoundationImages/SpriteSheets/{name}"));
    }
    let idx = src.index();
    if let Some(c) = &candidate
        && idx.exists(c)
    {
        return candidate;
    }
    if let Some(Some(unique)) = idx.unique.get(&lname) {
        return Some(unique.clone());
    }
    candidate
}

pub fn client_copy(src: &Source, relative: &str) -> bool {
    match resolve(src, relative) {
        Some(r) => paths::extension(&r) == paths::extension(relative) && src.index().exists(&r),
        None => false,
    }
}
