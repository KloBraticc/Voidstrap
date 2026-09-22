use crate::sha256::Sha256;
use std::io::{self, Read, Write};
use std::net::{Ipv4Addr, SocketAddr, TcpStream};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

pub const PORT_FILE: &str = "/data/local/tmp/voidstrap_helper_port";
pub const FLAGS_PATH: &str = "/data/local/tmp/ClientAppSettings.json";
pub const GAME_FPS_CAP: &str = "debug.graphics.game_default_frame_rate.disabled";
const NONCE_BYTES: usize = 16;
const PROOF_BYTES: usize = 32;
pub const PING: i32 = -1;
pub const STALE: i32 = -3;
pub const STREAM: i32 = -4;
const MAX_SCRIPT: usize = 8 * 1024 * 1024;
const SHELL_UID: u32 = 2000;
const UPGRADE_WAIT: Duration = Duration::from_millis(8000);
pub const FAILED: i32 = 2;
const FLAGS_DELIMITER: &str = "VOIDSTRAP_FLAGS_END";

pub fn proof(token: &str, nonce: &[u8], side: u8) -> [u8; 32] {
    let mut d = Sha256::new();
    d.update(token.as_bytes());
    d.update(nonce);
    d.update(&[side]);
    d.finish()
}

fn same(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

#[cfg(unix)]
fn owner(path: &str) -> io::Result<u32> {
    use std::os::unix::fs::MetadataExt;
    Ok(std::fs::metadata(path)?.uid())
}

#[cfg(not(unix))]
fn owner(_path: &str) -> io::Result<u32> {
    Err(io::Error::other("no helper"))
}

fn published_port() -> io::Result<u16> {
    let uid = owner(PORT_FILE).map_err(|_| io::Error::other("no helper"))?;
    if uid != 0 && uid != SHELL_UID {
        return Err(io::Error::other("untrusted"));
    }
    let text = std::fs::read_to_string(PORT_FILE).map_err(|_| io::Error::other("bad port"))?;
    match crate::java::trim(&text).parse::<u32>() {
        Ok(p) if (1..=65535).contains(&p) => Ok(p as u16),
        _ => Err(io::Error::other("bad port")),
    }
}

pub fn connect(token: &str) -> io::Result<TcpStream> {
    if token.is_empty() {
        return Err(io::Error::other("no token"));
    }
    let handshake = || -> io::Result<TcpStream> {
        let addr = SocketAddr::from((Ipv4Addr::LOCALHOST, published_port()?));
        let mut s = TcpStream::connect_timeout(&addr, Duration::from_millis(2000))?;
        s.set_read_timeout(Some(Duration::from_millis(2000)))?;
        let mut nonce = [0u8; NONCE_BYTES];
        s.read_exact(&mut nonce)?;
        s.write_all(&proof(token, &nonce, 0))?;
        s.flush()?;
        let mut answer = [0u8; PROOF_BYTES];
        s.read_exact(&mut answer)?;
        if !same(&answer, &proof(token, &nonce, 1)) {
            return Err(io::Error::other("untrusted helper"));
        }
        Ok(s)
    };
    handshake().map_err(|_| io::Error::other("handshake failed"))
}

fn read_int(s: &mut TcpStream) -> io::Result<i32> {
    let mut b = [0u8; 4];
    s.read_exact(&mut b)?;
    Ok(i32::from_be_bytes(b))
}

pub fn ping(token: &str, version: i32) -> i32 {
    let run = || -> io::Result<i32> {
        let mut s = connect(token)?;
        s.set_read_timeout(Some(Duration::from_millis(1500)))?;
        let mut req = Vec::with_capacity(8);
        req.extend_from_slice(&version.to_be_bytes());
        req.extend_from_slice(&PING.to_be_bytes());
        s.write_all(&req)?;
        read_int(&mut s)
    };
    run().unwrap_or(-1)
}

pub fn run_helper(token: &str, version: i32, script: &str, timeout: i32, known_uid: i32) -> Option<i32> {
    let body = script.as_bytes();
    if body.len() > MAX_SCRIPT {
        return Some(FAILED);
    }
    let give_up = Instant::now() + UPGRADE_WAIT;
    loop {
        let attempt = || -> io::Result<i32> {
            let mut s = connect(token)?;
            s.set_read_timeout(Some(Duration::from_secs((timeout.max(0) + 10) as u64)))?;
            let mut req = Vec::with_capacity(12 + body.len());
            req.extend_from_slice(&version.to_be_bytes());
            req.extend_from_slice(&timeout.to_be_bytes());
            req.extend_from_slice(&(body.len() as i32).to_be_bytes());
            req.extend_from_slice(body);
            s.write_all(&req)?;
            s.flush()?;
            read_int(&mut s)
        };
        let r = match attempt() {
            Ok(r) => r,
            Err(_) => {
                if Instant::now() > give_up || known_uid < 0 {
                    return None;
                }
                STALE
            }
        };
        if r != STALE {
            return Some(r);
        }
        if Instant::now() > give_up {
            return None;
        }
        std::thread::sleep(Duration::from_millis(400));
    }
}

fn modified_utf(s: &str) -> Vec<u8> {
    let mut body = Vec::new();
    for u in s.encode_utf16() {
        match u {
            0x0001..=0x007F => body.push(u as u8),
            0x0000 | 0x0080..=0x07FF => body.extend_from_slice(&[0xC0 | (u >> 6) as u8, 0x80 | (u & 0x3F) as u8]),
            _ => body.extend_from_slice(&[0xE0 | (u >> 12) as u8, 0x80 | ((u >> 6) & 0x3F) as u8, 0x80 | (u & 0x3F) as u8]),
        }
    }
    let mut out = (body.len() as u16).to_be_bytes().to_vec();
    out.extend_from_slice(&body);
    out
}

pub fn decode_modified_utf(b: &[u8]) -> Option<String> {
    let mut units: Vec<u16> = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        let c = b[i] as u16;
        if c < 0x80 {
            units.push(c);
            i += 1;
        } else if c & 0xE0 == 0xC0 {
            let c2 = *b.get(i + 1)? as u16;
            if c2 & 0xC0 != 0x80 {
                return None;
            }
            units.push(((c & 0x1F) << 6) | (c2 & 0x3F));
            i += 2;
        } else if c & 0xF0 == 0xE0 {
            let c2 = *b.get(i + 1)? as u16;
            let c3 = *b.get(i + 2)? as u16;
            if c2 & 0xC0 != 0x80 || c3 & 0xC0 != 0x80 {
                return None;
            }
            units.push(((c & 0x0F) << 12) | ((c2 & 0x3F) << 6) | (c3 & 0x3F));
            i += 3;
        } else {
            return None;
        }
    }
    Some(String::from_utf16_lossy(&units))
}

