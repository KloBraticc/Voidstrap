mod sha256;

use std::fs;
use std::io::{self, BufRead, BufReader, Read, Write};
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, TcpListener, TcpStream};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::process::{CommandExt, ExitStatusExt};
use std::path::Path;
use std::process::{self, Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

const PORT_FILE: &str = "/data/local/tmp/voidstrap_helper_port";
const HELPER_CLASS: &str = "com.voidstrap.android.HelperServer";
const NONCE_BYTES: usize = 16;
const PROOF_BYTES: usize = 32;
const VERSION: i32 = parse(env!("VOIDSTRAP_VERSION_CODE"));
const PING: i32 = -1;
const STOP: i32 = -2;
const STALE: i32 = -3;
const STREAM: i32 = -4;
const MAX_SCRIPT: i32 = 8 * 1024 * 1024;
const TARGETS: [&str; 2] = ["com.roblox.client", "com.roblox.client.vnggames"];
const STREAM_START: &str = "\u{1}START";
const STREAM_EXIT: &str = "\u{1}EXIT";
const STREAM_IDLE: &str = "\u{1}IDLE";
const STREAM_MARKERS: [&str; 12] = [
    "! Joining game",
    "game_join_loadtime",
    "UDMUX Address",
    "[FLog::Network] serverId:",
    "Time to disconnect replication data",
    "leaveUGCGameInternal",
    "doTeleport: joinScriptUrl",
    "[VoidstrapRPC]",
    "[BloxstrapRPC]",
    "ExpChat/mountClientApp",
    "SocialCounterpartyManager",
    "setStage: (stage:",
];
const STREAM_REGEX: &str = "Joining game|game_join_loadtime|UDMUX Address|serverId:|Time to disconnect|leaveUGCGameInternal|doTeleport|VoidstrapRPC|BloxstrapRPC|ExpChat|SocialCounterpartyManager|setStage";
const ALIVE_POLL: Duration = Duration::from_secs(3);
const KEEPALIVE_TICKS: u32 = 10;
const LINE_LIMIT: usize = 8000;
const PR_SET_PDEATHSIG: i32 = 1;
const SIGKILL: usize = 9;

struct Config {
    pkg: String,
    uid: String,
    token: String,
    apk: String,
    cleanup: Vec<u8>,
}

static CONFIG: OnceLock<Config> = OnceLock::new();
static RELAUNCH: Mutex<()> = Mutex::new(());
static PREFILTER: OnceLock<bool> = OnceLock::new();

unsafe extern "C" {
    fn getuid() -> u32;
    fn prctl(option: i32, ...) -> i32;
}

const fn parse(digits: &str) -> i32 {
    let b = digits.as_bytes();
    let mut v = 0;
    let mut i = 0;
    while i < b.len() {
        v = v * 10 + (b[i] - b'0') as i32;
        i += 1;
    }
    v
}

fn config() -> &'static Config {
    CONFIG.get().expect("config")
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() < 4 || !valid(&args) {
        return;
    }
    let mut cleanup = Vec::new();
    let _ = io::stdin().read_to_end(&mut cleanup);
    let _ = CONFIG.set(Config {
        pkg: args[0].clone(),
        uid: args[1].clone(),
        token: args[2].clone(),
        apk: args[3].clone(),
        cleanup,
    });
    let Some(server) = bind() else {
        println!("The Voidstrap helper could not listen");
        return;
    };
    println!("The Voidstrap helper started");
    let port = server.local_addr().map(|a| a.port()).unwrap_or(0);
    thread::spawn(move || watch(port));
    for client in server.incoming() {
        match client {
            Ok(s) => {
                thread::spawn(move || serve(s));
            }
            Err(_) => process::exit(0),
        }
    }
}

fn valid(args: &[String]) -> bool {
    let pkg = args[0].bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'.');
    let uid = args[1].bytes().all(|b| b.is_ascii_digit());
    let token = args[2].bytes().all(|b| b.is_ascii_hexdigit());
    pkg && uid && token && !args[0].is_empty() && !args[1].is_empty() && !args[2].is_empty()
}

