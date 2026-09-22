use crate::archive::Error;
use crate::hash;
use crate::java::{self, opt_bool, opt_int, opt_long, opt_str, value_of};
use crate::link::Uri;
use crate::mods::{first_json, paths, placer};
use crate::net::{self, Client};
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

pub const GAMEBANANA: &str = "GameBanana";
pub const MARKETPLACE: &str = "Kliko Mods";
const ROBLOX_GAME: i32 = 2879;
const MIN_SEARCH: usize = 2;
const API: &str = "https://gamebanana.com/apiv11";
const PAGE_SIZE: usize = 35;
const DETAIL: &str = "_idRow,_sText,_aFiles,_aGame,_nDownloadCount,_nLikeCount,_nPostCount,_sLicense,_aSubmitter,_aLicenseChecklist,_aPreviewMedia";
const MARKET_SOURCE: &str = "https://github.com/klikos-modloader/marketplace";
const MARKET_DOWNLOAD: &str = "https://github.com/klikos-modloader/marketplace/raw/refs/heads/main/";
const MARKET_RAW: &str = "https://raw.githubusercontent.com/klikos-modloader/marketplace/main/";
const MARKET_RAW_REFS: &str = "https://raw.githubusercontent.com/klikos-modloader/marketplace/refs/heads/main/";
const MARKET_TREE: &str = "https://api.github.com/repos/klikos-modloader/marketplace/git/trees/main?recursive=1";
const HIDDEN: [&str; 3] = ["maps", "effects", "castaways"];
const PACKAGE_EXTENSIONS: [&str; 4] = ["zip", "rar", "7z", "json"];
const MAX_PACKAGE: i64 = 256 * 1024 * 1024;
const UNEXPECTED: &str = "The catalog returned an unexpected reply.";

const BLOCKED: &[&str] = &[
    "exploit", "executor", "script hub", "scripthub", "cheat", "cheats", "cheating", "aimbot", "auto farm", "autofarm", "autofarming", "esp", "wallhack", "wall hack",
    "bypass anticheat", "anti cheat bypass", "anticheat bypass", "injector", "dll inject", "synapse", "krnl", "fluxus", "delta executor", "keyless", "no key system",
    "robux generator", "free robux", "account generator", "token grabber", "cookie logger", "remote access trojan", "keylogger", "stealer", "keygen", "serial key",
    "byfron bypass", "hyperion bypass", "silent aim", "lag switch", "dupe glitch", "duplication glitch", "fly hack", "speed hack", "god mode script",
];
const EXTERNAL: &[&str] = &[
    "python", "pip install", ".py", "py file", "py script", "browser extension", "chrome extension", "firefox extension", "edge extension", "opera extension",
    "browser addon", "browser add on", "chrome web store", "load unpacked", "unpacked extension", "userscript", "user script", "tampermonkey", "greasemonkey",
    "violentmonkey", ".crx", ".xpi", "web extension", "webextension",
];
const FLAG_TERMS: &[&str] = &[
    "fflag", "dfflag", "fint", "dfint", "fstring", "dfstring", "flog", "fastflag", "fastflags", "fast flag", "fast flags", "flag list", "flags list", "flag preset",
    "flags preset", "clientappsettings",
];
const REPLACEMENT_HINTS: [&str; 3] = ["fleasion", "assetwarp", "asset warp"];

fn fold(c: char) -> &'static str {
    match c {
        'ß' | 'ẞ' => "ss",
        'ſ' => "s",
        '\u{212A}' => "k",
        'ﬀ' => "ff",
        'ﬁ' => "fi",
        'ﬂ' => "fl",
        'ﬃ' => "ffi",
        'ﬄ' => "ffl",
        'ﬅ' | 'ﬆ' => "st",
        _ => "",
    }
}

fn find(terms: &[&str], hay: &str) -> Option<String> {
    let orig: Vec<(usize, char)> = hay.char_indices().collect();
    let mut folded: Vec<char> = Vec::new();
    let mut owner: Vec<usize> = Vec::new();
    let mut first: Vec<usize> = Vec::new();
    for (k, &(_, c)) in orig.iter().enumerate() {
        first.push(folded.len());
        let f = fold(c);
        if f.is_empty() {
            folded.push(c);
            owner.push(k);
        } else {
            for x in f.chars() {
                folded.push(x);
                owner.push(k);
            }
        }
    }
    first.push(folded.len());
    let alnum = |c: char| c.is_ascii_alphanumeric() || c == 'ſ' || c == '\u{212A}';
    let term_end = |t: &str, start: usize| -> Option<usize> {
        let mut p = start;
        for (i, w) in t.split(' ').enumerate() {
            if i > 0 {
                let q = p;
                while p < folded.len() && matches!(folded[p], ' ' | '_' | '-') {
                    p += 1;
                }
                if p == q {
                    return None;
                }
            }
            for tc in w.chars() {
                if folded.get(p) != Some(&tc) {
                    return None;
                }
                p += 1;
            }
        }
        let o = if p == folded.len() { orig.len() } else { owner[p] };
        (first[o] == p).then_some(o)
    };
    for k in 0..orig.len() {
        if k > 0 && alnum(orig[k - 1].1) {
            continue;
        }
        for t in terms {
            let Some(end) = term_end(t, first[k]) else { continue };
            if end < orig.len() && alnum(orig[end].1) {
                continue;
            }
            let e = if end < orig.len() { orig[end].0 } else { hay.len() };
            return Some(java::trim(&hay[orig[k].0..e]).to_string());
        }
    }
    None
}

fn flatten(parts: &[&str]) -> String {
    let mut sb = String::from(" ");
    for p in parts {
        if !p.is_empty() {
            sb.push_str(p);
            sb.push(' ');
        }
    }
    sb.to_lowercase()
}

fn strip_slashes(s: &str) -> String {
    java::strip_end(s, |c| c == '/')
}

fn game_banana_host(host: Option<&str>) -> bool {
    host.is_some_and(|h| java::eq_ignore_case(h, "gamebanana.com") || h.to_lowercase().ends_with(".gamebanana.com"))
}

pub fn marketplace_url(url: &str) -> bool {
    url.starts_with(MARKET_DOWNLOAD) || url.starts_with(MARKET_RAW) || url.starts_with(MARKET_RAW_REFS)
}

