use super::presets::Presets;
use super::variants;
use super::{fs, managed, paths, placer};
use crate::apk;
use std::collections::HashMap;
use std::io::{self, Read};
use std::path::Path;
use std::sync::Mutex;

const NOGUI_STATE: &str = "NoGuiFiles";
const MAX_SIDE: u32 = 4096;
const BLANK_ROOTS: [&str; 4] = [
    "content/textures/ui/",
    "ExtraContent/textures/ui/ImageSet/",
    "ExtraContent/textures/ui/LuaChat/",
    "ExtraContent/LuaPackages/Packages/_Index/FoundationImages/FoundationImages/SpriteSheets/",
];
const BLANK_KEEP: [&str; 2] = ["content/textures/ui/Controls/", "content/textures/ui/Input/"];
const BLANK_FONT_DIR: &str = "ExtraContent/LuaPackages/Packages/_Index/BuilderIcons/BuilderIcons/Font/";
const BLANK_FONT_ASSET: &str = "BlankIcons.ttf";

pub static LOCK: Mutex<()> = Mutex::new(());

fn target(entry: &str) -> Option<String> {
    let lower = entry.to_lowercase();
    let rel = paths::desktop_path(entry)?;
    let wanted = if rel.starts_with(BLANK_FONT_DIR) {
        lower.ends_with(".ttf") || lower.ends_with(".otf")
    } else if lower.ends_with(".png") {
        BLANK_ROOTS.iter().any(|p| rel.starts_with(p)) && !BLANK_KEEP.iter().any(|p| rel.starts_with(p))
    } else {
        false
    };
    (wanted && !paths::ignored(&rel)).then_some(rel)
}

pub fn read_state(p: &Presets, name: &str) -> String {
    std::fs::read(p.state(name)).map(|b| String::from_utf8_lossy(&b).into_owned()).unwrap_or_default()
}

pub fn write_state(p: &Presets, name: &str, value: &str) -> io::Result<()> {
    let f = p.state(name);
    if value.is_empty() {
        let _ = std::fs::remove_file(f);
        return Ok(());
    }
    fs::write(&f, value.as_bytes())
}

fn nogui_state(p: &Presets) -> Vec<(String, i64)> {
    let mut out: Vec<(String, i64)> = Vec::new();
    for line in read_state(p, NOGUI_STATE).split('\n') {
        let Some(tab) = line.find('\t') else {
            continue;
        };
        if tab == 0 || tab + 1 >= line.len() {
            continue;
        }
        if let Ok(n) = line[..tab].parse::<i64>() {
            let key = line[tab + 1..].to_string();
            match out.iter_mut().find(|(k, _)| *k == key) {
                Some(e) => e.1 = n,
                None => out.push((key, n)),
            }
        }
    }
    out
}

pub fn hide_core_gui(p: &Presets) -> bool {
    let blanked = nogui_state(p);
    !blanked.is_empty() && blanked.iter().all(|(rel, len)| p.ws(rel).is_file() && fs::length(&p.ws(rel)) == *len)
}

fn clear(p: &Presets) -> io::Result<()> {
    for (rel, len) in nogui_state(p) {
        let f = p.ws(&rel);
        if f.is_file() && fs::length(&f) == len {
            fs::delete_and_prune(&p.mods, &f);
        }
    }
    write_state(p, NOGUI_STATE, "")
}

fn entry_head(base: &Path, e: &apk::Entry) -> Option<[u8; 24]> {
    let mut f = std::fs::File::open(base).ok()?;
    let mut header = [0u8; 30];
    std::io::Seek::seek(&mut f, std::io::SeekFrom::Start(e.local_offset)).ok()?;
    f.read_exact(&mut header).ok()?;
    let data = e.local_offset + 30 + u16::from_le_bytes([header[26], header[27]]) as u64 + u16::from_le_bytes([header[28], header[29]]) as u64;
    std::io::Seek::seek(&mut f, std::io::SeekFrom::Start(data)).ok()?;
    let mut head = [0u8; 24];
    if e.method == 0 {
        f.read_exact(&mut head).ok()?;
        return Some(head);
    }
    if e.method != 8 {
        return None;
    }
    let mut raw = vec![0u8; (e.compressed.min(4096)) as usize];
    let n = f.read(&mut raw).ok()?;
    let mut state = miniz_oxide::inflate::stream::InflateState::new_boxed(miniz_oxide::DataFormat::Raw);
    let r = miniz_oxide::inflate::stream::inflate(&mut state, &raw[..n], &mut head, miniz_oxide::MZFlush::None);
    (r.bytes_written == 24).then_some(head)
}

pub fn set_hide_core_gui(p: &Presets, on: bool, base: Option<&Path>, cancel: &mut dyn FnMut() -> bool) -> io::Result<()> {
    let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
    clear(p)?;
    if !on {
        return Ok(());
    }
    let base = base.ok_or_else(|| io::Error::other("Roblox is not installed, so its interface images could not be read."))?;
    let font = p.resource(BLANK_FONT_ASSET)?;
    let dir = apk::read(base)?;
    let mut blanks: HashMap<(u32, u32), Vec<u8>> = HashMap::new();
    let mut state = String::new();
    let mut written = 0;
    for e in &dir.entries {
        if cancel() {
            return Err(io::Error::other("cancelled"));
        }
        let Some(rel) = target(&e.name) else {
            continue;
        };
        let data = if rel.starts_with(BLANK_FONT_DIR) {
            font.clone()
        } else {
            let Some(head) = entry_head(base, e) else {
                continue;
            };
            let Some((w, h)) = super::presets::png_size(&head).filter(|&(w, h)| w <= MAX_SIDE && h <= MAX_SIDE) else {
                continue;
            };
            match blanks.get(&(w, h)) {
                Some(b) => b.clone(),
                None => {
                    let Some(b) = crate::zipw::blank_png(w, h) else {
                        continue;
                    };
                    blanks.insert((w, h), b.clone());
                    b
                }
            }
        };
        fs::write(&p.ws(&rel), &data)?;
        state.push_str(&format!("{}\t{rel}\n", data.len()));
        written += 1;
    }
    if written == 0 {
        return Err(io::Error::other("No interface images were found in the Roblox app."));
    }
    write_state(p, NOGUI_STATE, &state)
}

fn drop_empty(dir: &Path) -> bool {
    let Ok(kids) = std::fs::read_dir(dir) else {
        return false;
    };
    let mut empty = true;
    for k in kids.flatten() {
        let path = k.path();
        if path.is_dir() && drop_empty(&path) {
            continue;
        }
        empty = false;
    }
    empty && std::fs::remove_dir(dir).is_ok()
}

fn prune(root: &Path, client: &placer::Index, slots: Option<&placer::Source>) {
    if !root.is_dir() {
        return;
    }
    for rel in managed::files(root) {
        let target = match slots {
            None => Some(rel.clone()),
            Some(src) => variants::resolve_slot(src, &rel),
        };
        if let Some(t) = target
            && client.exists(&t)
        {
            continue;
        }
        let _ = std::fs::remove_file(root.join(&rel));
    }
    drop_empty(root);
}

pub fn trim(folder: &Path, sources: &Path, src: &placer::Source) {
    let client = src.index();
    if client.files.is_empty() {
        return;
    }
    prune(folder, &client, None);
    prune(sources, &client, Some(src));
}
