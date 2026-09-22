use crate::java;
use serde_json::{Value, json};

pub const MAX_INPUT: usize = 2048;

pub struct Link {
    pub place: i64,
    pub instance: Option<String>,
    pub code: Option<String>,
    pub passthrough: Option<String>,
}

impl Link {
    pub fn uri(&self) -> String {
        if let Some(p) = &self.passthrough {
            return p.clone();
        }
        let mut s = format!("roblox://experiences/start?placeId={}", self.place);
        if let Some(i) = &self.instance {
            s.push_str(&format!("&gameInstanceId={}", encode(i)));
        }
        if let Some(c) = &self.code {
            s.push_str(&format!("&linkCode={}", encode(c)));
        }
        s
    }

    pub fn stable_id(&self) -> String {
        if let Some(p) = &self.passthrough {
            return format!("link_{:x}", java::hash(p) as u32);
        }
        let mut id = format!("game_{}", self.place);
        if let Some(i) = &self.instance {
            id.push_str(&format!("_i{:x}", java::hash(i) as u32));
        }
        if let Some(c) = &self.code {
            id.push_str(&format!("_p{:x}", java::hash(c) as u32));
        }
        id
    }

    pub fn to_json(&self) -> Value {
        json!({"placeId": self.place, "instanceId": self.instance, "linkCode": self.code, "isPlace": self.passthrough.is_none(), "uri": self.uri(), "id": self.stable_id()})
    }
}

fn encode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'_' | b'-' | b'!' | b'.' | b'~' | b'\'' | b'(' | b')' | b'*' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

fn decode(s: &str, plus: bool) -> String {
    let mut out = String::new();
    let mut bytes: Vec<u8> = Vec::new();
    let flush = |out: &mut String, bytes: &mut Vec<u8>| {
        if !bytes.is_empty() {
            out.push_str(&String::from_utf8_lossy(bytes));
            bytes.clear();
        }
    };
    let mut it = s.chars();
    while let Some(c) = it.next() {
        match c {
            '+' => {
                flush(&mut out, &mut bytes);
                out.push(if plus { ' ' } else { '+' });
            }
            '%' => {
                let mut v: u8 = 0;
                for _ in 0..2 {
                    let Some(h) = it.next() else {
                        flush(&mut out, &mut bytes);
                        out.push('\u{FFFD}');
                        return out;
                    };
                    match h.to_digit(16) {
                        Some(d) => v = v.wrapping_mul(16).wrapping_add(d as u8),
                        None => {
                            flush(&mut out, &mut bytes);
                            out.push('\u{FFFD}');
                            break;
                        }
                    }
                }
                bytes.push(v);
            }
            c => {
                flush(&mut out, &mut bytes);
                out.push(c);
            }
        }
    }
    flush(&mut out, &mut bytes);
    out
}

pub(crate) struct Uri<'a> {
    s: &'a str,
    ssi: Option<usize>,
}

impl<'a> Uri<'a> {
    pub(crate) fn parse(s: &'a str) -> Uri<'a> {
        Uri { s, ssi: s.find(':') }
    }

    fn scheme(&self) -> Option<&'a str> {
        self.ssi.map(|i| &self.s[..i])
    }

    fn after(&self) -> usize {
        self.ssi.map_or(0, |i| i + 1)
    }

    fn has_authority(&self) -> bool {
        self.s[self.after()..].starts_with("//")
    }