pub fn trusted_url(url: &str) -> bool {
    if !url.starts_with("https://") {
        return false;
    }
    let host = Uri::parse(url).host();
    if host.as_deref().is_some_and(|h| java::eq_ignore_case("github.com", h) || java::eq_ignore_case("raw.githubusercontent.com", h)) {
        return strip_slashes(url) == MARKET_SOURCE || marketplace_url(url);
    }
    game_banana_host(host.as_deref())
}

fn trusted_download(url: &str) -> bool {
    if marketplace_url(url) {
        return true;
    }
    url.starts_with("https://") && game_banana_host(Uri::parse(url).host().as_deref())
}

fn md5_shape(s: &str) -> bool {
    s.len() == 32 && s.bytes().all(|b| b.is_ascii_hexdigit())
}

#[derive(Clone, Default)]
pub struct ModFile {
    pub id: i64,
    pub name: String,
    pub url: String,
    pub md5: String,
    pub size: i64,
    pub description: String,
    pub av_state: String,
    pub av_result: String,
    pub analysis_state: String,
    pub analysis_result: String,
    pub executable: bool,
    pub probed: bool,
    pub listing_known: bool,
    pub listing_roblox: bool,
    pub listing_config: bool,
    pub listing_assets: bool,
    pub listing_cache: bool,
    pub lacks_roblox: bool,
}

impl ModFile {
    pub fn from(o: &Value) -> ModFile {
        ModFile {
            id: opt_long(o, "id", 0),
            name: opt_str(o, "name", ""),
            url: opt_str(o, "url", ""),
            md5: opt_str(o, "md5", ""),
            size: opt_long(o, "size", 0),
            description: opt_str(o, "description", ""),
            av_state: opt_str(o, "avState", ""),
            av_result: opt_str(o, "avResult", ""),
            analysis_state: opt_str(o, "analysisState", ""),
            analysis_result: opt_str(o, "analysisResult", ""),
            executable: opt_bool(o, "executable", false),
            probed: opt_bool(o, "probed", false),
            listing_known: opt_bool(o, "listingKnown", false),
            listing_roblox: opt_bool(o, "listingRoblox", false),
            listing_config: opt_bool(o, "listingConfig", false),
            listing_assets: opt_bool(o, "listingAssets", false),
            listing_cache: opt_bool(o, "listingCache", false),
            lacks_roblox: opt_bool(o, "lacksRoblox", false),
        }
    }

    pub fn to_json(&self) -> Value {
        json!({"id": self.id, "name": self.name, "url": self.url, "md5": self.md5, "size": self.size, "description": self.description,
            "avState": self.av_state, "avResult": self.av_result, "analysisState": self.analysis_state, "analysisResult": self.analysis_result,
            "executable": self.executable, "probed": self.probed, "listingKnown": self.listing_known, "listingRoblox": self.listing_roblox,
            "listingConfig": self.listing_config, "listingAssets": self.listing_assets, "listingCache": self.listing_cache, "lacksRoblox": self.lacks_roblox})
    }

    fn gamebanana(f: &Value) -> ModFile {
        ModFile {
            id: opt_long(f, "_idRow", 0),
            name: opt_str(f, "_sFile", ""),
            url: opt_str(f, "_sDownloadUrl", ""),
            md5: opt_str(f, "_sMd5Checksum", ""),
            size: opt_long(f, "_nFilesize", 0),
            av_state: opt_str(f, "_sAvState", ""),
            av_result: opt_str(f, "_sAvResult", ""),
            analysis_state: opt_str(f, "_sAnalysisState", ""),
            analysis_result: opt_str(f, "_sAnalysisResult", ""),
            ..ModFile::default()
        }
    }
}

#[derive(Clone, Default)]
pub struct Person {
    name: String,
    avatar: String,
    profile: String,
    title: String,
    points: i32,
}

impl Person {
    fn from(o: &Value) -> Person {
        Person { name: opt_str(o, "name", ""), avatar: opt_str(o, "avatar", ""), profile: opt_str(o, "profile", ""), title: opt_str(o, "title", ""), points: opt_int(o, "points", 0) }
    }

    fn to_json(&self) -> Value {
        json!({"name": self.name, "avatar": self.avatar, "profile": self.profile, "title": self.title, "points": self.points})
    }
}

#[derive(Clone, Default)]
pub struct Image {
    full: String,
    thumb: String,
    caption: String,
}

#[derive(Clone, Default)]
pub struct Post {
    title: String,
    author: String,
    status: String,
    time: i64,
    body: String,
    id: i64,
    reply_count: i32,
    removed: bool,
    avatar: String,
    author_title: String,
    profile: String,
    pinned: bool,
    stamps: i32,
    changes: Vec<(String, String)>,
    replies: Vec<Post>,
}

fn pairs(o: &Value, key: &str) -> Vec<(String, String)> {
    o.get(key)
        .and_then(Value::as_array)
        .map(|a| a.iter().map(|p| (p.get(0).map_or(String::new(), value_of), p.get(1).map_or(String::new(), value_of))).collect())
        .unwrap_or_default()
}

fn pairs_json(p: &[(String, String)]) -> Value {
    Value::Array(p.iter().map(|(a, b)| json!([a, b])).collect())
}

fn objects(o: &Value, key: &str) -> Vec<Value> {
    o.get(key).and_then(Value::as_array).map(|a| a.iter().filter(|x| x.is_object()).cloned().collect()).unwrap_or_default()
}

impl Post {
    pub fn from(o: &Value) -> Post {
        Post {
            title: opt_str(o, "title", ""),
            author: opt_str(o, "author", ""),
            status: opt_str(o, "status", ""),
            time: opt_long(o, "time", 0),
            body: opt_str(o, "body", ""),
            id: opt_long(o, "id", 0),
            reply_count: opt_int(o, "replyCount", 0),
            removed: opt_bool(o, "removed", false),
            avatar: opt_str(o, "avatar", ""),
            author_title: opt_str(o, "authorTitle", ""),
            profile: opt_str(o, "profile", ""),
            pinned: opt_bool(o, "pinned", false),
            stamps: opt_int(o, "stamps", 0),
            changes: pairs(o, "changes"),
            replies: objects(o, "replies").iter().map(Post::from).collect(),
        }
    }

    pub fn to_json(&self) -> Value {
        json!({"title": self.title, "author": self.author, "status": self.status, "time": self.time, "body": self.body, "id": self.id,
            "replyCount": self.reply_count, "removed": self.removed, "avatar": self.avatar, "authorTitle": self.author_title, "profile": self.profile,
            "pinned": self.pinned, "stamps": self.stamps, "changes": pairs_json(&self.changes), "replies": self.replies.iter().map(Post::to_json).collect::<Vec<_>>()})
    }
}

