use crate::audio::{self, AudioPump};
use crate::ffmpeg;
use crate::pipeline::{self, Candidate, Config};
use crate::ring::Ring;
use serde::Serialize;
use std::io::{Read, Write};
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub running: bool,
    pub recording: bool,
    pub recording_ms: u64,
    pub recording_bytes: u64,
    pub encoder_id: String,
    pub encoder_label: String,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_kbps: u32,
    pub buffered_ms: u64,
    pub buffer_seconds: u32,
    pub buffered_bytes: u64,
    pub last_error: String,
}

struct Shared {
    ring: Mutex<Ring>,
    status: Mutex<Status>,
    stop: AtomicBool,
}

pub struct Session {
    config: Config,
    shared: Arc<Shared>,
    worker: Option<JoinHandle<()>>,
}

impl Session {
    pub fn start(raw: Config) -> Result<Self, String> {
        let config = raw.normalized();
        let screen = pipeline::screen(config.window);
        let kbps = config.bitrate_kbps(screen.height);
        let keep = Duration::from_secs(config.buffer_seconds as u64);
        let cap = config.memory_cap(kbps);

        let status = Status {
            running: true,
            recording: false,
            recording_ms: 0,
            recording_bytes: 0,
            encoder_id: String::new(),
            encoder_label: "Starting...".into(),
            width: if config.height > 0 { config.height } else { screen.width },
            height: if config.height > 0 { config.height } else { screen.height },
            fps: config.fps,
            bitrate_kbps: kbps,
            buffered_ms: 0,
            buffer_seconds: config.buffer_seconds,
            buffered_bytes: 0,
            last_error: String::new(),
        };

        let shared = Arc::new(Shared {
            ring: Mutex::new(Ring::new(keep, cap)),
            status: Mutex::new(status),
            stop: AtomicBool::new(false),
        });

        let worker_cfg = config.clone();
        let worker_shared = shared.clone();
        let worker = thread::Builder::new()
            .name("voidclip-supervisor".into())
            .spawn(move || supervise(worker_cfg, worker_shared))
            .map_err(|e| format!("failed to spawn supervisor: {e}"))?;

        Ok(Session {
            config,
            shared,
            worker: Some(worker),
        })
    }

    pub fn stop(&mut self) {
        self.shared.stop.store(true, Ordering::SeqCst);
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
        if let Ok(mut ring) = self.shared.ring.lock() {
            if let Ok((temp_path, _)) = ring.stop_recording() {
                let _ = std::fs::remove_file(temp_path);
            }
        }
        if let Ok(mut st) = self.shared.status.lock() {
            st.running = false;
            st.recording = false;
        }
    }

    pub fn config(&self) -> &Config {
        &self.config
    }

    pub fn status(&self) -> Status {
        let (buffered_ms, buffered_bytes, rec_on, rec_ms, rec_bytes) = self
            .shared
            .ring
            .lock()
            .map(|r| {
                let (ron, rms, rbytes) = r.recording_info();
                (r.buffered().as_millis() as u64, r.bytes() as u64, ron, rms, rbytes)
            })
            .unwrap_or((0, 0, false, 0, 0));
        let mut st = self
            .shared
            .status
            .lock()
            .map(|s| s.clone())
            .unwrap_or_default();
        st.buffered_ms = buffered_ms.min(self.config.buffer_seconds as u64 * 1000);
        st.buffered_bytes = buffered_bytes;
        st.recording = rec_on;
        st.recording_ms = rec_ms;
        st.recording_bytes = rec_bytes;
        st
    }

    pub fn save(&self, output: &str, seconds: u32) -> Result<(), String> {
        let data = {
            let ring = self
                .shared
                .ring
                .lock()
                .map_err(|_| "buffer lock poisoned".to_string())?;
            if !ring.has_video() {
                return Err("No video frames in replay buffer yet".into());
            }
            ring.snapshot(seconds)
                .ok_or_else(|| "Replay buffer is empty".to_string())?
        };
        mux_mp4(&self.config.ffmpeg, &data, output)
    }

    pub fn start_recording(&self, temp_path: &str) -> Result<(), String> {
        let mut ring = self
            .shared
            .ring
            .lock()
            .map_err(|_| "buffer lock poisoned".to_string())?;
        if ring.is_recording() {
            return Ok(());
        }
        ring.start_recording(temp_path)
    }

