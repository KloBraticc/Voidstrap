use crate::archive::Error;
use crate::flags::{self, Flag, Reject};
use crate::java;
use crate::shell;
use serde_json::{Value, json};

fn s(a: &Value, k: &str) -> String {
    java::opt_str(a, k, "")
}

fn n(a: &Value, k: &str) -> i64 {
    java::opt_long(a, k, 0)
}

fn v(x: impl Into<Value>) -> Value {
    json!({ "v": x.into() })
}

fn flags_call(op: &str, a: &Value) -> Result<Value, Error> {
    Ok(match op {
        "parse" => match flags::parse(&s(a, "text")) {
            Ok(r) => r.to_json(),
            Err(e) => json!({"error": match e {
                Reject::NotObject => "not_object",
                Reject::TooMany => "too_many",
                Reject::Invalid => "invalid",
            }}),
        },
        "load" => flags::load(&s(a, "text")),
        "validKey" => v(flags::valid_key(&s(a, "key"))),
        "cleanKey" => v(flags::clean_key(&s(a, "text"))),
        "intFlag" => v(flags::int_flag(&s(a, "key"))),
        "parseTyped" => v(flags::parse_typed(&s(a, "type"), &s(a, "text")).map(|f: Flag| f.to_json())),
        _ => return Err(Error::Io(format!("unknown op flags.{op}"))),
    })
}

fn shell_call(op: &str, a: &Value) -> Result<Value, Error> {
    let token = s(a, "token");
    let version = n(a, "version") as i32;
    Ok(match op {
        "ping" => v(shell::ping(&token, version)),
        "run" => v(shell::run_helper(&token, version, &s(a, "script"), n(a, "timeout") as i32, n(a, "uid") as i32)),
        "su" => v(shell::run_su(&s(a, "script"), n(a, "timeout") as i32)),
        "rootProbe" => v(shell::root_probe()),
        "hasSu" => v(shell::has_su()),
        "safeForHeredoc" => v(shell::safe_for_heredoc(&s(a, "json"))),
        "writeScript" => v(format!("{}exit 0\n", shell::write_script(&s(a, "json")))),
        "removeScript" => v(shell::remove_script()),
        "launchScript" => v(shell::launch_sync_script(&s(a, "json"), java::opt_bool(a, "unlocked", false), &s(a, "pkg"), &s(a, "restore"))),
        "readable" => v(shell::readable(&s(a, "json"))),
        _ => return Err(Error::Io(format!("unknown op shell.{op}"))),
    })
}

fn f(a: &Value, k: &str) -> f64 {
    a.get(k).and_then(Value::as_f64).unwrap_or(f64::NAN)
}

#[cfg(feature = "matchmaker")]
fn strings(a: &Value, k: &str) -> Vec<String> {
    a.get(k).and_then(Value::as_array).map(|x| x.iter().filter_map(|v| v.as_str().map(str::to_string)).collect()).unwrap_or_default()
}

fn smart_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::smart;
    let files = std::path::PathBuf::from(s(a, "files"));
    let cache = std::path::PathBuf::from(s(a, "cache"));
    let client = crate::net::Client { agent: s(a, "agent") };
    Ok(match op {
        "regions" => v(smart::REGIONS.iter().map(|r| json!({"key": r.0, "lat": r.1, "lon": r.2})).collect::<Vec<_>>()),
        "fromTimeZone" => v(smart::from_time_zone(&s(a, "id"), n(a, "offset"))),
        "km" => v(smart::km(f(a, "lat1"), f(a, "lon1"), f(a, "lat2"), f(a, "lon2"))),
        "seen" => v(smart::seen(&files, n(a, "place")).len()),
        "pick" => v(smart::pick(&client, &files, n(a, "place"), (f(a, "lat"), f(a, "lon")), java::opt_bool(a, "near", true), java::opt_bool(a, "preferEmpty", true), n(a, "minFps"))
            .map(|c| json!({"job": c.job, "city": c.city, "ping": c.ping, "fps": c.fps}))),
        "onJoined" => v(smart::on_joined(&client, &cache, &files, &s(a, "job"), n(a, "place"), n(a, "universe"), &s(a, "address"))),
        "known" => v(smart::known_json(&files)),
        "forget" => {
            smart::forget(&files);
            v(true)
        }
        _ => return Err(Error::Io(format!("unknown op smart.{op}"))),
    })
}

#[cfg(feature = "matchmaker")]
fn mm_settings(a: &Value) -> crate::matchmaker::Settings {
    crate::matchmaker::Settings {
        preferred: s(a, "preferred"),
        blocked: strings(a, "blocked").into_iter().collect(),
        budget: n(a, "budget"),
        prefer_empty: java::opt_bool(a, "preferEmpty", false),
        v2: java::opt_bool(a, "v2", false),
        cookie: s(a, "cookie"),
        saved_geo: s(a, "savedGeo"),
    }
}