#[derive(Clone)]
pub struct Entry {
    pub id: i64,
    slug: String,
    pub name: String,
    summary: String,
    html: String,
    pub author: String,
    author_avatar: String,
    pub profile_url: String,
    icon_url: String,
    category: String,
    super_category: String,
    source: String,
    version: String,
    likes: i32,
    views: i32,
    downloads: i32,
    comment_count: i32,
    update_count: i32,
    issue_count: i32,
    updated: i64,
    tags: Vec<String>,
    images: Vec<Image>,
    pub files: Vec<ModFile>,
    license: String,
    license_rules: Vec<(String, String)>,
    submitter: Option<Person>,
    comments: Vec<Post>,
    updates: Vec<Post>,
    issues: Vec<Post>,
    detail_loaded: bool,
    replacement: bool,
}

impl Default for Entry {
    fn default() -> Entry {
        Entry {
            id: 0,
            slug: String::new(),
            name: String::new(),
            summary: String::new(),
            html: String::new(),
            author: String::new(),
            author_avatar: String::new(),
            profile_url: String::new(),
            icon_url: String::new(),
            category: String::new(),
            super_category: String::new(),
            source: GAMEBANANA.into(),
            version: String::new(),
            likes: 0,
            views: 0,
            downloads: 0,
            comment_count: 0,
            update_count: 0,
            issue_count: 0,
            updated: 0,
            tags: Vec::new(),
            images: Vec::new(),
            files: Vec::new(),
            license: String::new(),
            license_rules: Vec::new(),
            submitter: None,
            comments: Vec::new(),
            updates: Vec::new(),
            issues: Vec::new(),
            detail_loaded: false,
            replacement: false,
        }
    }
}

impl Entry {
    pub fn from(o: &Value) -> Entry {
        let posts = |k: &str| objects(o, k).iter().map(Post::from).collect();
        Entry {
            id: opt_long(o, "id", 0),
            slug: opt_str(o, "slug", ""),
            name: opt_str(o, "name", ""),
            summary: opt_str(o, "summary", ""),
            html: opt_str(o, "html", ""),
            author: opt_str(o, "author", ""),
            author_avatar: opt_str(o, "authorAvatar", ""),
            profile_url: opt_str(o, "profileUrl", ""),
            icon_url: opt_str(o, "iconUrl", ""),
            category: opt_str(o, "category", ""),
            super_category: opt_str(o, "superCategory", ""),
            source: opt_str(o, "source", GAMEBANANA),
            version: opt_str(o, "version", ""),
            likes: opt_int(o, "likes", 0),
            views: opt_int(o, "views", 0),
            downloads: opt_int(o, "downloads", 0),
            comment_count: opt_int(o, "commentCount", 0),
            update_count: opt_int(o, "updateCount", 0),
            issue_count: opt_int(o, "issueCount", 0),
            updated: opt_long(o, "updated", 0),
            tags: o.get("tags").and_then(Value::as_array).map(|a| a.iter().map(value_of).collect()).unwrap_or_default(),
            images: objects(o, "images").iter().map(|i| Image { full: opt_str(i, "full", ""), thumb: opt_str(i, "thumb", ""), caption: opt_str(i, "caption", "") }).collect(),
            files: objects(o, "files").iter().map(ModFile::from).collect(),
            license: opt_str(o, "license", ""),
            license_rules: pairs(o, "licenseRules"),
            submitter: o.get("submitter").filter(|v| v.is_object()).map(Person::from),
            comments: posts("comments"),
            updates: posts("updates"),
            issues: posts("issues"),
            detail_loaded: opt_bool(o, "detailLoaded", false),
            replacement: opt_bool(o, "replacement", false),
        }
    }

    pub fn to_json(&self) -> Value {
        let posts = |p: &[Post]| p.iter().map(Post::to_json).collect::<Vec<_>>();
        json!({"id": self.id, "slug": self.slug, "name": self.name, "summary": self.summary, "html": self.html, "author": self.author,
            "authorAvatar": self.author_avatar, "profileUrl": self.profile_url, "iconUrl": self.icon_url, "category": self.category,
            "superCategory": self.super_category, "source": self.source, "version": self.version, "likes": self.likes, "views": self.views,
            "downloads": self.downloads, "commentCount": self.comment_count, "updateCount": self.update_count, "issueCount": self.issue_count,
            "updated": self.updated, "tags": self.tags,
            "images": self.images.iter().map(|i| json!({"full": i.full, "thumb": i.thumb, "caption": i.caption})).collect::<Vec<_>>(),
            "files": self.files.iter().map(ModFile::to_json).collect::<Vec<_>>(), "license": self.license,
            "licenseRules": pairs_json(&self.license_rules), "submitter": self.submitter.as_ref().map(Person::to_json),
            "comments": posts(&self.comments), "updates": posts(&self.updates), "issues": posts(&self.issues),
            "detailLoaded": self.detail_loaded, "replacement": self.replacement})
    }
}

pub fn inspect_listing(e: &Entry) -> Option<String> {
    if e.id <= 0 || java::trim(&e.name).is_empty() {
        return Some("The entry is incomplete.".into());
    }
    if !trusted_url(&e.profile_url) {
        return Some("The entry does not come from a trusted community source.".into());
    }
    if HIDDEN.contains(&e.category.to_lowercase().as_str()) || HIDDEN.contains(&e.super_category.to_lowercase().as_str()) {
        return Some("The entry is in a category Voidstrap does not show.".into());
    }
    let tags = e.tags.join(" ");
    let hay = flatten(&[&e.name, &e.summary, &e.category, &e.super_category, &tags]);
    if let Some(t) = find(BLOCKED, &hay) {
        return Some(format!("The entry looks like a cheat or an exploit, matched on {t}."));
    }
    if let Some(t) = find(EXTERNAL, &hay) {
        return Some(format!("The entry is a separate program or a browser extension, matched on {t}."));
    }
    None
}

pub fn inspect_detail(e: &Entry) -> Option<String> {
    if let Some(l) = inspect_listing(e) {
        return Some(l);
    }
    let hay = flatten(&[&e.html]);
    if let Some(t) = find(BLOCKED, &hay) {
        return Some(format!("The description advertises cheating, matched on {t}."));
    }
    if let Some(t) = find(EXTERNAL, &hay) {
        return Some(format!("The description describes a separate program or a browser extension, matched on {t}."));
    }
    if !e.files.iter().any(|f| inspect_file(f).is_none()) {
        return Some("No scanned package is available for this mod.".into());
    }
    if flag_list(e) {
        return Some("The entry is a fast flag list, not a Roblox mod.".into());
    }
    None
}

