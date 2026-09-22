use crate::java::{self, opt_bool, opt_int, opt_long, opt_str};
use crate::net::{Client, sha1_hex};
use serde_json::{Map, Value, json};
use std::collections::HashMap;
use std::path::Path;
use std::sync::Mutex;

const MINUTE: i64 = 60_000;
const NEWS_FEED: &str = "https://devforum.roblox.com/c/updates/announcements/36.json";
const CATALOG: &str = "https://catalog.roblox.com/v1/search/items/details?Category=1&Limit=10&SortType=3";
const ACRONYMS: [&str; 15] = ["rdc", "ip", "ugc", "api", "sdk", "ai", "ui", "ux", "vr", "ar", "os", "fps", "cdn", "npc", "id"];
const LIMITED: [&str; 3] = ["limited", "limitedunique", "collectible"];
const TYPES: &[(i32, &str)] = &[
    (1, "Image"), (2, "T-Shirt"), (3, "Audio"), (4, "Mesh"), (8, "Hat"), (9, "Place"), (10, "Model"), (11, "Shirt"), (12, "Pants"), (13, "Decal"), (17, "Head"), (18, "Face"),
    (19, "Gear"), (21, "Badge"), (24, "Animation"), (27, "Torso"), (28, "Right Arm"), (29, "Left Arm"), (30, "Left Leg"), (31, "Right Leg"), (32, "Package"), (34, "Game Pass"),
    (38, "Plugin"), (40, "MeshPart"), (41, "Hair Accessory"), (42, "Face Accessory"), (43, "Neck Accessory"), (44, "Shoulder Accessory"), (45, "Front Accessory"),
    (46, "Back Accessory"), (47, "Waist Accessory"), (48, "Climb Animation"), (49, "Death Animation"), (50, "Fall Animation"), (51, "Idle Animation"), (52, "Jump Animation"),
    (53, "Run Animation"), (54, "Swim Animation"), (55, "Walk Animation"), (56, "Pose Animation"), (61, "Emote"), (62, "Video"), (64, "T-Shirt Accessory"),
    (65, "Shirt Accessory"), (66, "Pants Accessory"), (67, "Jacket Accessory"), (68, "Sweater Accessory"), (69, "Shorts Accessory"), (70, "Left Shoe Accessory"),
    (71, "Right Shoe Accessory"), (72, "Dress Skirt Accessory"), (76, "Eyebrow Accessory"), (77, "Eyelash Accessory"), (78, "Mood Animation"), (79, "Dynamic Head"),
];

pub fn forget(cache: &Path, url: &str) {
    let _ = std::fs::remove_file(cache.join("feeds").join(format!("{}.json", sha1_hex(url.as_bytes()))));
}

pub fn time(iso: &str) -> i64 {
    java::parse_instant(iso).unwrap_or(0)
}

fn clip(s: &str, max: usize) -> String {
    java::utf16_prefix(s, max).to_string()
}

fn obj<'a>(o: &'a Value, key: &str) -> Option<&'a Value> {
    o.get(key).filter(|v| v.is_object())
}

fn strict<'a>(v: &'a Option<Value>, key: &str) -> impl Iterator<Item = &'a Value> {
    v.iter().filter_map(move |v| v.get(key).and_then(Value::as_array)).flatten().take_while(|o| o.is_object())
}

fn loose<'a>(v: &'a Option<Value>, key: &str) -> impl Iterator<Item = &'a Value> {
    v.iter().filter_map(move |v| v.get(key).and_then(Value::as_array)).flatten().filter(|o| o.is_object())
}

fn join(ids: &[i64]) -> String {
    ids.iter().map(i64::to_string).collect::<Vec<_>>().join(",")
}

fn opt_url(o: &Value, key: &str) -> Option<String> {
    o.get(key).and_then(Value::as_str).map(str::to_string)
}

#[derive(Default)]
pub struct Game {
    pub place: i64,
    pub universe: i64,
    pub name: String,
    pub icon: Option<String>,
    pub thumb: Option<String>,
    pub creator: String,
    pub description: String,
    pub genre: String,
    pub playing: i64,
    pub visits: i64,
    pub max_players: i32,
    pub created: i64,
    pub updated: i64,
    pub like: i32,
}