    pub fn stop_recording(&self, output: &str) -> Result<(), String> {
        let (temp_path, bytes) = {
            let mut ring = self
                .shared
                .ring
                .lock()
                .map_err(|_| "buffer lock poisoned".to_string())?;
            ring.stop_recording()?
        };
        if bytes == 0 {
            let _ = std::fs::remove_file(&temp_path);
            return Err("No video frames were captured during recording".into());
        }
        let res = mux_ts_file(&self.config.ffmpeg, &temp_path, output);
        let _ = std::fs::remove_file(&temp_path);
        res
    }
}

pub fn mux_ts_file(ffmpeg_bin: &str, input_ts: &str, output: &str) -> Result<(), String> {
    if let Some(parent) = std::path::Path::new(output).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let args = vec![
        "-hide_banner".into(),
        "-loglevel".into(),
        "error".into(),
        "-y".into(),
        "-f".into(),
        "mpegts".into(),
        "-i".into(),
        input_ts.into(),
        "-map".into(),
        "0:v:0".into(),
        "-map".into(),
        "0:a:0?".into(),
        "-c".into(),
        "copy".into(),
        "-bsf:a".into(),
        "aac_adtstoasc".into(),
        "-movflags".into(),
        "+faststart".into(),
        output.into(),
    ];
    ffmpeg::run(ffmpeg_bin, &args, Duration::from_secs(120)).map(|_| ())
}

pub fn mux_mp4(ffmpeg_bin: &str, data: &[u8], output: &str) -> Result<(), String> {
    if let Some(parent) = std::path::Path::new(output).parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    let mut child = ffmpeg::command(ffmpeg_bin)
        .args([
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "mpegts",
            "-i",
            "pipe:0",
            "-map",
            "0:v:0",
            "-map",
            "0:a:0?",
            "-c",
            "copy",
            "-bsf:a",
            "aac_adtstoasc",
            "-movflags",
            "+faststart",
            output,
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("failed to start ffmpeg for save: {e}"))?;

    if let Some(mut stdin) = child.stdin.take() {
        let owned = data.to_vec();
        thread::spawn(move || {
            let _ = stdin.write_all(&owned);
        });
    }

    let out = child
        .wait_with_output()
        .map_err(|e| format!("ffmpeg wait failed: {e}"))?;
    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr);
        return Err(ffmpeg::last_line(&err).unwrap_or_else(|| "Failed to mux MP4 clip".into()));
    }
    Ok(())
}

impl Drop for Session {
    fn drop(&mut self) {
        self.stop();
    }
}

fn supervise(cfg: Config, shared: Arc<Shared>) {
    let caps = ffmpeg::capabilities(&cfg.ffmpeg);
    let list = pipeline::candidates(&cfg, &caps, audio::SAMPLE_RATE);
    if list.is_empty() {
        if let Ok(mut st) = shared.status.lock() {
            st.running = false;
            st.last_error = "No compatible capture pipeline found".into();
        }
        return;
    }

    let mut index = 0usize;
    while !shared.stop.load(Ordering::Relaxed) {
        let candidate = &list[index % list.len()];
        if let Ok(mut st) = shared.status.lock() {
            st.encoder_id = candidate.id.clone();
            st.encoder_label = candidate.label.clone();
            st.width = candidate.width;
            st.height = candidate.height;
        }
        if let Ok(mut r) = shared.ring.lock() {
            r.clear();
        }

        let produced = run_candidate(&cfg, candidate, &shared);
        if shared.stop.load(Ordering::Relaxed) {
            break;
        }
        if !produced {
            index += 1;
            if index >= list.len() * 2 {
                thread::sleep(Duration::from_secs(2));
            } else {
                thread::sleep(Duration::from_millis(150));
            }
        } else {
            thread::sleep(Duration::from_millis(400));
        }
    }

    if let Ok(mut st) = shared.status.lock() {
        st.running = false;
    }
}

