use crate::http;
use crate::java;
use crate::net;
use serde_json::{Map, Value, json};
use std::collections::{HashMap, HashSet, VecDeque};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicUsize, Ordering};
use std::sync::mpsc;
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

const TAG: &str = "VoidstrapMatchmaker";
const REMOTE_URL: &str = "https://raw.githubusercontent.com/KloBraticc/Voidstrap-Resources/main/ServerLocations.json";
pub const MIN_CANDIDATES: i64 = 8;
pub const MAX_CANDIDATES: i64 = 64;
const FILTERED_CEILING: i64 = 80;
const PROBE_CONCURRENCY: usize = 8;
const JOIN_TIMEOUT_MS: i64 = 2500;
const MAX_JOIN_BYTES: i32 = 1024 * 1024;
const MAX_PAGES: usize = 5;
const EMPTY_PREFERENCE_MS: f64 = 60.0;
const FULLNESS_TIEBREAK_MS: f64 = 8.0;
const CLOSEST_BAND_MS: f64 = 12.0;
const HANDOFF_PING_MS: f64 = 120.0;
const HANDOFF_FLOOR_MULTIPLIER: f64 = 4.0;
const EARLY_EXIT_MIN_RESULTS: usize = 12;
const EARLY_EXIT_CLOSEST: usize = 6;
const DEADLINE_MS: i64 = 25_000;
const GEO_TTL_MS: i64 = 6 * 3600_000;
const IP_LOOKUP_TIMEOUT_MS: i64 = 4000;
const IP_CACHE_LIMIT: usize = 1024;
const FAIL_COOLDOWN_MS: i64 = 10 * 60_000;
const RESOLVED_TTL_MS: i64 = 2 * 60_000;

#[derive(Clone, Debug)]
pub struct Datacenter {
    pub city: String,
    pub region: String,
    pub country: String,
    pub lat: f64,
    pub lon: f64,
}

impl Datacenter {
    fn new(city: &str, region: &str, country: &str, lat: f64, lon: f64) -> Self {
        Datacenter { city: city.to_string(), region: region.to_string(), country: normalize_country(country), lat, lon }
    }

    pub fn key(&self) -> String {
        format!("{}|{}", self.city, self.country)
    }

    pub fn to_json(&self) -> Value {
        json!({"city": self.city, "region": self.region, "country": self.country, "lat": self.lat, "lon": self.lon})
    }
}

#[derive(Clone, Debug, Default)]
pub struct Geo {
    pub lat: f64,
    pub lon: f64,
    pub city: String,
    pub region: String,
    pub country: String,
}

#[derive(Clone, Debug)]
pub struct Candidate {
    pub job_id: String,
    pub ip: String,
    pub port: i64,
    pub dc: Datacenter,
    pub km: f64,
    pub playing: i64,
    pub max_players: i64,
    pub estimated_ping: i64,
    pub score: f64,
    pub blocked_closest_city: Option<String>,
}

impl Candidate {
    pub fn to_json(&self) -> Value {
        json!({"jobId": self.job_id, "ip": self.ip, "port": self.port, "dc": self.dc.to_json(), "km": self.km, "playing": self.playing,
            "maxPlayers": self.max_players, "estimatedPing": self.estimated_ping, "score": self.score, "blockedClosestCity": self.blocked_closest_city})
    }
}

#[derive(Clone)]
struct Server {
    job_id: String,
    playing: i64,
    max_players: i64,
}

struct Cidr {
    network: u64,
    mask: u64,
    cidr: String,
    dc: Datacenter,
}

struct Tables {
    loaded: bool,
    cidrs: Vec<Cidr>,
    files: PathBuf,
}

static TABLES: Mutex<Tables> = Mutex::new(Tables { loaded: false, cidrs: Vec::new(), files: PathBuf::new() });
static IP_CACHE: Mutex<Option<HashMap<String, Datacenter>>> = Mutex::new(None);
static IP_FAILED: Mutex<Option<HashMap<String, i64>>> = Mutex::new(None);
static IP_INFLIGHT: Mutex<Option<HashSet<String>>> = Mutex::new(None);
static RESOLVED: Mutex<Option<HashMap<String, (String, i64, i64)>>> = Mutex::new(None);
static CSRF: Mutex<Option<String>> = Mutex::new(None);
static BACKOFF_UNTIL: AtomicI64 = AtomicI64::new(0);
static GEO: Mutex<Option<(Geo, i64)>> = Mutex::new(None);
static REPORT: Mutex<String> = Mutex::new(String::new());
pub static REJECTED: AtomicBool = AtomicBool::new(false);

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

pub fn normalize_country(country: &str) -> String {
    const PAIRS: &[(&str, &str)] = &[
        ("US", "USA"), ("USA", "USA"), ("United States", "USA"), ("United States of America", "USA"), ("GB", "UK"), ("UK", "UK"), ("United Kingdom", "UK"),
        ("Great Britain", "UK"), ("England", "UK"), ("NL", "Netherlands"), ("Netherlands", "Netherlands"), ("Holland", "Netherlands"), ("FR", "France"),
        ("France", "France"), ("DE", "Germany"), ("Germany", "Germany"), ("PL", "Poland"), ("Poland", "Poland"), ("IN", "India"), ("India", "India"),
        ("JP", "Japan"), ("Japan", "Japan"), ("SG", "Singapore"), ("Singapore", "Singapore"), ("AU", "Australia"), ("Australia", "Australia"), ("CN", "China"),
        ("China", "China"), ("HK", "China"), ("Hong Kong", "China"), ("CA", "Canada"), ("Canada", "Canada"), ("BR", "Brazil"), ("Brazil", "Brazil"),
        ("KR", "South Korea"), ("South Korea", "South Korea"), ("Korea", "South Korea"), ("TW", "Taiwan"), ("Taiwan", "Taiwan"), ("ZA", "South Africa"),
        ("South Africa", "South Africa"), ("AE", "UAE"), ("United Arab Emirates", "UAE"), ("RU", "Russia"), ("Russia", "Russia"), ("MX", "Mexico"),
        ("Mexico", "Mexico"), ("CL", "Chile"), ("Chile", "Chile"), ("AR", "Argentina"), ("Argentina", "Argentina"),
    ];
    let t = java::trim(country);
    let l = java::lower(t);
    PAIRS.iter().find(|(k, _)| java::lower(k) == l).map_or_else(|| t.to_string(), |(_, v)| v.to_string())
}

