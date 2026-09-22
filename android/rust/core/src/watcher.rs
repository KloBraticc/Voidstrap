use serde_json::Value;

const NEW_PRIVATE: i64 = 2;
const SPECIFIC_PRIVATE: i64 = 3;

#[derive(Clone, Default, Debug, PartialEq)]
pub struct Data {
    pub place_id: i64,
    pub job_id: String,
    pub universe_id: i64,
    pub user_id: i64,
    pub server_type: u8,
    pub server_address: String,
    pub joined: i64,
    pub teleport: bool,
    pub launch_data: String,
}

pub const PUBLIC: u8 = 0;
pub const PRIVATE: u8 = 1;
pub const RESERVED: u8 = 2;

#[derive(Debug, PartialEq)]
pub enum Event {
    Joined(Data),
    Left(Data),
    Menu,
    Log(&'static str, String),
    Rpc(String, String),
}

#[derive(Default)]
pub struct Watcher {
    data: Data,
    in_game: bool,
    teleport_marker: bool,
    reserved_marker: bool,
    last_user_id: i64,
    last_rpc: i64,
}

fn parse_long(s: &str) -> i64 {
    s.parse::<i64>().unwrap_or(0)
}

fn is_terminator(c: char) -> bool {
    matches!(c, '\n' | '\r' | '\u{85}' | '\u{2028}' | '\u{2029}')
}

fn until_terminator(s: &str) -> &str {
    s.find(is_terminator).map_or(s, |i| &s[..i])
}

fn is_space(c: char) -> bool {
    matches!(c, ' ' | '\t' | '\n' | '\u{0B}' | '\u{0C}' | '\r')
}

fn java_trim(s: &str) -> &str {
    s.trim_matches(|c: char| c <= ' ')
}

fn take_while(s: &str, f: impl Fn(char) -> bool) -> (&str, &str) {
    let end = s.find(|c: char| !f(c)).unwrap_or(s.len());
    (&s[..end], &s[end..])
}

fn digits(s: &str) -> Option<(&str, &str)> {
    let (d, rest) = take_while(s, |c| c.is_ascii_digit());
    (!d.is_empty()).then_some((d, rest))
}

fn occurrences<'a>(hay: &'a str, needle: &'a str) -> impl Iterator<Item = usize> + 'a {
    hay.match_indices(needle).map(|(i, _)| i)
}

fn joining(line: &str) -> Option<(String, String, String)> {
    const P: &str = "! Joining game '";
    for at in occurrences(line, P) {
        let rest = &line[at + P.len()..];
        let (job, _) = take_while(rest, |c| c.is_ascii_digit() || ('a'..='f').contains(&c) || c == '-');
        if job.len() < 36 {
            continue;
        }
        let job = &job[..36];
        let rest = &line[at + P.len() + 36..];
        let Some(rest) = rest.strip_prefix("' place ") else { continue };
        let Some((place, rest)) = digits(rest) else { continue };
        let Some(rest) = rest.strip_prefix(" at ") else { continue };
        let (address, _) = take_while(rest, |c| c.is_ascii_digit() || c == '.');
        if address.is_empty() {
            continue;
        }
        return Some((job.to_string(), place.to_string(), address.to_string()));
    }
    None
}

fn referral(line: &str) -> Option<&str> {
    const P: &str = "referral_page:";
    for at in occurrences(line, P) {
        let (v, _) = take_while(&line[at + P.len()..], |c| c != ',');
        if !v.is_empty() {
            return Some(v);
        }
    }
    None
}

fn number_after<'a>(line: &'a str, prefix: &str) -> Option<&'a str> {
    occurrences(line, prefix).find_map(|at| digits(&line[at + prefix.len()..]).map(|(d, _)| d))
}

fn udmux(line: &str) -> Option<&str> {
    const P: &str = "UDMUX Address = ";
    for at in occurrences(line, P) {
        let (first, rest) = take_while(&line[at + P.len()..], |c| c.is_ascii_digit() || c == '.');
        if first.is_empty() {
            continue;
        }
        let Some(rest) = rest.strip_prefix(", Port = ") else { continue };
        let Some((_, rest)) = digits(rest) else { continue };
        let Some(rest) = rest.strip_prefix(" | RCC Server Address = ") else { continue };
        let (second, rest) = take_while(rest, |c| c.is_ascii_digit() || c == '.');
        if second.is_empty() {
            continue;
        }
        let Some(rest) = rest.strip_prefix(", Port = ") else { continue };
        if digits(rest).is_none() {
            continue;
        }
        return Some(first);
    }
    None
}

