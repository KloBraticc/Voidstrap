use crate::java::{self, opt_bool, opt_str};
use crate::net::Client;
use serde_json::{Value, json};

pub const RELEASES: &str = "https://api.github.com/repos/KloBraticc/Voidstrap/releases?per_page=20";
pub const PAGE: &str = "https://github.com/KloBraticc/Voidstrap/releases";
const MAX_BODY: i32 = 2 * 1024 * 1024;
const MAX_NOTES: usize = 1200;
pub const MIN_APK_BYTES: i64 = 1024 * 1024;
pub const MAX_APK_BYTES: i64 = 256 * 1024 * 1024;

pub struct Release {
    pub version: String,
    pub code: i64,
    pub url: String,
    pub name: String,
    pub size: i64,
    pub notes: String,
    pub page: String,
}

pub fn version_code(version: &str) -> i64 {
    let mut code: i64 = 0;
    let mut parts = 0;
    for part in version.split('.') {
        if parts == 4 {
            break;
        }
        let digits: String = part.chars().take_while(char::is_ascii_digit).collect();
        if digits.is_empty() {
            return 0;
        }
        let Ok(n) = digits.parse::<i64>() else { return 0 };
        if !(0..=99).contains(&n) {
            return 0;
        }
        code = code * 100 + n;
        parts += 1;
    }
    if parts == 0 { 0 } else { code }
}

fn clean_version(tag: &str) -> String {
    let t = java::trim(tag);
    let t = t.strip_prefix('v').or_else(|| t.strip_prefix('V')).unwrap_or(t);
    java::trim(t).to_string()
}

fn wanted_asset(name: &str, flavor: &str) -> bool {
    let n = java::lower(name);
    n.ends_with(".apk") && n.contains("android") && n.contains(&java::lower(flavor))
}

fn notes(body: &str) -> String {
    let mut out = String::new();
    for line in body.replace('\r', "").split('\n') {
        let t = java::trim(line);
        if t.is_empty() || t.starts_with("<!--") {
            continue;
        }
        if !out.is_empty() {
            out.push('\n');
        }
        out.push_str(t);
        if java::utf16_len(&out) > MAX_NOTES {
            break;
        }
    }
    if java::utf16_len(&out) > MAX_NOTES {
        format!("{}...", java::trim(java::utf16_prefix(&out, MAX_NOTES)))
    } else {
        out
    }
}

fn asset_of(release: &Value, flavor: &str) -> Option<(String, String, i64)> {
    let assets = release.get("assets").and_then(Value::as_array)?;
    for a in assets.iter().filter(|a| a.is_object()) {
        let name = opt_str(a, "name", "");
        if !wanted_asset(&name, flavor) {
            continue;
        }
        let url = opt_str(a, "browser_download_url", "");
        let size = java::opt_long(a, "size", 0);
        if !url.starts_with("https://github.com/") || !(MIN_APK_BYTES..=MAX_APK_BYTES).contains(&size) {
            continue;
        }
        return Some((name, url, size));
    }
    None
}

pub struct Report {
    pub release: Option<Release>,
    pub seen: usize,
    pub newest_tag: String,
    pub reason: &'static str,
}

pub fn newest(client: &Client, flavor: &str, installed: i64, allow_prerelease: bool) -> Report {
    let mut report = Report { release: None, seen: 0, newest_tag: String::new(), reason: "the releases feed could not be read" };
    let Some(body) = client.get(RELEASES, MAX_BODY) else {
        return report;
    };
    let Ok(list) = serde_json::from_slice::<Value>(&body) else {
        report.reason = "the releases feed was not readable json";
        return report;
    };
    let Some(list) = list.as_array() else {
        report.reason = "the releases feed was not a list";
        return report;
    };
    report.seen = list.len();
    report.reason = "no release is newer than the installed build";
    let mut newer = false;
    let mut best: Option<Release> = None;
    for r in list.iter().filter(|r| r.is_object()) {
        let tag = clean_version(&opt_str(r, "tag_name", ""));
        if version_code(&tag) > version_code(&report.newest_tag) {
            report.newest_tag = tag;
        }
    }
    for r in list.iter().filter(|r| r.is_object()) {
        if opt_bool(r, "draft", false) || (opt_bool(r, "prerelease", false) && !allow_prerelease) {
            continue;
        }
        let version = clean_version(&opt_str(r, "tag_name", ""));
        let code = version_code(&version);
        if code <= installed {
            continue;
        }
        newer = true;
        let Some((name, url, size)) = asset_of(r, flavor) else { continue };
        if best.as_ref().is_some_and(|b| b.code >= code) {
            continue;
        }
        best = Some(Release {
            version,
            code,
            url,
            name,
            size,
            notes: notes(&opt_str(r, "body", "")),
            page: {
                let p = opt_str(r, "html_url", "");
                if p.starts_with("https://github.com/") { p } else { PAGE.to_string() }
            },
        });
    }
    if best.is_some() {
        report.reason = "a newer release is available";
    } else if newer {
        report.reason = "a newer release exists but publishes no android package for this flavor";
    }
    report.release = best;
    report
}

pub fn to_json(r: &Release) -> Value {
    json!({
        "version": r.version,
        "code": r.code,
        "url": r.url,
        "name": r.name,
        "size": r.size,
        "notes": r.notes,
        "page": r.page,
    })
}

pub fn call(op: &str, a: &Value) -> Result<Value, crate::archive::Error> {
    Ok(match op {
        "code" => json!({"v": version_code(&opt_str(a, "version", ""))}),
        "latest" => {
            let client = Client { agent: opt_str(a, "agent", "Voidstrap Android") };
            let flavor = opt_str(a, "flavor", "direct");
            let installed = java::opt_long(a, "installed", 0);
            let pre = opt_bool(a, "prerelease", false);
            let report = newest(&client, &flavor, installed, pre);
            match &report.release {
                Some(r) => json!({"found": true, "release": to_json(r), "seen": report.seen, "newestTag": report.newest_tag, "reason": report.reason}),
                None => json!({"found": false, "seen": report.seen, "newestTag": report.newest_tag, "reason": report.reason}),
            }
        }
        _ => return Err(crate::archive::Error::Io(format!("unknown op update.{op}"))),
    })
}