fn report(s: &str) {
    *lock(&REPORT) = s.to_string();
    net::info(TAG, s);
}

pub fn last_report() -> String {
    lock(&REPORT).clone()
}

fn round(x: f64) -> i64 {
    (x + 0.5).floor() as i64
}

pub fn estimate_ping_ms(km: f64) -> i64 {
    if km.is_nan() || km < 0.0 {
        return -1;
    }
    round(5.0 + km / 75.0).clamp(1, 999)
}

fn estimate_rtt(km: f64) -> f64 {
    if km.is_nan() || km < 0.0 {
        return 999.0;
    }
    5.0 + km / 75.0
}

pub fn haversine_km(lat1: f64, lon1: f64, lat2: f64, lon2: f64) -> f64 {
    let d_lat = (lat2 - lat1).to_radians();
    let d_lon = (lon2 - lon1).to_radians();
    let a = (d_lat / 2.0).sin() * (d_lat / 2.0).sin() + lat1.to_radians().cos() * lat2.to_radians().cos() * (d_lon / 2.0).sin() * (d_lon / 2.0).sin();
    6371.0 * 2.0 * a.sqrt().atan2((1.0 - a).sqrt())
}

pub fn matches_preferred(dc: &Datacenter, preferred: &str) -> bool {
    if java::trim(preferred).is_empty() {
        return false;
    }
    let (city, country) = preferred.split_once('|').unwrap_or((preferred, ""));
    if !java::eq_ignore_case(&dc.city, city) {
        return false;
    }
    if country.is_empty() || dc.country.is_empty() {
        return true;
    }
    java::eq_ignore_case(&normalize_country(country), &dc.country)
}

pub fn ip_to_long(ip: &str) -> Option<u64> {
    let p: Vec<&str> = java::trim(ip).split('.').collect();
    if p.len() != 4 {
        return None;
    }
    let mut v: u64 = 0;
    for s in p {
        let b: i64 = s.parse().ok()?;
        if !(0..=255).contains(&b) {
            return None;
        }
        v = (v << 8) | b as u64;
    }
    Some(v)
}

pub fn is_private(ip: &str) -> bool {
    let Some(v) = ip_to_long(ip) else {
        return true;
    };
    let a = (v >> 24) as u32;
    let b = ((v >> 16) & 0xFF) as u32;
    a == 0 || a == 10 || a == 127 || (a == 100 && (64..=127).contains(&b)) || (a == 172 && (16..=31).contains(&b)) || (a == 192 && b == 168) || (a == 169 && b == 254) || a >= 224
}

fn subnet(ip: &str) -> Option<String> {
    let v = ip_to_long(ip)?;
    Some(format!("{}.{}.{}.0/24", (v >> 24) & 0xFF, (v >> 16) & 0xFF, (v >> 8) & 0xFF))
}

fn opt_f64(o: &Value, k: &str, fallback: f64) -> f64 {
    match o.get(k) {
        Some(Value::Number(n)) => n.as_f64().unwrap_or(fallback),
        Some(Value::String(s)) => s.trim().parse().unwrap_or(f64::NAN),
        None | Some(Value::Null) => fallback,
        _ => f64::NAN,
    }
}

fn add_entries(t: &mut Tables, a: &[Value], replace: bool) -> bool {
    let mut added = false;
    for e in a.iter().filter(|e| e.is_object()) {
        let cidr = java::trim(&java::opt_str(e, "cidr", "")).to_string();
        let city = java::trim(&java::opt_str(e, "city", "")).to_string();
        let lat = opt_f64(e, "lat", 0.0);
        let lon = opt_f64(e, "lon", 0.0);
        if cidr.is_empty() || city.is_empty() || (lat == 0.0 && lon == 0.0) || lat.abs() > 90.0 || lon.abs() > 180.0 {
            continue;
        }
        let parts: Vec<&str> = cidr.split('/').collect();
        if parts.len() != 2 {
            continue;
        }
        let Some(ip) = ip_to_long(parts[0]) else {
            continue;
        };
        let Ok(prefix) = parts[1].parse::<i64>() else {
            continue;
        };
        if !(0..=32).contains(&prefix) {
            continue;
        }
        let exists = t.cidrs.iter().any(|x| java::eq_ignore_case(&x.cidr, &cidr));
        if exists && !replace {
            continue;
        }
        let mask = if prefix == 0 { 0 } else { (0xFFFF_FFFFu64 << (32 - prefix)) & 0xFFFF_FFFF };
        let dc = Datacenter::new(&city, &java::opt_str(e, "region", ""), &java::opt_str(e, "country", ""), lat, lon);
        t.cidrs.push(Cidr { network: ip & mask, mask, cidr, dc });
        added = true;
    }
    added
}