impl Game {
    pub fn from(o: &Value) -> Game {
        Game {
            place: opt_long(o, "placeId", 0),
            universe: opt_long(o, "universeId", 0),
            name: opt_str(o, "name", ""),
            icon: opt_url(o, "iconUrl"),
            thumb: opt_url(o, "thumbUrl"),
            creator: opt_str(o, "creator", ""),
            description: opt_str(o, "description", ""),
            genre: opt_str(o, "genre", ""),
            playing: opt_long(o, "playing", 0),
            visits: opt_long(o, "visits", 0),
            max_players: opt_int(o, "maxPlayers", 0),
            created: opt_long(o, "created", 0),
            updated: opt_long(o, "updated", 0),
            like: opt_int(o, "likePercent", -1),
        }
    }

    pub fn to_json(&self) -> Value {
        json!({"placeId": self.place, "universeId": self.universe, "name": self.name, "iconUrl": self.icon, "thumbUrl": self.thumb, "creator": self.creator,
            "description": self.description, "genre": self.genre, "playing": self.playing, "visits": self.visits, "maxPlayers": self.max_players,
            "created": self.created, "updated": self.updated, "likePercent": self.like})
    }

    fn title(&self) -> String {
        if self.name.is_empty() { format!("Place {}", self.place) } else { self.name.clone() }
    }
}

pub fn enrich(c: &Client, cache: &Path, games: &mut [Game]) {
    for g in games.iter_mut() {
        if g.universe > 0 {
            continue;
        }
        let url = format!("https://apis.roblox.com/universes/v1/places/{}/universe", g.place);
        if let Some(o) = c.cached_json(cache, &url, 7 * 24 * 60 * MINUTE) {
            g.universe = opt_long(&o, "universeId", 0);
        }
        if g.universe <= 0 {
            forget(cache, &url);
        }
    }
    let mut order: Vec<i64> = Vec::new();
    let mut by: HashMap<i64, Vec<usize>> = HashMap::new();
    for (i, g) in games.iter().enumerate() {
        if g.universe > 0 {
            by.entry(g.universe)
                .or_insert_with(|| {
                    order.push(g.universe);
                    Vec::new()
                })
                .push(i);
        }
    }
    for chunk in order.chunks(50) {
        let ids = join(chunk);
        let games_url = format!("https://games.roblox.com/v1/games?universeIds={ids}");
        let icons_url = format!("https://thumbnails.roblox.com/v1/games/icons?universeIds={ids}&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false");
        let d = c.cached_json(cache, &games_url, 5 * MINUTE);
        for o in strict(&d, "data") {
            for &i in by.get(&opt_long(o, "id", 0)).into_iter().flatten() {
                let g = &mut games[i];
                let name = opt_str(o, "name", "");
                if !name.is_empty() {
                    g.name = clip(&name, 200);
                }
                g.description = opt_str(o, "description", "");
                g.creator = obj(o, "creator").map_or(String::new(), |c| opt_str(c, "name", ""));
                g.playing = opt_long(o, "playing", 0);
                g.visits = opt_long(o, "visits", 0);
                g.max_players = opt_int(o, "maxPlayers", 0);
                g.created = time(&opt_str(o, "created", ""));
                g.updated = time(&opt_str(o, "updated", ""));
                let genre = opt_str(o, "genre_l1", "");
                g.genre = if genre.is_empty() { opt_str(o, "genre", "") } else { genre };
            }
        }
        let d = c.cached_json(cache, &format!("https://games.roblox.com/v1/games/votes?universeIds={ids}"), 5 * MINUTE);
        for o in strict(&d, "data") {
            let up = opt_long(o, "upVotes", 0);
            let total = up.wrapping_add(opt_long(o, "downVotes", 0));
            if total > 0 {
                for &i in by.get(&opt_long(o, "id", 0)).into_iter().flatten() {
                    games[i].like = java::round(up as f64 * 100.0 / total as f64) as i32;
                }
            }
        }
        let d = c.cached_json(cache, &icons_url, 24 * 60 * MINUTE);
        for o in strict(&d, "data") {
            let url = opt_str(o, "imageUrl", "");
            if url.starts_with("https://") {
                for &i in by.get(&opt_long(o, "targetId", 0)).into_iter().flatten() {
                    games[i].icon = Some(url.clone());
                }
            }
        }
        for id in chunk {
            for &i in &by[id] {
                if games[i].name.is_empty() {
                    forget(cache, &games_url);
                }
                if games[i].icon.is_none() {
                    forget(cache, &icons_url);
                }
            }
        }
        let d = c.cached_json(
            cache,
            &format!("https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={ids}&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false"),
            24 * 60 * MINUTE,
        );
        for o in strict(&d, "data") {
            let url = match o.get("thumbnails").and_then(Value::as_array) {
                Some(t) if !t.is_empty() => match t[0].is_object() {
                    true => opt_str(&t[0], "imageUrl", ""),
                    false => break,
                },
                _ => String::new(),
            };
            if url.starts_with("https://") {
                for &i in by.get(&opt_long(o, "universeId", 0)).into_iter().flatten() {
                    games[i].thumb = Some(url.clone());
                }
            }
        }
    }
}

