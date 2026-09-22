pub mod cache;
pub mod engine;
pub mod extract;
pub mod fs;
pub mod interface;
pub mod managed;
pub mod paths;
pub mod placer;
pub mod presets;
pub mod variants;

use crate::archive::Error;
use crate::java;
use engine::Roots;
use managed::Managed;
use presets::Presets;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};

fn s(a: &Value, k: &str) -> String {
    java::opt_str(a, k, "")
}

fn opt(a: &Value, k: &str) -> Option<String> {
    a.get(k).and_then(Value::as_str).map(str::to_string)
}

fn b(a: &Value, k: &str) -> bool {
    java::opt_bool(a, k, false)
}

fn n(a: &Value, k: &str) -> i64 {
    java::opt_long(a, k, 0)
}

fn index(a: &Value) -> placer::Source {
    let i = a.get("index").cloned().unwrap_or(Value::Null);
    placer::Source { key: s(&i, "key"), apk: opt(&i, "apk") }
}

fn managed(a: &Value) -> Managed {
    Managed::new(&s(a, "managed"))
}

fn pairs(a: &Value, k: &str) -> Vec<(String, String)> {
    a.get(k)
        .and_then(Value::as_array)
        .map(|x| x.iter().filter_map(|p| Some((p.get(0)?.as_str()?.to_string(), p.get(1)?.as_str()?.to_string()))).collect())
        .unwrap_or_default()
}

fn record(r: managed::Record) -> Value {
    r.to_json()
}

fn v(x: impl Into<Value>) -> Value {
    json!({ "v": x.into() })
}

fn io(e: std::io::Error) -> Error {
    Error::Io(e.to_string())
}