fn join_type(line: &str) -> Option<&str> {
    const P: &str = "JoinTypeId";
    for at in occurrences(line, P) {
        let after = &line[at + P.len()..];
        for quote in ["\"", "%22", ""] {
            let Some(rest) = after.strip_prefix(quote) else { continue };
            for colon in [":", "%3a"] {
                if let Some(rest) = rest.strip_prefix(colon) {
                    if let Some((d, _)) = digits(rest) {
                        return Some(d);
                    }
                }
            }
        }
    }
    None
}

fn player(line: &str) -> Option<(&str, &str, &str)> {
    let mut starts: Vec<(usize, &str)> = occurrences(line, "added: ").map(|i| (i, "added")).collect();
    starts.extend(occurrences(line, "removed: ").map(|i| (i, "removed")));
    starts.sort();
    for (at, kind) in starts {
        let trimmed = line[at + kind.len() + 2..].trim_end_matches(is_space);
        if trimmed.contains(is_terminator) {
            continue;
        }
        let digit_start = trimmed.len() - trimmed.bytes().rev().take_while(u8::is_ascii_digit).count();
        if digit_start == trimmed.len() {
            continue;
        }
        let Some(name) = trimmed[..digit_start].strip_suffix(' ') else { continue };
        return Some((kind, name, &trimmed[digit_start..]));
    }
    None
}

fn after_literal<'a>(line: &'a str, prefix: &str) -> Option<&'a str> {
    line.find(prefix).map(|at| until_terminator(&line[at + prefix.len()..]))
}

fn added(line: &str) -> Option<&str> {
    const P: &str = "playerAdded:";
    for at in occurrences(line, P) {
        let (_, rest) = take_while(&line[at + P.len()..], is_space);
        if let Some(rest) = rest.strip_prefix("userId=") {
            if let Some((d, _)) = digits(rest) {
                return Some(d);
            }
        }
    }
    None
}

fn removed(line: &str) -> Option<&str> {
    let lower = line.to_ascii_lowercase();
    let mut starts: Vec<(usize, bool)> = occurrences(&lower, "playerremoving:").map(|i| (i, true)).collect();
    starts.extend(occurrences(&lower, "purging ").map(|i| (i, false)));
    starts.sort();
    for (at, first) in starts {
        let rest = if first {
            let (_, rest) = take_while(&lower[at + "playerremoving:".len()..], is_space);
            let Some(rest) = rest.strip_prefix("userid=") else { continue };
            rest
        } else {
            let after = &lower[at + "purging ".len()..];
            let Some(rest) = ["social counterparties", "age group", "compatibility tokens"].iter().find_map(|w| after.strip_prefix(w)) else { continue };
            let Some(rest) = rest.strip_prefix(" for player") else { continue };
            let (ws, rest) = take_while(rest, is_space);
            if ws.is_empty() {
                continue;
            }
            rest
        };
        if let Some((d, _)) = digits(rest) {
            let start = lower.len() - rest.len();
            return Some(&line[start..start + d.len()]);
        }
    }
    None
}

fn rpc(line: &str) -> Option<&str> {
    let v = line.find("[VoidstrapRPC] ");
    let b = line.find("[BloxstrapRPC] ");
    let at = match (v, b) {
        (Some(x), Some(y)) => x.min(y),
        (x, y) => x.or(y)?,
    };
    Some(until_terminator(&line[at + "[VoidstrapRPC] ".len()..]))
}

fn opt_string(v: Option<&Value>) -> String {
    match v {
        None => String::new(),
        Some(Value::String(s)) => s.clone(),
        Some(other) => other.to_string(),
    }
}

pub fn is_private_ip(ip: &str) -> bool {
    if ip.is_empty() {
        return true;
    }
    let mut parts: Vec<&str> = ip.split('.').collect();
    while parts.last() == Some(&"") {
        parts.pop();
    }
    if parts.len() != 4 {
        return true;
    }
    let a = parse_long(parts[0]) as i32;
    let b = parse_long(parts[1]) as i32;
    a == 10 || a == 127 || a == 0 || (a == 172 && (16..=31).contains(&b)) || (a == 192 && b == 168) || (a == 100 && (64..=127).contains(&b)) || (a == 169 && b == 254)
}

impl Watcher {
    pub fn in_game(&self) -> bool {
        self.in_game
    }

    pub fn current(&self) -> Option<Data> {
        self.in_game.then(|| self.data.clone())
    }

