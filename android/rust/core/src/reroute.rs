use crate::java;
use crate::matchmaker::{self, Settings};
use crate::net;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::sync::Mutex;
use std::sync::atomic::{AtomicI64, Ordering};

const MIN_GAIN_MS: f64 = 25.0;
const ACCEPTABLE_PING_MS: f64 = 60.0;
const SETTLE_MS: u64 = 2000;
const ATTEMPT_RESET_MS: i64 = 10 * 60_000;
const HOP_COOLDOWN_MS: i64 = 90_000;

struct State {
    attempts: HashMap<i64, i64>,
    attempt_at: HashMap<i64, i64>,
    tried: HashMap<i64, HashSet<String>>,
    last_hop: i64,
    rejoin_target: Option<String>,
    rejoin_place: i64,
}

static STATE: Mutex<Option<State>> = Mutex::new(None);
static REJOINING_UNTIL: AtomicI64 = AtomicI64::new(0);

fn with<R>(f: impl FnOnce(&mut State) -> R) -> R {
    let mut g = STATE.lock().unwrap_or_else(|e| e.into_inner());
    f(g.get_or_insert_with(|| State { attempts: HashMap::new(), attempt_at: HashMap::new(), tried: HashMap::new(), last_hop: 0, rejoin_target: None, rejoin_place: 0 }))
}

fn clear_attempt(place: i64) {
    with(|s| {
        s.attempts.remove(&place);
        s.attempt_at.remove(&place);
        s.tried.remove(&place);
    });
}

fn next_attempt(place: i64) -> i64 {
    with(|s| {
        let now = java::now_ms();
        let reset = s.attempt_at.get(&place).is_none_or(|at| now - at > ATTEMPT_RESET_MS);
        if reset {
            s.attempts.insert(place, 0);
        }
        s.attempt_at.insert(place, now);
        let n = s.attempts.entry(place).or_insert(0);
        *n += 1;
        *n
    })
}

fn add_tried(place: i64, job: &str) {
    if job.is_empty() {
        return;
    }
    with(|s| {
        s.tried.entry(place).or_default().insert(job.to_string());
    });
}

fn clear_rejoin() {
    with(|s| {
        s.rejoin_target = None;
        s.rejoin_place = 0;
    });
}

pub fn expect(place: i64, city: &str) {
    with(|s| {
        s.rejoin_target = Some(city.to_string());
        s.rejoin_place = place;
    });
}

pub fn rejoining() -> bool {
    java::now_ms() < REJOINING_UNTIL.load(Ordering::SeqCst)
}

pub struct Joined {
    pub place: i64,
    pub job: String,
    pub address: String,
    pub public: bool,
    pub excluded: bool,
    pub enabled: bool,
    pub max_retries: i64,
}

pub struct Outcome {
    pub alert: Vec<Value>,
    pub rejoin: Option<String>,
    pub save_geo: Option<Value>,
}

pub fn on_joined(d: &Joined, s: &Settings) -> Outcome {
    let mut out = Outcome { alert: Vec::new(), rejoin: None, save_geo: None };
    if d.address.is_empty() || matchmaker::is_private(&d.address) {
        return out;
    }
    if d.excluded {
        clear_attempt(d.place);
        return out;
    }
    if !d.enabled {
        return out;
    }
    if !d.public {
        clear_attempt(d.place);
        return out;
    }
    if s.cookie.is_empty() {
        return out;
    }
    std::thread::sleep(std::time::Duration::from_millis(SETTLE_MS));
    let (geo, save) = matchmaker::geo(&s.saved_geo);
    out.save_geo = save;
    let Some(geo) = geo else {
        return out;
    };
    let Some(current) = matchmaker::lookup(&d.address, java::now_ms() + 12_000) else {
        net::info("VoidstrapMatchmaker", "Current datacenter could not be resolved, staying put");
        clear_attempt(d.place);
        return out;
    };
    let current_ping = matchmaker::estimate_ping_ms(matchmaker::haversine_km(geo.lat, geo.lon, current.lat, current.lon));
    let current_blocked = s.blocked.contains(&current.key());
    let has_preferred = !s.preferred.is_empty();
    let wants_other = has_preferred && !matchmaker::matches_preferred(&current, &s.preferred);
    if has_preferred && !wants_other && !current_blocked {
        clear_rejoin();
        clear_attempt(d.place);
        out.alert.push(json!(["preferred", current.city, current_ping]));
        return out;
    }
    let (target, target_place, last_hop) = with(|st| (st.rejoin_target.clone(), st.rejoin_place, st.last_hop));
    let via_rejoin = target.is_some() && target_place == d.place;
    let landed = !via_rejoin || target.as_deref().is_some_and(|t| t.is_empty() || java::eq_ignore_case(t, &current.city));
    if via_rejoin && !current_blocked && !wants_other && landed {
        clear_rejoin();
        clear_attempt(d.place);
        out.alert.push(json!(["connected", current.city, current_ping]));
        return out;
    }
    if !via_rejoin && java::now_ms() - last_hop < HOP_COOLDOWN_MS {
        return out;
    }
    let mut tried = with(|st| st.tried.get(&d.place).cloned().unwrap_or_default());
    if !d.job.is_empty() {
        tried.insert(d.job.clone());
    }
    let picked = matchmaker::pick(d.place, &tried, s);
    if out.save_geo.is_none() {
        out.save_geo = picked.save_geo;
    }
    let Some(best) = picked.candidate else {
        clear_rejoin();
        clear_attempt(d.place);
        if current_blocked {
            out.alert.push(json!(["blocked_none", current.city]));
        }
        return out;
    };
    let same_dc = java::eq_ignore_case(&current.key(), &best.dc.key());
    let best_preferred = has_preferred && matchmaker::matches_preferred(&best.dc, &s.preferred);
    let gain = current_ping as f64 - best.estimated_ping as f64;
    let moving = (current_blocked && !same_dc) || (wants_other && best_preferred) || (!same_dc && !has_preferred && current_ping as f64 > ACCEPTABLE_PING_MS && gain >= MIN_GAIN_MS);
    if !moving {
        clear_rejoin();
        clear_attempt(d.place);
        if current_blocked {
            out.alert.push(json!(["blocked_same", current.city]));
        } else if wants_other {
            out.alert.push(json!(["preferred_empty", s.preferred.split('|').next().unwrap_or(""), current.city]));
        } else {
            out.alert.push(json!(["good", current_ping]));
        }
        return out;
    }
    let attempt = next_attempt(d.place);
    if attempt > d.max_retries {
        clear_rejoin();
        clear_attempt(d.place);
        out.alert.push(json!(["limit", d.max_retries, current.city]));
        return out;
    }
    add_tried(d.place, &d.job);
    add_tried(d.place, &best.job_id);
    out.alert.push(json!(["moving", best.dc.city, best.estimated_ping]));
    if attempt > 1 {
        out.alert.push(json!(["attempt", attempt, d.max_retries]));
    }
    if let Some(c) = &best.blocked_closest_city {
        out.alert.push(json!(["closer", c]));
    }
    with(|st| {
        st.last_hop = java::now_ms();
        st.rejoin_target = Some(best.dc.city.clone());
        st.rejoin_place = d.place;
    });
    net::info("VoidstrapMatchmaker", &format!("Rerouting from {} ({current_ping}ms) to {} ({}ms)", current.city, best.dc.city, best.estimated_ping));
    REJOINING_UNTIL.store(java::now_ms() + 45_000, Ordering::SeqCst);
    out.rejoin = Some(best.job_id);
    out
}
