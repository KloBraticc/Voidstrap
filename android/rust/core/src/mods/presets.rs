use super::{fs, managed, paths, placer};
use crate::apk;
use crate::java;
use crate::sha256;
use serde_json::Value;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub const DEATH: &str = "content/sounds/oof.ogg";
pub const FONT: &str = "content/fonts/CustomFont.ttf";
pub const FAMILIES: &str = "content/fonts/families";
pub const SKY: &str = "PlatformContent/pc/textures/sky";
pub const CURSOR_DIR: &str = "content/textures/Cursors/KeyboardMouse";
pub const SHIFTLOCK: &str = "content/textures/MouseLockedCursor.png";
pub const FONT_ASSET: &str = "rbxasset://fonts/CustomFont.ttf";
pub const CUSTOM_SKY: &str = "Voidstrap Custom";
pub const CURSOR_DEFAULT: i64 = 0;
pub const CURSOR_CUSTOM: i64 = 10;
pub const SLOT_FILES: [&str; 4] = ["ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png", "ArrowCursorDecalDrag.png"];
pub const SLOT_DRAG: usize = 3;
pub const SLOT_SHIFTLOCK: usize = 4;
const MAX_FONT: u64 = 32 * 1024 * 1024;
const MAX_IMAGE: u32 = 4096;

static LOCK: Mutex<()> = Mutex::new(());

pub fn cursor_folder(style: i64) -> Option<&'static str> {
    Some(match style {
        9 => "BibataModernIce",
        1 => "FPSCursor",
        2 => "CleanCursor",
        3 => "DotCursor",
        4 => "StoofsCursor",
        5 => "From2006",
        6 => "From2013",
        7 => "WhiteDotCursor",
        8 => "VerySmallWhiteDot",
        _ => return None,
    })
}

pub struct Presets {
    pub mods: PathBuf,
    pub state: PathBuf,
    pub apk: PathBuf,
}

impl Presets {
    pub fn from(args: &Value) -> Presets {
        let s = |k: &str| PathBuf::from(java::opt_str(args, k, ""));
        Presets { mods: s("mods"), state: s("state"), apk: s("apk") }
    }

    pub fn ws(&self, rel: &str) -> PathBuf {
        self.mods.join(rel)
    }

    pub fn state(&self, name: &str) -> PathBuf {
        let _ = std::fs::create_dir_all(&self.state);
        self.state.join(name)
    }