fn asset_images(c: &Client, cache: &Path, ids: &[i64], size: &str, format: &str) -> HashMap<i64, String> {
    let mut out = HashMap::new();
    for chunk in ids.chunks(50) {
        let d = c.cached_json(cache, &format!("https://thumbnails.roblox.com/v1/assets?assetIds={}&size={size}&format={format}&isCircular=false", join(chunk)), 24 * 60 * MINUTE);
        for o in strict(&d, "data") {
            let url = opt_str(o, "imageUrl", "");
            if url.starts_with("https://") {
                out.insert(opt_long(o, "targetId", 0), url);
            }
        }
    }
    out
}

pub fn events(c: &Client, cache: &Path, games: &[Game], now: i64) -> Vec<Value> {
    struct Event {
        id: String,
        title: String,
        subtitle: String,
        start: i64,
        media: i64,
    }
    let mut found: Vec<Event> = Vec::new();
    let mut checked = 0;
    for g in games {
        if g.universe <= 0 {
            continue;
        }
        checked += 1;
        if checked > 12 {
            continue;
        }
        let d = c.cached_json(cache, &format!("https://apis.roblox.com/virtual-events/v1/universes/{}/virtual-events", g.universe), 20 * MINUTE);
        for o in strict(&d, "data") {
            if opt_str(o, "eventStatus", "") != "active" || opt_str(o, "eventVisibility", "") != "public" {
                continue;
            }
            let t = obj(o, "eventTime");
            let end = t.map_or(0, |t| time(&opt_str(t, "endUtc", "")));
            if end > 0 && end < now {
                continue;
            }
            let sub = opt_str(o, "displaySubtitle", &opt_str(o, "subtitle", ""));
            let mut media = 0;
            if let Some(thumbs) = o.get("thumbnails").and_then(Value::as_array).filter(|a| !a.is_empty()) {
                if !thumbs[0].is_object() {
                    break;
                }
                media = opt_long(&thumbs[0], "mediaId", 0);
            }
            let e = Event {
                id: opt_str(o, "id", ""),
                title: opt_str(o, "displayTitle", &opt_str(o, "title", "")),
                subtitle: if sub.is_empty() { g.title() } else { sub },
                start: t.map_or(0, |t| time(&opt_str(t, "startUtc", ""))),
                media,
            };
            if !e.id.is_empty() {
                found.push(e);
            }
        }
    }
    found.sort_by_key(|e| if e.start == 0 { i64::MAX } else { e.start });
    found.truncate(24);
    let media: Vec<i64> = found.iter().map(|e| e.media).filter(|&m| m > 0).collect();
    let images = asset_images(c, cache, &media, "768x432", "Jpeg");
    found.iter().map(|e| json!({"id": e.id, "title": e.title, "subtitle": e.subtitle, "start": e.start, "mediaId": e.media, "thumbUrl": images.get(&e.media)})).collect()
}

pub fn passes(c: &Client, cache: &Path, universe: i64) -> Vec<Value> {
    let d = c.cached_json(cache, &format!("https://apis.roblox.com/game-passes/v1/universes/{universe}/game-passes?passView=Full&pageSize=100"), 10 * MINUTE);
    let mut out: Vec<(i64, String, bool, i64, i64)> = Vec::new();
    for o in strict(&d, "gamePasses") {
        let id = opt_long(o, "id", 0);
        let p = (id, opt_str(o, "displayName", &opt_str(o, "name", "")), opt_bool(o, "isForSale", false), opt_long(o, "price", 0), opt_long(o, "displayIconImageAssetId", 0));
        if id > 0 {
            out.push(p);
        }
    }
    let assets: Vec<i64> = out.iter().map(|p| p.4).filter(|&a| a > 0).collect();
    let images = asset_images(c, cache, &assets, "150x150", "Png");
    out.iter().map(|p| json!({"id": p.0, "name": p.1, "forSale": p.2, "price": p.3, "iconAsset": p.4, "iconUrl": images.get(&p.4)})).collect()
}