fn run_candidate(cfg: &Config, candidate: &Candidate, shared: &Arc<Shared>) -> bool {
    let mut cmd = ffmpeg::command(&cfg.ffmpeg);
    cmd.args(&candidate.args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if candidate.audio_pipe {
        cmd.stdin(Stdio::piped());
    } else {
        cmd.stdin(Stdio::null());
    }

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            if let Ok(mut st) = shared.status.lock() {
                st.last_error = format!("ffmpeg launch error: {e}");
            }
            return false;
        }
    };

    let mut pump = if candidate.audio_pipe {
        child
            .stdin
            .take()
            .map(|stdin| AudioPump::start(stdin, cfg.game_audio, cfg.microphone))
    } else {
        None
    };

    let stderr_handle = child.stderr.take().map(|mut err| {
        thread::spawn(move || {
            let mut buf = Vec::new();
            let _ = err.read_to_end(&mut buf);
            String::from_utf8_lossy(&buf).into_owned()
        })
    });

    let stdout_shared = shared.clone();
    let reader = child.stdout.take().map(|mut out| {
        thread::spawn(move || {
            let mut chunk = vec![0u8; 188 * 174];
            while !stdout_shared.stop.load(Ordering::Relaxed) {
                match out.read(&mut chunk) {
                    Ok(0) => break,
                    Ok(n) => {
                        if let Ok(mut ring) = stdout_shared.ring.lock() {
                            ring.feed(&chunk[..n]);
                        }
                    }
                    Err(_) => break,
                }
            }
        })
    });

    let started = Instant::now();
    let mut produced_video = false;

    while !shared.stop.load(Ordering::Relaxed) {
        if !produced_video {
            produced_video = shared.ring.lock().map(|r| r.has_video()).unwrap_or(false);
            if !produced_video && started.elapsed() > Duration::from_secs(5) {
                break;
            }
        }
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) => thread::sleep(Duration::from_millis(40)),
            Err(_) => break,
        }
    }

    if let Some(mut p) = pump.take() {
        p.stop();
    }
    ffmpeg::kill(&mut child);
    if let Some(r) = reader {
        let _ = r.join();
    }
    if let Some(eh) = stderr_handle {
        if let Ok(err_text) = eh.join() {
            if !produced_video {
                if let Some(line) = ffmpeg::last_line(&err_text) {
                    if let Ok(mut st) = shared.status.lock() {
                        st.last_error = line;
                    }
                }
            }
        }
    }
    produced_video
}

pub fn trim(ffmpeg_bin: &str, input: &str, output: &str, start_ms: u64, end_ms: u64) -> Result<(), String> {
    if end_ms <= start_ms + 100 {
        return Err("Trim range is too short".into());
    }
    if let Some(parent) = std::path::Path::new(output).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let args = vec![
        "-hide_banner".into(),
        "-loglevel".into(),
        "error".into(),
        "-y".into(),
        "-ss".into(),
        ffmpeg::seconds(start_ms),
        "-to".into(),
        ffmpeg::seconds(end_ms),
        "-i".into(),
        input.into(),
        "-map".into(),
        "0:v:0".into(),
        "-map".into(),
        "0:a:0?".into(),
        "-c".into(),
        "copy".into(),
        "-avoid_negative_ts".into(),
        "make_zero".into(),
        "-movflags".into(),
        "+faststart".into(),
        output.into(),
    ];
    ffmpeg::run(ffmpeg_bin, &args, Duration::from_secs(60)).map(|_| ())
}