pub struct Env {
    pub cache: PathBuf,
    pub agent: String,
}

pub fn setup(builtin: &str, cache: PathBuf, files: PathBuf, agent: &str) {
    let mut t = lock(&TABLES);
    if t.loaded {
        return;
    }
    t.loaded = true;
    t.files = files.clone();
    match serde_json::from_str::<Value>(builtin) {
        Ok(Value::Array(a)) => {
            add_entries(&mut t, &a, false);
        }
        _ => net::warn(TAG, "Built in datacenter list could not be read"),
    }
    let client = net::Client { agent: agent.to_string() };
    match client.cached_json(&cache, REMOTE_URL, 24 * 3600_000) {
        Some(remote) => {
            if let Some(servers) = remote.get("Servers").and_then(Value::as_object) {
                let list: Vec<Value> = servers
                    .iter()
                    .filter(|(_, e)| e.is_object())
                    .map(|(name, e)| {
                        let mut o = Map::new();
                        o.insert("cidr".into(), java::opt_str(e, "Cidr", name).into());
                        o.insert("city".into(), java::opt_str(e, "City", "").into());
                        o.insert("region".into(), java::opt_str(e, "Region", "").into());
                        o.insert("country".into(), java::opt_str(e, "Country", "").into());
                        o.insert("lat".into(), json!(opt_f64(e, "Lat", 0.0)));
                        o.insert("lon".into(), json!(opt_f64(e, "Lon", 0.0)));
                        Value::Object(o)
                    })
                    .collect();
                add_entries(&mut t, &list, false);
            }
        }
        None => net::warn(TAG, "Server locations list could not be downloaded, using the built in list"),
    }
    if let Ok(text) = std::fs::read(files.join("matchmaker_learned.json"))
        && let Ok(Value::Array(a)) = serde_json::from_slice::<Value>(&text)
    {
        add_entries(&mut t, &a, false);
    }
}

pub fn map(ip: &str) -> Option<Datacenter> {
    let v = ip_to_long(ip)?;
    lock(&TABLES).cidrs.iter().find(|x| (v & x.mask) == x.network).map(|x| x.dc.clone())
}

pub fn datacenters() -> Vec<Datacenter> {
    let t = lock(&TABLES);
    let mut seen = HashSet::new();
    t.cidrs.iter().filter(|x| seen.insert(x.dc.key())).map(|x| x.dc.clone()).collect()
}

fn nearest_km(geo: &Geo) -> f64 {
    let best = datacenters().iter().map(|dc| haversine_km(geo.lat, geo.lon, dc.lat, dc.lon)).fold(f64::INFINITY, f64::min);
    if best.is_infinite() { 0.0 } else { best }
}

fn valid(lat: f64, lon: f64) -> bool {
    !lat.is_nan() && !lon.is_nan() && lat.abs() <= 90.0 && lon.abs() <= 180.0 && !(lat == 0.0 && lon == 0.0)
}

pub fn saved_geo(text: &str) -> Option<Geo> {
    let o: Value = serde_json::from_str(text).ok().filter(Value::is_object)?;
    o.get("lat")?;
    let g = Geo {
        lat: opt_f64(&o, "lat", f64::NAN),
        lon: opt_f64(&o, "lon", f64::NAN),
        city: java::opt_str(&o, "city", ""),
        region: java::opt_str(&o, "region", ""),
        country: java::opt_str(&o, "country", ""),
    };
    valid(g.lat, g.lon).then_some(g)
}

pub fn geo_json(g: &Geo) -> Value {
    json!({"lat": g.lat, "lon": g.lon, "city": g.city, "region": g.region, "country": g.country})
}

struct Response {
    code: i32,
    body: String,
    csrf: Option<String>,
    content_type: Option<String>,
    retry_after_ms: i64,
}

fn roblox_host(url: &str) -> bool {
    let Some(rest) = url.strip_prefix("https://") else {
        return false;
    };
    let host = rest.split(['/', '?', '#']).next().unwrap_or("");
    let host = host.rsplit('@').next().unwrap_or(host);
    let host = java::lower(host.split(':').next().unwrap_or(host));
    host == "roblox.com" || host.ends_with(".roblox.com")
}

fn request(method: &str, url: &str, cookie: Option<&str>, token: Option<&str>, body: Option<&str>, agent: &str, timeout: i64) -> Option<Response> {
    let cookie_header = cookie.filter(|_| roblox_host(url)).map(|c| format!(".ROBLOSECURITY={c}"));
    let mut headers: Vec<(&str, &str)> = vec![("User-Agent", agent), ("Accept", "application/json")];
    if let Some(c) = &cookie_header {
        headers.push(("Cookie", c));
    }
    if url.starts_with("https://gamejoin.") {
        headers.push(("Referer", "https://www.roblox.com/"));
    }
    if let Some(t) = token {
        headers.push(("X-CSRF-TOKEN", t));
    }
    if body.is_some() {
        headers.push(("Content-Type", "application/json; charset=utf-8"));
    }
    let r = http::request(method, url, &headers, body.map(str::as_bytes), timeout.clamp(0, i32::MAX as i64) as i32, MAX_JOIN_BYTES * 4, false)?;
    let retry_after_ms = r.header("Retry-After").and_then(|v| v.trim().parse::<f64>().ok()).map_or(0, |s| (s * 1000.0) as i64);
    Some(Response {
        code: r.code,
        csrf: r.header("x-csrf-token").map(str::to_string),
        content_type: r.header("Content-Type").map(str::to_string),
        retry_after_ms,
        body: r.text(),
    })
}

