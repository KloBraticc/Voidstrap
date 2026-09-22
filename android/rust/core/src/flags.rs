use crate::java;
use serde_json::{Value, json};

pub const MAX_ENTRIES: usize = 10000;
pub const MAX_KEY: usize = 256;
pub const MAX_VALUE: usize = 4096;
pub const MAX_NAME: usize = 64;
pub const FORMAT: &str = "voidstrap.flags";

#[derive(Clone, Debug, PartialEq)]
pub enum Flag {
    Bool(bool),
    Long(i64),
    Double(f64),
    Text(String),
}

impl Flag {
    pub fn to_json(&self) -> Value {
        match self {
            Flag::Bool(b) => json!(b),
            Flag::Long(n) => json!(n),
            Flag::Double(d) => json!(d),
            Flag::Text(s) => json!(s),
        }
    }
}

fn clip(s: &str, max: usize) -> String {
    java::utf16_prefix(s, max).to_string()
}

fn java_whitespace(c: char) -> bool {
    matches!(c, '\t' | '\n' | '\u{b}' | '\u{c}' | '\r' | '\u{1c}'..='\u{1f}')
        || (c.is_whitespace() && !matches!(c, '\u{a0}' | '\u{2007}' | '\u{202f}') && !matches!(c, '\u{85}'))
}

pub fn valid_key(key: &str) -> bool {
    let n = key.len();
    (1..=MAX_KEY).contains(&n) && key.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
}

pub fn stored_key(key: &str) -> bool {
    !key.is_empty() && java::utf16_len(key) <= MAX_KEY && !key.chars().any(|c| java_whitespace(c) || c.is_control())
}

pub fn int_flag(key: &str) -> bool {
    key.starts_with("FInt") || key.starts_with("DFInt") || key.starts_with("SFInt")
}

pub fn clean_key(text: &str) -> String {
    let t = java::trim(text);
    let t = t.trim_start_matches(['"', '\'']);
    t.trim_end_matches(|c: char| matches!(c, '"' | '\'' | ':' | '=' | ',') || java::regex_space(c)).to_string()
}

pub fn valid_value(v: &Flag) -> bool {
    match v {
        Flag::Bool(_) | Flag::Long(_) => true,
        Flag::Double(d) => d.is_finite(),
        Flag::Text(s) => java::utf16_len(s) <= MAX_VALUE && !s.chars().any(|c| c.is_control() && c != '\t'),
    }
}

fn integer(t: &str) -> Option<i64> {
    let (neg, digits) = match t.strip_prefix('-') {
        Some(d) => (true, d),
        None => (false, t),
    };
    if !(1..=18).contains(&digits.chars().count()) {
        return None;
    }
    let mut n: i64 = 0;
    for c in digits.chars() {
        n = n * 10 + java::char_digit(c)? as i64;
    }
    Some(if neg { -n } else { n })
}

fn hex_double(s: &str) -> Option<f64> {
    let (neg, body) = match s.as_bytes().first()? {
        b'-' => (true, &s[1..]),
        b'+' => (false, &s[1..]),
        _ => (false, s),
    };
    let body = body.strip_prefix("0x").or_else(|| body.strip_prefix("0X"))?;
    let p = body.find(['p', 'P'])?;
    let (mant, exp) = (&body[..p], &body[p + 1..]);
    let exp = exp.strip_suffix(['f', 'F', 'd', 'D']).unwrap_or(exp);
    let e_digits = exp.strip_prefix(['+', '-']).unwrap_or(exp);
    if e_digits.is_empty() || !e_digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let (int, frac) = mant.split_once('.').unwrap_or((mant, ""));
    if int.is_empty() && frac.is_empty() || !int.chars().chain(frac.chars()).all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    let mut value = 0f64;
    for c in int.chars() {
        value = value * 16.0 + c.to_digit(16)? as f64;
    }
    let mut scale = 1.0 / 16.0;
    for c in frac.chars() {
        value += c.to_digit(16)? as f64 * scale;
        scale /= 16.0;
    }
    let e: i32 = exp.parse::<i64>().ok()?.clamp(-5000, 5000) as i32;
    let v = value * 2f64.powi(e);
    Some(if neg { -v } else { v })
}