    fn authority(&self) -> Option<&'a str> {
        if !self.has_authority() {
            return None;
        }
        let start = self.after() + 2;
        let end = self.s[start..].find(['/', '\\', '?', '#']).map_or(self.s.len(), |i| start + i);
        Some(&self.s[start..end])
    }

    pub(crate) fn host(&self) -> Option<String> {
        let a = self.authority()?;
        let user = a.rfind('@').map_or(0, |i| i + 1);
        let mut port = None;
        for (i, b) in a.bytes().enumerate().rev() {
            if b == b':' {
                port = Some(i);
                break;
            }
            if !b.is_ascii_digit() {
                break;
            }
        }
        Some(decode(port.map_or(&a[user..], |p| &a[user..p]), false))
    }

    fn path(&self) -> Option<&'a str> {
        if let Some(i) = self.ssi
            && self.s.as_bytes().get(i + 1) != Some(&b'/')
        {
            return None;
        }
        let len = self.s.len();
        let start = if self.has_authority() {
            let from = self.after() + 2;
            match self.s[from..].find(['?', '#', '/', '\\']) {
                None => len,
                Some(k) => match self.s.as_bytes()[from + k] {
                    b'?' | b'#' => return Some(""),
                    _ => from + k,
                },
            }
        } else {
            self.after()
        };
        let end = self.s[start..].find(['?', '#']).map_or(len, |k| start + k);
        Some(&self.s[start..end])
    }

    fn segments(&self) -> Vec<String> {
        self.path().map_or_else(Vec::new, |p| p.split('/').filter(|x| !x.is_empty()).map(|x| decode(x, false)).collect())
    }

    fn query(&self) -> Option<&'a str> {
        let from = self.ssi.unwrap_or(0);
        let q = from + self.s[from..].find('?')?;
        match self.s[from..].find('#').map(|f| from + f) {
            None => Some(&self.s[q + 1..]),
            Some(f) if f < q => None,
            Some(f) => Some(&self.s[q + 1..f]),
        }
    }

    fn fragment(&self) -> Option<&'a str> {
        let from = self.ssi.unwrap_or(0);
        self.s[from..].find('#').map(|f| &self.s[from + f + 1..])
    }

    fn param(&self, key: &str) -> Option<String> {
        let q = self.query()?;
        let key = encode(key);
        for part in q.split('&') {
            let name = part.split_once('=').map_or(part, |(n, _)| n);
            if name == key {
                return Some(part.split_once('=').map_or(String::new(), |(_, v)| decode(v, true)));
            }
        }
        None
    }
}

fn digits(s: &str) -> bool {
    let n = s.chars().count();
    (1..=18).contains(&n) && s.chars().all(|c| java::digit(c).is_some())
}

fn parse_id(s: Option<&str>) -> i64 {
    match s {
        Some(s) if digits(s) => s.chars().try_fold(0i64, |acc, c| acc.checked_mul(10)?.checked_add(java::char_digit(c)? as i64)).unwrap_or(0),
        _ => 0,
    }
}

fn blank_to_null(s: Option<String>) -> Option<String> {
    s.map(|s| java::trim(&s).to_string()).filter(|s| !s.is_empty())
}

pub fn place(place: i64, instance: Option<String>, code: Option<String>) -> Option<Link> {
    if place <= 0 {
        return None;
    }
    let instance = blank_to_null(instance);
    let code = blank_to_null(code);
    if instance.is_some() && code.is_some() {
        return None;
    }
    if let Some(i) = &instance {
        let n = i.len();
        if !((8..=64).contains(&n) && i.bytes().all(|b| b.is_ascii_hexdigit() || b == b'-')) {
            return None;
        }
    }
    if let Some(c) = &code {
        let n = c.len();
        if !((1..=128).contains(&n) && c.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')) {
            return None;
        }
    }
    Some(Link { place, instance, code, passthrough: None })
}

fn pass(uri: String) -> Option<Link> {
    Some(Link { place: 0, instance: None, code: None, passthrough: Some(uri) })
}

fn region_placeid(rest: &str) -> Option<&str> {
    let mut chars = rest.char_indices();
    for want in "placeId=".chars() {
        let (_, c) = chars.next()?;
        if c.len_utf16() > 1 || !java::eq_ignore_case(&c.to_string(), &want.to_string()) {
            return None;
        }
    }
    Some(chars.next().map_or("", |(i, _)| &rest[i..]))
}