    pub fn resource(&self, name: &str) -> io::Result<Vec<u8>> {
        let dir = apk::read(&self.apk)?;
        let want = format!("assets/mods/{name}");
        let e = dir.entries.iter().find(|e| e.name == want).ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, want.clone()))?;
        apk::read_entry(&self.apk, e.local_offset, e.method, e.compressed, e.size)
    }

    fn prune(&self, f: &Path) {
        fs::delete_and_prune(&self.mods, f);
    }

    pub fn preset_on(&self, pairs: &[(String, String)]) -> bool {
        for (dest, res) in pairs {
            match self.resource(res) {
                Ok(data) if fs::same(&self.ws(dest), &data) => return true,
                Ok(_) => {}
                Err(_) => return false,
            }
        }
        false
    }

    pub fn set_preset(&self, pairs: &[(String, String)], on: bool) -> io::Result<()> {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        for (dest, res) in pairs {
            let data = self.resource(res)?;
            let f = self.ws(dest);
            if on {
                fs::write(&f, &data)?;
            } else if fs::same(&f, &data) {
                self.prune(&f);
            }
        }
        Ok(())
    }

    pub fn remove_death(&self) {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let _ = std::fs::remove_file(self.state("CustomDeathSoundSource"));
        self.prune(&self.ws(DEATH));
    }

    pub fn slot_path(slot: usize) -> String {
        if slot == SLOT_SHIFTLOCK { "MouseLockedCursor.png".into() } else { format!("Cursors/KeyboardMouse/{}", SLOT_FILES[slot]) }
    }

    pub fn working(&self, slot: usize) -> PathBuf {
        self.state("CursorCustom").join(Self::slot_path(slot))
    }

    fn apply_cursor_locked(&self, style: i64) -> io::Result<()> {
        for name in SLOT_FILES {
            self.prune(&self.ws(&format!("{CURSOR_DIR}/{name}")));
        }
        if style == CURSOR_CUSTOM {
            for (s, name) in SLOT_FILES.iter().enumerate() {
                let w = self.working(s);
                let dest = self.ws(&format!("{CURSOR_DIR}/{name}"));
                if w.is_file() {
                    fs::copy(&w, &dest)?;
                } else {
                    self.prune(&dest);
                }
            }
        } else if let Some(folder) = cursor_folder(style) {
            for name in SLOT_FILES {
                let Ok(data) = self.resource(&format!("Cursor/{folder}/{name}")) else {
                    continue;
                };
                fs::write(&self.ws(&format!("{CURSOR_DIR}/{name}")), &data)?;
            }
        }
        Ok(())
    }

    pub fn apply_cursor(&self, style: i64) -> io::Result<()> {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        self.apply_cursor_locked(style)
    }

    pub fn has_custom_cursor(&self) -> bool {
        (0..=2).any(|s| self.working(s).is_file())
    }

    pub fn set_custom_cursor(&self, png: &[u8]) -> io::Result<()> {
        {
            let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
            for s in 0..=2 {
                fs::write(&self.working(s), png)?;
            }
        }
        self.apply_cursor(CURSOR_CUSTOM)
    }

    pub fn remove_custom_cursor(&self, current: i64) -> io::Result<Option<i64>> {
        {
            let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
            for s in 0..=2 {
                let _ = std::fs::remove_file(self.working(s));
            }
        }
        if current != CURSOR_CUSTOM {
            return Ok(None);
        }
        let style = if self.working(SLOT_DRAG).is_file() { CURSOR_CUSTOM } else { CURSOR_DEFAULT };
        self.apply_cursor(style)?;
        Ok(Some(style))
    }

    pub fn set_shift_lock(&self, png: &[u8]) -> io::Result<()> {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        fs::write(&self.ws(SHIFTLOCK), png)
    }

    pub fn remove_shift_lock(&self) {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        self.prune(&self.ws(SHIFTLOCK));
    }

    pub fn sets_folder(&self) -> PathBuf {
        let d = self.state("CursorSets");
        let _ = std::fs::create_dir_all(&d);
        d
    }

    pub fn cursor_sets(&self) -> Vec<String> {
        let mut out: Vec<String> = fs::list_raw(&self.sets_folder()).into_iter().filter(|d| d.is_dir()).map(|d| fs::name_of(&d)).collect();
        out.sort_by(|a, b| java::cmp_ignore_case(a, b));
        out
    }

    pub fn unique_set_name(&self, base: &str) -> String {
        let mut name = base.to_string();
        let mut i = 2;
        while self.sets_folder().join(&name).exists() {
            name = format!("{base} {i}");
            i += 1;
        }
        name
    }

    pub fn set_folder(&self, name: &str) -> io::Result<PathBuf> {
        let n = paths::valid_set_name(Some(name)).ok_or_else(|| io::Error::other("That cursor set name is not valid"))?;
        Ok(self.sets_folder().join(n))
    }

    pub fn create_set(&self, requested: &str) -> io::Result<String> {
        let n = paths::valid_set_name(Some(requested)).ok_or_else(|| io::Error::other("That name cannot be used for a folder. Try a different name."))?;
        let f = self.sets_folder().join(&n);
        if f.exists() {
            return Err(io::Error::other(format!("A cursor set named {n} already exists.")));
        }
        std::fs::create_dir_all(&f).map_err(|_| io::Error::other("mkdir"))?;
        Ok(n)
    }

    pub fn set_slot(&self, set: &str, slot: usize) -> io::Result<PathBuf> {
        Ok(self.set_folder(set)?.join(Self::slot_path(slot)))
    }

    fn copy_or_delete(from: &Path, to: &Path) -> io::Result<()> {
        if from.is_file() {
            fs::copy(from, to)
        } else {
            let _ = std::fs::remove_file(to);
            Ok(())
        }
    }

    pub fn copy_current_to_set(&self, set: &str) -> io::Result<()> {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        for s in 0..SLOT_FILES.len() {
            Self::copy_or_delete(&self.working(s), &self.set_slot(set, s)?)?;
        }
        Self::copy_or_delete(&self.ws(SHIFTLOCK), &self.set_slot(set, SLOT_SHIFTLOCK)?)
    }

    pub fn use_set(&self, set: &str) -> io::Result<()> {
        {
            let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
            for s in 0..SLOT_FILES.len() {
                Self::copy_or_delete(&self.set_slot(set, s)?, &self.working(s))?;
            }
            let shift = self.set_slot(set, SLOT_SHIFTLOCK)?;
            if shift.is_file() {
                fs::copy(&shift, &self.ws(SHIFTLOCK))?;
            }
        }
        self.apply_cursor(CURSOR_CUSTOM)
    }

    pub fn rename_set(&self, old: &str, requested: &str) -> io::Result<String> {
        let folder = self.set_folder(old)?;
        let n = paths::valid_set_name(Some(requested)).ok_or_else(|| io::Error::other("That name cannot be used for a folder. Try a different name."))?;
        if n == old {
            return Ok(n);
        }
        let dest = self.sets_folder().join(&n);
        if !java::eq_ignore_case(&n, old) && dest.exists() {
            return Err(io::Error::other(format!("A cursor set named {n} already exists.")));
        }
        let tmp = self.sets_folder().join(format!("{n}.{}", java::now_nanos()));
        if std::fs::rename(&folder, &tmp).is_err() || std::fs::rename(&tmp, &dest).is_err() {
            return Err(io::Error::other("rename"));
        }
        Ok(n)
    }

    pub fn delete_set(&self, set: &str) -> io::Result<()> {
        fs::delete(&self.set_folder(set)?);
        Ok(())
    }

    pub fn export_set(&self, set: &str, out: std::fs::File) -> io::Result<()> {
        let folder = self.set_folder(set)?;
        let mut zip = crate::zipw::Writer::new(out);
        for rel in managed::files(&folder) {
            zip.file(&rel, &fs::read_all(&folder.join(&rel))?)?;
        }
        zip.finish()
    }

    pub fn import_set(&self, display: Option<&str>, input: std::fs::File) -> io::Result<(String, Vec<String>)> {
        let base = match display {
            Some(d) => strip_zip(d),
            None => "Imported cursor set".to_string(),
        };
        let cleaned: String = base.chars().map(|c| if "/\\<>:\"|?*".contains(c) { ' ' } else { c }).collect();
        let clean = paths::valid_set_name(Some(java::trim(&cleaned)));
        let name = self.unique_set_name(clean.as_deref().unwrap_or("Imported cursor set"));
        let folder = self.sets_folder().join(&name);
        let result = (|| {
            std::fs::create_dir_all(&folder).map_err(|_| io::Error::other("mkdir"))?;
            let mut sink = CursorZip { folder: folder.clone(), current: None, buf: Vec::new(), entries: 0, copied: 0, convert: Vec::new(), stop: false };
            crate::archive::zip_reader(input, &mut sink).map_err(|e| io::Error::other(match e {
                crate::archive::Error::Rejected(m) | crate::archive::Error::Io(m) => m,
            }))?;
            sink.flush()?;
            if sink.copied == 0 {
                return Err(io::Error::other("That zip does not contain any cursor images."));
            }
            Ok(sink.convert)
        })();
        match result {
            Ok(convert) => Ok((name, convert)),
            Err(e) => {
                fs::delete(&folder);
                Err(e)
            }
        }
    }

    pub fn has_custom_sky(&self) -> bool {
        let d = self.state("CustomSkybox");
        placer::SKY_FACES.iter().all(|f| d.join(f).is_file())
    }

    pub fn custom_face(&self, face: usize) -> PathBuf {
        self.state("CustomSkyboxPick").join(placer::SKY_FACES[face])
    }

    pub fn sky_names(&self, online: Option<&str>, saved: &str) -> Vec<String> {
        let mut names: Vec<String> = Vec::new();
        let mut is_online = false;
        if let Some(text) = online
            && let Ok(Value::Array(arr)) = serde_json::from_str::<Value>(text)
        {
            for o in &arr {
                if !o.is_object() || java::opt_str(o, "type", "") != "dir" {
                    continue;
                }
                let n = java::opt_str(o, "name", "");
                if paths::safe_sky_name(&n) && !names.contains(&n) {
                    names.push(n);
                }
            }
            is_online = !names.is_empty();
        }
        if !is_online {
            for d in fs::list_raw(&self.state("SkyboxPacks")) {
                let n = fs::name_of(&d);
                if d.is_dir() && paths::safe_sky_name(&n) {
                    names.push(n);
                }
            }
        }
        names.sort_by(|a, b| {
            if java::eq_ignore_case(a, "Default") {
                return std::cmp::Ordering::Less;
            }
            if java::eq_ignore_case(b, "Default") {
                return std::cmp::Ordering::Greater;
            }
            java::cmp_ignore_case(a, b)
        });
        if self.has_custom_sky() && !names.iter().any(|n| n == CUSTOM_SKY) {
            let at = names.iter().position(|n| n == "Default").map_or(0, |i| i + 1);
            names.insert(at, CUSTOM_SKY.into());
        }
        if paths::safe_sky_name(saved) && !names.iter().any(|n| n == saved) {
            names.push(saved.to_string());
        }
        if !names.iter().any(|n| n == "Default") {
            names.insert(0, "Default".into());
        }
        names
    }

    pub fn apply_sky(&self, name: &str, enabled: bool, source: Option<&Path>, log: &mut Vec<String>) -> io::Result<()> {
        if !paths::safe_sky_name(name) {
            return Err(io::Error::other("The selected skybox name is invalid."));
        }
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let target = self.ws(SKY);
        if !enabled || java::eq_ignore_case(name, "Default") {
            fs::delete(&target);
            self.prune(&target);
            log.push("skybox turned off, faces removed from the workspace".into());
            return Ok(());
        }
        let source = match source {
            Some(s) => s.to_path_buf(),
            None => self.state("CustomSkybox"),
        };
        for f in placer::SKY_FACES {
            if !source.join(f).is_file() {
                log.push(format!("skybox {name} incomplete, missing {f}"));
                return Err(io::Error::other("The selected skybox is incomplete."));
            }
        }
        for f in placer::SKY_FACES {
            fs::copy(&source.join(f), &target.join(f))?;
        }
        log.push(format!("skybox {name} copied {} faces into the workspace", placer::SKY_FACES.len()));
        Ok(())
    }

    pub fn save_custom_sky(&self) -> io::Result<()> {
        if !(0..6).all(|i| self.custom_face(i).is_file()) {
            return Err(io::Error::other("Choose an image for every skybox face"));
        }
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let dest = self.state("CustomSkybox");
        let staging = PathBuf::from(format!("{}.new.{}", dest.display(), java::now_nanos()));
        let result = (|| {
            for (i, f) in placer::SKY_FACES.iter().enumerate() {
                fs::copy(&self.custom_face(i), &staging.join(f))?;
            }
            fs::delete(&dest);
            std::fs::rename(&staging, &dest).map_err(|_| io::Error::other("move"))
        })();
        fs::delete(&staging);
        result
    }

    pub fn remove_custom_sky(&self) {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        fs::delete(&self.state("CustomSkybox"));
        fs::delete(&self.state("CustomSkyboxPick"));
    }

    pub fn font_source(&self) -> PathBuf {
        self.state("CustomFontSource.ttf")
    }

    fn write_font(&self, scale: i64) -> io::Result<()> {
        let data = fs::read_all(&self.font_source())?;
        fs::write(&self.ws(FONT), &crate::font::scale(&data, (scale as f64 / 100.0).max(0.01)))
    }

    pub fn use_font(&self, source: &Path, scale: i64) -> io::Result<()> {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        fs::write(&self.font_source(), &fs::read_all(source)?)?;
        self.write_font(scale)
    }

    pub fn set_font_scale(&self, scale: i64) -> io::Result<()> {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        if self.font_source().is_file() {
            self.write_font(scale)?;
        }
        Ok(())
    }

    pub fn remove_font(&self) {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        self.prune(&self.ws(FONT));
        let _ = std::fs::remove_file(self.font_source());
        self.remove_generated_families();
    }

    pub fn remove_generated_families(&self) {
        let families = self.ws(FAMILIES);
        for f in fs::list_raw(&families) {
            if fs::name_of(&f).ends_with(".json") && generated_family(&f) {
                let _ = std::fs::remove_file(&f);
            }
        }
        self.prune(&families.join(".none"));
    }

    pub fn prepare_for_apply(&self, base: Option<&Path>, log: &mut Vec<String>, pkg: &str) {
        let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
        if !self.ws(FONT).is_file() {
            self.remove_generated_families();
            log.push("no custom font set, generated font families removed".into());
            return;
        }
        let Some(base) = base else {
            log.push(format!("font families skipped, no readable apk for {pkg}"));
            return;
        };
        let dir = match apk::read(base) {
            Ok(d) => d,
            Err(e) => {
                log.push(format!("font families failed, java.io.IOException: {e}"));
                return;
            }
        };
        let prefix = "assets/content/fonts/families/";
        let families = self.ws(FAMILIES);
        let (mut written, mut seen) = (0, 0);
        for e in &dir.entries {
            let Some(file) = e.name.strip_prefix(prefix) else {
                continue;
            };
            if !e.name.ends_with(".json") || file.contains('/') {
                continue;
            }
            seen += 1;
            let target = families.join(file);
            if target.is_file() && !generated_family(&target) {
                continue;
            }
            let rewritten = apk::read_entry(base, e.local_offset, e.method, e.compressed, e.size).and_then(|raw| {
                let mut family: Value = serde_json::from_slice(&raw).map_err(|x| io::Error::other(format!("org.json.JSONException: {x}")))?;
                if !family.is_object() {
                    return Err(io::Error::other("org.json.JSONException: not an object"));
                }
                let faces = family.get_mut("faces").and_then(Value::as_array_mut);
                let Some(faces) = faces.filter(|f| !f.is_empty()) else {
                    return Ok(None);
                };
                for face in faces.iter_mut() {
                    if let Some(o) = face.as_object_mut() {
                        o.insert("assetId".into(), FONT_ASSET.into());
                    }
                }
                Ok(Some(java::to_string(&family, 2)))
            });
            match rewritten {
                Ok(Some(text)) => match fs::write(&target, text.as_bytes()) {
                    Ok(()) => written += 1,
                    Err(x) => log.push(format!("font family {} failed, java.io.IOException: {x}", e.name)),
                },
                Ok(None) => {}
                Err(x) => log.push(format!("font family {} failed, {x}", e.name)),
            }
        }
        log.push(format!("font families rewritten {written} of {seen} from {}", fs::name_of(base)));
    }
}

