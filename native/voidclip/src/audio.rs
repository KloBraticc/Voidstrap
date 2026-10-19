use std::io::Write;
use std::process::ChildStdin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

pub const SAMPLE_RATE: u32 = 48_000;
const CHANNELS: usize = 2;
const TICK_MS: u64 = 10;
const TICK_FRAMES: usize = (SAMPLE_RATE as usize * TICK_MS as usize) / 1000;
const TICK_SAMPLES: usize = TICK_FRAMES * CHANNELS;
const MAX_QUEUE_SAMPLES: usize = (SAMPLE_RATE as usize * CHANNELS) / 10;

pub struct AudioPump {
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl AudioPump {
    pub fn start(stdin: ChildStdin, game_audio: bool, microphone: bool) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let stop_flag = stop.clone();
        let handle = thread::Builder::new()
            .name("voidclip-audio".into())
            .spawn(move || run_pump(stdin, game_audio, microphone, stop_flag))
            .ok();
        AudioPump { stop, handle }
    }

    pub fn stop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

impl Drop for AudioPump {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(windows)]
fn run_pump(mut stdin: ChildStdin, game_audio: bool, microphone: bool, stop: Arc<AtomicBool>) {
    use cpal::traits::HostTrait;

    let host = cpal::default_host();
    let game_buf: Arc<Mutex<std::collections::VecDeque<f32>>> =
        Arc::new(Mutex::new(std::collections::VecDeque::with_capacity(MAX_QUEUE_SAMPLES)));
    let mic_buf: Arc<Mutex<std::collections::VecDeque<f32>>> =
        Arc::new(Mutex::new(std::collections::VecDeque::with_capacity(MAX_QUEUE_SAMPLES)));

    let _game_stream = if game_audio {
        host.default_output_device()
            .and_then(|dev| build_input_stream(&dev, game_buf.clone()))
    } else {
        None
    };

    let _mic_stream = if microphone {
        host.default_input_device()
            .and_then(|dev| build_input_stream(&dev, mic_buf.clone()))
    } else {
        None
    };

    let mut mix = vec![0.0f32; TICK_SAMPLES];
    let mut bytes = vec![0u8; TICK_SAMPLES * 4];
    let start = Instant::now();
    let mut frames_written: u64 = 0;

    while !stop.load(Ordering::Relaxed) {
        let elapsed_ns = start.elapsed().as_nanos();
        let target_frames = ((elapsed_ns * SAMPLE_RATE as u128) / 1_000_000_000) as u64;
        if frames_written + TICK_FRAMES as u64 > target_frames {
            let next_ns = ((frames_written + TICK_FRAMES as u64) as u128 * 1_000_000_000) / SAMPLE_RATE as u128;
            let wait_ns = next_ns.saturating_sub(elapsed_ns) as u64;
            thread::sleep(Duration::from_nanos(wait_ns).min(Duration::from_millis(4)));
            continue;
        }

        while frames_written + TICK_FRAMES as u64 <= target_frames && !stop.load(Ordering::Relaxed) {
            mix.fill(0.0);
            if game_audio {
                if let Ok(mut q) = game_buf.lock() {
                    for s in mix.iter_mut() {
                        if let Some(v) = q.pop_front() {
                            *s += v;
                        } else {
                            break;
                        }
                    }
                }
            }
            if microphone {
                if let Ok(mut q) = mic_buf.lock() {
                    for s in mix.iter_mut() {
                        if let Some(v) = q.pop_front() {
                            *s = (*s + v * 0.9).clamp(-1.0, 1.0);
                        } else {
                            break;
                        }
                    }
                }
            }
            for (i, sample) in mix.iter().enumerate() {
                let b = sample.clamp(-1.0, 1.0).to_le_bytes();
                let base = i * 4;
                bytes[base..base + 4].copy_from_slice(&b);
            }
            if stdin.write_all(&bytes).is_err() {
                return;
            }
            frames_written += TICK_FRAMES as u64;
        }
        let _ = stdin.flush();
    }
}

#[cfg(windows)]
fn build_input_stream(
    device: &cpal::Device,
    queue: Arc<Mutex<std::collections::VecDeque<f32>>>,
) -> Option<cpal::Stream> {
    use cpal::traits::{DeviceTrait, StreamTrait};

    let supported = device
        .default_input_config()
        .or_else(|_| device.default_output_config())
        .ok()?;
    let in_rate = supported.sample_rate().0;
    let in_channels = supported.channels() as usize;
    let config: cpal::StreamConfig = supported.clone().into();

    let push_samples = move |data: &[f32]| {
        if in_channels == 0 {
            return;
        }
        let frames = data.len() / in_channels;
        if frames == 0 {
            return;
        }
        if let Ok(mut q) = queue.lock() {
            if in_rate == SAMPLE_RATE {
                for f in 0..frames {
                    let base = f * in_channels;
                    let l = data[base];
                    let r = if in_channels > 1 { data[base + 1] } else { l };
                    while q.len() + 2 > MAX_QUEUE_SAMPLES {
                        q.pop_front();
                        q.pop_front();
                    }
                    q.push_back(l);
                    q.push_back(r);
                }
            } else {
                let out_frames = ((frames as u64 * SAMPLE_RATE as u64) / in_rate.max(1) as u64) as usize;
                for of in 0..out_frames {
                    let src = (of * frames) / out_frames.max(1);
                    let base = src.min(frames - 1) * in_channels;
                    let l = data[base];
                    let r = if in_channels > 1 { data[base + 1] } else { l };
                    while q.len() + 2 > MAX_QUEUE_SAMPLES {
                        q.pop_front();
                        q.pop_front();
                    }
                    q.push_back(l);
                    q.push_back(r);
                }
            }
        }
    };

    let err_fn = move |_err| {};
    let stream = match supported.sample_format() {
        cpal::SampleFormat::F32 => device
            .build_input_stream(
                &config,
                move |data: &[f32], _| push_samples(data),
                err_fn,
                None,
            )
            .ok()?,
        cpal::SampleFormat::I16 => {
            let push = push_samples;
            device
                .build_input_stream(
                    &config,
                    move |data: &[i16], _| {
                        let converted: Vec<f32> = data.iter().map(|&x| x as f32 / 32768.0).collect();
                        push(&converted);
                    },
                    err_fn,
                    None,
                )
                .ok()?
        }
        cpal::SampleFormat::U16 => {
            let push = push_samples;
            device
                .build_input_stream(
                    &config,
                    move |data: &[u16], _| {
                        let converted: Vec<f32> =
                            data.iter().map(|&x| (x as f32 - 32768.0) / 32768.0).collect();
                        push(&converted);
                    },
                    err_fn,
                    None,
                )
                .ok()?
        }
        _ => return None,
    };
    stream.play().ok()?;
    Some(stream)
}

#[cfg(not(windows))]
fn run_pump(mut stdin: ChildStdin, _game_audio: bool, _microphone: bool, stop: Arc<AtomicBool>) {
    let bytes = vec![0u8; TICK_SAMPLES * 4];
    let start = Instant::now();
    let mut frames_written: u64 = 0;
    while !stop.load(Ordering::Relaxed) {
        let elapsed_ns = start.elapsed().as_nanos();
        let target_frames = ((elapsed_ns * SAMPLE_RATE as u128) / 1_000_000_000) as u64;
        if frames_written + TICK_FRAMES as u64 > target_frames {
            thread::sleep(Duration::from_millis(4));
            continue;
        }
        while frames_written + TICK_FRAMES as u64 <= target_frames && !stop.load(Ordering::Relaxed) {
            if stdin.write_all(&bytes).is_err() {
                return;
            }
            frames_written += TICK_FRAMES as u64;
        }
        let _ = stdin.flush();
    }
}