fn timestamp(s: &[char], i: usize) -> Option<usize> {
    let d = |k: usize| s.get(k).and_then(|&c| java::digit(c)).is_some();
    let l = |k: usize, c: char| s.get(k) == Some(&c);
    let shape = (0..4).all(|k| d(i + k))
        && l(i + 4, '-')
        && d(i + 5)
        && d(i + 6)
        && l(i + 7, '-')
        && d(i + 8)
        && d(i + 9)
        && l(i + 10, 'T')
        && d(i + 11)
        && d(i + 12)
        && l(i + 13, ':')
        && d(i + 14)
        && d(i + 15)
        && l(i + 16, ':')
        && d(i + 17)
        && d(i + 18);
    if !shape {
        return None;
    }
    let mut p = i + 19;
    if l(p, '.') && d(p + 1) {
        let mut q = p + 1;
        while d(q) {
            q += 1;
        }
        if l(q, 'Z') {
            return Some(q + 1);
        }
    }
    if l(p, 'Z') {
        p += 1;
        return Some(p);
    }
    None
}

fn join_tail(s: &[char], k: usize) -> Option<(usize, usize)> {
    let lit = |p: usize, t: &str| -> Option<usize> {
        let mut p = p;
        for c in t.chars() {
            if s.get(p) != Some(&c) {
                return None;
            }
            p += 1;
        }
        Some(p)
    };
    let mut p = lit(k, "Joining game '")?;
    let hex = p;
    while s.get(p).is_some_and(|c| c.is_ascii_hexdigit() || *c == '-') {
        p += 1;
    }
    if p == hex {
        return None;
    }
    let p = lit(p, "' place ")?;
    let mut q = p;
    while s.get(q).is_some_and(|&c| java::digit(c).is_some()) {
        q += 1;
    }
    (q > p).then_some((p, q))
}

pub fn joins(text: &str) -> Vec<(i64, i64)> {
    let s: Vec<char> = text.chars().collect();
    let mut out = Vec::new();
    let mut i = 0;
    while i < s.len() {
        let Some(ts) = timestamp(&s, i) else {
            i += 1;
            continue;
        };
        let mut le = ts;
        while le < s.len() && !java::line_terminator(s[le]) {
            le += 1;
        }
        let Some((ds, de)) = (ts..le).rev().find_map(|k| join_tail(&s, k)) else {
            i += 1;
            continue;
        };
        let when = time(&s[i..ts].iter().collect::<String>());
        let place = s[ds..de].iter().try_fold(0i64, |acc, &c| acc.checked_mul(10)?.checked_add(java::char_digit(c)? as i64));
        if let Some(place) = place
            && when > 0
            && place > 0
        {
            out.push((when, place));
        }
        i = de;
    }
    out
}

fn image(raw: &str) -> String {
    let v = java::trim(raw);
    if let Some(rest) = v.strip_prefix("//") {
        return format!("https://{rest}");
    }
    if v.starts_with("https://") { v.to_string() } else { String::new() }
}

fn tag(tags: Option<&Vec<Value>>) -> String {
    for o in tags.into_iter().flatten() {
        let raw = if o.is_object() { opt_str(o, "name", "") } else { java::value_of(o) };
        let t = java::trim(&raw);
        if t.is_empty() || java::eq_ignore_case(t, "featured") || t == "null" {
            continue;
        }
        let words: Vec<String> = t
            .replace('-', " ")
            .split(' ')
            .filter(|w| !w.is_empty())
            .map(|w| {
                if ACRONYMS.contains(&w.to_lowercase().as_str()) {
                    w.to_uppercase()
                } else {
                    let mut ch = w.chars();
                    let first = ch.next().map(java::upper_char).into_iter();
                    first.chain(ch).collect()
                }
            })
            .collect();
        return words.join(" ");
    }
    String::new()
}

fn shorten(s: &str, keep: usize, max: usize) -> String {
    if java::utf16_len(s) <= max { s.to_string() } else { format!("{}...", java::trim(java::utf16_prefix(s, keep))) }
}