fn get(url: &str, cookie: Option<&str>, timeout: i64) -> Option<String> {
    let r = request("GET", url, cookie, None, None, "Voidstrap/1.0", timeout)?;
    (r.code == 200).then_some(r.body)
}

fn geo_from(url: &str, timeout: i64) -> Option<Geo> {
    let parsed = (|| {
        let o: Value = serde_json::from_str(&get(url, None, timeout)?).ok().filter(Value::is_object)?;
        let mut g = Geo::default();
        if o.get("loc").is_some() {
            let loc = java::opt_str(&o, "loc", "");
            let p: Vec<&str> = loc.split(',').collect();
            if p.len() != 2 {
                return None;
            }
            g.lat = crate::flags::java_double(p[0])?;
            g.lon = crate::flags::java_double(p[1])?;
            g.country = java::opt_str(&o, "country", "");
        } else {
            if o.get("success").is_some() && !java::opt_bool(&o, "success", false) {
                return None;
            }
            if java::opt_bool(&o, "error", false) {
                return None;
            }
            if o.get("latitude").is_none() || o.get("longitude").is_none() {
                return None;
            }
            g.lat = opt_f64(&o, "latitude", f64::NAN);
            g.lon = opt_f64(&o, "longitude", f64::NAN);
            g.country = if o.get("country_code").is_some() { java::opt_str(&o, "country_code", "") } else { java::opt_str(&o, "country", "") };
        }
        g.city = java::opt_str(&o, "city", "");
        g.region = java::opt_str(&o, "region", "");
        Some(g)
    })();
    if parsed.is_none() {
        net::warn(TAG, &format!("Location provider failed: {url}"));
    }
    parsed
}

fn first_geo() -> Option<Geo> {
    let (tx, rx) = mpsc::channel();
    for url in ["https://ipinfo.io/json", "https://ipwho.is/", "https://ipapi.co/json/"] {
        let tx = tx.clone();
        std::thread::spawn(move || {
            let _ = tx.send(geo_from(url, 8000));
        });
    }
    drop(tx);
    let end = java::now_ms() + 9000;
    for _ in 0..3 {
        let left = end - java::now_ms();
        if left <= 0 {
            break;
        }
        match rx.recv_timeout(Duration::from_millis(left as u64)) {
            Ok(Some(g)) if valid(g.lat, g.lon) => return Some(g),
            Ok(_) => {}
            Err(_) => break,
        }
    }
    None
}

pub fn geo(saved: &str) -> (Option<Geo>, Option<Value>) {
    if let Some((g, at)) = lock(&GEO).as_ref()
        && java::now_ms() - at < GEO_TTL_MS
    {
        return (Some(g.clone()), None);
    }
    match first_geo() {
        None => {
            let g = saved_geo(saved);
            if g.is_none() {
                report("All location providers failed and no location was saved yet, cannot match by location");
            }
            (g, None)
        }
        Some(g) => {
            *lock(&GEO) = Some((g.clone(), java::now_ms()));
            let save = geo_json(&g);
            (Some(g), Some(save))
        }
    }
}

fn trim_map<V>(m: &mut HashMap<String, V>) {
    if m.len() > IP_CACHE_LIMIT {
        m.clear();
    }
}

pub fn lookup(ip: &str, deadline: i64) -> Option<Datacenter> {
    if ip.is_empty() || is_private(ip) {
        return None;
    }
    if let Some(dc) = map(ip) {
        return Some(dc);
    }
    let net_key = subnet(ip)?;
    if let Some(dc) = lock(&IP_CACHE).get_or_insert_with(HashMap::new).get(&net_key) {
        return Some(dc.clone());
    }
    if let Some(failed) = lock(&IP_FAILED).get_or_insert_with(HashMap::new).get(&net_key)
        && java::now_ms() - failed < FAIL_COOLDOWN_MS
    {
        return None;
    }
    if !lock(&IP_INFLIGHT).get_or_insert_with(HashSet::new).insert(net_key.clone()) {
        return None;
    }
    let result = (|| {
        let mut g = None;
        for url in [format!("https://ipinfo.io/{ip}/json"), format!("https://ipwho.is/{ip}"), format!("https://ipapi.co/{ip}/json/")] {
            let timeout = IP_LOOKUP_TIMEOUT_MS.min(deadline - java::now_ms());
            if timeout < 500 {
                break;
            }
            if let Some(found) = geo_from(&url, timeout)
                && valid(found.lat, found.lon)
            {
                g = Some(found);
                break;
            }
        }
        let Some(g) = g else {
            let mut failed = lock(&IP_FAILED);
            let m = failed.get_or_insert_with(HashMap::new);
            trim_map(m);
            m.insert(net_key.clone(), java::now_ms());
            return None;
        };
        let dc = Datacenter::new(&g.city, &g.region, &g.country, g.lat, g.lon);
        {
            let mut cache = lock(&IP_CACHE);
            let m = cache.get_or_insert_with(HashMap::new);
            trim_map(m);
            m.insert(net_key.clone(), dc.clone());
        }
        learn(ip, &dc);
        report(&format!("Learned a new datacenter at {net_key}: {}, {}", dc.city, dc.country));
        Some(dc)
    })();
    lock(&IP_INFLIGHT).get_or_insert_with(HashSet::new).remove(&net_key);
    result
}

