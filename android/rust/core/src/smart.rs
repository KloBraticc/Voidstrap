use crate::java;
use crate::net::{self, Client};
use serde_json::{Map, Value, json};
use std::collections::HashMap;
use std::path::Path;
use std::sync::Mutex;

const PAGE_LIMIT: i64 = 100;
const MAX_PAGES: usize = 3;
const UNKNOWN_PING: f64 = 250.0;
const MAX_PING: f64 = 300.0;
const FPS_FLOOR: f64 = 55.0;
const LOW_FPS_PENALTY: f64 = 40.0;
const FILL_WEIGHT: f64 = 30.0;
const NEAR_KM: f64 = 1500.0;
const FAR_KM: f64 = 4000.0;
const FRESH_MS: i64 = 12 * 60 * 60 * 1000;
const LOCATION_CACHE_MS: i64 = 7 * 24 * 60 * 60 * 1000;
const KNOWN_LIMIT: usize = 200;
const FILE: &str = "smart_servers.json";
static FILE_LOCK: Mutex<()> = Mutex::new(());

pub const REGIONS: [(&str, f64, f64); 17] = [
    ("us_east", 39.04, -77.49),
    ("us_central", 32.78, -96.80),
    ("us_west", 34.05, -118.24),
    ("south_america", -23.55, -46.63),
    ("uk", 51.51, -0.13),
    ("europe_west", 52.37, 4.90),
    ("europe_central", 50.11, 8.68),
    ("europe_east", 44.43, 26.10),
    ("middle_east", 25.20, 55.27),
    ("india", 19.08, 72.88),
    ("southeast_asia", 1.35, 103.82),
    ("east_asia", 22.32, 114.17),
    ("japan", 35.68, 139.69),
    ("korea", 37.57, 126.98),
    ("australia", -33.87, 151.21),
    ("new_zealand", -36.85, 174.76),
    ("south_africa", -26.20, 28.05),
];

const SOUTH_AMERICA: &[&str] = &[
    "Sao_Paulo", "Argentina", "Buenos_Aires", "Santiago", "Montevideo", "Bogota", "Lima", "Caracas", "La_Paz", "Asuncion", "Guayaquil", "Manaus", "Recife", "Fortaleza",
    "Belem", "Cuiaba", "Porto_Velho", "Campo_Grande", "Bahia", "Maceio", "Araguaina", "Cayenne", "Paramaribo", "Guyana", "Punta_Arenas",
];
const UK: &[&str] = &["London", "Dublin", "Belfast", "Guernsey", "Isle_of_Man", "Jersey"];
const EUROPE_WEST: &[&str] = &["Paris", "Brussels", "Amsterdam", "Luxembourg", "Madrid", "Monaco", "Andorra", "Lisbon", "Canary", "Madeira", "Faroe", "Reykjavik"];
const EAST_ASIA: &[&str] = &["Hong_Kong", "Taipei", "Shanghai", "Macau", "Chongqing", "Harbin", "Urumqi"];
const SOUTHERN_AFRICA: &[&str] = &[
    "Johannesburg", "Maputo", "Harare", "Lusaka", "Gaborone", "Windhoek", "Maseru", "Mbabane", "Blantyre", "Lubumbashi", "Nairobi", "Kampala", "Dar_es_Salaam",
    "Addis_Ababa", "Luanda", "Kinshasa",
];

fn has(id: &str, names: &[&str]) -> bool {
    names.iter().any(|n| id.contains(n))
}

pub fn region(key: &str) -> Option<(&'static str, f64, f64)> {
    REGIONS.iter().find(|r| r.0 == key).copied()
}

pub fn from_time_zone(id: &str, raw_offset_ms: i64) -> &'static str {
    let hours = raw_offset_ms as f64 / 3_600_000.0;
    if ["America/", "US/", "Canada/", "Brazil/", "Chile/"].iter().any(|p| id.starts_with(p)) {
        if id.starts_with("Brazil/") || id.starts_with("Chile/") || has(id, SOUTH_AMERICA) || hours >= -3.25 {
            return "south_america";
        }
        if hours >= -5.5 {
            return "us_east";
        }
        if hours >= -7.0 {
            return "us_central";
        }
        return "us_west";
    }
    if id.starts_with("Europe/") || id.starts_with("Atlantic/") {
        if has(id, UK) {
            return "uk";
        }
        if has(id, EUROPE_WEST) || hours <= 0.0 {
            return "europe_west";
        }
        if hours >= 2.0 {
            return "europe_east";
        }
        return "europe_central";
    }
    if id.starts_with("Australia/") {
        return "australia";
    }
    if id.starts_with("Pacific/") {
        if hours >= 11.5 {
            return "new_zealand";
        }
        if hours <= -8.0 {
            return "us_west";
        }
        return "australia";
    }
    if id.starts_with("Africa/") {
        if has(id, SOUTHERN_AFRICA) {
            return "south_africa";
        }
        if hours >= 2.0 {
            return "middle_east";
        }
        return "europe_west";
    }
    if id.starts_with("Asia/") || id.starts_with("Indian/") {
        if id.ends_with("Tokyo") {
            return "japan";
        }
        if id.ends_with("Seoul") || id.ends_with("Pyongyang") {
            return "korea";
        }
        if has(id, EAST_ASIA) {
            return "east_asia";
        }
        if hours >= 9.0 {
            return "japan";
        }
        if hours >= 7.0 {
            return "southeast_asia";
        }
        if hours >= 5.0 {
            return "india";
        }
        if hours >= 3.0 {
            return "middle_east";
        }
        return "europe_east";
    }
    let lon = hours * 15.0;
    let mut best = REGIONS[0].0;
    let mut best_gap = f64::MAX;
    for r in REGIONS {
        let gap = ((((r.2 - lon) % 360.0) + 540.0) % 360.0 - 180.0).abs();
        if gap < best_gap {
            best_gap = gap;
            best = r.0;
        }
    }
    best
}

