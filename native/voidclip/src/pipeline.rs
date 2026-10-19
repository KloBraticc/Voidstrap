use crate::ffmpeg::Capabilities;
use serde::Deserialize;

#[derive(Deserialize, Clone, Copy, Debug, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct Region {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", default)]
pub struct Config {
    pub ffmpeg: String,
    pub buffer_seconds: u32,
    pub fps: u32,
    pub height: u32,
    pub quality: u32,
    pub codec: String,
    pub game_audio: bool,
    pub microphone: bool,
    pub cursor: bool,
    pub window: i64,
    pub region: Option<Region>,
    pub display: String,
    pub screen: u32,
    pub preferred: String,
    pub max_memory_mb: u32,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            ffmpeg: "ffmpeg".into(),
            buffer_seconds: 30,
            fps: 60,
            height: 0,
            quality: 2,
            codec: "h264".into(),
            game_audio: true,
            microphone: false,
            cursor: false,
            window: 0,
            region: None,
            display: String::new(),
            screen: 0,
            preferred: String::new(),
            max_memory_mb: 0,
        }
    }
}

impl Config {
    pub fn normalized(mut self) -> Self {
        self.buffer_seconds = self.buffer_seconds.clamp(5, 600);
        self.fps = self.fps.clamp(15, 240);
        self.quality = self.quality.min(3);
        if self.codec != "hevc" {
            self.codec = "h264".into();
        }
        self
    }

    pub fn bitrate_kbps(&self, native_height: u32) -> u32 {
        let base = [8_000.0, 14_000.0, 22_000.0, 40_000.0][self.quality as usize];
        let height = if self.height > 0 { self.height.min(native_height.max(1)) } else { native_height } as f64;
        let factor = (height * height * self.fps as f64) / (1080.0 * 1080.0 * 60.0);
        let codec = if self.codec == "hevc" { 0.7 } else { 1.0 };
        ((base * factor.clamp(0.2, 5.0) * codec) as u32).clamp(1_500, 150_000)
    }

    pub fn memory_cap(&self, kbps: u32) -> usize {
        if self.max_memory_mb > 0 {
            return self.max_memory_mb as usize * 1024 * 1024;
        }
        let expected = kbps as usize * 1000 / 8 * (self.buffer_seconds as usize + 4);
        (expected * 2).clamp(64 * 1024 * 1024, 3 * 1024 * 1024 * 1024)
    }
}

#[derive(Clone, Debug)]
pub struct Candidate {
    pub id: String,
    pub label: String,
    pub args: Vec<String>,
    pub audio_pipe: bool,
    pub scaled: bool,
    pub width: u32,
    pub height: u32,
}

pub struct Screen {
    pub output: u32,
    pub adapter: u32,
    pub vendor: u32,
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

#[cfg(windows)]
pub fn screen(window: i64) -> Screen {
    use windows::Win32::Foundation::{HWND, POINT};
    use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIFactory1};
    use windows::Win32::Graphics::Gdi::{
        GetMonitorInfoW, MonitorFromPoint, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST,
        MONITOR_DEFAULTTOPRIMARY,
    };
    let mut result = Screen { output: 0, adapter: 0, vendor: 0, x: 0, y: 0, width: 1920, height: 1080 };
    unsafe {
        let monitor = if window != 0 {
            MonitorFromWindow(HWND(window as isize as *mut core::ffi::c_void), MONITOR_DEFAULTTONEAREST)
        } else {
            MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY)
        };
        let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        if GetMonitorInfoW(monitor, &mut info).as_bool() {
            let r = info.rcMonitor;
            result.x = r.left;
            result.y = r.top;
            result.width = (r.right - r.left).max(1) as u32;
            result.height = (r.bottom - r.top).max(1) as u32;
        }
        if let Ok(factory) = CreateDXGIFactory1::<IDXGIFactory1>() {
            let mut a = 0;
            'adapters: while let Ok(adapter) = factory.EnumAdapters1(a) {
                let vendor = adapter.GetDesc1().map(|d| d.VendorId).unwrap_or(0);
                if result.vendor == 0 && vendor != 0 {
                    result.vendor = vendor;
                }
                let mut o = 0;
                while let Ok(output) = adapter.EnumOutputs(o) {
                    if let Ok(desc) = output.GetDesc() {
                        if desc.Monitor.0 == monitor.0 {
                            result.adapter = a;
                            result.output = o;
                            if vendor != 0 {
                                result.vendor = vendor;
                            }
                            break 'adapters;
                        }
                    }
                    o += 1;
                }
                a += 1;
            }
        }
    }
    result
}