fn bind() -> Option<TcpListener> {
    stop_old();
    for _ in 0..20 {
        if let Ok(s) = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)) {
            if let Ok(addr) = s.local_addr() {
                if publish(addr.port()).is_ok() {
                    return Some(s);
                }
            }
        }
        thread::sleep(Duration::from_millis(250));
    }
    None
}

fn publish(port: u16) -> io::Result<()> {
    let _ = fs::remove_file(PORT_FILE);
    fs::write(PORT_FILE, port.to_string())?;
    fs::set_permissions(PORT_FILE, fs::Permissions::from_mode(0o644))
}

fn published_port() -> Option<u16> {
    fs::read_to_string(PORT_FILE).ok()?.trim().parse().ok().filter(|p| *p > 0)
}

fn stop_old() {
    let Some(port) = published_port() else { return };
    let addrs = [SocketAddr::from((Ipv4Addr::LOCALHOST, port)), SocketAddr::from((Ipv6Addr::LOCALHOST, port))];
    let Some(mut s) = addrs.iter().find_map(|a| TcpStream::connect_timeout(a, Duration::from_secs(2)).ok()) else {
        return;
    };
    let _ = s.set_read_timeout(Some(Duration::from_secs(2)));
    let _ = (|| -> io::Result<()> {
        let mut nonce = [0u8; NONCE_BYTES];
        s.read_exact(&mut nonce)?;
        s.write_all(&proof(&nonce, 0))?;
        let mut answer = [0u8; PROOF_BYTES];
        s.read_exact(&mut answer)?;
        if !same(&answer, &proof(&nonce, 1)) {
            return Ok(());
        }
        write_int(&mut s, VERSION)?;
        write_int(&mut s, STOP)?;
        read_int(&mut s).map(|_| ())
    })();
}

fn watch(port: u16) {
    let c = config();
    loop {
        thread::sleep(Duration::from_secs(5));
        if Path::new(&c.apk).exists() {
            if published_port() != Some(port) {
                process::exit(0);
            }
            continue;
        }
        if installed() {
            relaunch();
            return;
        }
        if !c.cleanup.is_empty() {
            run(&c.cleanup, 120);
        }
        process::exit(0);
    }
}

fn installed() -> bool {
    let Ok(mut child) = Command::new("pm")
        .args(["path", &config().pkg])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
    else {
        return true;
    };
    let mut out = Vec::new();
    if let Some(mut o) = child.stdout.take() {
        let _ = o.read_to_end(&mut out);
    }
    if wait_for(&mut child, Duration::from_secs(20)).is_none() {
        let _ = child.kill();
        let _ = child.wait();
    }
    String::from_utf8_lossy(&out).contains("package:")
}

fn serve(mut s: TcpStream) {
    let _ = handle(&mut s);
}

fn handle(s: &mut TcpStream) -> io::Result<()> {
    s.set_read_timeout(Some(Duration::from_secs(10)))?;
    let nonce = random_nonce()?;
    s.write_all(&nonce)?;
    let mut offered = [0u8; PROOF_BYTES];
    s.read_exact(&mut offered)?;
    if !same(&offered, &proof(&nonce, 0)) {
        return Ok(());
    }
    s.write_all(&proof(&nonce, 1))?;
    let version = read_int(s)?;
    let timeout = read_int(s)?;
    if timeout == STOP {
        write_int(s, 0)?;
        let _ = fs::remove_file(PORT_FILE);
        process::exit(0);
    }
    if timeout == PING {
        write_int(s, unsafe { getuid() } as i32)?;
        if version != VERSION {
            relaunch();
        }
        return Ok(());
    }
    if version != VERSION {
        write_int(s, STALE)?;
        relaunch();
        return Ok(());
    }
    if timeout == STREAM {
        let target = read_utf(s)?;
        if !TARGETS.contains(&target.as_str()) {
            return Ok(());
        }
        write_int(s, 0)?;
        s.set_read_timeout(None)?;
        return stream(&target, s.try_clone()?);
    }
    let length = read_int(s)?;
    if !(0..=MAX_SCRIPT).contains(&length) {
        return Ok(());
    }
    let mut script = vec![0u8; length as usize];
    s.read_exact(&mut script)?;
    write_int(s, run(&script, timeout.clamp(1, 600) as u64))
}