pub fn thumbnail(ffmpeg_bin: &str, input: &str, output: &str, time_ms: u64, width: u32) -> Result<(), String> {
    if let Some(parent) = std::path::Path::new(output).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let w = width.clamp(120, 1920);
    let args = vec![
        "-hide_banner".into(),
        "-loglevel".into(),
        "error".into(),
        "-y".into(),
        "-ss".into(),
        ffmpeg::seconds(time_ms),
        "-i".into(),
        input.into(),
        "-frames:v".into(),
        "1".into(),
        "-vf".into(),
        format!("scale={w}:-2"),
        "-q:v".into(),
        "4".into(),
        output.into(),
    ];
    ffmpeg::run(ffmpeg_bin, &args, Duration::from_secs(20)).map(|_| ())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn find_ffmpeg() -> Option<String> {
        if let Ok(out) = ffmpeg::command("ffmpeg").arg("-version").output() {
            if out.status.success() {
                return Some("ffmpeg".into());
            }
        }
        #[cfg(windows)]
        if let Ok(local) = std::env::var("LOCALAPPDATA") {
            let pkg_dir = std::path::Path::new(&local).join("Microsoft").join("WinGet").join("Packages");
            if let Ok(entries) = std::fs::read_dir(pkg_dir) {
                for e in entries.flatten() {
                    if e.file_name().to_string_lossy().contains("FFmpeg") {
                        if let Some(found) = walk_for_ffmpeg(&e.path()) {
                            return Some(found);
                        }
                    }
                }
            }
        }
        None
    }

    #[cfg(windows)]
    fn walk_for_ffmpeg(dir: &std::path::Path) -> Option<String> {
        let entries = std::fs::read_dir(dir).ok()?;
        for e in entries.flatten() {
            let p = e.path();
            if p.is_dir() {
                if let Some(f) = walk_for_ffmpeg(&p) {
                    return Some(f);
                }
            } else if p.file_name().map_or(false, |n| n == "ffmpeg.exe") {
                return Some(p.to_string_lossy().into_owned());
            }
        }
        None
    }

    #[test]
    fn full_mpegts_ring_remux_probe_thumbnail_and_trim() {
        let Some(ff) = find_ffmpeg() else {
            return;
        };
        let caps = ffmpeg::capabilities(&ff);
        let enc = if caps.encoder("h264_nvenc") { "h264_nvenc" } else { "libx264" };
        let mut cmd = ffmpeg::command(&ff);
        cmd.args([
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=1280x720:rate=60:duration=3",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:sample_rate=48000:duration=3",
            "-c:v",
            enc,
            "-g",
            "30",
            "-bf",
            "0",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-f",
            "mpegts",
            "pipe:1",
        ]);
        let out = cmd.output().expect("ffmpeg synthetic mpegts generation");
        assert!(out.status.success(), "synthetic stream failed");
        assert!(out.stdout.len() > 188 * 100);

        let mut ring = Ring::new(Duration::from_secs(30), 64 * 1024 * 1024);
        ring.feed(&out.stdout);
        assert!(ring.has_video());

        let snap = ring.snapshot(30).expect("snapshot");
        let tmp = std::env::temp_dir().join(format!("voidclip_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&tmp);
        std::fs::create_dir_all(&tmp).unwrap();

        let clip_mp4 = tmp.join("clip.mp4");
        let clip_str = clip_mp4.to_string_lossy().into_owned();
        mux_mp4(&ff, &snap, &clip_str).expect("mux_mp4 should succeed");
        assert!(clip_mp4.exists());

        let dur = ffmpeg::duration_ms(&ff, &clip_str);
        assert!(dur >= 2500 && dur <= 3500, "unexpected duration: {dur}");

        let thumb_jpg = tmp.join("thumb.jpg");
        let thumb_str = thumb_jpg.to_string_lossy().into_owned();
        thumbnail(&ff, &clip_str, &thumb_str, 500, 360).expect("thumbnail should succeed");
        assert!(thumb_jpg.exists());

        let trimmed_mp4 = tmp.join("trimmed.mp4");
        let trimmed_str = trimmed_mp4.to_string_lossy().into_owned();
        trim(&ff, &clip_str, &trimmed_str, 0, 1500).expect("trim should succeed");
        assert!(trimmed_mp4.exists());
        let trim_dur = ffmpeg::duration_ms(&ff, &trimmed_str);
        assert!(trim_dur >= 1000 && trim_dur <= 2200, "unexpected trimmed duration: {trim_dur}");

        let rec_ts = tmp.join("rec.ts");
        let rec_ts_str = rec_ts.to_string_lossy().into_owned();
        let rec_mp4 = tmp.join("rec.mp4");
        let rec_mp4_str = rec_mp4.to_string_lossy().into_owned();
        let mut rec_ring = Ring::new(Duration::from_secs(30), 64 * 1024 * 1024);
        rec_ring.start_recording(&rec_ts_str).expect("start_recording");
        assert!(rec_ring.is_recording());
        rec_ring.feed(&out.stdout);
        let (stopped_path, bytes) = rec_ring.stop_recording().expect("stop_recording");
        assert!(!rec_ring.is_recording());
        assert_eq!(stopped_path, rec_ts_str);
        assert!(bytes > 1000);
        mux_ts_file(&ff, &rec_ts_str, &rec_mp4_str).expect("mux_ts_file");
        assert!(rec_mp4.exists());
        let rec_dur = ffmpeg::duration_ms(&ff, &rec_mp4_str);
        assert!(rec_dur >= 2500 && rec_dur <= 3500, "unexpected rec duration: {rec_dur}");

        let _ = std::fs::remove_dir_all(&tmp);
    }
}