fn learn(ip: &str, dc: &Datacenter) {
    if dc.city.is_empty() {
        return;
    }
    let Some(cidr) = subnet(ip) else {
        return;
    };
    let mut e = Map::new();
    e.insert("cidr".into(), cidr.into());
    e.insert("city".into(), dc.city.clone().into());
    e.insert("region".into(), dc.region.clone().into());
    e.insert("country".into(), dc.country.clone().into());
    e.insert("lat".into(), json!(dc.lat));
    e.insert("lon".into(), json!(dc.lon));
    let e = Value::Object(e);
    let mut t = lock(&TABLES);
    if !add_entries(&mut t, std::slice::from_ref(&e), false) {
        return;
    }
    let f = t.files.join("matchmaker_learned.json");
    let mut all: Vec<Value> = match std::fs::read(&f) {
        Ok(b) => match serde_json::from_slice::<Value>(&b) {
            Ok(Value::Array(a)) => a,
            _ => return,
        },
        Err(_) if f.is_file() => return,
        Err(_) => Vec::new(),
    };
    all.push(e);
    while all.len() > 512 {
        all.remove(0);
    }
    let _ = std::fs::write(&f, java::to_string(&Value::Array(all), 0));
}

pub struct Settings {
    pub preferred: String,
    pub blocked: HashSet<String>,
    pub budget: i64,
    pub prefer_empty: bool,
    pub v2: bool,
    pub cookie: String,
    pub saved_geo: String,
}

fn safe_headroom(x: &Candidate) -> bool {
    if x.max_players <= 0 {
        return true;
    }
    x.max_players - x.playing >= 2.max((x.max_players as f64 * 0.08).ceil() as i64)
}

fn stratify(items: &[Server], target: usize) -> Vec<Server> {
    let step = items.len() as f64 / target as f64;
    (0..target).map(|i| items[(items.len() - 1).min((i as f64 * step).floor() as usize)].clone()).collect()
}

fn headroom_penalty(playing: i64, max: i64) -> f64 {
    if max <= 0 {
        return 0.0;
    }
    match max - playing {
        o if o <= 1 => 120.0,
        2 => 35.0,
        _ => 0.0,
    }
}

fn population_penalty(playing: i64, max: i64, prefer_empty: bool) -> f64 {
    let fullness = if max > 0 { (playing as f64 / max as f64).clamp(0.0, 1.0) } else { 0.5 };
    if prefer_empty {
        return fullness * EMPTY_PREFERENCE_MS + headroom_penalty(playing, max);
    }
    let sparse = match playing {
        p if p <= 0 => 100.0,
        1 => 80.0,
        2 => 60.0,
        3 => 45.0,
        _ if fullness < 0.15 => (0.15 - fullness) * 80.0,
        _ => 0.0,
    };
    let crowded = if fullness > 0.85 { (fullness - 0.85) * 120.0 } else { 0.0 };
    sparse + crowded + (fullness - 0.65).abs() * FULLNESS_TIEBREAK_MS + headroom_penalty(playing, max)
}

fn sort(items: Vec<Server>, prefer_empty: bool) -> Vec<Server> {
    let mut out = items;
    if prefer_empty {
        out.sort_by_key(|x| x.playing);
    } else {
        let fill = |s: &Server| if s.max_players > 0 { s.playing as f64 / s.max_players as f64 } else if s.playing > 0 { 0.5 } else { 0.0 };
        out.sort_by(|a, b| fill(b).partial_cmp(&fill(a)).unwrap_or(std::cmp::Ordering::Equal).then(b.playing.cmp(&a.playing)));
    }
    out
}

fn list_servers(place_id: i64, cookie: &str, prefer_empty: bool, deadline: i64) -> Vec<Server> {
    let mut items = Vec::new();
    let mut seen = HashSet::new();
    let mut cursor: Option<String> = None;
    let backoff = [0u64, 750, 2000];
    for page in 0..MAX_PAGES {
        let mut url = format!("https://games.roblox.com/v1/games/{place_id}/servers/Public?excludeFullGames=true&limit=100&sortOrder={}", if prefer_empty { "Asc" } else { "Desc" });
        if let Some(c) = &cursor {
            url.push_str(&format!("&cursor={}", http::encode(c)));
        }
        let mut next: Option<String> = None;
        let mut ok = false;
        for (attempt, wait) in backoff.iter().enumerate() {
            if ok {
                break;
            }
            if java::now_ms() > deadline {
                return sort(items, prefer_empty);
            }
            if *wait > 0 {
                std::thread::sleep(Duration::from_millis(*wait));
            }
            let last = attempt == backoff.len() - 1;
            let Some(r) = request("GET", &url, Some(cookie), None, None, "Voidstrap/1.0", 10000) else {
                if last {
                    return sort(items, prefer_empty);
                }
                continue;
            };
            if r.code == 429 {
                if last {
                    return sort(items, prefer_empty);
                }
                continue;
            }
            if r.code != 200 {
                net::info(TAG, &format!("Server list page {} returned HTTP {}", page + 1, r.code));
                return sort(items, prefer_empty);
            }
            match serde_json::from_str::<Value>(&r.body).ok().filter(Value::is_object) {
                Some(o) => {
                    if let Some(data) = o.get("data").and_then(Value::as_array) {
                        for e in data.iter().filter(|e| e.is_object()) {
                            let id = java::opt_str(e, "id", "");
                            if id.is_empty() || !seen.insert(id.clone()) {
                                continue;
                            }
                            let playing = java::opt_long(e, "playing", 0);
                            let max = java::opt_long(e, "maxPlayers", 0);
                            if max > 0 && playing >= max {
                                continue;
                            }
                            items.push(Server { job_id: id, playing, max_players: max });
                        }
                    }
                    next = match o.get("nextPageCursor") {
                        None | Some(Value::Null) => None,
                        Some(_) => Some(java::opt_str(&o, "nextPageCursor", "")),
                    };
                    ok = true;
                }
                None => {
                    if last {
                        return sort(items, prefer_empty);
                    }
                }
            }
        }
        match next {
            Some(n) if !n.is_empty() => cursor = Some(n),
            _ => break,
        }
    }
    sort(items, prefer_empty)
}

