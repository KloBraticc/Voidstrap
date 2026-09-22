use crate::java;
use crate::mods::first_json;
use serde_json::{Map, Value};
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::Mutex;
use std::time::Duration;

const ENDPOINT: &str = "https://translate.googleapis.com/translate_a/t?client=gtx&sl=auto&tl=";
const MAX_BATCH: usize = 64;
const MAX_BODY: i32 = 4 * 1024 * 1024;
const TIMEOUT_MS: i32 = 15000;
const OFFLINE_PAUSE_MS: i64 = 30000;
const MAX_ATTEMPTS: usize = 4;
const BASE_BACKOFF_MS: i64 = 700;
const MAX_BACKOFF_MS: i64 = 8000;
const PROTECTED: [&str; 6] = ["Roblox Studio", "Voidstrap", "Roblox", "Discord", "Bloxstrap", "Chevstrap"];
const USER_AGENT: &str = "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";
const CACHE_FILE: &str = "Translations.json";

static MEMORY: Mutex<Option<HashMap<String, String>>> = Mutex::new(None);
static IN_FLIGHT: Mutex<Option<HashSet<String>>> = Mutex::new(None);
static NET: Mutex<()> = Mutex::new(());
static SAVE: Mutex<()> = Mutex::new(());
static LOADED: AtomicBool = AtomicBool::new(false);
static OFFLINE_UNTIL: AtomicI64 = AtomicI64::new(0);

fn key(lang: &str, text: &str) -> String {
    format!("{lang}|{text}")
}

fn get(k: &str) -> Option<String> {
    MEMORY.lock().ok()?.as_ref()?.get(k).cloned()
}

fn put(k: String, v: String) {
    if let Ok(mut m) = MEMORY.lock() {
        m.get_or_insert_with(HashMap::new).insert(k, v);
    }
}

pub fn skip(s: &str) -> bool {
    let t = java::trim(s);
    if t.is_empty() || PROTECTED.iter().any(|p| java::eq_ignore_case(t, p)) {
        return true;
    }
    let lower = s.to_lowercase();
    if ["://", "www.", ".com", ".net", ".org", ".gg"].iter().any(|p| lower.contains(p)) {
        return true;
    }
    !s.chars().any(java::is_letter)
}

pub fn load(cache: &Path) {
    if LOADED.swap(true, Ordering::SeqCst) {
        return;
    }
    let Ok(data) = std::fs::read(cache.join(CACHE_FILE)) else { return };
    let Some(Value::Object(root)) = first_json(&String::from_utf8_lossy(&data)) else { return };
    if let Ok(mut m) = MEMORY.lock() {
        let m = m.get_or_insert_with(HashMap::new);
        for (k, v) in &root {
            m.insert(k.clone(), java::value_of(v));
        }
    }
}

fn save(cache: &Path) {
    let Ok(_guard) = SAVE.lock() else { return };
    let root: Map<String, Value> = match MEMORY.lock() {
        Ok(m) => m.as_ref().map(|m| m.iter().map(|(k, v)| (k.clone(), Value::String(v.clone()))).collect()).unwrap_or_default(),
        Err(_) => return,
    };
    let tmp = cache.join(format!("{CACHE_FILE}.tmp"));
    if std::fs::write(&tmp, java::to_string(&Value::Object(root), 0)).is_ok() && std::fs::rename(&tmp, cache.join(CACHE_FILE)).is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
}

pub fn lookup(lang: &str, texts: &[Option<String>]) -> Vec<Value> {
    texts
        .iter()
        .map(|t| match t {
            None => Value::Bool(false),
            Some(t) if skip(t) => Value::Bool(false),
            Some(t) => get(&key(lang, t)).map_or(Value::Null, Value::String),
        })
        .collect()
}

fn index_ignore_case(hay: &[char], needle: &[char], from: usize) -> Option<usize> {
    if needle.len() > hay.len() {
        return None;
    }
    (from..=hay.len() - needle.len()).find(|&i| {
        hay[i..i + needle.len()].iter().zip(needle).all(|(a, b)| a.len_utf16() == 1 && java::eq_ignore_case(&a.to_string(), &b.to_string()))
    })
}

fn replace_ignore_case(text: &str, needle: &str, replacement: &str) -> String {
    let hay: Vec<char> = text.chars().collect();
    let n: Vec<char> = needle.chars().collect();
    let mut out = String::new();
    let mut at = 0;
    while let Some(found) = index_ignore_case(&hay, &n, at) {
        out.extend(&hay[at..found]);
        out.push_str(replacement);
        at = found + n.len();
    }
    out.extend(&hay[at..]);
    out
}