pub fn inspect_file(f: &ModFile) -> Option<String> {
    if f.lacks_roblox {
        return Some("The package holds no Roblox client files, so it is not a Roblox mod.".into());
    }
    if f.executable {
        return Some("The package contains a program or a script, so it is not a Roblox mod.".into());
    }
    if !trusted_download(&f.url) {
        return Some("The download does not come from a verified GameBanana file host over https.".into());
    }
    let ext = paths::extension(&f.name);
    if ext == "json" && flag_file(&f.name) {
        return Some("The file is a fast flag list, not a mod.".into());
    }
    if !PACKAGE_EXTENSIONS.contains(&ext.as_str()) {
        return Some(format!("Only zip, rar and 7z packages and Fleasion JSON configs can be installed, this one is .{ext}."));
    }
    if marketplace_url(&f.url) {
        return None;
    }
    if f.size <= 0 || f.size > MAX_PACKAGE {
        return Some("The package is larger than the 256 MB limit.".into());
    }
    if ext == "json" && f.size > 4 * 1024 * 1024 {
        return Some("The replacement config is larger than the 4 MB limit.".into());
    }
    if !java::eq_ignore_case("done", &f.av_state) || !java::eq_ignore_case("clean", &f.av_result) {
        return Some("The GameBanana virus scan did not report the file as clean.".into());
    }
    if !java::eq_ignore_case("done", &f.analysis_state) || !java::eq_ignore_case("ok", &f.analysis_result) {
        return Some("The GameBanana content analysis did not pass.".into());
    }
    if !md5_shape(&f.md5) {
        return Some("The package does not publish a usable checksum.".into());
    }
    None
}

pub fn android_block(f: &ModFile) -> Option<String> {
    if paths::extension(&f.name) == "json" {
        return Some("This is a Fleasion or AssetWarp replacement config. Those replace online Roblox assets through a desktop proxy, which Android cannot run.".into());
    }
    if f.listing_known && !f.listing_roblox && !f.listing_cache && f.listing_config {
        return Some("This pack only has a Fleasion or AssetWarp replacement config, which needs the desktop app.".into());
    }
    if f.listing_known && !f.listing_roblox && !f.listing_cache {
        return Some("This pack replaces online Roblox assets by ID through Fleasion or AssetWarp, which only run on desktop.".into());
    }
    None
}

fn flag_file(name: &str) -> bool {
    let stem = name.rfind('.').map_or(name, |i| &name[..i]);
    let compact: String = stem.chars().filter(char::is_ascii_alphanumeric).collect::<String>().to_lowercase();
    ["fflag", "fastflag", "clientappsettings", "ixpsettings"].iter().any(|t| compact.contains(t))
}

fn flag_list(e: &Entry) -> bool {
    let ok: Vec<&ModFile> = e.files.iter().filter(|f| inspect_file(f).is_none()).collect();
    if ok.is_empty() || ok.iter().any(|f| paths::extension(&f.name) != "json") {
        return false;
    }
    find(FLAG_TERMS, &flatten(&[&e.name, &e.summary, &e.html])).is_some()
}

fn hash_name(name: &str) -> bool {
    md5_shape(name)
}

fn last_segment(rel: &str) -> &str {
    rel.rfind('/').map_or(rel, |i| &rel[i + 1..])
}

fn summarize(src: &placer::Source, f: &mut ModFile, entries: &[String]) {
    let (mut roblox, mut config, mut assets, mut cache) = (false, false, false, false);
    for entry in entries {
        let rel = paths::arch_normalize(entry);
        let name = last_segment(&rel);
        if name.is_empty() {
            continue;
        }
        if hash_name(name) {
            if !rel.to_uppercase().contains("RESTORE") {
                cache = true;
            }
            continue;
        }
        if paths::extension(name) == "json" {
            if !flag_file(name) && placer::resolve(src, &rel).is_none() {
                config = true;
            }
            continue;
        }
        if !paths::installable(&rel) {
            continue;
        }
        assets = true;
        if !roblox && placer::resolve(src, &rel).is_some() {
            roblox = true;
        }
    }
    f.listing_known = true;
    f.listing_roblox = roblox;
    f.listing_config = config;
    f.listing_assets = assets;
    f.listing_cache = cache;
}

fn dangerous_listing(src: &placer::Source, entries: &[String]) -> bool {
    entries.iter().any(|e| {
        let rel = paths::arch_normalize(e);
        paths::dangerous(&paths::extension(&rel)) && !placer::client_copy(src, &rel)
    })
}

fn roblox_mod(src: &placer::Source, entries: &[String], replacement: bool) -> bool {
    for e in entries {
        let rel = paths::arch_normalize(e);
        let name = last_segment(&rel);
        if name.is_empty() {
            continue;
        }
        if paths::extension(name) == "json" || hash_name(name) {
            return true;
        }
        if !paths::installable(&rel) {
            continue;
        }
        if replacement || placer::resolve(src, &rel).is_some() {
            return true;
        }
    }
    false
}

fn text(c: &Client, url: &str, limit: i32) -> Result<String, Error> {
    c.get(url, limit).map(|b| String::from_utf8_lossy(&b).into_owned()).ok_or_else(|| Error::Io("download failed".into()))
}

fn after_noise(text: &str, open: char) -> &str {
    text.find(&format!("\n{open}")).map_or(text, |i| &text[i + 1..])
}

fn parsed(c: &Client, url: &str, want: fn(&Value) -> bool, open: char) -> Result<Value, Error> {
    let t = text(c, url, 8 * 1024 * 1024)?;
    first_json(&t).filter(want).or_else(|| first_json(after_noise(&t, open)).filter(want)).ok_or_else(|| Error::Io(UNEXPECTED.into()))
}

fn json_object(c: &Client, url: &str) -> Result<Value, Error> {
    parsed(c, url, Value::is_object, '{')
}

fn json_array(c: &Client, url: &str) -> Result<Vec<Value>, Error> {
    parsed(c, url, Value::is_array, '[').map(|v| v.as_array().cloned().unwrap_or_default())
}