fn strip_zip(d: &str) -> String {
    if d.len() >= 4 && d[d.len() - 4..].eq_ignore_ascii_case(".zip") { d[..d.len() - 4].to_string() } else { d.to_string() }
}

pub fn generated_family(f: &Path) -> bool {
    let Ok(text) = std::fs::read(f) else {
        return false;
    };
    let Ok(v) = serde_json::from_slice::<Value>(&text) else {
        return false;
    };
    let Some(faces) = v.get("faces").and_then(Value::as_array) else {
        return false;
    };
    !faces.is_empty() && faces.iter().all(|face| face.is_object() && java::opt_str(face, "assetId", "") == FONT_ASSET)
}

pub fn valid_font(f: &Path) -> bool {
    let len = fs::length(f) as u64;
    if !f.is_file() || !(12..=MAX_FONT).contains(&len) {
        return false;
    }
    let mut h = [0u8; 4];
    if std::fs::File::open(f).and_then(|mut x| io::Read::read_exact(&mut x, &mut h)).is_err() {
        return false;
    }
    matches!(u32::from_be_bytes(h), 0x0001_0000 | 0x4F54_544F | 0x7472_7565)
}

pub fn gstatic_url(css: &str) -> Option<String> {
    let lower = css.to_ascii_lowercase();
    let needle = "https://fonts.gstatic.com/";
    let mut from = 0;
    while let Some(i) = lower[from..].find(needle).map(|i| i + from) {
        let rest = &css[i + needle.len()..];
        let end = rest.find(|c: char| c == ')' || c == '\'' || c == '"' || java::regex_space(c)).unwrap_or(rest.len());
        let body = &rest[..end];
        let lb = body.to_ascii_lowercase();
        if let Some(t) = lb.rfind(".ttf")
            && t > 0
        {
            return Some(css[i..i + needle.len() + t + 4].to_string());
        }
        from = i + 1;
    }
    None
}