pub fn stream(token: &str, version: i32, target: &str, cancel: &mut dyn FnMut() -> bool, on_line: &mut dyn FnMut(String) -> bool) -> io::Result<()> {
    let mut s = connect(token)?;
    s.set_read_timeout(Some(Duration::from_millis(10000)))?;
    let mut req = Vec::new();
    req.extend_from_slice(&version.to_be_bytes());
    req.extend_from_slice(&STREAM.to_be_bytes());
    req.extend_from_slice(&modified_utf(target));
    s.write_all(&req)?;
    s.flush()?;
    if read_int(&mut s)? != 0 {
        return Err(io::Error::other("stale"));
    }
    s.set_read_timeout(Some(Duration::from_millis(1000)))?;
    let mut pending: Vec<u8> = Vec::new();
    let mut buf = [0u8; 8192];
    loop {
        if cancel() {
            return Err(io::Error::other("closed"));
        }
        match s.read(&mut buf) {
            Ok(0) => return Err(io::Error::from(io::ErrorKind::UnexpectedEof)),
            Ok(n) => pending.extend_from_slice(&buf[..n]),
            Err(e) if matches!(e.kind(), io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut) => continue,
            Err(e) => return Err(e),
        }
        let mut at = 0;
        while pending.len() - at >= 2 {
            let len = u16::from_be_bytes([pending[at], pending[at + 1]]) as usize;
            if pending.len() - at - 2 < len {
                break;
            }
            let line = decode_modified_utf(&pending[at + 2..at + 2 + len]).ok_or_else(|| io::Error::other("malformed"))?;
            at += 2 + len;
            if !on_line(line) {
                return Err(io::Error::other("closed"));
            }
        }
        pending.drain(..at);
    }
}

fn wait(child: &mut Child, timeout: Duration) -> Option<i32> {
    let deadline = Instant::now() + timeout;
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Some(code(status)),
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(25)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                return None;
            }
        }
    }
}

#[cfg(unix)]
fn code(status: std::process::ExitStatus) -> i32 {
    use std::os::unix::process::ExitStatusExt;
    status.code().unwrap_or_else(|| 0x80 + status.signal().unwrap_or(0))
}

#[cfg(not(unix))]
fn code(status: std::process::ExitStatus) -> i32 {
    status.code().unwrap_or(FAILED)
}

fn spawn_su() -> io::Result<(Child, io::PipeReader)> {
    let (reader, writer) = io::pipe()?;
    let child = Command::new("su").stdin(Stdio::piped()).stdout(Stdio::from(writer.try_clone()?)).stderr(Stdio::from(writer)).spawn()?;
    Ok((child, reader))
}