pub fn categories(c: &Client) -> Result<Vec<Value>, Error> {
    let arr = json_array(c, &format!("{API}/Mod/Categories?_idGameRow={ROBLOX_GAME}&_sSort=a_to_z&_bShowEmpty=false"))?;
    let mut out = Vec::new();
    for o in arr.iter().filter(|o| o.is_object()) {
        if opt_bool(o, "_bIsObsolete", false) {
            continue;
        }
        let id = opt_long(o, "_idRow", 0);
        let name = opt_str(o, "_sName", "");
        if id <= 0 || java::trim(&name).is_empty() || HIDDEN.contains(&name.to_lowercase().as_str()) {
            continue;
        }
        out.push(json!({"id": id, "name": name, "count": opt_int(o, "_nItemCount", 0)}));
    }
    Ok(out)
}

fn game_mismatch(o: &Value) -> bool {
    o.get("_aGame").filter(|g| g.is_object()).is_some_and(|g| g.get("_idRow").is_some() && opt_int(g, "_idRow", 0) != ROBLOX_GAME)
}

fn plain(s: &str) -> String {
    crate::http::html(s, false)
}

fn plain_lines(s: &str) -> String {
    crate::http::html(s, true)
}

fn summarize_html(html: &str) -> String {
    let t = plain(html);
    if java::utf16_len(&t) > 240 { format!("{}...", java::trim(java::utf16_prefix(&t, 240))) } else { t }
}

fn person(o: &Value) -> Person {
    Person {
        name: opt_str(o, "_sName", ""),
        avatar: opt_str(o, "_sAvatarUrl", ""),
        profile: opt_str(o, "_sProfileUrl", ""),
        title: opt_str(o, "_sUserTitle", ""),
        points: opt_int(o, "_nPoints", 0),
    }
}

fn apply_profile(e: &mut Entry, root: &Value) -> bool {
    if game_mismatch(root) {
        return false;
    }
    e.comments.clear();
    e.updates.clear();
    e.issues.clear();
    e.html = opt_str(root, "_sText", "");
    e.summary = summarize_html(&e.html);
    e.downloads = opt_int(root, "_nDownloadCount", 0);
    e.likes = e.likes.max(opt_int(root, "_nLikeCount", 0));
    e.comment_count = opt_int(root, "_nPostCount", 0);
    e.update_count = opt_int(root, "_nUpdatesCount", 0);
    e.issue_count = opt_int(root, "_nAllTodosCount", 0);
    e.license = plain(&opt_str(root, "_sLicense", ""));
    e.submitter = root.get("_aSubmitter").filter(|v| v.is_object()).map(person);
    e.license_rules.clear();
    if let Some(check) = root.get("_aLicenseChecklist").filter(|v| v.is_object()) {
        for (key, label) in [("yes", "Allowed"), ("ask", "Ask first"), ("no", "Not allowed")] {
            for it in check.get(key).and_then(Value::as_array).into_iter().flatten() {
                let t = if it.is_object() { opt_str(it, "_sText", "") } else { value_of(it) };
                if !java::trim(&t).is_empty() {
                    e.license_rules.push((label.into(), plain(&t)));
                }
            }
        }
    }
    e.images.clear();
    e.files.clear();
    let media = root.get("_aPreviewMedia").filter(|v| v.is_object());
    for im in media.map(|m| objects(m, "_aImages")).unwrap_or_default() {
        let base = opt_str(&im, "_sBaseUrl", "");
        let file = opt_str(&im, "_sFile", "");
        let thumb = opt_str(&im, "_sFile220", "");
        if base.is_empty() || file.is_empty() {
            continue;
        }
        let full = format!("{}/{file}", strip_slashes(&base));
        let thumb = if thumb.is_empty() { full.clone() } else { format!("{}/{thumb}", strip_slashes(&base)) };
        if trusted_url(&full) && trusted_url(&thumb) {
            e.images.push(Image { full, thumb, caption: opt_str(&im, "_sCaption", "") });
        }
    }
    for f in objects(root, "_aFiles") {
        let mut m = ModFile::gamebanana(&f);
        m.description = opt_str(&f, "_sDescription", "");
        if let Some(Value::Object(w)) = f.get("_aAnalysisWarnings") {
            for k in w.keys() {
                let k = k.to_lowercase();
                if k.contains("exe") || k.contains("executable") || k.contains("script") {
                    m.executable = true;
                }
            }
        }
        e.files.push(m);
    }
    e.replacement = looks_replacement(e);
    e.detail_loaded = true;
    true
}

fn looks_replacement(e: &Entry) -> bool {
    if e.files.iter().any(|f| paths::extension(&f.name) == "json" && !flag_file(&f.name)) {
        return true;
    }
    let t = format!("{} {} {}", e.name, e.summary, e.html).to_lowercase();
    REPLACEMENT_HINTS.iter().any(|h| t.contains(h))
}

fn fallback_long(o: &Value, keys: &[&str]) -> i64 {
    let mut v = 0;
    for k in keys.iter().rev() {
        v = opt_long(o, k, v);
    }
    v
}

fn read_listing(r: &Value) -> Option<Entry> {
    if !r.is_object() || !java::eq_ignore_case("Mod", &opt_str(r, "_sModelName", "")) {
        return None;
    }
    if opt_bool(r, "_bIsObsolete", false) || opt_bool(r, "_bHasContentRatings", false) || !opt_bool(r, "_bHasFiles", false) || game_mismatch(r) {
        return None;
    }
    let sub_name = |k: &str, f: &str| r.get(k).filter(|v| v.is_object()).map_or(String::new(), |o| opt_str(o, f, ""));
    let mut e = Entry {
        id: opt_long(r, "_idRow", 0),
        name: opt_str(r, "_sName", ""),
        version: opt_str(r, "_sVersion", ""),
        profile_url: opt_str(r, "_sProfileUrl", ""),
        super_category: sub_name("_aRootCategory", "_sName"),
        category: sub_name("_aSubCategory", "_sName"),
        author: sub_name("_aSubmitter", "_sName"),
        author_avatar: sub_name("_aSubmitter", "_sAvatarUrl"),
        likes: opt_int(r, "_nLikeCount", 0),
        views: opt_int(r, "_nViewCount", 0),
        updated: fallback_long(r, &["_tsDateUpdated", "_tsDateModified", "_tsDateAdded"]).wrapping_mul(1000),
        ..Entry::default()
    };
    for t in r.get("_aTags").and_then(Value::as_array).into_iter().flatten() {
        let v = if t.is_object() { opt_str(t, "_sTitle", "") } else { value_of(t) };
        if !java::trim(&v).is_empty() {
            e.tags.push(v);
        }
    }
    let media = r.get("_aPreviewMedia").filter(|v| v.is_object());
    for im in media.map(|m| objects(m, "_aImages")).unwrap_or_default() {
        let base = opt_str(&im, "_sBaseUrl", "");
        let mut file = opt_str(&im, "_sFile220", "");
        if file.is_empty() {
            file = opt_str(&im, "_sFile", "");
        }
        if base.is_empty() || file.is_empty() {
            continue;
        }
        let url = format!("{}/{file}", strip_slashes(&base));
        if trusted_url(&url) {
            e.icon_url = url;
            break;
        }
    }
    Some(e)
}