fn uuid_dashed() -> String {
    let h = java::uuid_hex();
    format!("{}-{}-{}-{}-{}", &h[..8], &h[8..12], &h[12..16], &h[16..20], &h[20..])
}

fn join(v2: bool, place_id: i64, job_id: &str, cookie: &str, token: Option<&str>, timeout: i64) -> Option<Response> {
    let mut body = Map::new();
    body.insert("placeId".into(), place_id.into());
    body.insert("gameId".into(), job_id.into());
    body.insert("gameJoinAttemptId".into(), uuid_dashed().into());
    if v2 {
        body.insert("joinOrigin".into(), "VoidstrapFetchInfo".into());
    }
    let url = if v2 { "https://gamejoin.roblox.com/v2/join-game-instance" } else { "https://gamejoin.roblox.com/v1/join-game-instance" };
    request("POST", url, Some(cookie), token, Some(&java::to_string(&Value::Object(body), 0)), "Roblox/WinInet", timeout)
}

fn prime_csrf(place_id: i64, cookie: &str, v2: bool) {
    if lock(&CSRF).is_some() {
        return;
    }
    if let Some(r) = join(v2, place_id, "00000000-0000-0000-0000-000000000000", cookie, None, JOIN_TIMEOUT_MS)
        && let Some(c) = r.csrf
    {
        *lock(&CSRF) = Some(c);
    }
}

fn sse_json(text: &str) -> String {
    let mut first = String::new();
    let normalized = text.replace("\r\n", "\n");
    for block in normalized.split("\n\n") {
        if java::trim(block).is_empty() {
            continue;
        }
        let mut event = "message".to_string();
        let mut data = String::new();
        for raw in block.split('\n') {
            let line = raw.strip_suffix('\r').unwrap_or(raw);
            if line.is_empty() || line.starts_with(':') {
                continue;
            }
            let (field, val) = match line.find(':') {
                Some(sep) => (&line[..sep], line[sep + 1..].trim_start_matches(' ')),
                None => (line, ""),
            };
            if field == "event" {
                event = val.to_string();
            } else if field == "data" {
                if !data.is_empty() {
                    data.push('\n');
                }
                data.push_str(val);
            }
        }
        if data.is_empty() {
            continue;
        }
        if first.is_empty() {
            first = data.clone();
        }
        if event == "ResponseReady" {
            return data;
        }
    }
    first
}

fn parse_join(r: &Response) -> Option<Value> {
    if java::trim(&r.body).is_empty() {
        return None;
    }
    let text = if r.content_type.as_deref().is_some_and(|c| c.to_lowercase().contains("text/event-stream")) { sse_json(&r.body) } else { r.body.clone() };
    serde_json::from_str::<Value>(&text).ok().filter(Value::is_object)
}

fn has_join_script(o: &Value) -> bool {
    o.get("joinScript").is_some_and(Value::is_object) || o.get("UdmuxEndpoints").is_some_and(Value::is_array) || !java::opt_str(o, "MachineAddress", "").is_empty()
}

fn parse_join_response(root: &Value) -> (String, i64) {
    let mut ip: Option<String> = None;
    let mut port = 0;
    let script = root.get("joinScript").filter(|v| v.is_object());
    let endpoints = root.get("UdmuxEndpoints").filter(|v| v.is_array()).or_else(|| script.and_then(|s| s.get("UdmuxEndpoints")).filter(|v| v.is_array()));
    if let Some(first) = endpoints.and_then(Value::as_array).and_then(|a| a.first()).filter(|v| v.is_object()) {
        ip = first.get("Address").map(|_| java::opt_str(first, "Address", ""));
        port = java::opt_long(first, "Port", 0).max(0);
    }
    let mut ip = ip.unwrap_or_default();
    if ip.is_empty() {
        ip = java::opt_str(root, "MachineAddress", "");
        if ip.is_empty()
            && let Some(s) = script
        {
            ip = java::opt_str(s, "MachineAddress", "");
        }
    }
    if port == 0 {
        port = if root.get("ServerPort").is_some() { java::opt_long(root, "ServerPort", 0) } else { script.map_or(0, |s| java::opt_long(s, "ServerPort", 0)) };
    }
    (ip, port)
}