pub fn java_double(text: &str) -> Option<f64> {
    let s = java::trim(text);
    if s.is_empty() {
        return None;
    }
    let unsigned = s.strip_prefix(['+', '-']).unwrap_or(s);
    if unsigned == "NaN" {
        return Some(f64::NAN);
    }
    if unsigned == "Infinity" {
        return Some(if s.starts_with('-') { f64::NEG_INFINITY } else { f64::INFINITY });
    }
    if unsigned.starts_with("0x") || unsigned.starts_with("0X") {
        return hex_double(s);
    }
    let core = s.strip_suffix(['f', 'F', 'd', 'D']).unwrap_or(s);
    let digits = core.strip_prefix(['+', '-']).unwrap_or(core);
    let (mant, exp) = match digits.find(['e', 'E']) {
        Some(i) => (&digits[..i], Some(&digits[i + 1..])),
        None => (digits, None),
    };
    let (int, frac) = mant.split_once('.').unwrap_or((mant, ""));
    if int.is_empty() && frac.is_empty() || !int.bytes().chain(frac.bytes()).all(|b| b.is_ascii_digit()) {
        return None;
    }
    if let Some(e) = exp {
        let e = e.strip_prefix(['+', '-']).unwrap_or(e);
        if e.is_empty() || !e.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
    }
    core.parse::<f64>().ok()
}

fn unquote(t: &str) -> &str {
    match t.strip_prefix('"').and_then(|x| x.strip_suffix('"')) {
        Some(inner) if t.len() >= 2 && !inner.contains(java::line_terminator) => inner,
        _ => t,
    }
}

pub fn parse_typed(kind: &str, text: &str) -> Option<Flag> {
    match kind {
        "bool" => {
            let b = unquote(java::trim(text));
            if b.eq_ignore_ascii_case("true") {
                Some(Flag::Bool(true))
            } else if b.eq_ignore_ascii_case("false") {
                Some(Flag::Bool(false))
            } else {
                None
            }
        }
        "number" => {
            let t = unquote(java::trim(text));
            if let Some(n) = integer(t) {
                return Some(Flag::Long(n));
            }
            let d = Flag::Double(java_double(t)?);
            valid_value(&d).then_some(d)
        }
        _ => {
            let s = Flag::Text(text.to_string());
            valid_value(&s).then_some(s)
        }
    }
}

pub fn tolerant(json: &str) -> String {
    let mut text = java::trim(json).to_string();
    if text.starts_with('\u{feff}') {
        text = java::trim(&text['\u{feff}'.len_utf8()..]).to_string();
    }
    if text.starts_with('"') {
        text = format!("{{{text}\n}}");
    }
    let t: Vec<char> = text.chars().collect();
    let n = t.len();
    let mut out: Vec<char> = Vec::with_capacity(n);
    let mut i = 0;
    while i < n {
        let ch = t[i];
        if ch == '"' {
            let mut j = i + 1;
            while j < n && t[j] != '"' {
                j += if t[j] == '\\' { 2 } else { 1 };
            }
            out.extend_from_slice(&t[i..(j + 1).min(n)]);
            i = j;
        } else if ch == '/' && i + 1 < n && t[i + 1] == '/' {
            while i + 1 < n && t[i + 1] != '\n' {
                i += 1;
            }
        } else if ch == '/' && i + 1 < n && t[i + 1] == '*' {
            let end = (i + 2..n.saturating_sub(1)).find(|&k| t[k] == '*' && t[k + 1] == '/');
            i = match end {
                Some(e) => e + 1,
                None => n,
            };
        } else {
            if ch == '}' || ch == ']' {
                let mut k = out.len();
                while k > 0 && java_whitespace(out[k - 1]) {
                    k -= 1;
                }
                if k > 0 && out[k - 1] == ',' {
                    out.remove(k - 1);
                }
            }
            out.push(ch);
        }
        i += 1;
    }
    out.into_iter().collect()
}