static LISTINGS: Mutex<Option<HashMap<i64, Vec<String>>>> = Mutex::new(None);

fn listing(c: &Client, file_id: i64) -> Option<Vec<String>> {
    if file_id <= 0 {
        return None;
    }
    if let Some(hit) = LISTINGS.lock().ok().and_then(|m| m.as_ref().and_then(|m| m.get(&file_id).cloned())) {
        return Some(hit);
    }
    let t = text(c, &format!("{API}/File/{file_id}/RawFileList"), 2 * 1024 * 1024).ok()?;
    let lines: Vec<String> = t.split('\n').map(java::trim).filter(|l| !l.is_empty()).map(str::to_string).collect();
    if let Ok(mut m) = LISTINGS.lock() {
        m.get_or_insert_with(HashMap::new).insert(file_id, lines.clone());
    }
    Some(lines)
}

fn probe(c: &Client, src: &placer::Source, entries: &mut [Entry]) {
    let mut work: Vec<(usize, usize)> = Vec::new();
    for (ei, e) in entries.iter().enumerate() {
        for (fi, f) in e.files.iter().enumerate() {
            let ext = paths::extension(&f.name);
            if f.probed || !matches!(ext.as_str(), "zip" | "rar" | "7z") || inspect_file(f).is_some() {
                continue;
            }
            work.push((ei, fi));
        }
    }
    if work.is_empty() {
        return;
    }
    let next = AtomicUsize::new(0);
    let done: Mutex<Vec<(usize, usize, ModFile)>> = Mutex::new(Vec::new());
    let view: &[Entry] = entries;
    std::thread::scope(|s| {
        for _ in 0..work.len().min(8) {
            s.spawn(|| {
                loop {
                    let i = next.fetch_add(1, Ordering::SeqCst);
                    let Some(&(ei, fi)) = work.get(i) else { break };
                    let mut f = view[ei].files[fi].clone();
                    let lines = listing(c, f.id);
                    f.probed = true;
                    if let Some(lines) = lines.filter(|l| !l.is_empty()) {
                        summarize(src, &mut f, &lines);
                        if dangerous_listing(src, &lines) {
                            f.executable = true;
                        } else if !roblox_mod(src, &lines, view[ei].replacement) {
                            f.lacks_roblox = true;
                        }
                    }
                    if let Ok(mut d) = done.lock() {
                        d.push((ei, fi, f));
                    }
                }
            });
        }
    });
    for (ei, fi, f) in done.into_inner().unwrap_or_default() {
        entries[ei].files[fi] = f;
    }
}

fn batch_details(c: &Client, entries: &mut [Entry]) -> Result<(), Error> {
    for chunk in entries.chunks_mut(PAGE_SIZE) {
        let ids = chunk.iter().map(|e| e.id.to_string()).collect::<Vec<_>>().join(",");
        let arr = json_array(c, &format!("{API}/Mod/Multi?_csvRowIds={ids}&_csvProperties={DETAIL}"))?;
        let mut by: HashMap<i64, &Value> = HashMap::new();
        for o in arr.iter().filter(|o| o.is_object()) {
            let id = opt_long(o, "_idRow", 0);
            if id > 0 {
                by.insert(id, o);
            }
        }
        for e in chunk.iter_mut() {
            if let Some(o) = by.get(&e.id) {
                apply_profile(e, o);
            }
        }
    }
    Ok(())
}

pub fn browse(c: &Client, src: &placer::Source, page: i64, sort: &str, category: i64, search: &str) -> Result<Value, Error> {
    let q = java::trim(search);
    let searching = java::utf16_len(q) >= MIN_SEARCH;
    let game = net::uri_encode("_aFilters[Generic_Game]");
    let url = if searching {
        format!("{API}/Util/Search/Results?_sSearchString={}&_idGameRow={ROBLOX_GAME}&_sModelName=Mod&_nPage={page}", net::uri_encode(q))
    } else if category > 0 {
        format!("{API}/Mod/Index?_nPage={page}&_nPerpage={PAGE_SIZE}&{game}={ROBLOX_GAME}&{}={category}", net::uri_encode("_aFilters[Generic_Category]"))
    } else {
        let key = match sort {
            "Newest" => "Generic_Newest",
            "Recently updated" => "Generic_LatestModified",
            _ => "",
        };
        format!("{API}/Mod/Index?_nPage={page}&_nPerpage={PAGE_SIZE}&{game}={ROBLOX_GAME}{}", if key.is_empty() { String::new() } else { format!("&_sSort={key}") })
    };
    let root = json_object(c, &url)?;
    let total = root.get("_aMetadata").filter(|m| m.is_object()).map_or(0, |m| opt_int(m, "_nRecordCount", 0));
    let page_size = if searching { 15 } else { PAGE_SIZE as i32 };
    let Some(records) = root.get("_aRecords").and_then(Value::as_array) else {
        return Ok(json!({"entries": [], "total": total, "page": page, "pageSize": page_size, "returned": 0}));
    };
    let mut seen = HashSet::new();
    let mut candidates: Vec<Entry> = Vec::new();
    for r in records {
        let Some(e) = read_listing(r) else { continue };
        if inspect_listing(&e).is_some() {
            continue;
        }
        if seen.insert(e.id) {
            candidates.push(e);
        }
    }
    batch_details(c, &mut candidates)?;
    probe(c, src, &mut candidates);
    let entries: Vec<Value> = candidates.iter().filter(|e| e.detail_loaded && inspect_detail(e).is_none()).map(Entry::to_json).collect();
    Ok(json!({"entries": entries, "total": total, "page": page, "pageSize": page_size, "returned": records.len()}))
}