    pub fn user_id(&self) -> i64 {
        self.last_user_id
    }

    pub fn reset(&mut self) -> Option<Data> {
        let was = self.in_game;
        let old = std::mem::take(&mut self.data);
        self.in_game = false;
        self.teleport_marker = false;
        self.reserved_marker = false;
        was.then_some(old)
    }

    pub fn feed(&mut self, line: &str, now: i64) -> Option<Event> {
        let mut event = None;
        if line.contains("! Joining game") {
            if let Some((job, place, address)) = joining(line) {
                let was = self.in_game;
                let old = std::mem::take(&mut self.data);
                self.in_game = false;
                self.data.job_id = job;
                self.data.place_id = parse_long(&place);
                self.data.server_address = if is_private_ip(&address) { String::new() } else { address };
                self.data.teleport = self.teleport_marker;
                self.teleport_marker = false;
                if self.reserved_marker {
                    self.data.server_type = RESERVED;
                }
                self.reserved_marker = false;
                if was {
                    event = Some(Event::Left(old));
                }
            }
        } else if !self.in_game && self.data.place_id != 0 {
            if line.contains("game_join_loadtime") {
                if let Some(r) = referral(line) {
                    if self.data.server_type != RESERVED && (r.contains("RequestPrivateGame") || r.contains("GameDetailPageJSHybridEvent")) {
                        self.data.server_type = PRIVATE;
                    }
                }
                if let Some(u) = number_after(line, "universeid:") {
                    self.data.universe_id = parse_long(u);
                }
                if let Some(id) = number_after(line, "userid:") {
                    self.data.user_id = parse_long(id);
                    self.last_user_id = self.data.user_id;
                }
            } else if line.contains("UDMUX Address") {
                if let Some(a) = udmux(line) {
                    if !is_private_ip(a) {
                        self.data.server_address = a.to_string();
                    }
                }
            } else if line.contains("[FLog::Network] serverId:") {
                self.in_game = true;
                self.data.joined = now;
                event = Some(Event::Joined(self.data.clone()));
            }
        } else if self.in_game {
            if line.contains("Time to disconnect replication data") || line.contains("leaveUGCGameInternal") {
                let old = std::mem::take(&mut self.data);
                self.in_game = false;
                event = Some(Event::Left(old));
            } else if line.contains("doTeleport: joinScriptUrl") {
                self.teleport_marker = true;
                if let Some(t) = join_type(line) {
                    let t = parse_long(t) as i32 as i64;
                    if t == NEW_PRIVATE || t == SPECIFIC_PRIVATE {
                        self.reserved_marker = true;
                    }
                }
            } else if line.contains("[VoidstrapRPC]") || line.contains("[BloxstrapRPC]") {
                if let Some(body) = rpc(line) {
                    if now - self.last_rpc > 1000 {
                        let parsed = serde_json::Deserializer::from_str(body).into_iter::<Value>().next();
                        if let Some(Ok(Value::Object(o))) = parsed {
                            let command = opt_string(o.get("command"));
                            if command == "SetLaunchData" {
                                let launch = opt_string(o.get("data"));
                                if launch.encode_utf16().count() <= 200 {
                                    self.data.launch_data = launch;
                                }
                            }
                            if command == "SetRichPresence" || command == "SetLaunchData" {
                                self.last_rpc = now;
                                let body = match o.get("data") {
                                    Some(v @ Value::Object(_)) => v.to_string(),
                                    _ => "{}".to_string(),
                                };
                                event = Some(Event::Rpc(command, body));
                            }
                        }
                    }
                }
            } else if line.contains("[DFLog::SocialCounterpartyManager]") {
                if let Some(id) = added(line) {
                    event = Some(Event::Log("join", id.to_string()));
                } else if let Some(id) = removed(line) {
                    event = Some(Event::Log("leave", id.to_string()));
                }
            } else if line.contains("ExpChat/mountClientApp") {
                let joined = if line.contains("- Player ") { player(line) } else { None };
                if let Some((kind, name, id)) = joined {
                    let kind = if kind == "added" { "join" } else { "leave" };
                    event = Some(Event::Log(kind, format!("{} ({id})", java_trim(name))));
                } else if line.contains("MessageReceived") {
                    if let Some(text) = after_literal(line, "Success Text: ") {
                        event = Some(Event::Log("chat", java_trim(text).to_string()));
                    }
                }
            }
        }
        if event.is_none() && !self.in_game && line.contains("setStage: (stage:LuaApp)") {
            event = Some(Event::Menu);
        }
        event
    }
}