#[derive(Debug, PartialEq)]
pub enum Reject {
    NotObject,
    TooMany,
    Invalid,
}

#[derive(Debug, Default, PartialEq)]
pub struct Import {
    pub name: Option<String>,
    pub values: Vec<(String, Flag)>,
    pub duplicates: Vec<String>,
    pub rejected: Vec<String>,
}

impl Import {
    fn add(&mut self, key: &str, v: Flag) {
        if self.values.iter().any(|(k, _)| k == key) {
            self.duplicates.push(clip(key, 80));
        } else {
            self.values.push((key.to_string(), v));
        }
    }

    pub fn to_json(&self) -> Value {
        json!({
            "name": self.name,
            "values": self.values.iter().map(|(k, v)| json!([k, v.to_json()])).collect::<Vec<_>>(),
            "duplicates": self.duplicates,
            "rejected": self.rejected,
        })
    }
}

#[derive(Debug, PartialEq)]
enum Tok {
    Object,
    Array,
    Str(String),
    Num(String),
    Bool(bool),
    Null,
}

struct Reader {
    c: Vec<char>,
    p: usize,
}

type R<T> = Result<T, Reject>;

impl Reader {
    fn ws(&mut self) {
        while self.p < self.c.len() && matches!(self.c[self.p], ' ' | '\t' | '\n' | '\r') {
            self.p += 1;
        }
    }

    fn peek_char(&mut self) -> Option<char> {
        self.ws();
        self.c.get(self.p).copied()
    }

    fn expect(&mut self, ch: char) -> R<()> {
        if self.peek_char() == Some(ch) {
            self.p += 1;
            Ok(())
        } else {
            Err(Reject::Invalid)
        }
    }

    fn string(&mut self) -> R<String> {
        self.p += 1;
        let mut s = String::new();
        loop {
            let ch = *self.c.get(self.p).ok_or(Reject::Invalid)?;
            self.p += 1;
            match ch {
                '"' => return Ok(s),
                '\\' => {
                    let e = *self.c.get(self.p).ok_or(Reject::Invalid)?;
                    self.p += 1;
                    match e {
                        'u' => {
                            let hex: String = self.c.get(self.p..self.p + 4).ok_or(Reject::Invalid)?.iter().collect();
                            let unit = u16::from_str_radix(&hex, 16).map_err(|_| Reject::Invalid)?;
                            self.p += 4;
                            let mut units = vec![unit];
                            if (0xD800..0xDC00).contains(&unit) && self.c.get(self.p) == Some(&'\\') && self.c.get(self.p + 1) == Some(&'u') {
                                let hex2: String = self.c.get(self.p + 2..self.p + 6).unwrap_or(&[]).iter().collect();
                                if let Ok(low) = u16::from_str_radix(&hex2, 16)
                                    && (0xDC00..0xE000).contains(&low)
                                {
                                    units.push(low);
                                    self.p += 6;
                                }
                            }
                            s.push_str(&String::from_utf16_lossy(&units));
                        }
                        't' => s.push('\t'),
                        'b' => s.push('\u{8}'),
                        'n' => s.push('\n'),
                        'r' => s.push('\r'),
                        'f' => s.push('\u{c}'),
                        other => s.push(other),
                    }
                }
                other => s.push(other),
            }
        }
    }

    fn literal(&mut self) -> R<Tok> {
        let start = self.p;
        while self.p < self.c.len() && !matches!(self.c[self.p], '/' | '\\' | ';' | '#' | '=' | '{' | '}' | '[' | ']' | ':' | ',' | ' ' | '\t' | '\u{c}' | '\r' | '\n') {
            self.p += 1;
        }
        let lit: String = self.c[start..self.p].iter().collect();
        if lit.is_empty() {
            return Err(Reject::Invalid);
        }
        if lit.eq_ignore_ascii_case("null") {
            return Ok(Tok::Null);
        }
        if lit.eq_ignore_ascii_case("true") {
            return Ok(Tok::Bool(true));
        }
        if lit.eq_ignore_ascii_case("false") {
            return Ok(Tok::Bool(false));
        }
        if number(&lit) { Ok(Tok::Num(lit)) } else { Err(Reject::Invalid) }
    }