fn records(c: &Client, url: &str) -> Vec<Value> {
    let mut out: Vec<Value> = Vec::new();
    for page in 1..=4 {
        let Ok(root) = json_object(c, &format!("{url}{}_nPage={page}", if url.contains('?') { "&" } else { "?" })) else { break };
        let Some(recs) = root.get("_aRecords").and_then(Value::as_array) else { break };
        let before = out.len();
        out.extend(recs.iter().filter(|r| r.is_object()).cloned());
        if out.len() == before {
            break;
        }
        let Some(meta) = root.get("_aMetadata").filter(|m| m.is_object()) else { break };
        if out.len() as i64 >= opt_int(meta, "_nRecordCount", 0) as i64 || opt_bool(meta, "_bIsComplete", false) {
            break;
        }
    }
    out
}

fn poster(p: &mut Post, who: Option<&Value>) {
    let Some(who) = who.filter(|w| w.is_object()) else { return };
    p.author = opt_str(who, "_sName", "");
    p.author_title = plain(&opt_str(who, "_sUserTitle", ""));
    let avatar = opt_str(who, "_sAvatarUrl", "");
    if trusted_url(&avatar) && !avatar.contains("/img/defaults/") {
        p.avatar = avatar;
    }
    let profile = opt_str(who, "_sProfileUrl", "");
    if trusted_url(&profile) {
        p.profile = profile;
    }
}

fn post(r: &Value) -> Post {
    let who = r.get("_aPoster").filter(|v| v.is_object());
    let mut p = Post { removed: who.is_none(), ..Post::default() };
    if p.removed {
        return p;
    }
    p.id = opt_long(r, "_idRow", 0);
    p.reply_count = opt_int(r, "_nReplyCount", 0);
    poster(&mut p, who);
    p.pinned = opt_int(r, "_iPinLevel", 0) > 0;
    p.stamps = opt_int(r, "_nStampScore", 0);
    p.body = plain_lines(&opt_str(r, "_sText", ""));
    p.time = fallback_long(r, &["_tsDateAdded", "_tsDateModified"]).wrapping_mul(1000);
    p
}

fn community(c: &Client, e: &mut Entry) {
    e.comments.clear();
    e.updates.clear();
    e.issues.clear();
    for r in records(c, &format!("{API}/Mod/{}/Posts", e.id)) {
        e.comments.push(post(&r));
    }
    for r in records(c, &format!("{API}/Mod/{}/Updates", e.id)) {
        let mut p = Post::default();
        poster(&mut p, r.get("_aSubmitter"));
        for item in r.get("_aChangeLog").and_then(Value::as_array).into_iter().flatten() {
            let t = if item.is_object() { plain(&opt_str(item, "text", "")) } else { String::new() };
            if !t.is_empty() {
                p.changes.push((plain(&opt_str(item, "cat", "")), t));
            }
        }
        p.title = opt_str(&r, "_sName", "");
        let version = opt_str(&r, "_sVersion", "");
        if let Some(first) = version.chars().next() {
            p.status = if java::char_digit(first).is_some() { format!("v{version}") } else { version };
        }
        p.body = plain_lines(&opt_str(&r, "_sText", ""));
        p.time = fallback_long(&r, &["_tsDateAdded", "_tsDateModified"]).wrapping_mul(1000);
        e.updates.push(p);
    }
    for r in records(c, &format!("{API}/Mod/{}/Todos", e.id)) {
        let mut p = Post::default();
        let who = r.get("_aSubmitter").filter(|v| v.is_object()).or_else(|| r.get("_aPoster"));
        poster(&mut p, who);
        p.title = opt_str(&r, "_sName", &opt_str(&r, "_sTitle", ""));
        if p.title.is_empty() {
            p.title = opt_str(&r, "_sTitle", "");
        }
        p.status = opt_str(&r, "_sStatus", "");
        let mut body = opt_str(&r, "_sText", "");
        if java::trim(&body).is_empty() {
            body = opt_str(&r, "_sDescription", "");
        }
        p.body = plain_lines(&body);
        p.time = fallback_long(&r, &["_tsDateAdded", "_tsDateModified"]).wrapping_mul(1000);
        e.issues.push(p);
    }
    e.comment_count = e.comment_count.max(e.comments.len() as i32);
    e.update_count = e.update_count.max(e.updates.len() as i32);
    e.issue_count = e.issue_count.max(e.issues.len() as i32);
}

pub fn detail(c: &Client, src: &placer::Source, e: &mut Entry) -> Result<bool, Error> {
    if e.source == GAMEBANANA {
        if !e.detail_loaded {
            let root = json_object(c, &format!("{API}/Mod/{}/ProfilePage", e.id))?;
            if !apply_profile(e, &root) {
                return Ok(false);
            }
        }
        probe(c, src, std::slice::from_mut(e));
        if inspect_detail(e).is_some() {
            return Ok(false);
        }
        community(c, e);
        return Ok(true);
    }
    if inspect_detail(e).is_some() {
        return Ok(false);
    }
    if !e.icon_url.is_empty() && e.images.is_empty() {
        e.images.push(Image { full: e.icon_url.clone(), thumb: e.icon_url.clone(), caption: e.name.clone() });
    }
    Ok(true)
}

fn replies(c: &Client, p: &mut Post, depth: i32, budget: &mut i32) {
    if p.reply_count <= 0 || p.id <= 0 || depth >= 8 || *budget <= 0 || !p.replies.is_empty() {
        return;
    }
    *budget -= 1;
    for r in records(c, &format!("{API}/Post/{}/Posts", p.id)) {
        p.replies.push(post(&r));
    }
    for child in p.replies.iter_mut() {
        replies(c, child, depth + 1, budget);
    }
}

pub fn load_replies(c: &Client, posts: &mut [Post]) {
    let mut budget = 40;
    for p in posts.iter_mut() {
        replies(c, p, 0, &mut budget);
    }
}

fn stable_id(value: &str) -> i64 {
    let mut hash: i64 = 5381;
    for u in value.encode_utf16() {
        let low = char::from_u32(u as u32).map_or(u as i64, |c| java::lower_char(c) as i64);
        hash = ((hash << 5) + hash + low) & 0x7FFF_FFFF;
    }
    hash
}

fn shorten(s: &str) -> String {
    if java::utf16_len(s) > 240 { format!("{}...", java::trim(java::utf16_prefix(s, 240))) } else { s.to_string() }
}