#[cfg(feature = "matchmaker")]
fn mm_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::matchmaker as mm;
    use std::sync::atomic::Ordering;
    let geo_json = |g: &Option<mm::Geo>| g.as_ref().map(mm::geo_json);
    match op {
        "km" => return Ok(v(mm::haversine_km(f(a, "lat1"), f(a, "lon1"), f(a, "lat2"), f(a, "lon2")))),
        "ping" => return Ok(v(mm::estimate_ping_ms(f(a, "km")))),
        "matchesPreferred" => {
            let dc = a.get("dc").cloned().unwrap_or(Value::Null);
            let d = mm::Datacenter { city: s(&dc, "city"), region: s(&dc, "region"), country: s(&dc, "country"), lat: 0.0, lon: 0.0 };
            return Ok(v(mm::matches_preferred(&d, &s(a, "preferred"))));
        }
        "lastReport" => return Ok(v(mm::last_report())),
        "expect" => {
            crate::reroute::expect(n(a, "place"), &s(a, "city"));
            return Ok(v(true));
        }
        "rejoining" => return Ok(v(crate::reroute::rejoining())),
        _ => {}
    }
    mm::setup(&s(a, "builtin"), std::path::PathBuf::from(s(a, "cache")), std::path::PathBuf::from(s(a, "files")), &s(a, "agent"));
    Ok(match op {
        "datacenters" => v(mm::datacenters().iter().map(mm::Datacenter::to_json).collect::<Vec<_>>()),
        "geo" => {
            let (g, save) = mm::geo(&s(a, "savedGeo"));
            json!({"geo": geo_json(&g), "save": save})
        }
        "lookup" => v(mm::lookup(&s(a, "ip"), java::now_ms() + 12_000).map(|d| d.to_json())),
        "pick" => {
            let exclude = strings(a, "exclude").into_iter().collect();
            let p = mm::pick(n(a, "place"), &exclude, &mm_settings(a));
            json!({"candidate": p.candidate.map(|c| c.to_json()), "save": p.save_geo, "rejected": mm::REJECTED.swap(false, Ordering::SeqCst)})
        }
        "reroute" => {
            let d = crate::reroute::Joined {
                place: n(a, "place"),
                job: s(a, "job"),
                address: s(a, "address"),
                public: java::opt_bool(a, "public", false),
                excluded: java::opt_bool(a, "excluded", false),
                enabled: java::opt_bool(a, "enabled", false),
                max_retries: n(a, "maxRetries"),
            };
            let o = crate::reroute::on_joined(&d, &mm_settings(a));
            json!({"alert": o.alert, "rejoin": o.rejoin, "save": o.save_geo, "rejected": mm::REJECTED.swap(false, Ordering::SeqCst)})
        }
        _ => return Err(Error::Io(format!("unknown op mm.{op}"))),
    })
}

fn feed_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::feed;
    let cache = std::path::PathBuf::from(s(a, "cache"));
    let client = crate::net::Client { agent: s(a, "agent") };
    let list = |k: &str| a.get(k).and_then(Value::as_array).cloned().unwrap_or_default();
    let missing = || Error::Io("unavailable".into());
    Ok(match op {
        "enrich" => {
            let mut games: Vec<feed::Game> = list("games").iter().map(feed::Game::from).collect();
            feed::enrich(&client, &cache, &mut games);
            v(games.iter().map(feed::Game::to_json).collect::<Vec<_>>())
        }
        "events" => v(feed::events(&client, &cache, &list("games").iter().map(feed::Game::from).collect::<Vec<_>>(), java::now_ms())),
        "passes" => v(feed::passes(&client, &cache, n(a, "universe"))),
        "joins" => {
            let text = std::fs::read(s(a, "path")).map(|b| String::from_utf8_lossy(&b).into_owned()).unwrap_or_default();
            v(feed::joins(&text).into_iter().map(|(t, p)| json!([t, p])).collect::<Vec<_>>())
        }
        "news" => v(feed::news(&client, &cache, n(a, "latest").max(0) as usize).ok_or_else(missing)?),
        "catalog" => v(feed::catalog(&client, &cache).ok_or_else(missing)?),
        "cards" => v(feed::cards(&client, &cache, &list("cards"))),
        "meta" => v(feed::meta(&client, n(a, "place"))),
        "game" => feed::game(&client, n(a, "place"), n(a, "universe"), &s(a, "address"), java::opt_bool(a, "location", false)),
        "cardTitle" => v(feed::card_title(&s(a, "title"))),
        "thumbnail" => v(feed::thumbnail(&s(a, "image"), n(a, "width"))),
        "count" => v(feed::count(n(a, "n"))),
        "time" => v(feed::time(&s(a, "iso"))),
        _ => return Err(Error::Io(format!("unknown op feed.{op}"))),
    })
}

fn link_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::link;
    let opt = |k: &str| a.get(k).and_then(Value::as_str).map(str::to_string);
    let l = match op {
        "parse" => link::parse(&s(a, "text")),
        "findIn" => link::find_in(&s(a, "text")),
        "place" => link::place(n(a, "placeId"), opt("instanceId"), opt("linkCode")),
        _ => return Err(Error::Io(format!("unknown op link.{op}"))),
    };
    Ok(v(l.map(|l| l.to_json())))
}