    fn value(&mut self) -> R<Tok> {
        match self.peek_char() {
            None => Err(Reject::Invalid),
            Some('{') => {
                self.p += 1;
                Ok(Tok::Object)
            }
            Some('[') => {
                self.p += 1;
                Ok(Tok::Array)
            }
            Some('"') => Ok(Tok::Str(self.string()?)),
            Some('\'') => Err(Reject::Invalid),
            Some(_) => self.literal(),
        }
    }

    fn skip_rest(&mut self, open: Tok) -> R<()> {
        match open {
            Tok::Object => {
                if self.peek_char() == Some('}') {
                    self.p += 1;
                    return Ok(());
                }
                loop {
                    if self.peek_char() != Some('"') {
                        return Err(Reject::Invalid);
                    }
                    self.string()?;
                    self.expect(':')?;
                    let v = self.value()?;
                    self.skip_rest(v)?;
                    match self.peek_char() {
                        Some(',') => self.p += 1,
                        Some('}') => {
                            self.p += 1;
                            return Ok(());
                        }
                        _ => return Err(Reject::Invalid),
                    }
                }
            }
            Tok::Array => {
                if self.peek_char() == Some(']') {
                    self.p += 1;
                    return Ok(());
                }
                loop {
                    let v = self.value()?;
                    self.skip_rest(v)?;
                    match self.peek_char() {
                        Some(',') => self.p += 1,
                        Some(']') => {
                            self.p += 1;
                            return Ok(());
                        }
                        _ => return Err(Reject::Invalid),
                    }
                }
            }
            _ => Ok(()),
        }
    }

    fn members(&mut self, mut each: impl FnMut(&mut Reader, String) -> R<()>) -> R<()> {
        if self.peek_char() == Some('}') {
            self.p += 1;
            return Ok(());
        }
        let mut count = 0;
        loop {
            if self.peek_char() != Some('"') {
                return Err(Reject::Invalid);
            }
            let key = self.string()?;
            count += 1;
            if count > MAX_ENTRIES {
                return Err(Reject::TooMany);
            }
            self.expect(':')?;
            each(self, key)?;
            match self.peek_char() {
                Some(',') => self.p += 1,
                Some('}') => {
                    self.p += 1;
                    return Ok(());
                }
                _ => return Err(Reject::Invalid),
            }
        }
    }

    fn read_one(&mut self, key: &str, into: &mut Import) -> R<()> {
        let t = self.value()?;
        let v = match t {
            Tok::Bool(b) => Some(Flag::Bool(b)),
            Tok::Num(lit) => match integer(&lit) {
                Some(n) => Some(Flag::Long(n)),
                None => {
                    let d = Flag::Double(lit.parse::<f64>().map_err(|_| Reject::Invalid)?);
                    valid_value(&d).then_some(d)
                }
            },
            Tok::Str(s) => Some(Flag::Text(s)),
            other => {
                self.skip_rest(other)?;
                None
            }
        };
        match v {
            Some(v) if valid_key(key) && valid_value(&v) => into.add(key, v),
            _ => into.rejected.push(clip(key, 80)),
        }
        Ok(())
    }
}

fn number(lit: &str) -> bool {
    let b = lit.as_bytes();
    let mut i = 0;
    let at = |i: usize| b.get(i).copied().unwrap_or(0);
    if at(i) == b'-' {
        i += 1;
    }
    match at(i) {
        b'0' => i += 1,
        b'1'..=b'9' => {
            while at(i).is_ascii_digit() {
                i += 1;
            }
        }
        _ => return false,
    }
    if at(i) == b'.' {
        i += 1;
        while at(i).is_ascii_digit() {
            i += 1;
        }
    }
    if matches!(at(i), b'e' | b'E') {
        i += 1;
        if matches!(at(i), b'+' | b'-') {
            i += 1;
        }
        if !at(i).is_ascii_digit() {
            return false;
        }
        while at(i).is_ascii_digit() {
            i += 1;
        }
    }
    i == b.len()
}