#[cfg(not(windows))]
pub fn screen(_window: i64) -> Screen {
    Screen { output: 0, adapter: 0, vendor: 0, x: 0, y: 0, width: 1920, height: 1080 }
}

fn even(value: f64) -> u32 {
    let v = value.round() as u32;
    (v + (v & 1)).max(2)
}

struct Target {
    scale: bool,
    width: u32,
    height: u32,
}

fn target(cfg: &Config, native_w: u32, native_h: u32) -> Target {
    if cfg.height == 0 || cfg.height >= native_h {
        return Target { scale: false, width: native_w, height: native_h };
    }
    let aspect = native_w as f64 / native_h.max(1) as f64;
    Target { scale: true, width: even(cfg.height as f64 * aspect), height: cfg.height & !1 }
}

fn encoder_name(family: &str, codec: &str) -> String {
    match family {
        "x264" => "libx264".into(),
        _ => format!("{}_{}", if codec == "hevc" { "hevc" } else { "h264" }, family),
    }
}

fn encoder_args(family: &str, encoder: &str, kbps: u32, fps: u32) -> Vec<String> {
    let b = format!("{kbps}k");
    let buf = format!("{}k", kbps * 2);
    let g = fps.to_string();
    let mut a: Vec<String> = vec!["-c:v".into(), encoder.into()];
    let extra: Vec<&str> = match family {
        "nvenc" => vec!["-preset", "p3", "-tune", "ll", "-rc", "cbr", "-zerolatency", "1"],
        "amf" => vec!["-usage", "lowlatency", "-quality", "speed", "-rc", "cbr"],
        "qsv" => vec!["-preset", "veryfast"],
        "mf" => vec!["-rate_control", "cbr", "-hw_encoding", "1", "-scenario", "display_remoting"],
        "vaapi" => vec!["-rc_mode", "CBR"],
        "videotoolbox" => vec!["-realtime", "1"],
        _ => vec!["-preset", "ultrafast", "-tune", "zerolatency", "-threads", "2"],
    };
    a.extend(extra.into_iter().map(String::from));
    a.extend(["-b:v".into(), b.clone()]);
    if family != "mf" && family != "videotoolbox" {
        a.extend(["-maxrate".into(), b, "-bufsize".into(), buf]);
    }
    a.extend(["-g".into(), g.clone()]);
    if family != "mf" {
        a.extend(["-bf".into(), "0".into()]);
    }
    a.extend(["-fps_mode".into(), "cfr".into(), "-r".into(), g]);
    a
}

fn join(filters: &[String]) -> String {
    if filters.is_empty() {
        "null".into()
    } else {
        filters.join(",")
    }
}

struct Build<'a> {
    cfg: &'a Config,
    kbps: u32,
    head: Vec<String>,
    inputs: Vec<String>,
    video_source: String,
    audio_args: Vec<String>,
    audio_pipe: bool,
}

impl Build<'_> {
    fn candidate(&self, id: &str, label: &str, family: &str, filters: Vec<String>, t: &Target, scaled: bool) -> Candidate {
        let encoder = encoder_name(family, &self.cfg.codec);
        let graph = format!("{}{}[v]", self.video_source, join(&filters));
        let mut args: Vec<String> = vec!["-hide_banner".into(), "-loglevel".into(), "error".into(), "-nostats".into()];
        if !self.audio_pipe {
            args.push("-nostdin".into());
        }
        args.extend(self.head.iter().cloned());
        args.extend(self.inputs.iter().cloned());
        args.extend(["-filter_complex".into(), graph, "-map".into(), "[v]".into()]);
        args.extend(encoder_args(family, &encoder, self.kbps, self.cfg.fps));
        args.extend(self.audio_args.iter().cloned());
        args.extend([
            "-f".into(), "mpegts".into(), "-muxdelay".into(), "0".into(), "-muxpreload".into(), "0".into(), "pipe:1".into(),
        ]);
        Candidate {
            id: id.into(),
            label: label.into(),
            args,
            audio_pipe: self.audio_pipe,
            scaled,
            width: t.width,
            height: t.height,
        }
    }
}