pub const SPAWN_FAILED: i32 = -2;

pub fn run_su(script: &str, timeout: i32) -> i32 {
    let Ok((mut child, mut out)) = spawn_su() else {
        return SPAWN_FAILED;
    };
    std::thread::spawn(move || {
        let mut b = [0u8; 4096];
        while matches!(out.read(&mut b), Ok(n) if n > 0) {}
    });
    let written = child.stdin.take().map(|mut i| i.write_all(script.as_bytes())).unwrap_or(Err(io::Error::other("stdin")));
    if written.is_err() {
        let _ = child.kill();
        let _ = child.wait();
        return -1;
    }
    wait(&mut child, Duration::from_secs(timeout.max(0) as u64)).unwrap_or(-1)
}

pub fn capture_su(script: &str, timeout: i32) -> Option<String> {
    let (mut child, mut out) = spawn_su().ok()?;
    let seen = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
    let sink = seen.clone();
    let reader = std::thread::spawn(move || {
        let mut b = [0u8; 4096];
        while let Ok(n) = out.read(&mut b) {
            if n == 0 {
                break;
            }
            sink.lock().unwrap_or_else(|e| e.into_inner()).extend_from_slice(&b[..n]);
        }
    });
    let written = child.stdin.take().map(|mut i| i.write_all(script.as_bytes())).unwrap_or(Err(io::Error::other("stdin")));
    if written.is_err() {
        let _ = child.kill();
        let _ = child.wait();
        return None;
    }
    wait(&mut child, Duration::from_secs(timeout.max(0) as u64))?;
    let done = Instant::now() + Duration::from_millis(2000);
    while !reader.is_finished() && Instant::now() < done {
        std::thread::sleep(Duration::from_millis(20));
    }
    let all = seen.lock().unwrap_or_else(|e| e.into_inner()).clone();
    Some(String::from_utf8_lossy(&all).into_owned())
}

pub fn has_su() -> bool {
    let path = std::env::var("PATH").unwrap_or_default();
    format!("{path}:/system/bin:/system/xbin").split(':').any(|d| !d.is_empty() && std::path::Path::new(d).join("su").exists())
}

pub fn root_probe() -> bool {
    let Some(out) = capture_su("echo SHELL_TEST\nid\n", 30) else {
        return false;
    };
    let mut shell = false;
    for line in out.split('\n') {
        if !shell {
            shell = line.contains("SHELL_TEST");
            continue;
        }
        if line.contains("uid=0") {
            return true;
        }
    }
    false
}

pub fn safe_for_heredoc(json: &str) -> bool {
    json.split('\n').all(|line| crate::java::trim(line) != FLAGS_DELIMITER)
}

pub fn write_script(json: &str) -> String {
    let tmp = format!("{FLAGS_PATH}.new");
    format!(
        "rm -f {tmp}\ncat > {tmp} <<'{FLAGS_DELIMITER}' || exit 2\n{json}\n{FLAGS_DELIMITER}\nif cmp -s {tmp} {FLAGS_PATH}; then rm -f {tmp}; else mv -f {tmp} {FLAGS_PATH} || exit 2; fi\nchmod 0644 {FLAGS_PATH} || exit 2\n"
    )
}

pub fn remove_script() -> String {
    format!("rm -f {FLAGS_PATH} || exit 2\nexit 0\n")
}

pub fn launch_sync_script(json: &str, unlocked: bool, pkg: &str, restore_refresh: &str) -> String {
    let body = if json == "{}" { format!("if [ -e {FLAGS_PATH} ]; then rm -f {FLAGS_PATH}; c=1; fi\n") } else { write_script(json) };
    format!(
        "c=0\no=$(getprop {GAME_FPS_CAP})\n{body}{restore_refresh}setprop {GAME_FPS_CAP} {unlocked}\n[ \"${{o:-false}}\" = {unlocked} ] || c=1\np=$(pidof {pkg})\np=${{p%% *}}\nif [ -n \"$p\" ] && {{ [ $c = 1 ] || [ {FLAGS_PATH} -nt /proc/$p ]; }}; then am force-stop {pkg}; fi\nexit 0\n"
    )
}

pub fn readable(json: &str) -> bool {
    let f = std::path::Path::new(FLAGS_PATH);
    if json == "{}" {
        return !f.exists();
    }
    match std::fs::read(f) {
        Ok(b) => {
            let on = String::from_utf8_lossy(&b);
            on == json || on == format!("{json}\n")
        }
        Err(_) => false,
    }
}