fn proof(nonce: &[u8], side: u8) -> [u8; PROOF_BYTES] {
    let mut data = config().token.as_bytes().to_vec();
    data.extend_from_slice(nonce);
    data.push(side);
    sha256::digest(&data)
}

fn same(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |d, (x, y)| d | (x ^ y)) == 0
}

fn random_nonce() -> io::Result<[u8; NONCE_BYTES]> {
    let mut nonce = [0u8; NONCE_BYTES];
    fs::File::open("/dev/urandom")?.read_exact(&mut nonce)?;
    Ok(nonce)
}

fn read_int(s: &mut TcpStream) -> io::Result<i32> {
    let mut b = [0u8; 4];
    s.read_exact(&mut b)?;
    Ok(i32::from_be_bytes(b))
}

fn write_int(s: &mut TcpStream, v: i32) -> io::Result<()> {
    s.write_all(&v.to_be_bytes())
}

fn read_utf(s: &mut TcpStream) -> io::Result<String> {
    let mut len = [0u8; 2];
    s.read_exact(&mut len)?;
    let mut body = vec![0u8; u16::from_be_bytes(len) as usize];
    s.read_exact(&mut body)?;
    Ok(String::from_utf8_lossy(&body).into_owned())
}

fn java_utf(line: &str) -> Vec<u8> {
    let mut body = vec![0u8, 0u8];
    for u in line.encode_utf16().take(LINE_LIMIT) {
        match u {
            0x0001..=0x007F => body.push(u as u8),
            0x0000 | 0x0080..=0x07FF => body.extend_from_slice(&[0xC0 | (u >> 6) as u8, 0x80 | (u & 0x3F) as u8]),
            _ => body.extend_from_slice(&[0xE0 | (u >> 12) as u8, 0x80 | ((u >> 6) & 0x3F) as u8, 0x80 | (u & 0x3F) as u8]),
        }
    }
    let n = (body.len() - 2) as u16;
    body[..2].copy_from_slice(&n.to_be_bytes());
    body
}

fn send(out: &Mutex<TcpStream>, line: &str) -> io::Result<()> {
    let mut s = out.lock().map_err(|_| io::Error::from(io::ErrorKind::BrokenPipe))?;
    s.write_all(&java_utf(line))
}

fn wanted(line: &str) -> bool {
    STREAM_MARKERS.iter().any(|m| line.contains(m))
}

fn pid_of(target: &str) -> Option<String> {
    let out = Command::new("pidof").arg(target).stdin(Stdio::null()).stderr(Stdio::null()).output().ok()?;
    let first = String::from_utf8_lossy(&out.stdout).split_whitespace().next()?.to_string();
    first.bytes().all(|b| b.is_ascii_digit()).then_some(first)
}

fn alive(pid: &str, target: &str) -> bool {
    fs::read(format!("/proc/{pid}/cmdline"))
        .map(|b| b.split(|x| *x == 0).next().unwrap_or(&[]) == target.as_bytes())
        .unwrap_or(false)
}

