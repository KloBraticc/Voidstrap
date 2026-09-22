use crate::java::{self, opt_bool, opt_long, opt_str, regex_space, trim, utf16_len, utf16_prefix, word_boundary};
use serde_json::{Value, json};

const DOWNLOAD: &str = "https://github.com/KloBraticc/Voidstrap/releases";
const DISCORD: &str = "https://discord.gg/bzdbHHytFR";
const GITHUB: &str = "https://github.com/KloBraticc/Voidstrap";
const LOGO: &str = "https://raw.githubusercontent.com/KloBraticc/Voidstrap/main/src/Voidstrap.App/Voidstrap.png";

const IDLE: [(&str, &str); 3] = [
    ("blue", "https://upload.wikimedia.org/wikipedia/commons/thumb/f/f2/Roblox_%282025%29_%28App_Icon%29.svg/250px-Roblox_%282025%29_%28App_Icon%29.svg.png"),
    ("black", "https://devforum-uploads.s3.dualstack.us-east-2.amazonaws.com/uploads/original/5X/c/4/c/2/c4c28132e4907f73ea0430d18d769c06276e39cc.png"),
    ("old", "https://static.wikia.nocookie.net/logopedia/images/b/b7/ROBLOX_2006-2009.svg/revision/latest/scale-to-width-down/1000?cb=20250403121056"),
];