pub fn call(op: &str, a: &Value, data: Option<&[u8]>, file: Option<std::fs::File>, cancel: &mut dyn FnMut() -> bool) -> Result<Value, Error> {
    let roots = || Roots::from(a);
    let presets = || Presets::from(a);
    Ok(match op {
        "ignored" => v(paths::ignored(&s(a, "rel"))),
        "assetPath" => v(paths::asset_path(&s(a, "rel"))),
        "normalizeName" => v(paths::normalize_name(opt(a, "name").as_deref(), opt(a, "fallback").as_deref())),
        "validSetName" => v(paths::valid_set_name(opt(a, "name").as_deref())),

        "resolve" => v(placer::resolve(&index(a), &s(a, "rel"))),
        "resolveSlot" => v(variants::resolve_slot(&index(a), &s(a, "rel"))),
        "inspectPath" => {
            let src = index(a);
            v(paths::inspect_path(&s(a, "rel"), &mut |x| placer::client_copy(&src, x)))
        }
        "clientCopy" => v(placer::client_copy(&index(a), &s(a, "rel"))),
        "clientFiles" => v(index(a).index().files.iter().cloned().collect::<Vec<_>>()),

        "files" => v(managed::files(Path::new(&s(a, "root")))),
        "load" => v(managed(a).load().into_iter().map(record).collect::<Vec<_>>()),
        "create" => record(managed(a).create(opt(a, "name").as_deref()).map_err(io)?),
        "adopt" => record(managed(a).adopt(Path::new(&s(a, "staged")), opt(a, "name").as_deref()).map_err(io)?),
        "rename" => {
            managed(a).rename(&s(a, "id"), opt(a, "name").as_deref()).map_err(io)?;
            v(true)
        }
        "setEnabled" => {
            managed(a).set_enabled(&s(a, "id"), b(a, "enabled")).map_err(io)?;
            v(true)
        }
        "move" => {
            let to = a.get("to").and_then(Value::as_i64);
            managed(a).move_to(&s(a, "id"), to, n(a, "delta")).map_err(io)?;
            v(true)
        }
        "delete" => {
            managed(a).delete(&s(a, "id")).map_err(io)?;
            v(true)
        }
        "readPack" => v(managed::read_pack(Path::new(&s(a, "folder")))),
        "writePack" => {
            managed::write_pack(Path::new(&s(a, "folder")), a.get("pack").unwrap_or(&Value::Null));
            v(true)
        }
        "scan" => v(managed(a).scan()),
        "enabledFiles" => v(managed(a)
            .enabled_files()
            .into_iter()
            .map(|(r, f, rel)| json!({"record": record(r), "source": f.to_string_lossy(), "relative": rel}))
            .collect::<Vec<_>>()),

        "collect" => v(engine::collect(&roots()).into_iter().map(|(r, f)| json!([r, f.to_string_lossy()])).collect::<Vec<_>>()),
        "originalApk" => v(engine::original_apk(&roots(), &s(a, "pkg"), opt(a, "target").as_deref())),
        "mountedFor" => v(engine::mounted_for(&s(a, "pkg"), &s(a, "target"))),
        "plan" => match engine::plan(&roots(), &s(a, "pkg"), &s(a, "identity"), opt(a, "target").as_deref()).map_err(io)? {
            Some(p) => p.to_json(),
            None => json!({"none": true}),
        },
        "stamp" => v(engine::stamp(&roots(), &s(a, "pkg"))),
        "stampExists" => v(engine::stamp_exists(&roots(), &s(a, "pkg"))),
        "deleteStamp" => {
            engine::delete_stamp(&roots(), &s(a, "pkg"));
            v(true)
        }
        "saveStamp" => {
            engine::save_stamp(&roots(), &s(a, "pkg"), &s(a, "fingerprint"), &s(a, "target"), n(a, "original"), n(a, "files"));
            v(true)
        }
        "script" => v(engine::script(&s(a, "pkg"), &s(a, "target"), opt(a, "build").as_deref(), b(a, "restart"))),
        "removeScript" => v(engine::remove_script(&s(a, "pkg"), &s(a, "target"), b(a, "restart"))),
        "safePkg" => v(engine::safe_pkg(&s(a, "pkg"))),
        "skyKind" => v(engine::sky_kind(Path::new(&s(a, "path")))),

        "cachePending" => v(cache::pending(&roots(), &s(a, "pkg"), b(a, "enabled"))),
        "cacheWanted" => v(cache::wanted(&roots()).into_iter().map(|(h, f)| json!([h, f.to_string_lossy()])).collect::<Vec<_>>()),
        "cacheSync" => match cache::sync_script(&roots(), &s(a, "pkg"), b(a, "enabled"), b(a, "restoreAll")) {
            Some((script, hashes)) => json!({"script": script, "hashes": hashes}),
            None => json!({"skip": true}),
        },
        "cacheSynced" => {
            let hashes: Vec<String> = a.get("hashes").and_then(Value::as_array).map(|x| x.iter().filter_map(|h| h.as_str().map(str::to_string)).collect()).unwrap_or_default();
            cache::synced(&roots(), &s(a, "pkg"), &hashes);
            v(true)
        }
        "isCacheMod" => v(cache::is_cache_mod(Path::new(&s(a, "folder")))),
        "cacheHasEntries" => v(extract::cache_has_entries(Path::new(&s(a, "archive")), cancel)?),
        "cacheInstall" => record(extract::cache_install(Path::new(&s(a, "archive")), opt(a, "name").as_deref(), a.get("pack").filter(|p| p.is_object()), &managed(a), cancel)?),

        "inspect" => {
            let r = extract::inspect(Path::new(&s(a, "archive")), &index(a), cancel)?;
            json!({"robloxContent": r.roblox_content, "files": r.files, "bytes": r.bytes})
        }
        "installManaged" => record(extract::install_managed(
            Path::new(&s(a, "archive")),
            opt(a, "name").as_deref(),
            a.get("pack").filter(|p| p.is_object()),
            &managed(a),
            &index(a),
            cancel,
        )?),
        "capture" => {
            let m = managed(a);
            v(extract::capture(Path::new(&s(a, "archive")), &m.sources(&s(a, "id")), &index(a), cancel)?)
        }
        "hasSlots" => v(variants::has_slots(&managed(a), &s(a, "id"))),
        "buildSlots" => v(variants::build(&managed(a), &s(a, "id"), &index(a)).map_err(io)?),
        "applySlots" => v(variants::apply(&managed(a), &s(a, "id"), a.get("slots").and_then(Value::as_array).map(Vec::as_slice).unwrap_or(&[])).map_err(io)?),
        "deleteSources" => {
            fs::delete(&managed(a).sources(&s(a, "id")));
            v(true)
        }

        "presetOn" => v(presets().preset_on(&pairs(a, "pairs"))),
        "setPreset" => {
            presets().set_preset(&pairs(a, "pairs"), b(a, "on")).map_err(io)?;
            v(true)
        }
        "removeDeath" => {
            presets().remove_death();
            v(true)
        }
        "writeFile" => {
            fs::write(Path::new(&s(a, "path")), data.unwrap_or(&[])).map_err(io)?;
            v(true)
        }
        "copyFile" => {
            fs::copy(Path::new(&s(a, "from")), Path::new(&s(a, "to"))).map_err(io)?;
            v(true)
        }
        "deleteAndPrune" => {
            fs::delete_and_prune(Path::new(&s(a, "mods")), Path::new(&s(a, "path")));
            v(true)
        }
        "applyCursor" => {
            presets().apply_cursor(n(a, "style")).map_err(io)?;
            v(true)
        }
        "hasCustomCursor" => v(presets().has_custom_cursor()),
        "setCustomCursor" => {
            presets().set_custom_cursor(data.unwrap_or(&[])).map_err(io)?;
            v(true)
        }
        "removeCustomCursor" => v(presets().remove_custom_cursor(n(a, "current")).map_err(io)?),
        "setShiftLock" => {
            presets().set_shift_lock(data.unwrap_or(&[])).map_err(io)?;
            v(true)
        }
        "removeShiftLock" => {
            presets().remove_shift_lock();
            v(true)
        }
        "working" => v(presets().working(n(a, "slot") as usize).to_string_lossy().into_owned()),
        "cursorSets" => v(presets().cursor_sets()),
        "uniqueSetName" => v(presets().unique_set_name(&s(a, "base"))),
        "createSet" => v(presets().create_set(&s(a, "name")).map_err(io)?),
        "setSlot" => v(presets().set_slot(&s(a, "set"), n(a, "slot") as usize).map_err(io)?.to_string_lossy().into_owned()),
        "copyCurrentToSet" => {
            presets().copy_current_to_set(&s(a, "set")).map_err(io)?;
            v(true)
        }
        "useSet" => {
            presets().use_set(&s(a, "set")).map_err(io)?;
            v(true)
        }
        "renameSet" => v(presets().rename_set(&s(a, "old"), &s(a, "name")).map_err(io)?),
        "deleteSet" => {
            presets().delete_set(&s(a, "set")).map_err(io)?;
            v(true)
        }
        "exportSet" => {
            let f = file.ok_or_else(|| Error::Io("stream".into()))?;
            presets().export_set(&s(a, "set"), f).map_err(io)?;
            v(true)
        }
        "importSet" => {
            let f = file.ok_or_else(|| Error::Io("stream".into()))?;
            let (name, convert) = presets().import_set(opt(a, "display").as_deref(), f).map_err(io)?;
            json!({"name": name, "convert": convert})
        }
        "exportDir" => {
            let f = file.ok_or_else(|| Error::Io("stream".into()))?;
            export_dir(Path::new(&s(a, "path")), f).map_err(io)?;
            v(true)
        }
        "hasCustomSky" => v(presets().has_custom_sky()),
        "skyNames" => v(presets().sky_names(opt(a, "online").as_deref(), &s(a, "saved"))),
        "applySky" => {
            let mut log = Vec::new();
            let r = presets().apply_sky(&s(a, "name"), b(a, "enabled"), opt(a, "source").as_deref().map(Path::new), &mut log);
            match r {
                Ok(()) => json!({"log": log}),
                Err(e) => json!({"log": log, "error": e.to_string()}),
            }
        }
        "saveCustomSky" => {
            presets().save_custom_sky().map_err(io)?;
            v(true)
        }
        "removeCustomSky" => {
            presets().remove_custom_sky();
            v(true)
        }
        "useFont" => {
            presets().use_font(Path::new(&s(a, "source")), n(a, "scale")).map_err(io)?;
            v(true)
        }
        "setFontScale" => {
            presets().set_font_scale(n(a, "scale")).map_err(io)?;
            v(true)
        }
        "removeFont" => {
            presets().remove_font();
            v(true)
        }
        "prepareForApply" => {
            let mut log = Vec::new();
            let base = opt(a, "base").map(PathBuf::from);
            presets().prepare_for_apply(base.as_deref(), &mut log, &s(a, "pkg"));
            json!({"log": log})
        }
        "validFont" => v(presets::valid_font(Path::new(&s(a, "path")))),
        "fontCatalog" => v(font_catalog(&s(a, "text"))?),
        "gstaticUrl" => v(presets::gstatic_url(&s(a, "css"))),
        "fontFileName" => v(presets::font_file_name(&s(a, "family"), &s(a, "url"))),
        "sha256File" => v(crate::sha256::file(Path::new(&s(a, "path"))).map(|h| crate::sha256::hex_upper(&h)).unwrap_or_default()),

        "hideCoreGui" => v(interface::hide_core_gui(&presets())),
        "setHideCoreGui" => {
            let base = opt(a, "base").map(PathBuf::from);
            interface::set_hide_core_gui(&presets(), b(a, "on"), base.as_deref(), cancel).map_err(io)?;
            v(true)
        }
        "readState" => v(interface::read_state(&presets(), &s(a, "name"))),
        "writeState" => {
            interface::write_state(&presets(), &s(a, "name"), &s(a, "value")).map_err(io)?;
            v(true)
        }
        "trim" => {
            let m = managed(a);
            let id = s(a, "id");
            interface::trim(&m.folder(&id).map_err(io)?, &m.sources(&id), &index(a));
            v(true)
        }
        _ => return Err(Error::Io(format!("unknown op {op}"))),
    })
}