fn token(i: usize) -> String {
    format!("VSTK{i}Z")
}

fn mask(text: &str) -> String {
    PROTECTED.iter().enumerate().fold(text.to_string(), |out, (i, p)| replace_ignore_case(&out, p, &token(i)))
}

fn unmask(translated: &str, source: &str) -> String {
    let src: Vec<char> = source.chars().collect();
    let mut out = translated.to_string();
    for (i, p) in PROTECTED.iter().enumerate() {
        let n: Vec<char> = p.chars().collect();
        let Some(at) = index_ignore_case(&src, &n, 0) else { continue };
        let original: String = src[at..at + n.len()].iter().collect();
        out = replace_ignore_case(&out, &token(i), &original);
    }
    out
}

fn value(node: Option<&Value>) -> Option<String> {
    match node? {
        Value::String(s) => Some(s.clone()),
        Value::Array(a) => a.first().and_then(Value::as_str).map(str::to_string),
        _ => None,
    }
}

fn parse(lang: &str, chunk: &[String], body: &str) -> bool {
    let Some(Value::Array(root)) = first_json(body) else { return false };
    if chunk.len() == 1 && !root.is_empty() {
        let Some(one) = value(root.first()) else { return false };
        put(key(lang, &chunk[0]), unmask(&one, &chunk[0]));
        return true;
    }
    if root.len() != chunk.len() {
        return false;
    }
    let mut any = false;
    for (i, src) in chunk.iter().enumerate() {
        if let Some(v) = value(root.get(i)) {
            put(key(lang, src), unmask(&v, src));
            any = true;
        }
    }
    any
}

fn send(lang: &str, chunk: &[String]) -> i64 {
    let form = chunk.iter().map(|t| format!("q={}", crate::http::encode(&mask(t)))).collect::<Vec<_>>().join("&");
    let headers = [("Content-Type", "application/x-www-form-urlencoded; charset=utf-8"), ("User-Agent", USER_AGENT), ("Accept", "*/*")];
    let Some(r) = crate::http::request("POST", &format!("{ENDPOINT}{}", crate::http::encode(lang)), &headers, Some(form.as_bytes()), TIMEOUT_MS, MAX_BODY, true) else {
        OFFLINE_UNTIL.store(java::now_ms() + OFFLINE_PAUSE_MS, Ordering::SeqCst);
        return -1;
    };
    if r.code == 429 || r.code / 100 == 5 {
        let header = r.header("Retry-After").and_then(|h| java::trim(h).parse::<i64>().ok()).unwrap_or(0);
        return if header > 0 { (header * 1000).min(MAX_BACKOFF_MS) } else { BASE_BACKOFF_MS };
    }
    if r.code / 100 != 2 {
        crate::net::warn("Voidstrap", &format!("Translation endpoint returned {}", r.code));
        return -1;
    }
    if parse(lang, chunk, &r.text()) { 0 } else { -1 }
}

fn request(lang: &str, chunk: &[String]) -> bool {
    for _ in 0..MAX_ATTEMPTS {
        if java::now_ms() < OFFLINE_UNTIL.load(Ordering::SeqCst) {
            return false;
        }
        let wait = {
            let _net = NET.lock();
            send(lang, chunk)
        };
        if wait == 0 {
            return true;
        }
        if wait < 0 {
            return false;
        }
        std::thread::sleep(Duration::from_millis(wait as u64));
    }
    false
}

pub fn fetch(cache: &Path, lang: &str, texts: &[String]) {
    let mut pending: Vec<String> = Vec::new();
    {
        let Ok(mut flight) = IN_FLIGHT.lock() else { return };
        let flight = flight.get_or_insert_with(HashSet::new);
        for t in texts {
            let k = key(lang, t);
            if skip(t) || get(&k).is_some() || pending.contains(t) || !flight.insert(k) {
                continue;
            }
            pending.push(t.clone());
        }
    }
    if pending.is_empty() {
        return;
    }
    let mut any = false;
    for chunk in pending.chunks(MAX_BATCH) {
        any |= request(lang, chunk);
    }
    if let Ok(mut flight) = IN_FLIGHT.lock()
        && let Some(f) = flight.as_mut()
    {
        for t in &pending {
            f.remove(&key(lang, t));
        }
    }
    if any {
        save(cache);
    }
}