fn attempt(v2: bool, place_id: i64, job_id: &str, cookie: &str, deadline: i64, allow_alternate: &mut bool) -> Option<(String, i64, i64)> {
    *allow_alternate = false;
    for _ in 0..2 {
        let wait = BACKOFF_UNTIL.load(Ordering::SeqCst) - java::now_ms();
        if wait > 0 {
            let jitter = (java::random_bytes::<1>()[0] as i64 * 175) / 255;
            std::thread::sleep(Duration::from_millis((wait + 25 + jitter) as u64));
        }
        let left = deadline - java::now_ms();
        if left <= 0 {
            return None;
        }
        let token = lock(&CSRF).clone();
        let Some(r) = join(v2, place_id, job_id, cookie, token.as_deref(), JOIN_TIMEOUT_MS.min(left)) else {
            *allow_alternate = true;
            return None;
        };
        if r.code == 403
            && let Some(c) = &r.csrf
        {
            *lock(&CSRF) = Some(c.clone());
            continue;
        }
        if r.code == 429 {
            let delay = if r.retry_after_ms > 0 { r.retry_after_ms } else { 1500 };
            BACKOFF_UNTIL.fetch_max(java::now_ms() + delay.min(8000), Ordering::SeqCst);
            return None;
        }
        if !(200..300).contains(&r.code) {
            if r.code == 401 {
                REJECTED.store(true, Ordering::SeqCst);
                report("Roblox rejected your login, sign in again");
            }
            *allow_alternate = matches!(r.code, 400 | 404 | 405 | 408) || r.code >= 500;
            return None;
        }
        let Some(o) = parse_join(&r).filter(has_join_script) else {
            *allow_alternate = true;
            return None;
        };
        let (ip, port) = parse_join_response(&o);
        if ip.is_empty() || is_private(&ip) {
            *allow_alternate = true;
            return None;
        }
        return Some((ip, port, java::now_ms()));
    }
    None
}

fn resolve(place_id: i64, job_id: &str, cookie: &str, v2: bool, deadline: i64) -> Option<(String, i64, i64)> {
    let key = format!("{place_id}:{job_id}");
    if let Some(c) = lock(&RESOLVED).get_or_insert_with(HashMap::new).get(&key)
        && java::now_ms() - c.2 < RESOLVED_TTL_MS
    {
        return Some(c.clone());
    }
    let mut alt = false;
    let mut r = attempt(v2, place_id, job_id, cookie, deadline, &mut alt);
    if r.is_none() && alt && java::now_ms() < deadline {
        r = attempt(!v2, place_id, job_id, cookie, deadline, &mut alt);
    }
    if let Some(v) = &r {
        let mut m = lock(&RESOLVED);
        let map = m.get_or_insert_with(HashMap::new);
        if map.len() > 2048 {
            map.clear();
        }
        map.insert(key, v.clone());
    }
    r
}

struct ProbeJob {
    place_id: i64,
    cookie: String,
    geo: Geo,
    preferred: String,
    blocked: HashSet<String>,
    prefer_empty: bool,
    floor_ms: f64,
    v2: bool,
    deadline: i64,
}

fn probe(job: ProbeJob, servers: Vec<Server>) -> Vec<Candidate> {
    let workers = PROBE_CONCURRENCY.min(servers.len().max(1));
    let job = Arc::new(job);
    let queue = Arc::new(Mutex::new(VecDeque::from(servers)));
    let results: Arc<Mutex<Vec<Candidate>>> = Arc::new(Mutex::new(Vec::new()));
    let stop = Arc::new(AtomicBool::new(false));
    let good_enough = Arc::new(AtomicUsize::new(0));
    let done = Arc::new((Mutex::new(0usize), Condvar::new()));
    for _ in 0..workers {
        let (job, queue, results, stop, good_enough, done) = (job.clone(), queue.clone(), results.clone(), stop.clone(), good_enough.clone(), done.clone());
        std::thread::spawn(move || {
            loop {
                if stop.load(Ordering::SeqCst) || java::now_ms() >= job.deadline {
                    break;
                }
                let Some(sv) = lock(&queue).pop_front() else {
                    break;
                };
                let Some((ip, port, _)) = resolve(job.place_id, &sv.job_id, &job.cookie, job.v2, job.deadline) else {
                    continue;
                };
                let Some(dc) = lookup(&ip, job.deadline) else {
                    continue;
                };
                let km = haversine_km(job.geo.lat, job.geo.lon, dc.lat, dc.lon);
                let ping = estimate_rtt(km);
                let usable = !job.blocked.contains(&dc.key());
                let on_target = if !job.preferred.is_empty() { matches_preferred(&dc, &job.preferred) } else { ping <= job.floor_ms + CLOSEST_BAND_MS };
                let populated = job.prefer_empty || sv.playing >= 4 || (sv.max_players > 0 && sv.playing as f64 >= (sv.max_players as f64 * 0.15).ceil());
                let cand = Candidate {
                    job_id: sv.job_id.clone(),
                    ip,
                    port,
                    dc,
                    km,
                    playing: sv.playing,
                    max_players: sv.max_players,
                    estimated_ping: round(ping).clamp(1, 999),
                    score: ping + population_penalty(sv.playing, sv.max_players, job.prefer_empty),
                    blocked_closest_city: None,
                };
                let count = {
                    let mut r = lock(&results);
                    r.push(cand);
                    r.len()
                };
                let required = if !job.preferred.is_empty() { 3 } else { EARLY_EXIT_CLOSEST };
                if usable && on_target && populated && good_enough.fetch_add(1, Ordering::SeqCst) + 1 >= required && count >= EARLY_EXIT_MIN_RESULTS {
                    stop.store(true, Ordering::SeqCst);
                }
            }
            let (m, cv) = &*done;
            *lock(m) += 1;
            cv.notify_all();
        });
    }
    let (m, cv) = &*done;
    let mut finished = lock(m);
    while *finished < workers {
        let left = job.deadline - java::now_ms();
        if left <= 0 {
            break;
        }
        finished = cv.wait_timeout(finished, Duration::from_millis(left.max(1) as u64)).map(|r| r.0).unwrap_or_else(|e| e.into_inner().0);
    }
    drop(finished);
    stop.store(true, Ordering::SeqCst);
    lock(&results).clone()
}

pub struct Picked {
    pub candidate: Option<Candidate>,
    pub save_geo: Option<Value>,
}