pub fn font_file_name(family: &str, url: &str) -> String {
    sha256::hex_upper(&sha256::digest(format!("{family}|{url}").as_bytes()))[..20].to_string()
}

pub fn png_size(data: &[u8]) -> Option<(u32, u32)> {
    if data.len() < 24 || data[..4] != [0x89, b'P', b'N', b'G'] || &data[12..16] != b"IHDR" {
        return None;
    }
    let w = u32::from_be_bytes([data[16], data[17], data[18], data[19]]);
    let h = u32::from_be_bytes([data[20], data[21], data[22], data[23]]);
    (w > 0 && h > 0 && (w as i32) > 0 && (h as i32) > 0).then_some((w, h))
}

struct CursorZip {
    folder: PathBuf,
    current: Option<usize>,
    buf: Vec<u8>,
    entries: usize,
    copied: usize,
    convert: Vec<String>,
    stop: bool,
}

impl CursorZip {
    fn flush(&mut self) -> io::Result<()> {
        let Some(slot) = self.current.take() else {
            return Ok(());
        };
        let data = std::mem::take(&mut self.buf);
        let out = self.folder.join(Presets::slot_path(slot));
        let ok_png = png_size(&data).is_some_and(|(w, h)| w <= MAX_IMAGE && h <= MAX_IMAGE) && data.len() > 8;
        fs::write(&out, &data)?;
        if !ok_png {
            self.convert.push(out.to_string_lossy().into_owned());
        }
        self.copied += 1;
        Ok(())
    }
}