pub fn news(c: &Client, cache: &Path, latest: usize) -> Option<Vec<Value>> {
    let feed = c.cached_json(cache, NEWS_FEED, 30 * MINUTE)?;
    let list = obj(&feed, "topic_list")?.clone();
    let wrapped = Some(list);
    let mut out: Vec<(i64, Value, bool)> = Vec::new();
    for t in loose(&wrapped, "topics") {
        let id = opt_long(t, "id", 0);
        if opt_bool(t, "pinned", false) || id <= 0 {
            continue;
        }
        let title = java::trim(&opt_str(t, "title", "")).to_string();
        if title.is_empty() {
            continue;
        }
        let mut excerpt = opt_str(t, "excerpt", "").replace(['\n', '\r'], " ");
        while excerpt.contains("  ") {
            excerpt = excerpt.replace("  ", " ");
        }
        let slug = opt_str(t, "slug", "");
        let created = time(&opt_str(t, "created_at", ""));
        let img = image(&opt_str(t, "image_url", ""));
        let has_image = !img.is_empty();
        out.push((
            created,
            json!({
                "id": id,
                "title": shorten(&title, 89, 90),
                "summary": shorten(java::trim(&excerpt), 149, 150),
                "url": format!("https://devforum.roblox.com/t/{}/{id}", if slug.is_empty() { "topic" } else { &slug }),
                "image": img,
                "tag": tag(t.get("tags").and_then(Value::as_array)),
                "created": created,
            }),
            has_image,
        ));
    }
    out.sort_by(|a, b| b.0.cmp(&a.0));
    if latest == 0 {
        return Some(out.into_iter().map(|n| n.1).collect());
    }
    let with: Vec<Value> = out.iter().filter(|n| n.2).map(|n| n.1.clone()).collect();
    let without = out.iter().filter(|n| !n.2).map(|n| n.1.clone());
    Some(with.into_iter().chain(without).take(latest).collect())
}

pub fn card_title(title: &str) -> String {
    let v = java::trim(title);
    let units: Vec<u16> = v.encode_utf16().collect();
    if units.len() <= 44 {
        return v.to_string();
    }
    let mut cut = (0..=43).rev().find(|&i| units[i] == b' ' as u16).map_or(-1, |i| i as i64);
    if cut < 24 {
        cut = 43;
    }
    java::strip_end(&String::from_utf16_lossy(&units[..cut as usize]), |c| matches!(c, ' ' | ',' | ':' | ';'))
}

pub fn thumbnail(image: &str, width: i64) -> String {
    if image.is_empty() {
        return String::new();
    }
    format!("https://wsrv.nl/?url={}&w={width}&output=webp&q=80&we", crate::http::encode(image))
}

pub fn count(n: i64) -> String {
    let trim = |v: f64| {
        let r = java::round(v * 10.0) as f64 / 10.0;
        if r == r.floor() { (r as i64).to_string() } else { format!("{r:.1}") }
    };
    if n >= 1_000_000_000 {
        format!("{}B", trim(n as f64 / 1_000_000_000.0))
    } else if n >= 1_000_000 {
        format!("{}M", trim(n as f64 / 1_000_000.0))
    } else if n >= 1_000 {
        format!("{}K", trim(n as f64 / 1_000.0))
    } else {
        n.to_string()
    }
}

fn thumbs(c: &Client, cache: &Path, base: &str, ids: &[i64], images: &mut HashMap<i64, String>) {
    if ids.is_empty() {
        return;
    }
    let d = c.cached_json(cache, &format!("{base}{}&size=150x150&format=Png&isCircular=false", join(ids)), 10 * MINUTE);
    for t in loose(&d, "data") {
        let url = opt_str(t, "imageUrl", "");
        if opt_str(t, "state", "") == "Completed" && url.starts_with("https://") {
            images.insert(opt_long(t, "targetId", 0), url);
        }
    }
}