pub fn pick(place_id: i64, exclude: &HashSet<String>, s: &Settings) -> Picked {
    let deadline = java::now_ms() + DEADLINE_MS;
    let started = java::now_ms();
    let mut out = Picked { candidate: None, save_geo: None };
    if s.cookie.is_empty() {
        report("No Roblox login found, sign in to use the matchmaker");
        return out;
    }
    let filtering = !s.preferred.is_empty() || !s.blocked.is_empty();
    let mut budget = s.budget.clamp(MIN_CANDIDATES, MAX_CANDIDATES);
    if filtering {
        budget = FILTERED_CEILING.min(budget * 2);
    }
    let (geo_tx, geo_rx) = mpsc::channel();
    let saved = s.saved_geo.clone();
    std::thread::spawn(move || {
        let _ = geo_tx.send(geo(&saved));
    });
    let (pool_tx, pool_rx) = mpsc::channel();
    let (cookie, prefer_empty, v2) = (s.cookie.clone(), s.prefer_empty, s.v2);
    std::thread::spawn(move || {
        let _ = pool_tx.send(list_servers(place_id, &cookie, prefer_empty, deadline));
    });
    let cookie = s.cookie.clone();
    std::thread::spawn(move || prime_csrf(place_id, &cookie, v2));
    let wait = || Duration::from_millis((deadline - java::now_ms()).max(1) as u64);
    let (geo, save) = match geo_rx.recv_timeout(wait()) {
        Ok(g) => g,
        Err(_) => {
            report("Initial server search failed or timed out");
            return out;
        }
    };
    out.save_geo = save;
    let pool = match pool_rx.recv_timeout(wait()) {
        Ok(p) => p,
        Err(_) => {
            report("Initial server search failed or timed out");
            return out;
        }
    };
    let Some(geo) = geo else {
        report("Cannot match without your location");
        return out;
    };
    let filtered: Vec<Server> = pool.into_iter().filter(|sv| !exclude.contains(&sv.job_id)).collect();
    if filtered.is_empty() {
        report("No untried public servers available for this place");
        return out;
    }
    let probe_list = if filtered.len() as i64 > budget { stratify(&filtered, budget as usize) } else { filtered.clone() };
    net::info(TAG, &format!("Server list ready in {}ms, probing {} of {} servers for place {place_id}", java::now_ms() - started, probe_list.len(), filtered.len()));
    let floor_ms = estimate_rtt(nearest_km(&geo));
    let probe_len = probe_list.len();
    let job = ProbeJob {
        place_id,
        cookie: s.cookie.clone(),
        geo,
        preferred: s.preferred.clone(),
        blocked: s.blocked.clone(),
        prefer_empty: s.prefer_empty,
        floor_ms,
        v2: s.v2,
        deadline,
    };
    let probed = probe(job, probe_list);
    if probed.is_empty() {
        report("No probed server could be resolved to a datacenter");
        return out;
    }
    net::info(TAG, &format!("Probed {} of {probe_len} servers, {}ms total", probed.len(), java::now_ms() - started));
    let closest_overall = probed.iter().fold(None::<&Candidate>, |best, x| match best {
        Some(b) if b.km <= x.km => Some(b),
        _ => Some(x),
    });
    let mut allowed: Vec<Candidate> = probed.iter().filter(|x| !s.blocked.contains(&x.dc.key())).cloned().collect();
    if allowed.is_empty() {
        report("Every probed server was in a blocked datacenter, nothing to pick");
        return out;
    }
    let blocked_closest = closest_overall.filter(|c| s.blocked.contains(&c.dc.key())).map(|c| c.dc.city.clone());
    if !s.preferred.is_empty() {
        let in_preferred: Vec<Candidate> = allowed.iter().filter(|x| matches_preferred(&x.dc, &s.preferred)).cloned().collect();
        if !in_preferred.is_empty() {
            allowed = in_preferred;
        }
    } else {
        let closest = allowed.iter().map(|x| x.estimated_ping as f64).fold(f64::MAX, f64::min);
        let close: Vec<Candidate> = allowed.iter().filter(|x| x.estimated_ping as f64 <= closest + CLOSEST_BAND_MS).cloned().collect();
        if !close.is_empty() {
            allowed = close;
        }
        if !s.prefer_empty {
            let active: Vec<Candidate> = allowed.iter().filter(|x| x.playing >= 4).cloned().collect();
            if !active.is_empty() {
                allowed = active;
            }
            let headroom: Vec<Candidate> = allowed.iter().filter(|x| safe_headroom(x)).cloned().collect();
            if !headroom.is_empty() {
                allowed = headroom;
            }
        }
    }
    let mut winner = allowed.iter().fold(None::<&Candidate>, |best, x| match best {
        Some(b) if b.score <= x.score => Some(b),
        _ => Some(x),
    });
    let Some(mut winner) = winner.take().cloned() else {
        return out;
    };
    winner.blocked_closest_city = blocked_closest;
    let players = if winner.max_players > 0 { format!("{}/{} players", winner.playing, winner.max_players) } else { "player count unknown".into() };
    let winner_preferred = !s.preferred.is_empty() && matches_preferred(&winner.dc, &s.preferred);
    if !winner_preferred && winner.estimated_ping as f64 > HANDOFF_PING_MS && winner.estimated_ping as f64 > floor_ms * HANDOFF_FLOOR_MULTIPLIER {
        report(&format!("Every server found is far away, the best is about {}ms, letting Roblox pick a fresh nearby server", winner.estimated_ping));
        return out;
    }
    report(&format!("Picked {}, about {}ms, {players}", winner.dc.city, winner.estimated_ping));
    out.candidate = Some(winner);
    out
}
