use std::collections::HashMap;
use std::io::Read;
use std::process::{Child, Command, Stdio};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

pub fn command(ffmpeg: &str) -> Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let mut command = Command::new(ffmpeg);
        command.creation_flags(0x0800_0000 | 0x0000_8000);
        command
    }
    #[cfg(not(windows))]
    {
        Command::new(ffmpeg)
    }
}

pub fn run(ffmpeg: &str, args: &[String], timeout: Duration) -> Result<String, String> {
    let mut child = command(ffmpeg)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("ffmpeg could not start: {e}"))?;
    let stdout = drain(child.stdout.take());
    let stderr = drain(child.stderr.take());
    let started = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                let out = stdout.join().unwrap_or_default();
                let err = stderr.join().unwrap_or_default();
                if status.success() {
                    return Ok(out + &err);
                }
                return Err(last_line(&err).unwrap_or_else(|| format!("ffmpeg failed with {status}")));
            }
            Ok(None) => {
                if started.elapsed() > timeout {
                    kill(&mut child);
                    return Err("ffmpeg took too long".into());
                }
                std::thread::sleep(Duration::from_millis(15));
            }
            Err(e) => return Err(e.to_string()),
        }
    }
}

fn drain<R: Read + Send + 'static>(stream: Option<R>) -> std::thread::JoinHandle<String> {
    std::thread::spawn(move || {
        let mut text = String::new();
        if let Some(mut stream) = stream {
            let mut bytes = Vec::new();
            let _ = stream.read_to_end(&mut bytes);
            text = String::from_utf8_lossy(&bytes).into_owned();
        }
        text
    })
}

pub fn last_line(text: &str) -> Option<String> {
    text.lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .last()
        .map(str::to_owned)
}

pub fn kill(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

pub struct Capabilities {
    pub encoders: String,
    pub filters: String,
}

impl Capabilities {
    pub fn encoder(&self, name: &str) -> bool {
        self.encoders.split_whitespace().any(|w| w == name)
    }

    pub fn filter(&self, name: &str) -> bool {
        self.filters.split_whitespace().any(|w| w == name)
    }
}

pub fn capabilities(ffmpeg: &str) -> std::sync::Arc<Capabilities> {
    static CACHE: OnceLock<Mutex<HashMap<String, std::sync::Arc<Capabilities>>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Some(found) = cache.lock().ok().and_then(|c| c.get(ffmpeg).cloned()) {
        return found;
    }
    let list = |kind: &str| {
        run(ffmpeg, &["-hide_banner".into(), kind.into()], Duration::from_secs(10)).unwrap_or_default()
    };
    let caps = std::sync::Arc::new(Capabilities {
        encoders: list("-encoders"),
        filters: list("-filters"),
    });
    if let Ok(mut c) = cache.lock() {
        c.insert(ffmpeg.to_owned(), caps.clone());
    }
    caps
}

pub fn duration_ms(ffmpeg: &str, input: &str) -> i64 {
    let output = command(ffmpeg)
        .args(["-hide_banner", "-i", input])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .output();
    match output {
        Ok(o) => parse_duration(&String::from_utf8_lossy(&o.stderr)).unwrap_or(-1),
        Err(_) => -1,
    }
}

fn parse_duration(text: &str) -> Option<i64> {
    let start = text.find("Duration: ")? + 10;
    let value = text[start..].split(',').next()?.trim();
    let mut parts = value.split(':');
    let h: f64 = parts.next()?.parse().ok()?;
    let m: f64 = parts.next()?.parse().ok()?;
    let s: f64 = parts.next()?.parse().ok()?;
    Some(((h * 3600.0 + m * 60.0 + s) * 1000.0) as i64)
}

pub fn seconds(ms: u64) -> String {
    format!("{}.{:03}", ms / 1000, ms % 1000)
}

#[cfg(test)]
mod tests {
    #[test]
    fn parses_duration() {
        assert_eq!(super::parse_duration("  Duration: 00:01:02.50, start"), Some(62500));
    }
}