#[derive(Clone, Copy)]
enum Tok {
    Lit(&'static str),
    Sep,
    Spaces,
    Opt(&'static str),
}

use Tok::{Lit, Opt, Sep, Spaces};

const MARKERS: [(&[Tok], &str); 18] = [
    (&[Lit("pre"), Sep, Lit("alpha")], "[PRE-ALPHA]"),
    (&[Lit("early"), Spaces, Lit("access")], "[EARLY ACCESS]"),
    (&[Lit("open"), Spaces, Lit("beta")], "[OPEN BETA]"),
    (&[Lit("closed"), Spaces, Lit("beta")], "[CLOSED BETA]"),
    (&[Lit("in"), Spaces, Lit("works")], "[IN WORKS]"),
    (&[Lit("in"), Sep, Lit("dev"), Opt("elopment")], "[IN DEV]"),
    (&[Lit("under"), Spaces, Lit("construction")], "[WIP]"),
    (&[Lit("work"), Spaces, Lit("in"), Spaces, Lit("progress")], "[WIP]"),
    (&[Lit("beta")], "[BETA]"),
    (&[Lit("alpha")], "[ALPHA]"),
    (&[Lit("testing")], "[TESTING]"),
    (&[Lit("preview")], "[PREVIEW]"),
    (&[Lit("prototype")], "[PROTOTYPE]"),
    (&[Lit("experimental")], "[EXPERIMENTAL]"),
    (&[Lit("unreleased")], "[UNRELEASED]"),
    (&[Lit("snapshot")], "[SNAPSHOT]"),
    (&[Lit("demo")], "[DEMO]"),
    (&[Lit("wip")], "[WIP]"),
];

fn folded(c: char) -> Option<&'static str> {
    const LOWER: &str = "abcdefghijklmnopqrstuvwxyz";
    Some(match c {
        'a'..='z' => &LOWER[c as usize - 'a' as usize..][..1],
        'A'..='Z' => &LOWER[c as usize - 'A' as usize..][..1],
        '\u{17F}' => "s",
        '\u{212A}' => "k",
        '\u{DF}' | '\u{1E9E}' => "ss",
        '\u{FB00}' => "ff",
        '\u{FB01}' => "fi",
        '\u{FB02}' => "fl",
        '\u{FB03}' => "ffi",
        '\u{FB04}' => "ffl",
        '\u{FB05}' | '\u{FB06}' => "st",
        _ => return None,
    })
}

fn literal(s: &[char], mut at: usize, word: &str) -> Option<usize> {
    let mut rest = word;
    while !rest.is_empty() {
        rest = rest.strip_prefix(folded(*s.get(at)?)?)?;
        at += 1;
    }
    Some(at)
}

fn spaces(s: &[char], mut at: usize) -> usize {
    while s.get(at).is_some_and(|&c| regex_space(c)) {
        at += 1;
    }
    at
}

fn words(s: &[char], mut at: usize, toks: &[Tok]) -> Vec<usize> {
    for (i, t) in toks.iter().enumerate() {
        match *t {
            Lit(w) => match literal(s, at, w) {
                Some(n) => at = n,
                None => return Vec::new(),
            },
            Sep => {
                if s.get(at).is_some_and(|&c| c == '-' || regex_space(c)) {
                    at += 1;
                }
            }
            Spaces => {
                let n = spaces(s, at);
                if n == at {
                    return Vec::new();
                }
                at = n;
            }
            Opt(w) => {
                let mut ends = Vec::new();
                if let Some(n) = literal(s, at, w) {
                    ends.extend(words(s, n, &toks[i + 1..]));
                }
                ends.extend(words(s, at, &toks[i + 1..]));
                return ends;
            }
        }
    }
    vec![at]
}

fn marker_at(s: &[char], p: usize, toks: &[Tok]) -> Option<usize> {
    if matches!(s[p], '[' | '(' | '{') {
        return words(s, spaces(s, p + 1), toks).into_iter().find_map(|e| {
            let r = spaces(s, e);
            s.get(r).is_some_and(|c| matches!(c, ']' | ')' | '}')).then_some(r + 1)
        });
    }
    if !word_boundary(s, p) {
        return None;
    }
    words(s, p, toks).into_iter().any(|e| word_boundary(s, e) && spaces(s, e) == s.len()).then_some(s.len())
}

fn replace_all(s: &[char], with: &str, mut at: impl FnMut(&[char], usize) -> Option<usize>) -> (Vec<char>, bool) {
    let mut out = Vec::new();
    let mut found = false;
    let mut p = 0;
    let mut copied = 0;
    while p < s.len() {
        match at(s, p) {
            Some(end) => {
                out.extend_from_slice(&s[copied..p]);
                out.extend(with.chars());
                found = true;
                copied = end;
                p = end;
            }
            None => p += 1,
        }
    }
    out.extend_from_slice(&s[copied..]);
    (out, found)
}

fn empty_bracket(s: &[char], p: usize) -> Option<usize> {
    if !matches!(s[p], '[' | '(' | '{') {
        return None;
    }
    let r = spaces(s, p + 1);
    s.get(r).is_some_and(|c| matches!(c, ']' | ')' | '}')).then_some(r + 1)
}

fn space_run(s: &[char], p: usize) -> Option<usize> {
    let r = spaces(s, p);
    (r - p >= 2).then_some(r)
}

fn leftover(s: &[char], p: usize) -> Option<usize> {
    let mut r = spaces(s, p);
    let start = r;
    while s.get(r).is_some_and(|c| matches!(c, '-' | '|' | ':' | '~' | '/' | ',')) {
        r += 1;
    }
    (r > start && spaces(s, r) == s.len()).then_some(s.len())
}

fn collect(s: &[char]) -> String {
    s.iter().collect()
}

pub fn beta_tag(name: &str) -> (String, Option<&'static str>) {
    if trim(name).is_empty() {
        return (name.to_string(), None);
    }
    let mut working: Vec<char> = name.chars().collect();
    let mut tag = None;
    for (toks, label) in MARKERS {
        let (next, found) = replace_all(&working, " ", |s, p| marker_at(s, p, toks));
        if found {
            tag.get_or_insert(label);
            working = next;
        }
    }
    let Some(tag) = tag else { return (name.to_string(), None) };
    let working = replace_all(&working, " ", empty_bracket).0;
    let working: Vec<char> = trim(&collect(&replace_all(&working, " ", space_run).0)).chars().collect();
    let working = trim(&collect(&replace_all(&working, "", leftover).0)).to_string();
    (if working.is_empty() { name.to_string() } else { working }, Some(tag))
}

fn server_name(v: &Value) -> String {
    match v {
        Value::Object(m) => {
            for (k, x) in m {
                if let Value::String(s) = x {
                    if matches!(k.to_lowercase().as_str(), "servername" | "reservedservername" | "privateservername") {
                        return s.clone();
                    }
                }
                let nested = server_name(x);
                if !nested.is_empty() {
                    return nested;
                }
            }
            String::new()
        }
        Value::Array(a) => a.iter().map(server_name).find(|n| !n.is_empty()).unwrap_or_default(),
        _ => String::new(),
    }
}

pub fn reserved_name(launch: &str) -> String {
    if trim(launch).is_empty() {
        return String::new();
    }
    java::lenient(launch).map(|v| server_name(&v)).unwrap_or_default()
}

fn on(a: &Value, key: &str) -> bool {
    opt_bool(a, key, false)
}

pub fn shown(custom: &str, name: &str) -> String {
    let custom = trim(custom);
    if !custom.is_empty() {
        return custom.to_string();
    }
    if trim(name).is_empty() { "Private experience".to_string() } else { name.to_string() }
}

fn small(settings: &Value, account: &Value) -> (String, String) {
    let image = opt_str(account, "image", "");
    if !on(settings, "account") || image.is_empty() {
        return (LOGO.to_string(), "Voidstrap".to_string());
    }
    (image, opt_str(account, "text", ""))
}

#[allow(clippy::too_many_arguments)]
fn presence(details: String, state: String, image: String, text: String, small: (String, String), start: i64, status: bool, buttons: Vec<[String; 2]>) -> Value {
    json!({"details": details, "state": state, "largeImage": image, "largeText": text, "smallImage": small.0, "smallText": small.1, "start": start, "detailsStatus": status, "buttons": buttons})
}

static ACCOUNTS: std::sync::Mutex<Vec<(i64, bool, Value)>> = std::sync::Mutex::new(Vec::new());

fn roblox_image(url: &str) -> bool {
    let host = url.strip_prefix("https://").and_then(|r| r.split(['/', '?', '#']).next()).unwrap_or("");
    let host = host.rsplit('@').next().unwrap_or(host).split(':').next().unwrap_or("");
    url.len() <= 250 && (host.ends_with(".rbxcdn.com") || host.ends_with(".roblox.com"))
}

pub fn account(client: &crate::net::Client, user: i64, circular: bool) -> Option<Value> {
    if user <= 0 {
        return None;
    }
    if let Some(hit) = ACCOUNTS.lock().ok()?.iter().find(|(id, c, _)| *id == user && *c == circular).map(|(_, _, v)| v.clone()) {
        return Some(hit);
    }
    let data = client.json(&format!("https://users.roblox.com/v1/users/{user}")).filter(Value::is_object)?;
    let size = if circular { "150x150" } else { "180x180" };
    let thumbs = client.json(&format!("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={user}&size={size}&format=Png&isCircular={circular}"))?;
    let head = thumbs.get("data")?.as_array()?.first().cloned().unwrap_or(Value::Null);
    let image = opt_str(&head, "imageUrl", "");
    if !image.starts_with("https://") || (circular && !roblox_image(&image)) {
        return None;
    }
    let display = opt_str(&data, "displayName", "");
    let name = opt_str(&data, "name", "");
    let text = if !circular { format!("{display} (@{name})") } else if display.is_empty() { name } else { display };
    let v = json!({"image": image, "text": text});
    if let Ok(mut cache) = ACCOUNTS.lock() {
        if cache.len() >= 256 {
            cache.remove(0);
        }
        cache.push((user, circular, v.clone()));
    }
    Some(v)
}

pub fn game(settings: &Value, data: &Value, game: &Value, account: &Value, flag_count: i64) -> Value {
    let shown_name = shown(&opt_str(settings, "customName", ""), &opt_str(game, "name", ""));
    let (name, tag) = if on(settings, "beta") { beta_tag(&shown_name) } else { (shown_name.clone(), None) };
    let kind = opt_str(data, "serverType", "PUBLIC");
    let launch = opt_str(data, "launchData", "");
    let location = opt_str(game, "location", "");
    let mut details = Vec::new();
    if on(settings, "name") {
        details.push(name);
    }
    if on(settings, "server") {
        details.push(match kind.as_str() {
            "PRIVATE" => "Private Server".to_string(),
            "RESERVED" => match reserved_name(&launch) {
                r if r.is_empty() => "Reserved Server".to_string(),
                r => format!("Reserved Server: {r}"),
            },
            _ => "Public Server".to_string(),
        });
    }
    if on(settings, "location") && !location.is_empty() {
        details.push(location);
    }
    let mut detail = details.join(" | ");
    if let Some(tag) = tag {
        detail = if detail.is_empty() { tag.to_string() } else { format!("{detail} {tag}") };
    }
    let creator = opt_str(game, "creator", "");
    let mut state = if on(settings, "creator") && !creator.is_empty() {
        format!("by {creator}{}", if opt_bool(game, "verified", false) { " \u{2611}\u{fe0f}" } else { "" })
    } else {
        String::new()
    };
    if on(settings, "flagCount") {
        state = if state.is_empty() { format!("FFlags: {flag_count}") } else { format!("{state} | FFlags: {flag_count}") };
    }
    let custom_icon = trim(&opt_str(settings, "customIcon", "")).to_string();
    let icon = on(settings, "icon");
    let image = if !custom_icon.is_empty() { custom_icon.clone() } else if icon { opt_str(game, "icon", "") } else { String::new() };
    let text = if custom_icon.is_empty() && icon { shown_name } else { String::new() };
    let place = opt_long(data, "placeId", 0);
    let job = opt_str(data, "jobId", "");
    let mut buttons = Vec::new();
    if on(settings, "joining") && (kind == "PUBLIC" || (kind == "RESERVED" && !launch.is_empty())) && !job.is_empty() {
        let mut link = format!("https://www.roblox.com/games/start?placeId={place}&gameInstanceId={job}");
        if !launch.is_empty() {
            link += &format!("&launchData={}", crate::net::uri_encode(&launch));
        }
        buttons.push(["Join server".to_string(), link]);
    }
    buttons.push(["Game Page".to_string(), format!("https://www.roblox.com/games/{place}")]);
    let status = !trim(&detail).is_empty();
    presence(detail, state, image, text, small(settings, account), opt_long(data, "joined", 0), status, buttons)
}

pub fn idle(icon: &str, start: i64, settings: &Value, account: &Value) -> Value {
    let url = IDLE.iter().find(|(k, _)| *k == icon).unwrap_or(&IDLE[0]).1;
    presence("Inside Voidstrap".into(), "Browsing Roblox".into(), url.into(), "Roblox".into(), small(settings, account), start, true, Vec::new())
}

fn field(current: &str, value: &str, original: &str) -> String {
    if value.is_empty() || utf16_len(value) > 128 {
        return current.to_string();
    }
    if value == "<reset>" { original.to_string() } else { value.to_string() }
}

pub fn apply_rpc(current: &Value, original: &Value, text: &str) -> Option<Value> {
    let data = java::lenient(text).filter(Value::is_object)?;
    let mut p = current.clone();
    let get = |o: &Value, k: &str| opt_str(o, k, "");
    p["details"] = field(&get(current, "details"), &get(&data, "details"), &get(original, "details")).into();
    p["state"] = field(&get(current, "state"), &get(&data, "state"), &get(original, "state")).into();
    for (key, image_key, text_key) in [("smallImage", "smallImage", "smallText"), ("largeImage", "largeImage", "largeText")] {
        let Some(image) = data.get(key).filter(|v| v.is_object()) else { continue };
        if on(image, "clear") {
            p[image_key] = "".into();
        } else if on(image, "reset") {
            p[image_key] = get(original, image_key).into();
            p[text_key] = get(original, text_key).into();
        } else if image.get("CustomKey").is_some_and(|k| !k.is_null() && !get(image, "CustomKey").is_empty()) {
            p[image_key] = get(image, "CustomKey").into();
        } else {
            let asset = opt_long(image, "assetId", 0);
            if asset > 0 {
                p[image_key] = format!("https://assetdelivery.roblox.com/v1/asset/?id={asset}").into();
            }
            let hover = get(image, "hoverText");
            if !hover.is_empty() {
                p[text_key] = hover.into();
            }
        }
    }
    Some(p)
}

fn clip(value: &str) -> String {
    let flat = value.replace(['\r', '\n'], " ");
    let spaced: Vec<char> = trim(&flat).chars().collect();
    let mut out = String::new();
    let mut run = 0;
    for &c in &spaced {
        run = if c == ' ' { run + 1 } else { 0 };
        if run < 2 {
            out.push(c);
        }
    }
    if utf16_len(&out) > 128 { format!("{}...", trim(utf16_prefix(&out, 125))) } else { out }
}

pub fn app(a: &Value) -> Value {
    let scene = on(a, "scene");
    let mut details = clip(&opt_str(a, "details", ""));
    let mut state = clip(&opt_str(a, "state", ""));
    if utf16_len(&details) < 2 {
        details = "Voidstrap".into();
    }
    if utf16_len(&state) < 2 {
        state = "Exploring Voidstrap".into();
    }
    let https = |k: &str| Some(opt_str(a, k, "")).filter(|v| scene && v.starts_with("https://"));
    let version = format!("Voidstrap Android v{}", opt_str(a, "version", ""));
    let avatar = a.get("avatar").cloned().unwrap_or(Value::Null);
    let (image, text, small) = match https("image") {
        Some(image) => {
            let text = opt_str(a, "imageText", "");
            (image, clip(if text.is_empty() { &details } else { &text }), (LOGO.to_string(), version))
        }
        None => {
            let face = opt_str(&avatar, "image", "");
            let who = opt_str(&avatar, "text", "");
            let small = if face.is_empty() { (String::new(), String::new()) } else { (face, if who.is_empty() { "Roblox".to_string() } else { who }) };
            (LOGO.to_string(), version, small)
        }
    };
    let buttons = match https("buttonUrl") {
        Some(url) => {
            let label = opt_str(a, "buttonLabel", "");
            let label = if label.is_empty() { "Open".to_string() } else { utf16_prefix(&label, 32).to_string() };
            vec![[label, url], ["Get Voidstrap".to_string(), DOWNLOAD.to_string()]]
        }
        None => vec![["Discord".to_string(), DISCORD.to_string()], ["Github".to_string(), GITHUB.to_string()]],
    };
    presence(details, state, image, text, small, opt_long(a, "start", 0), true, buttons)
}