pub fn km(lat1: f64, lon1: f64, lat2: f64, lon2: f64) -> f64 {
    let (p1, p2) = (lat1.to_radians(), lat2.to_radians());
    let dp = (lat2 - lat1).to_radians();
    let dl = (lon2 - lon1).to_radians();
    let a = (dp / 2.0).sin() * (dp / 2.0).sin() + p1.cos() * p2.cos() * (dl / 2.0).sin() * (dl / 2.0).sin();
    6371.0 * 2.0 * a.sqrt().atan2((1.0 - a).sqrt())
}

#[derive(Clone, Debug)]
pub struct Known {
    pub job: String,
    pub place: i64,
    pub game: String,
    pub city: String,
    pub country: String,
    pub lat: f64,
    pub lon: f64,
    pub time: i64,
}

impl Known {
    fn to_json(&self) -> Value {
        let mut o = Map::new();
        o.insert("job".into(), self.job.clone().into());
        o.insert("place".into(), self.place.into());
        o.insert("game".into(), self.game.clone().into());
        o.insert("city".into(), self.city.clone().into());
        o.insert("country".into(), self.country.clone().into());
        o.insert("lat".into(), json!(self.lat));
        o.insert("lon".into(), json!(self.lon));
        o.insert("time".into(), self.time.into());
        Value::Object(o)
    }
}

fn opt_double(o: &Value, k: &str) -> f64 {
    match o.get(k) {
        Some(Value::Number(n)) => n.as_f64().unwrap_or(f64::NAN),
        Some(Value::String(s)) => s.trim().parse().unwrap_or(f64::NAN),
        _ => f64::NAN,
    }
}

pub fn known(files: &Path) -> Vec<Known> {
    let _g = FILE_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    read_known(files)
}

fn read_known(files: &Path) -> Vec<Known> {
    let Ok(b) = std::fs::read(files.join(FILE)) else {
        return Vec::new();
    };
    let Ok(Value::Array(list)) = serde_json::from_slice::<Value>(&b) else {
        return Vec::new();
    };
    list.iter()
        .filter(|o| o.is_object())
        .map(|o| Known {
            job: java::opt_str(o, "job", ""),
            place: java::opt_long(o, "place", 0),
            game: java::opt_str(o, "game", ""),
            city: java::opt_str(o, "city", ""),
            country: java::opt_str(o, "country", ""),
            lat: opt_double(o, "lat"),
            lon: opt_double(o, "lon"),
            time: java::opt_long(o, "time", 0),
        })
        .filter(|k| !k.job.is_empty() && !k.lat.is_nan() && !k.lon.is_nan())
        .collect()
}

fn remember(files: &Path, k: Known) {
    let _g = FILE_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let mut list = read_known(files);
    list.retain(|o| o.job != k.job);
    list.insert(0, k);
    list.truncate(KNOWN_LIMIT);
    let out = Value::Array(list.iter().map(Known::to_json).collect());
    let f = files.join(FILE);
    let tmp = files.join(format!("{FILE}.tmp"));
    if std::fs::write(&tmp, java::to_string(&out, 0)).is_ok() && std::fs::rename(&tmp, &f).is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
}

pub fn forget(files: &Path) {
    let _g = FILE_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let _ = std::fs::remove_file(files.join(FILE));
}

struct Server {
    id: String,
    playing: i64,
    max: i64,
    ping: i64,
    fps: f64,
}

fn healthy(s: &Server, min_fps: i64) -> bool {
    if s.max > 0 && s.playing >= s.max {
        return false;
    }
    if min_fps > 0 && s.fps > 0.0 && s.fps < min_fps as f64 {
        return false;
    }
    s.ping <= 0 || s.ping as f64 <= MAX_PING
}

fn quality(s: &Server, prefer_empty: bool) -> f64 {
    let mut cost = if s.ping > 0 { s.ping as f64 } else { UNKNOWN_PING };
    if s.fps > 0.0 && s.fps < FPS_FLOOR {
        cost += LOW_FPS_PENALTY;
    }
    let fill = if s.max > 0 { s.playing as f64 / s.max as f64 } else { 0.0 };
    cost += if prefer_empty { fill * FILL_WEIGHT } else { (1.0 - fill) * FILL_WEIGHT };
    cost
}