pub fn catalog(c: &Client, cache: &Path) -> Option<Vec<Value>> {
    let d = Some(c.cached_json(cache, CATALOG, 10 * MINUTE)?);
    let mut out: Vec<(i64, Map<String, Value>)> = Vec::new();
    let (mut ids, mut bundles) = (Vec::new(), Vec::new());
    for o in loose(&d, "data") {
        let id = opt_long(o, "id", 0);
        if id <= 0 {
            continue;
        }
        let bundle = java::eq_ignore_case(&opt_str(o, "itemType", ""), "Bundle");
        let t = opt_int(o, "assetType", 0);
        let kind = if bundle {
            "Bundle"
        } else if t <= 0 {
            ""
        } else {
            TYPES.iter().find(|(k, _)| *k == t).map_or("Asset", |(_, v)| v)
        };
        let limited = o.get("itemRestrictions").and_then(Value::as_array).is_some_and(|r| {
            r.iter().any(|v| LIMITED.contains(&java::value_of(v).to_lowercase().as_str()))
        });
        let price = if java::is_null(o, "price") { opt_int(o, "lowestPrice", 0) } else { opt_int(o, "price", 0) };
        let mut m = Map::new();
        m.insert("id".into(), id.into());
        m.insert("name".into(), clip(&opt_str(o, "name", ""), 120).into());
        m.insert("creator".into(), clip(&opt_str(o, "creatorName", ""), 80).into());
        m.insert("price".into(), price.into());
        m.insert("bundle".into(), bundle.into());
        m.insert("type".into(), kind.into());
        m.insert("limited".into(), limited.into());
        if bundle { &mut bundles } else { &mut ids }.push(id);
        out.push((id, m));
    }
    let mut images = HashMap::new();
    thumbs(c, cache, "https://thumbnails.roblox.com/v1/assets?assetIds=", &ids, &mut images);
    thumbs(c, cache, "https://thumbnails.roblox.com/v1/bundles/thumbnails?bundleIds=", &bundles, &mut images);
    Some(
        out.into_iter()
            .map(|(id, mut m)| {
                m.insert("image".into(), images.get(&id).cloned().unwrap_or_default().into());
                Value::Object(m)
            })
            .collect(),
    )
}

pub fn cards(c: &Client, cache: &Path, cards: &[Value]) -> Vec<Value> {
    let mut out: Vec<Map<String, Value>> = cards.iter().map(|v| v.as_object().cloned().unwrap_or_default()).collect();
    let mut universes = vec![0i64; out.len()];
    for (i, g) in cards.iter().enumerate() {
        universes[i] = opt_long(g, "universeId", 0);
        if let Some(o) = c.cached_json(cache, &format!("https://apis.roblox.com/universes/v1/places/{}/universe", opt_long(g, "placeId", 0)), 7 * 24 * 60 * MINUTE) {
            universes[i] = opt_long(&o, "universeId", 0);
        }
        out[i].insert("universeId".into(), universes[i].into());
    }
    let mut order: Vec<i64> = Vec::new();
    let mut by: HashMap<i64, usize> = HashMap::new();
    for (i, &u) in universes.iter().enumerate() {
        if u > 0 {
            if by.insert(u, i).is_none() {
                order.push(u);
            }
        }
    }
    if !order.is_empty() {
        let ids = join(&order);
        let d = c.cached_json(cache, &format!("https://games.roblox.com/v1/games?universeIds={ids}"), 5 * MINUTE);
        for o in loose(&d, "data") {
            let Some(&i) = by.get(&opt_long(o, "id", 0)) else { continue };
            let name = opt_str(o, "name", "");
            if !name.is_empty() {
                out[i].insert("name".into(), clip(&name, 200).into());
            }
            if let Some(cr) = obj(o, "creator") {
                out[i].insert("creator".into(), clip(&opt_str(cr, "name", ""), 80).into());
            }
            let playing = opt_long(o, "playing", 0);
            out[i].insert("players".into(), (if playing > 0 { count(playing) } else { "?".into() }).into());
        }
        let d = c.cached_json(cache, &format!("https://games.roblox.com/v1/games/votes?universeIds={ids}"), 5 * MINUTE);
        for o in loose(&d, "data") {
            let Some(&i) = by.get(&opt_long(o, "id", 0)) else { continue };
            let up = opt_long(o, "upVotes", 0);
            let total = up.wrapping_add(opt_long(o, "downVotes", 0));
            out[i].insert("likes".into(), (if total > 0 { format!("{}%", java::round(up as f64 * 100.0 / total as f64)) } else { "?".into() }).into());
        }
        let d = c.cached_json(cache, &format!("https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={ids}&countPerUniverse=1&size=768x432&format=Png&isCircular=false"), 24 * 60 * MINUTE);
        for o in loose(&d, "data") {
            let i = by.get(&opt_long(o, "universeId", 0));
            let t = o.get("thumbnails").and_then(Value::as_array);
            let (Some(&i), Some(t)) = (i, t) else { continue };
            if t.is_empty() {
                continue;
            }
            if !t[0].is_object() {
                break;
            }
            let url = opt_str(&t[0], "imageUrl", "");
            if url.starts_with("https://") {
                out[i].insert("thumbnail".into(), url.into());
            }
        }
    }
    out.into_iter().map(Value::Object).collect()
}