pub fn marketplace(c: &Client, sort: &str, search: &str) -> Result<Value, Error> {
    let arr = json_array(c, &format!("{MARKET_RAW}index.json"))?;
    let q = java::trim(search).to_lowercase();
    let mut found: Vec<Entry> = Vec::new();
    for o in arr.iter().filter(|o| o.is_object()) {
        let id = opt_str(o, "id", "");
        let name = opt_str(o, "name", "");
        let download = opt_str(o, "download", "");
        if java::trim(&id).is_empty() || java::trim(&name).is_empty() || !marketplace_url(&download) {
            continue;
        }
        let thumb = opt_str(o, "thumbnail", "");
        let html = opt_str(o, "description", "");
        let summary = shorten(java::trim(&html.replace('\n', " ")));
        let e = Entry {
            id: stable_id(&id),
            slug: id.clone(),
            name,
            author: opt_str(o, "author", ""),
            profile_url: MARKET_SOURCE.into(),
            icon_url: if marketplace_url(&thumb) { thumb } else { String::new() },
            category: "Roblox file mod".into(),
            super_category: "Marketplace".into(),
            source: MARKETPLACE.into(),
            html,
            summary,
            files: vec![ModFile {
                name: format!("{id}.zip"),
                url: download,
                av_state: "done".into(),
                av_result: "clean".into(),
                analysis_state: "done".into(),
                analysis_result: "ok".into(),
                ..ModFile::default()
            }],
            detail_loaded: true,
            ..Entry::default()
        };
        if !q.is_empty() && !e.name.to_lowercase().contains(&q) && !e.summary.to_lowercase().contains(&q) && !e.author.to_lowercase().contains(&q) {
            continue;
        }
        if inspect_detail(&e).is_some() {
            continue;
        }
        found.push(e);
    }
    if sort == "Name" {
        found.sort_by(|a, b| java::cmp_ignore_case(&a.name, &b.name));
    }
    let n = found.len();
    Ok(json!({"entries": found.iter().map(Entry::to_json).collect::<Vec<_>>(), "total": n, "page": 1, "pageSize": n, "returned": 0}))
}

fn file_for(c: &Client, mod_id: i64, file_id: i64) -> Result<ModFile, Error> {
    let root = json_object(c, &format!("{API}/Mod/{mod_id}?_csvProperties=_aFiles"))?;
    objects(&root, "_aFiles")
        .iter()
        .find(|f| opt_long(f, "_idRow", 0) == file_id)
        .map(|f| ModFile { id: file_id, ..ModFile::gamebanana(f) })
        .ok_or_else(|| Error::Io("That GameBanana file is no longer available.".into()))
}

pub fn by_id(c: &Client, mod_id: i64, file_id: i64, name: &str) -> Result<Value, Error> {
    let profile = json_object(c, &format!("{API}/Mod/{mod_id}/ProfilePage"))?;
    let mut e = Entry { id: mod_id, name: name.to_string(), ..Entry::default() };
    apply_profile(&mut e, &profile);
    e.profile_url = opt_str(&profile, "_sProfileUrl", &format!("https://gamebanana.com/mods/{mod_id}"));
    e.author = profile.get("_aSubmitter").filter(|v| v.is_object()).map_or(String::new(), |s| opt_str(s, "_sName", ""));
    let f = match e.files.iter().find(|m| m.id == file_id) {
        Some(m) => m.clone(),
        None => file_for(c, mod_id, file_id)?,
    };
    Ok(json!({"entry": e.to_json(), "file": f.to_json()}))
}

pub fn safe_name(name: &str, id: i64) -> String {
    let replaced: String = name.chars().map(|c| if matches!(c, '\\' | '/' | ':' | '*' | '?' | '"' | '<' | '>' | '|') { ' ' } else { c }).collect();
    let mut n = String::new();
    let mut space = false;
    for c in replaced.chars() {
        if java::regex_space(c) {
            space = true;
            continue;
        }
        if space {
            n.push(' ');
            space = false;
        }
        n.push(c);
    }
    if space {
        n.push(' ');
    }
    let mut n = java::trim(&n).to_string();
    if java::utf16_len(&n) > 60 {
        n = java::trim(java::utf16_prefix(&n, 60)).to_string();
    }
    if n.is_empty() { format!("Community mod {id}") } else { n }
}

pub fn md5(path: &Path) -> String {
    hash::file(hash::Md5::new(), path, b"").map_or(String::new(), |d| hash::hex(&d))
}

static BLOBS: Mutex<Option<HashMap<String, String>>> = Mutex::new(None);

fn blobs(c: &Client) -> Option<HashMap<String, String>> {
    let mut guard = BLOBS.lock().ok()?;
    if guard.is_none() {
        let root = json_object(c, MARKET_TREE).ok()?;
        let mut out = HashMap::new();
        for n in root.get("tree").and_then(Value::as_array).into_iter().flatten().filter(|n| n.is_object()) {
            out.insert(opt_str(n, "path", "").to_lowercase(), opt_str(n, "sha", ""));
        }
        *guard = Some(out);
    }
    guard.clone()
}

pub fn verify_blob(c: &Client, slug: &str, path: &Path) -> Result<(), Error> {
    let Some(expected) = blobs(c).and_then(|m| m.get(&format!("mods/{slug}.zip").to_lowercase()).cloned()) else {
        return Ok(());
    };
    let len = std::fs::metadata(path).map(|m| m.len()).map_err(|e| Error::Io(e.to_string()))?;
    let d = hash::file(hash::Sha1::new(), path, format!("blob {len}\0").as_bytes()).ok_or_else(|| Error::Io("read".into()))?;
    if !java::eq_ignore_case(&expected, &hash::hex(&d)) {
        return Err(Error::rejected("The download did not match the hash published by the marketplace repository, so it was discarded."));
    }
    Ok(())
}

pub fn prune(dir: &Path) {
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    let mut files: Vec<(std::path::PathBuf, u64, std::time::SystemTime)> = rd
        .flatten()
        .filter_map(|e| {
            let m = e.metadata().ok()?;
            Some((e.path(), m.len(), m.modified().unwrap_or(std::time::UNIX_EPOCH)))
        })
        .collect();
    files.sort_by_key(|f| f.2);
    let mut total: u64 = files.iter().map(|f| f.1).sum();
    for (p, len, _) in files {
        if total <= 256 * 1024 * 1024 {
            break;
        }
        total -= len;
        let _ = std::fs::remove_file(&p);
    }
}
