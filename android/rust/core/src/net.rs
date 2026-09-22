use crate::http;
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::time::Duration;

#[cfg(target_os = "android")]
#[link(name = "log")]
unsafe extern "C" {
    fn __android_log_write(prio: i32, tag: *const std::ffi::c_char, text: *const std::ffi::c_char) -> i32;
}

pub fn log(tag: &str, level: i32, text: &str) {
    #[cfg(target_os = "android")]
    {
        if let (Ok(t), Ok(m)) = (std::ffi::CString::new(tag), std::ffi::CString::new(text.replace('\0', " "))) {
            unsafe { __android_log_write(level, t.as_ptr(), m.as_ptr()) };
        }
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = (tag, level, text);
    }
}

pub fn info(tag: &str, text: &str) {
    log(tag, 4, text);
}

pub fn warn(tag: &str, text: &str) {
    log(tag, 5, text);
}

pub fn sha1_hex(data: &[u8]) -> String {
    use crate::hash::Digest;
    let mut d = crate::hash::Sha1::new();
    d.update(data);
    crate::hash::hex(&d.finish())
}

pub struct Client {
    pub agent: String,
}

impl Client {
    pub fn get(&self, url: &str, limit: i32) -> Option<Vec<u8>> {
        let r = http::request("GET", url, &[("Accept", "application/json, image/png"), ("User-Agent", &self.agent)], None, 8000, limit, true)?;
        (r.code == 200).then_some(r.body)
    }

    pub fn json(&self, url: &str) -> Option<Value> {
        serde_json::from_slice::<Value>(&self.get(url, 1024 * 1024)?).ok().filter(Value::is_object)
    }

    pub fn cached_json(&self, cache: &Path, url: &str, max_age_ms: i64) -> Option<Value> {
        let dir = cache.join("feeds");
        let _ = std::fs::create_dir_all(&dir);
        let f: PathBuf = dir.join(format!("{}.json", sha1_hex(url.as_bytes())));
        let read = |f: &Path| -> Option<Value> { serde_json::from_slice::<Value>(&std::fs::read(f).ok()?).ok().filter(Value::is_object) };
        let fresh = std::fs::metadata(&f)
            .ok()
            .filter(|m| m.is_file())
            .and_then(|m| m.modified().ok())
            .is_some_and(|t| t.elapsed().map_or(true, |age| age < Duration::from_millis(max_age_ms.max(0) as u64)));
        if fresh && let Some(v) = read(&f) {
            return Some(v);
        }
        match self.get(url, 1024 * 1024) {
            Some(data) => match serde_json::from_slice::<Value>(&data).ok().filter(Value::is_object) {
                Some(v) => {
                    let tmp = dir.join(format!("{}.json.tmp", sha1_hex(url.as_bytes())));
                    if std::fs::write(&tmp, &data).is_ok() && std::fs::rename(&tmp, &f).is_err() {
                        let _ = std::fs::remove_file(&tmp);
                    }
                    Some(v)
                }
                None => read(&f),
            },
            None => read(&f),
        }
    }
}

pub fn uri_encode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'!' | b'~' | b'*' | b'\'' | b'(' | b')' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}