fn scale_cpu(t: &Target) -> Vec<String> {
    if t.scale {
        vec![format!("scale={}:{}:flags=fast_bilinear", t.width, t.height)]
    } else {
        vec![]
    }
}

pub fn candidates(cfg: &Config, caps: &Capabilities, audio_rate: u32) -> Vec<Candidate> {
    let mut list = build_platform(cfg, caps, audio_rate);
    if !cfg.preferred.is_empty() {
        if let Some(i) = list.iter().position(|c| c.id == cfg.preferred) {
            let c = list.remove(i);
            list.insert(0, c);
        }
    }
    list
}

#[cfg(windows)]
fn build_platform(cfg: &Config, caps: &Capabilities, audio_rate: u32) -> Vec<Candidate> {
    let s = screen(cfg.window);
    let t = target(cfg, s.width, s.height);
    let kbps = cfg.bitrate_kbps(s.height);
    let audio_pipe = cfg.game_audio || cfg.microphone;
    let dda_head: Vec<String> = vec![
        "-init_hw_device".into(),
        format!("d3d11va=vcd:{}", s.adapter),
        "-filter_hw_device".into(),
        "vcd".into(),
    ];
    let mut dda_inputs = Vec::new();
    if audio_pipe {
        dda_inputs.extend(
            [
                "-fflags",
                "+nobuffer",
                "-probesize",
                "32",
                "-analyzeduration",
                "0",
                "-thread_queue_size",
                "4096",
                "-f",
                "f32le",
                "-ar",
            ]
            .into_iter()
            .map(String::from)
            .chain([
                audio_rate.to_string(),
                "-ac".into(),
                "2".into(),
                "-i".into(),
                "pipe:0".into(),
            ]),
        );
    }
    let mouse = if cfg.cursor { 1 } else { 0 };
    let dda_source = format!(
        "ddagrab=output_idx={}:framerate={}:draw_mouse={}:dup_frames=1:output_fmt=bgra:allow_fallback=1,",
        s.output, cfg.fps, mouse
    );
    let audio_args: Vec<String> = if audio_pipe {
        [
            "-map",
            "0:a",
            "-af",
            "aresample=async=1:first_pts=0",
            "-c:a",
            "aac",
            "-b:a",
            "160k",
            "-ar",
            "48000",
        ]
        .into_iter()
        .map(String::from)
        .collect()
    } else {
        Vec::new()
    };
    let gdi_index = if audio_pipe { 1 } else { 0 };
    let mut gdi_inputs = dda_inputs.clone();
    gdi_inputs.extend([
        "-thread_queue_size".to_string(), "512".into(), "-f".into(), "gdigrab".into(), "-framerate".into(), cfg.fps.to_string(),
        "-draw_mouse".into(), mouse.to_string(), "-offset_x".into(), s.x.to_string(), "-offset_y".into(), s.y.to_string(),
        "-video_size".into(), format!("{}x{}", s.width, s.height), "-i".into(), "desktop".into(),
    ]);
    let b_dda = Build {
        cfg,
        kbps,
        head: dda_head,
        inputs: dda_inputs,
        video_source: dda_source,
        audio_args: audio_args.clone(),
        audio_pipe,
    };
    let b_gdi = Build {
        cfg,
        kbps,
        head: Vec::new(),
        inputs: gdi_inputs,
        video_source: format!("[{gdi_index}:v]"),
        audio_args,
        audio_pipe,
    };
    let reset = "setpts=PTS-STARTPTS".to_string();
    let mut list = Vec::new();
    let dda_ok = caps.filter("ddagrab");
    let vendors: [(&str, &str); 3] = match s.vendor {
        0x1002 | 0x1022 => [("amf", "AMD AMF"), ("nvenc", "NVIDIA NVENC"), ("qsv", "Intel Quick Sync")],
        0x8086 => [("qsv", "Intel Quick Sync"), ("nvenc", "NVIDIA NVENC"), ("amf", "AMD AMF")],
        _ => [("nvenc", "NVIDIA NVENC"), ("amf", "AMD AMF"), ("qsv", "Intel Quick Sync")],
    };
    let native = Target { scale: false, width: s.width, height: s.height };
    for (family, label) in vendors {
        if !caps.encoder(&encoder_name(family, &cfg.codec)) {
            continue;
        }
        if dda_ok {
            if t.scale {
                let f = [vec!["hwdownload".into(), "format=bgra".into()], scale_cpu(&t), vec!["format=nv12".into(), reset.clone()]].concat();
                list.push(b_dda.candidate(&format!("{family}-dda-scaled"), label, family, f, &t, true));
            } else {
                let f = if family == "qsv" {
                    vec!["hwmap=derive_device=qsv".into(), "format=qsv".into(), "scale_qsv=format=nv12".into(), reset.clone()]
                } else {
                    vec![reset.clone()]
                };
                list.push(b_dda.candidate(&format!("{family}-native"), label, family, f, &native, false));
            }
        }
        if family != "qsv" {
            let f = [scale_cpu(&t), vec!["format=nv12".into(), reset.clone()]].concat();
            list.push(b_gdi.candidate(&format!("{family}-gdi"), label, family, f, &t, t.scale));
        }
    }
    if caps.encoder(&encoder_name("mf", &cfg.codec)) {
        if dda_ok {
            let f = [vec!["hwdownload".into(), "format=bgra".into()], scale_cpu(&t), vec!["format=nv12".into(), reset.clone()]].concat();
            list.push(b_dda.candidate("mf", "Media Foundation", "mf", f, &t, t.scale));
        }
        let f = [scale_cpu(&t), vec!["format=nv12".into(), reset.clone()]].concat();
        list.push(b_gdi.candidate("mf-gdi", "Media Foundation", "mf", f, &t, t.scale));
    }
    if dda_ok {
        let f = [vec!["hwdownload".into(), "format=bgra".into()], scale_cpu(&t), vec!["format=yuv420p".into(), reset.clone()]].concat();
        list.push(b_dda.candidate("x264-dda", "Software (x264)", "x264", f, &t, t.scale));
    }
    let f = [scale_cpu(&t), vec!["format=yuv420p".into(), reset]].concat();
    list.push(b_gdi.candidate("x264-gdi", "Software (x264)", "x264", f, &t, t.scale));
    list
}