fn catalog_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::catalog as cat;
    let client = crate::net::Client { agent: s(a, "agent") };
    let file = || cat::ModFile::from(a.get("file").unwrap_or(&Value::Null));
    let src = || {
        let i = a.get("index").cloned().unwrap_or(Value::Null);
        crate::mods::placer::Source { key: s(&i, "key"), apk: i.get("apk").and_then(Value::as_str).map(str::to_string) }
    };
    Ok(match op {
        "categories" => v(cat::categories(&client)?),
        "browse" => cat::browse(&client, &src(), n(a, "page"), &s(a, "sort"), n(a, "category"), &s(a, "search"))?,
        "detail" => {
            let mut e = cat::Entry::from(a.get("entry").unwrap_or(&Value::Null));
            let ok = cat::detail(&client, &src(), &mut e)?;
            json!({"entry": e.to_json(), "ok": ok})
        }
        "replies" => {
            let mut posts: Vec<cat::Post> = a.get("posts").and_then(Value::as_array).map(|p| p.iter().map(cat::Post::from).collect()).unwrap_or_default();
            cat::load_replies(&client, &mut posts);
            v(posts.iter().map(cat::Post::to_json).collect::<Vec<_>>())
        }
        "marketplace" => cat::marketplace(&client, &s(a, "sort"), &s(a, "search"))?,
        "inspectFile" => v(cat::inspect_file(&file())),
        "androidBlock" => v(cat::android_block(&file())),
        "marketplaceUrl" => v(cat::marketplace_url(&s(a, "url"))),
        "safeName" => v(cat::safe_name(&s(a, "name"), n(a, "id"))),
        "md5" => v(cat::md5(std::path::Path::new(&s(a, "path")))),
        "verifyBlob" => {
            cat::verify_blob(&client, &s(a, "slug"), std::path::Path::new(&s(a, "path")))?;
            v(true)
        }
        "prune" => {
            cat::prune(std::path::Path::new(&s(a, "dir")));
            v(true)
        }
        "byId" => cat::by_id(&client, n(a, "modId"), n(a, "fileId"), &s(a, "name"))?,
        _ => return Err(Error::Io(format!("unknown op catalog.{op}"))),
    })
}

fn translate_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::translate;
    let cache = std::path::PathBuf::from(s(a, "cache"));
    let texts = || a.get("texts").and_then(Value::as_array).cloned().unwrap_or_default();
    Ok(match op {
        "load" => {
            translate::load(&cache);
            v(true)
        }
        "lookup" => v(translate::lookup(&s(a, "lang"), &texts().iter().map(|t| t.as_str().map(str::to_string)).collect::<Vec<_>>())),
        "fetch" => {
            translate::fetch(&cache, &s(a, "lang"), &texts().iter().filter_map(|t| t.as_str().map(str::to_string)).collect::<Vec<_>>());
            v(true)
        }
        _ => return Err(Error::Io(format!("unknown op translate.{op}"))),
    })
}

fn presence_call(op: &str, a: &Value) -> Result<Value, Error> {
    use crate::presence;
    let obj = |k: &str| a.get(k).cloned().unwrap_or(Value::Null);
    Ok(match op {
        "game" => presence::game(&obj("settings"), &obj("data"), &obj("game"), &obj("account"), n(a, "flags")),
        "shown" => v(presence::shown(&s(a, "custom"), &s(a, "name"))),
        "idle" => presence::idle(&s(a, "icon"), n(a, "start"), &obj("settings"), &obj("account")),
        "account" => v(presence::account(&crate::net::Client { agent: s(a, "agent") }, n(a, "user"), java::opt_bool(a, "circular", false))),
        "rpc" => v(presence::apply_rpc(&obj("current"), &obj("original"), &s(a, "json"))),
        "app" => presence::app(a),
        _ => return Err(Error::Io(format!("unknown op presence.{op}"))),
    })
}

pub fn call(op: &str, a: &Value, data: Option<&[u8]>, file: Option<std::fs::File>, cancel: &mut dyn FnMut() -> bool) -> Result<Value, Error> {
    match op.split_once('.') {
        Some(("mods", rest)) => crate::mods::call(rest, a, data, file, cancel),
        Some(("flags", rest)) => flags_call(rest, a),
        Some(("shell", rest)) => shell_call(rest, a),
        Some(("smart", rest)) => smart_call(rest, a),
        Some(("feed", rest)) => feed_call(rest, a),
        Some(("link", rest)) => link_call(rest, a),
        Some(("catalog", rest)) => catalog_call(rest, a),
        Some(("translate", rest)) => translate_call(rest, a),
        Some(("presence", rest)) => presence_call(rest, a),
        #[cfg(feature = "matchmaker")]
        Some(("mm", rest)) => mm_call(rest, a),
        #[cfg(feature = "updates")]
        Some(("update", rest)) => crate::update::call(rest, a),
        _ => Err(Error::Io(format!("unknown op {op}"))),
    }
}