fn servers(client: &Client, place: i64, prefer_empty: bool) -> Vec<Server> {
    let mut out = Vec::new();
    let mut cursor = String::new();
    for _ in 0..MAX_PAGES {
        let mut url = format!("https://games.roblox.com/v1/games/{place}/servers/Public?excludeFullGames=true&limit={PAGE_LIMIT}&sortOrder={}", if prefer_empty { "Asc" } else { "Desc" });
        if !cursor.is_empty() {
            url.push_str(&format!("&cursor={}", net::uri_encode(&cursor)));
        }
        let Some(body) = client.json(&url) else {
            break;
        };
        let Some(data) = body.get("data").and_then(Value::as_array) else {
            break;
        };
        for o in data.iter().filter(|o| o.is_object()) {
            let id = java::opt_str(o, "id", "");
            if id.is_empty() {
                continue;
            }
            out.push(Server {
                id,
                playing: java::opt_long(o, "playing", 0),
                max: java::opt_long(o, "maxPlayers", 0),
                ping: java::opt_long(o, "ping", 0),
                fps: match o.get("fps") {
                    Some(Value::Number(n)) => n.as_f64().unwrap_or(0.0),
                    Some(Value::String(s)) => s.trim().parse().unwrap_or(0.0),
                    _ => 0.0,
                },
            });
        }
        cursor = java::opt_str(&body, "nextPageCursor", "");
        if cursor.is_empty() || cursor == "null" {
            break;
        }
    }
    out
}

pub fn seen(files: &Path, place: i64) -> HashMap<String, Known> {
    let now = java::now_ms();
    let mut out = HashMap::new();
    for k in known(files) {
        if k.place == place && now - k.time < FRESH_MS {
            out.insert(k.job.clone(), k);
        }
    }
    out
}

pub struct Choice {
    pub job: String,
    pub city: String,
    pub ping: i64,
    pub fps: f64,
}

pub fn pick(client: &Client, files: &Path, place: i64, me: (f64, f64), near: bool, prefer_empty: bool, min_fps: i64) -> Option<Choice> {
    let seen = seen(files, place);
    let list = servers(client, place, prefer_empty);
    let mut best: Option<(&Server, Option<&Known>)> = None;
    let mut best_cost = f64::MAX;
    for s in &list {
        if !healthy(s, min_fps) {
            continue;
        }
        let k = seen.get(&s.id);
        if near && k.is_none() {
            continue;
        }
        let dist = k.map_or(NEAR_KM, |k| km(me.0, me.1, k.lat, k.lon));
        if dist > if near { NEAR_KM } else { FAR_KM } {
            continue;
        }
        let cost = quality(s, prefer_empty) + dist / 10.0;
        if cost < best_cost {
            best_cost = cost;
            best = Some((s, k));
        }
    }
    best.map(|(s, k)| Choice { job: s.id.clone(), city: k.map(|k| k.city.clone()).unwrap_or_default(), ping: s.ping, fps: s.fps })
}

pub fn on_joined(client: &Client, cache: &Path, files: &Path, job: &str, place: i64, universe: i64, address: &str) -> bool {
    let ip_shape = (7..=15).contains(&address.len()) && address.bytes().all(|b| b.is_ascii_digit() || b == b'.');
    if job.is_empty() || place <= 0 || !ip_shape {
        return false;
    }
    let Some(info) = client.cached_json(cache, &format!("https://ipinfo.io/{address}/json"), LOCATION_CACHE_MS) else {
        return false;
    };
    let loc = java::opt_str(&info, "loc", "");
    let parts: Vec<&str> = loc.split(',').collect();
    if parts.len() != 2 {
        return false;
    }
    let (Some(lat), Some(lon)) = (crate::flags::java_double(parts[0]), crate::flags::java_double(parts[1])) else {
        return false;
    };
    let game = game_name(client, cache, place, universe);
    remember(
        files,
        Known {
            job: job.to_string(),
            place,
            game,
            city: java::utf16_prefix(&java::opt_str(&info, "city", ""), 80).to_string(),
            country: java::utf16_prefix(&java::opt_str(&info, "country", ""), 8).to_string(),
            lat,
            lon,
            time: java::now_ms(),
        },
    );
    true
}

fn game_name(client: &Client, cache: &Path, place: i64, universe: i64) -> String {
    let mut universe = universe;
    if universe <= 0 {
        universe = client
            .cached_json(cache, &format!("https://apis.roblox.com/universes/v1/places/{place}/universe"), LOCATION_CACHE_MS)
            .map_or(0, |o| java::opt_long(&o, "universeId", 0));
    }
    if universe <= 0 {
        return String::new();
    }
    let Some(games) = client.cached_json(cache, &format!("https://games.roblox.com/v1/games?universeIds={universe}"), 5 * 60 * 1000) else {
        return String::new();
    };
    games.get("data").and_then(Value::as_array).and_then(|a| a.first()).filter(|g| g.is_object()).map_or(String::new(), |g| java::utf16_prefix(&java::opt_str(g, "name", ""), 120).to_string())
}

pub fn known_json(files: &Path) -> Value {
    Value::Array(known(files).iter().map(Known::to_json).collect())
}