#[cfg(target_os = "linux")]
fn build_platform(cfg: &Config, caps: &Capabilities, _audio_rate: u32) -> Vec<Candidate> {
    let display = if !cfg.display.is_empty() {
        cfg.display.clone()
    } else {
        std::env::var("DISPLAY").unwrap_or_else(|_| ":0".into())
    };
    let (nw, nh, source) = match cfg.region {
        Some(r) if r.width > 0 && r.height > 0 => {
            let w = r.width & !1;
            let h = r.height & !1;
            (w, h, (format!("{w}x{h}"), format!("{display}+{},{}", r.x, r.y)))
        }
        _ => (1920, 1080, (String::new(), display.clone())),
    };
    let t = target(cfg, nw, nh);
    let kbps = cfg.bitrate_kbps(nh);
    let mut inputs: Vec<String> = vec![
        "-thread_queue_size".into(), "512".into(), "-f".into(), "x11grab".into(), "-framerate".into(), cfg.fps.to_string(),
        "-draw_mouse".into(), if cfg.cursor { "1" } else { "0" }.into(),
    ];
    if !source.0.is_empty() {
        inputs.extend(["-video_size".into(), source.0.clone()]);
    }
    inputs.extend(["-i".into(), source.1.clone()]);
    let mut audio_inputs = 0;
    if cfg.game_audio {
        inputs.extend(["-thread_queue_size", "1024", "-f", "pulse", "-i", "@DEFAULT_MONITOR@"].into_iter().map(String::from));
        audio_inputs += 1;
    }
    if cfg.microphone {
        inputs.extend(["-thread_queue_size", "1024", "-f", "pulse", "-i", "default"].into_iter().map(String::from));
        audio_inputs += 1;
    }
    let audio_args: Vec<String> = match audio_inputs {
        1 => ["-map", "1:a", "-af", "aresample=async=1000:first_pts=0", "-c:a", "aac", "-b:a", "160k", "-ar", "48000"]
            .into_iter()
            .map(String::from)
            .collect(),
        2 => [
            "-filter_complex",
            "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,aresample=async=1000:first_pts=0[a]",
            "-map",
            "[a]",
            "-c:a",
            "aac",
            "-b:a",
            "160k",
            "-ar",
            "48000",
        ]
        .into_iter()
        .map(String::from)
        .collect(),
        _ => Vec::new(),
    };
    let mut head = Vec::new();
    let render = "/dev/dri/renderD128";
    let vaapi = std::path::Path::new(render).exists() && caps.encoder(&encoder_name("vaapi", &cfg.codec));
    let mut list = Vec::new();
    let mut b = Build { cfg, kbps, head: head.clone(), inputs, video_source: "[0:v]".into(), audio_args, audio_pipe: false };
    if vaapi {
        head.extend(["-vaapi_device".into(), render.into()]);
        b.head = head;
        let mut f: Vec<String> = vec!["format=nv12".into(), "hwupload".into()];
        if t.scale {
            f.push(format!("scale_vaapi=w={}:h={}", t.width, t.height));
        }
        list.push(b.candidate("vaapi", "VA-API", "vaapi", f, &t, t.scale));
        b.head = Vec::new();
    }
    if caps.encoder(&encoder_name("nvenc", &cfg.codec)) {
        list.push(b.candidate("nvenc", "NVIDIA NVENC", "nvenc", scale_cpu(&t), &t, t.scale));
    }
    let f = [scale_cpu(&t), vec!["format=yuv420p".into()]].concat();
    list.push(b.candidate("x264", "Software (x264)", "x264", f, &t, t.scale));
    list
}