fn prefilter() -> bool {
    *PREFILTER.get_or_init(|| {
        let Ok(mut c) = Command::new("logcat")
            .args(["-e", STREAM_REGEX, "-d", "-t", "1"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
        else {
            return false;
        };
        match wait_for(&mut c, Duration::from_secs(10)) {
            Some(status) => status.success(),
            None => {
                let _ = c.kill();
                let _ = c.wait();
                false
            }
        }
    })
}

fn stream(target: &str, sock: TcpStream) -> io::Result<()> {
    let out = Arc::new(Mutex::new(sock));
    loop {
        let Some(pid) = pid_of(target) else {
            send(&out, STREAM_IDLE)?;
            thread::sleep(Duration::from_secs(2));
            continue;
        };
        send(&out, &format!("{STREAM_START} {pid}"))?;
        let mut args = vec!["-v".to_string(), "raw".into(), format!("--pid={pid}"), "-s".into(), "Roblox:I".into()];
        if prefilter() {
            args.push("-e".into());
            args.push(STREAM_REGEX.into());
        }
        let mut log = Command::new("logcat");
        log.args(&args).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::null());
        unsafe {
            log.pre_exec(|| {
                prctl(PR_SET_PDEATHSIG, SIGKILL);
                Ok(())
            });
        }
        let mut log = log.spawn()?;
        let failed = Arc::new(AtomicBool::new(false));
        if let Some(stdout) = log.stdout.take() {
            let out = Arc::clone(&out);
            let failed = Arc::clone(&failed);
            thread::spawn(move || {
                let mut r = BufReader::new(stdout);
                let mut buf = Vec::new();
                loop {
                    buf.clear();
                    match r.read_until(b'\n', &mut buf) {
                        Ok(0) | Err(_) => break,
                        Ok(_) => {
                            let text = String::from_utf8_lossy(&buf);
                            let line = text.trim_end_matches(['\n', '\r']);
                            if wanted(line) && send(&out, line).is_err() {
                                failed.store(true, Ordering::SeqCst);
                                break;
                            }
                        }
                    }
                }
            });
        }
        let mut ticks = 0u32;
        let mut result = Ok(());
        while !failed.load(Ordering::SeqCst) {
            thread::sleep(ALIVE_POLL);
            if !alive(&pid, target) {
                break;
            }
            ticks += 1;
            if ticks % KEEPALIVE_TICKS == 0 {
                if let Err(e) = send(&out, "") {
                    result = Err(e);
                    break;
                }
            }
        }
        let _ = log.kill();
        let _ = log.wait();
        result?;
        if failed.load(Ordering::SeqCst) {
            return Err(io::ErrorKind::BrokenPipe.into());
        }
        send(&out, STREAM_EXIT)?;
    }
}

fn wait_for(child: &mut Child, limit: Duration) -> Option<ExitStatus> {
    let end = Instant::now() + limit;
    let mut pause = Duration::from_millis(2);
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Some(status),
            Ok(None) if Instant::now() < end => {
                thread::sleep(pause);
                pause = (pause * 2).min(Duration::from_millis(50));
            }
            _ => return None,
        }
    }
}

fn run(script: &[u8], timeout: u64) -> i32 {
    let Ok(mut child) = Command::new("sh").stdin(Stdio::piped()).stdout(Stdio::null()).stderr(Stdio::null()).spawn() else {
        return 1;
    };
    let wrote = child.stdin.take().is_some_and(|mut input| input.write_all(script).is_ok());
    if !wrote {
        let _ = child.kill();
        let _ = child.wait();
        return 2;
    }
    match wait_for(&mut child, Duration::from_secs(timeout)) {
        Some(status) => status.code().unwrap_or_else(|| 128 + status.signal().unwrap_or(0)),
        None => {
            let _ = child.kill();
            let _ = child.wait();
            2
        }
    }
}

fn relaunch() {
    let _guard = RELAUNCH.lock();
    let c = config();
    let script = format!(
        "sleep 1\n\
         p=$(pm path {pkg} | sed -n 's/^package://p' | grep base.apk | head -n 1)\n\
         [ -z \"$p\" ] && p=$(pm path {pkg} | sed -n '1s/^package://p')\n\
         [ -z \"$p\" ] && exit 1\n\
         CLASSPATH=\"$p\" exec app_process /system/bin {HELPER_CLASS} {pkg} {uid} {token}\n",
        pkg = c.pkg,
        uid = c.uid,
        token = c.token
    );
    let spawned = Command::new("sh")
        .arg("-c")
        .arg(script)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn();
    if spawned.is_ok() {
        process::exit(0);
    }
}