pub fn first_json(text: &str) -> Option<Value> {
    serde_json::Deserializer::from_str(text).into_iter::<Value>().next()?.ok()
}

fn font_catalog(text: &str) -> Result<Vec<String>, Error> {
    let start = text.find('{').ok_or_else(|| Error::Io("json".into()))?;
    let root = first_json(&text[start..]).filter(Value::is_object).ok_or_else(|| Error::Io("json".into()))?;
    let mut names: Vec<String> = Vec::new();
    let mut seen = std::collections::HashSet::new();
    if let Some(families) = root.get("familyMetadataList").and_then(Value::as_array) {
        for f in families {
            let n = if f.is_object() { java::trim(&java::opt_str(f, "family", "")).to_string() } else { String::new() };
            if !n.is_empty() && java::utf16_len(&n) <= 128 && seen.insert(java::lower(&n)) {
                names.push(n);
            }
        }
    }
    names.sort_by(|a, b| java::cmp_ignore_case(a, b));
    names.truncate(3000);
    if names.is_empty() {
        return Err(Error::Io("empty".into()));
    }
    Ok(names)
}

fn export_dir(dir: &Path, out: std::fs::File) -> std::io::Result<()> {
    let mut zip = crate::zipw::Writer::new(out);
    if dir.is_dir() {
        fn walk(zip: &mut crate::zipw::Writer, dir: &Path, prefix: &str) -> std::io::Result<()> {
            for f in fs::list(dir) {
                let name = format!("{prefix}{}", fs::name_of(&f));
                if f.is_dir() {
                    zip.dir(&format!("{name}/"))?;
                    walk(zip, &f, &format!("{name}/"))?;
                } else {
                    zip.file(&name, &fs::read_all(&f)?)?;
                }
            }
            Ok(())
        }
        walk(&mut zip, dir, "")?;
        return zip.finish();
    }
    drop(zip);
    Err(std::io::Error::other("not a folder"))
}