#[cfg(target_os = "macos")]
fn build_platform(cfg: &Config, caps: &Capabilities, _audio_rate: u32) -> Vec<Candidate> {
    let (nw, nh) = match cfg.region {
        Some(r) if r.width > 0 && r.height > 0 => (r.width & !1, r.height & !1),
        _ => (1920, 1080),
    };
    let t = target(cfg, nw, nh);
    let kbps = cfg.bitrate_kbps(nh);
    let audio = if cfg.microphone { "0" } else { "none" };
    let inputs: Vec<String> = vec![
        "-thread_queue_size".into(), "512".into(), "-f".into(), "avfoundation".into(),
        "-capture_cursor".into(), if cfg.cursor { "1" } else { "0" }.into(),
        "-pixel_format".into(), "nv12".into(), "-framerate".into(), cfg.fps.to_string(),
        "-i".into(), format!("Capture screen {}:{}", cfg.screen, audio),
    ];
    let audio_args: Vec<String> = if cfg.microphone {
        ["-map", "0:a", "-af", "aresample=async=1000:first_pts=0", "-c:a", "aac", "-b:a", "160k", "-ar", "48000"]
            .into_iter()
            .map(String::from)
            .collect()
    } else {
        Vec::new()
    };
    let b = Build { cfg, kbps, head: Vec::new(), inputs, video_source: "[0:v]".into(), audio_args, audio_pipe: false };
    let mut list = Vec::new();
    if caps.encoder(&encoder_name("videotoolbox", &cfg.codec)) {
        list.push(b.candidate("videotoolbox", "Apple VideoToolbox", "videotoolbox", scale_cpu(&t), &t, t.scale));
    }
    let f = [scale_cpu(&t), vec!["format=yuv420p".into()]].concat();
    list.push(b.candidate("x264", "Software (x264)", "x264", f, &t, t.scale));
    list
}

#[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
fn build_platform(_cfg: &Config, _caps: &Capabilities, _audio_rate: u32) -> Vec<Candidate> {
    Vec::new()
}