fn from_scheme(s: &str) -> Option<Link> {
    let sep = s.find("://")?;
    let rest = &s[sep + 3..];
    if let Some(tail) = region_placeid(rest) {
        let q = format!("roblox://experiences/start?placeId={tail}");
        let u = Uri::parse(&q);
        return place(parse_id(u.param("placeId").as_deref()), u.param("gameInstanceId"), u.param("linkCode"));
    }
    let full = format!("roblox://{rest}");
    let u = Uri::parse(&full);
    let path = u.path().map(|p| decode(p, false));
    if u.host().is_some_and(|h| java::eq_ignore_case("experiences", &h)) && path.is_some_and(|p| java::eq_ignore_case("/start", &p)) {
        return place(parse_id(u.param("placeId").as_deref()), u.param("gameInstanceId"), u.param("linkCode"));
    }
    pass(full)
}

pub fn parse(input: &str) -> Option<Link> {
    let s = java::trim(input);
    if s.is_empty() || java::utf16_len(s) > MAX_INPUT {
        return None;
    }
    if s.chars().any(|c| java::is_iso_control(c) || java::is_whitespace(c)) {
        return None;
    }
    if digits(s) {
        return place(parse_id(Some(s)), None, None);
    }
    let lower = s.to_lowercase();
    if lower.starts_with("roblox://") || lower.starts_with("robloxmobile://") {
        return from_scheme(s);
    }
    let owned = if lower.starts_with("www.roblox.com/") || lower.starts_with("roblox.com/") { format!("https://{s}") } else { s.to_string() };
    let u = Uri::parse(&owned);
    if !u.scheme().is_some_and(|x| java::eq_ignore_case("https", x)) {
        return None;
    }
    let host = u.host()?.to_lowercase();
    if host == "ro.blox.com" {
        return pass(owned.clone());
    }
    if host != "roblox.com" && host != "www.roblox.com" {
        return None;
    }
    let seg = u.segments();
    let is = |i: usize, w: &str| seg.get(i).is_some_and(|x| java::eq_ignore_case(x, w));
    let g = if is(0, "games") {
        Some(0)
    } else if seg.len() > 1 && is(1, "games") {
        Some(1)
    } else {
        None
    };
    if let Some(g) = g {
        if is(g + 1, "start") {
            return place(parse_id(u.param("placeId").as_deref()), u.param("gameInstanceId"), u.param("linkCode"));
        }
        if seg.get(g + 1).is_some_and(|x| digits(x)) {
            return place(parse_id(seg.get(g + 1).map(String::as_str)), None, u.param("privateServerLinkCode"));
        }
    }
    if let Some(first) = seg.first().map(|x| x.to_lowercase())
        && matches!(first.as_str(), "share" | "share-links" | "join" | "shortlink")
    {
        let mut out = format!("https://www.roblox.com{}", u.path().unwrap_or(""));
        if let Some(q) = u.query().filter(|q| !q.is_empty()) {
            out.push('?');
            out.push_str(q);
        }
        if let Some(f) = u.fragment().filter(|f| !f.is_empty()) {
            out.push('#');
            out.push_str(f);
        }
        return pass(out);
    }
    None
}

fn strip_word(w: &str) -> &str {
    let lead = |c: char| matches!(c, '"' | '\'' | '(' | '<' | '[');
    let trail = |c: char| matches!(c, '"' | '\'' | ')' | '>' | ']' | '.' | ',' | '!' | '?' | ';' | ':');
    let start = w.find(|c: char| !lead(c)).unwrap_or(w.len());
    let rest = &w[start..];
    let end = rest.rfind(|c: char| !trail(c)).map_or(0, |i| i + rest[i..].chars().next().map_or(0, char::len_utf8));
    &rest[..end]
}

pub fn find_in(text: &str) -> Option<Link> {
    let whole = parse(text);
    if whole.is_some() || java::utf16_len(text) > MAX_INPUT {
        return whole;
    }
    for word in text.split(java::regex_space) {
        let w = strip_word(word);
        if w.is_empty() {
            continue;
        }
        if !w.to_lowercase().contains("roblox") && !digits(w) {
            continue;
        }
        if let Some(d) = parse(w) {
            return Some(d);
        }
    }
    None
}