pub fn parse(json: &str) -> Result<Import, Reject> {
    let mut r = Reader { c: tolerant(json).chars().collect(), p: 0 };
    if r.c.first() == Some(&'\u{feff}') {
        r.p = 1;
    }
    let first = r.value()?;
    match first {
        Tok::Object => {}
        Tok::Array => return Err(Reject::NotObject),
        _ => return Err(Reject::Invalid),
    }
    let mut raw = Import::default();
    let mut envelope: Option<Import> = None;
    let mut format: Option<String> = None;
    let mut name: Option<String> = None;
    r.members(|r, key| {
        let save = r.p;
        let t = r.value()?;
        if t == Tok::Object && key == "flags" {
            let mut into = Import::default();
            r.members(|r, k| r.read_one(&k, &mut into))?;
            envelope = Some(into);
            return Ok(());
        }
        if t == Tok::Object && key == "applicationSettings" {
            return r.members(|r, k| r.read_one(&k, &mut raw));
        }
        if let Tok::Str(s) = &t
            && (key == "format" || key == "name")
        {
            if key == "format" {
                format = Some(s.clone());
            } else {
                name = Some(s.clone());
            }
            raw.add(&key, Flag::Text(s.clone()));
            return Ok(());
        }
        r.p = save;
        r.read_one(&key, &mut raw)
    })?;
    if r.peek_char().is_some() {
        return Err(Reject::Invalid);
    }
    if format.as_deref() == Some(FORMAT)
        && let Some(mut e) = envelope
    {
        e.name = name.map(|n| clip(&n, MAX_NAME));
        return Ok(e);
    }
    Ok(raw)
}

fn from_json(v: &Value) -> Option<Flag> {
    match v {
        Value::Bool(b) => Some(Flag::Bool(*b)),
        Value::String(s) => {
            let f = Flag::Text(s.clone());
            valid_value(&f).then_some(f)
        }
        Value::Number(n) => match n.as_i64() {
            Some(i) => Some(Flag::Long(i)),
            None => {
                let d = Flag::Double(n.as_f64()?);
                valid_value(&d).then_some(d)
            }
        },
        _ => None,
    }
}

pub fn load(text: &str) -> Value {
    let o: Value = serde_json::from_str(text).ok().filter(Value::is_object).unwrap_or_else(|| json!({}));
    let now = java::now_ms();
    let mut profiles: Vec<Value> = Vec::new();
    if let Some(a) = o.get("profiles").and_then(Value::as_array) {
        for p in a.iter().filter(|p| p.is_object()) {
            let id = match p.get("id") {
                None => uuid_dashed(),
                Some(_) => java::opt_str(p, "id", ""),
            };
            let name = clip(&java::opt_str(p, "name", "Default"), MAX_NAME);
            let created = java::opt_long(p, "created", now);
            let updated = java::opt_long(p, "updated", created);
            let exported = java::opt_long(p, "exported", 0);
            let mut values: Vec<Value> = Vec::new();
            if let Some(m) = p.get("flags").and_then(Value::as_object) {
                for (k, v) in m {
                    if values.len() >= MAX_ENTRIES {
                        break;
                    }
                    if let Some(f) = from_json(v)
                        && stored_key(k)
                    {
                        values.push(json!([k, f.to_json()]));
                    }
                }
            }
            profiles.push(json!({"id": id, "name": name, "created": created, "updated": updated, "exported": exported, "flags": values}));
        }
    }
    if profiles.is_empty() {
        profiles.push(json!({"id": uuid_dashed(), "name": "Default", "created": now, "updated": now, "exported": 0, "flags": []}));
    }
    let first = profiles[0]["id"].as_str().unwrap_or_default().to_string();
    let mut current = java::opt_str(&o, "current", &first);
    if !profiles.iter().any(|p| p["id"].as_str() == Some(current.as_str())) {
        current = first;
    }
    json!({"current": current, "profiles": profiles})
}

fn uuid_dashed() -> String {
    let h = java::uuid_hex();
    format!("{}-{}-{}-{}-{}", &h[..8], &h[8..12], &h[12..16], &h[16..20], &h[20..])
}