impl crate::archive::Sink for CursorZip {
    fn entry(&mut self, name: &str, _size: i64) -> Result<(), crate::archive::Error> {
        self.flush()?;
        if self.stop {
            return Ok(());
        }
        self.entries += 1;
        if self.entries > 256 {
            self.stop = true;
            return Ok(());
        }
        if name.ends_with('/') {
            return Ok(());
        }
        let n = name.replace('\\', "/");
        let file = java::lower(&n[n.rfind('/').map_or(0, |i| i + 1)..]);
        let slot = SLOT_FILES.iter().position(|s| java::eq_ignore_case(s, &file));
        if slot.is_none() && file != "mouselockedcursor.png" {
            return Ok(());
        }
        self.current = Some(slot.unwrap_or(SLOT_SHIFTLOCK));
        Ok(())
    }

    fn data(&mut self, bytes: &[u8]) -> Result<(), crate::archive::Error> {
        if self.current.is_some() {
            if self.buf.len() + bytes.len() > 64 * 1024 * 1024 {
                return Err(crate::archive::Error::Io("too large".into()));
            }
            self.buf.extend_from_slice(bytes);
        }
        Ok(())
    }

    fn end(&mut self) -> Result<(), crate::archive::Error> {
        Ok(self.flush()?)
    }
}