static META: Mutex<Option<HashMap<i64, Value>>> = Mutex::new(None);

pub fn meta(c: &Client, place: i64) -> Option<Value> {
    if let Some(hit) = META.lock().ok().and_then(|m| m.as_ref().and_then(|m| m.get(&place).cloned())) {
        return Some(hit);
    }
    let universe = opt_long(&c.json(&format!("https://apis.roblox.com/universes/v1/places/{place}/universe"))?, "universeId", 0);
    if universe <= 0 {
        return None;
    }
    let games = c.json(&format!("https://games.roblox.com/v1/games?universeIds={universe}"))?;
    let mut m = Map::new();
    m.insert("universeId".into(), universe.into());
    if let Some(first) = games.get("data").and_then(Value::as_array).and_then(|a| a.first()) {
        if !first.is_object() {
            return None;
        }
        m.insert("name".into(), clip(&opt_str(first, "name", ""), 200).into());
    }
    let icons = c.json(&format!("https://thumbnails.roblox.com/v1/games/icons?universeIds={universe}&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false"));
    if let Some(first) = icons.as_ref().and_then(|v| v.get("data")).and_then(Value::as_array).and_then(|a| a.first()).filter(|o| o.is_object()) {
        let url = opt_str(first, "imageUrl", "");
        if url.starts_with("https://") {
            m.insert("iconUrl".into(), url.into());
        }
    }
    let v = Value::Object(m);
    if let Ok(mut cache) = META.lock() {
        cache.get_or_insert_with(HashMap::new).insert(place, v.clone());
    }
    Some(v)
}

pub fn game(c: &Client, place: i64, universe: i64, address: &str, location: bool) -> Value {
    let mut m = Map::new();
    let mut universe = universe;
    let mut fetch = || -> Option<()> {
        if universe <= 0 {
            universe = opt_long(&c.json(&format!("https://apis.roblox.com/universes/v1/places/{place}/universe"))?, "universeId", 0);
        }
        if universe <= 0 {
            return None;
        }
        let games = c.json(&format!("https://games.roblox.com/v1/games?universeIds={universe}"))?;
        if let Some(g) = games.get("data").and_then(Value::as_array).and_then(|a| a.first()).filter(|o| o.is_object()) {
            m.insert("name".into(), clip(&opt_str(g, "name", ""), 200).into());
            m.insert("description".into(), clip(&opt_str(g, "description", ""), 1000).into());
            if let Some(cr) = obj(g, "creator") {
                m.insert("creator".into(), clip(&opt_str(cr, "name", ""), 100).into());
                m.insert("verified".into(), opt_bool(cr, "hasVerifiedBadge", false).into());
            }
        }
        let icons = c.json(&format!("https://thumbnails.roblox.com/v1/games/icons?universeIds={universe}&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false"))?;
        if let Some(i) = icons.get("data").and_then(Value::as_array).and_then(|a| a.first()).filter(|o| o.is_object()) {
            let url = opt_str(i, "imageUrl", "");
            if url.starts_with("https://") {
                m.insert("icon".into(), url.into());
            }
        }
        Some(())
    };
    fetch();
    if !m.contains_key("icon")
        && place > 0
        && let Some(icons) = c.json(&format!("https://thumbnails.roblox.com/v1/places/gameicons?placeIds={place}&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false"))
        && let Some(i) = icons.get("data").and_then(Value::as_array).and_then(|a| a.first())
    {
        let url = opt_str(i, "imageUrl", "");
        if url.starts_with("https://") {
            m.insert("icon".into(), url.into());
        }
    }
    let ip = (7..=15).contains(&address.len()) && address.bytes().all(|b| b.is_ascii_digit() || b == b'.');
    if location && ip && let Some(info) = c.json(&format!("https://ipinfo.io/{address}/json")) {
        m.insert("info".into(), info);
    }
    Value::Object(m)
}
