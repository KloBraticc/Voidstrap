use std::time::Instant;

#[derive(Debug, Default, Clone, Copy)]
pub struct FrameLimit {
    next: i64,
}

impl FrameLimit {
    pub const fn new() -> Self {
        Self { next: 0 }
    }

    pub fn ready(&self, time: i64, interval: i64) -> bool {
        time >= self.next - interval / 2
    }

    pub fn allow(&mut self, time: i64, interval: i64) -> bool {
        if !self.ready(time, interval) {
            return false;
        }
        self.next = (if time > self.next + interval / 2 {
            time
        } else {
            self.next
        }) + interval;
        true
    }
}

pub const TIMING_SAMPLES: usize = 512;
pub const HISTORY_SAMPLES: usize = 60;

#[derive(Debug, Default, Clone, Copy)]
pub struct Timings {
    pub prepare_ms: f64,
    pub depth_ms: f64,
    pub present_ms: f64,
    pub menu_ms: f64,
    pub capture_age_ms: f64,
    pub capacity_wait_ms: f64,
}

#[derive(Debug, Default, Clone, Copy)]
pub struct GpuTimings {
    pub prepare_ms: f64,
    pub depth_ms: f64,
    pub effects_present_ms: f64,
}

#[derive(Debug, Clone, Copy)]
pub struct Sample {
    pub at: Instant,
    pub capture_fps: f64,
    pub program_fps: f64,
    pub fresh_fps: f64,
}

impl Default for Sample {
    fn default() -> Self {
        Self {
            at: Instant::now(),
            capture_fps: 0.0,
            program_fps: 0.0,
            fresh_fps: 0.0,
        }
    }
}

pub struct FrameStatistics {
    pub ready: bool,
    pub capture_fps: f64,
    pub program_fps: f64,
    pub fresh_fps: f64,
    pub processing_ms: f64,
    pub peak_processing_ms: f64,
    pub p95_processing_ms: f64,
    pub p99_processing_ms: f64,
    pub cpu: Timings,
    pub gpu_ready: bool,
    pub gpu_ms: f64,
    pub peak_gpu_ms: f64,
    pub p95_gpu_ms: f64,
    pub p99_gpu_ms: f64,
    pub gpu: GpuTimings,
    pub depth_fps: f64,
    pub depth_observed_ms: f64,
    pub depth_worker_ms: f64,
    pub peak_depth_observed_ms: f64,
    pub capacity_waits: u64,

    sample_start: Instant,
    capture_start: u64,
    presents: u64,
    fresh_frames: u64,
    processing_total_ms: f64,
    processing_peak_ms: f64,
    cpu_total: Timings,
    gpu_total: GpuTimings,
    gpu_count: u64,
    gpu_peak_ms: f64,
    depth_count: u64,
    depth_total_ms: f64,
    depth_worker_total_ms: f64,
    depth_peak_ms: f64,
    capacity_wait_count: u64,
    cpu_samples: [f64; TIMING_SAMPLES],
    gpu_samples: [f64; TIMING_SAMPLES],
    last_frame: Option<i64>,
    history: [Sample; HISTORY_SAMPLES],
    history_next: usize,
    history_count: usize,
}

impl Default for FrameStatistics {
    fn default() -> Self {
        Self::new(Instant::now(), 0)
    }
}

impl FrameStatistics {
    pub fn new(now: Instant, captured_frames: u64) -> Self {
        Self {
            ready: false,
            capture_fps: 0.0,
            program_fps: 0.0,
            fresh_fps: 0.0,
            processing_ms: 0.0,
            peak_processing_ms: 0.0,
            p95_processing_ms: 0.0,
            p99_processing_ms: 0.0,
            cpu: Timings::default(),
            gpu_ready: false,
            gpu_ms: 0.0,
            peak_gpu_ms: 0.0,
            p95_gpu_ms: 0.0,
            p99_gpu_ms: 0.0,
            gpu: GpuTimings::default(),
            depth_fps: 0.0,
            depth_observed_ms: 0.0,
            depth_worker_ms: 0.0,
            peak_depth_observed_ms: 0.0,
            capacity_waits: 0,
            sample_start: now,
            capture_start: captured_frames,
            presents: 0,
            fresh_frames: 0,
            processing_total_ms: 0.0,
            processing_peak_ms: 0.0,
            cpu_total: Timings::default(),
            gpu_total: GpuTimings::default(),
            gpu_count: 0,
            gpu_peak_ms: 0.0,
            depth_count: 0,
            depth_total_ms: 0.0,
            depth_worker_total_ms: 0.0,
            depth_peak_ms: 0.0,
            capacity_wait_count: 0,
            cpu_samples: [0.0; TIMING_SAMPLES],
            gpu_samples: [0.0; TIMING_SAMPLES],
            last_frame: None,
            history: [Sample::default(); HISTORY_SAMPLES],
            history_next: 0,
            history_count: 0,
        }
    }

    pub fn reset(&mut self, now: Instant, captured_frames: u64) {
        *self = Self::new(now, captured_frames);
    }

    pub fn record_present(&mut self, frame_timestamp: i64, elapsed_ms: f64, timings: Timings) {
        self.cpu_samples[self.presents as usize % TIMING_SAMPLES] = elapsed_ms;
        self.presents += 1;
        if self.last_frame != Some(frame_timestamp) {
            self.fresh_frames += 1;
        }
        self.last_frame = Some(frame_timestamp);
        self.processing_total_ms += elapsed_ms;
        self.processing_peak_ms = self.processing_peak_ms.max(elapsed_ms);
        self.cpu_total.prepare_ms += timings.prepare_ms;
        self.cpu_total.depth_ms += timings.depth_ms;
        self.cpu_total.present_ms += timings.present_ms;
        self.cpu_total.menu_ms += timings.menu_ms;
        self.cpu_total.capture_age_ms += timings.capture_age_ms;
        self.cpu_total.capacity_wait_ms += timings.capacity_wait_ms;
    }

    pub fn record_gpu(&mut self, timings: GpuTimings) {
        let elapsed_ms = timings.prepare_ms + timings.depth_ms + timings.effects_present_ms;
        self.gpu_samples[self.gpu_count as usize % TIMING_SAMPLES] = elapsed_ms;
        self.gpu_count += 1;
        self.gpu_total.prepare_ms += timings.prepare_ms;
        self.gpu_total.depth_ms += timings.depth_ms;
        self.gpu_total.effects_present_ms += timings.effects_present_ms;
        self.gpu_peak_ms = self.gpu_peak_ms.max(elapsed_ms);
    }

    pub fn record_depth(&mut self, observed_ms: f64, worker_ms: f64) {
        self.depth_count += 1;
        self.depth_total_ms += observed_ms;
        self.depth_worker_total_ms += worker_ms;
        self.depth_peak_ms = self.depth_peak_ms.max(observed_ms);
    }

    pub fn record_capacity_wait(&mut self) {
        self.capacity_wait_count += 1;
    }

    pub fn update(&mut self, now: Instant, captured_frames: u64) -> bool {
        let seconds = now.duration_since(self.sample_start).as_secs_f64();
        if seconds < 1.0 {
            return false;
        }

        self.capture_fps = (captured_frames - self.capture_start) as f64 / seconds;
        self.program_fps = self.presents as f64 / seconds;
        self.fresh_fps = self.fresh_frames as f64 / seconds;
        self.processing_ms = if self.presents > 0 {
            self.processing_total_ms / self.presents as f64
        } else {
            0.0
        };
        self.peak_processing_ms = self.processing_peak_ms;

        let (p95, p99) = percentiles(&self.cpu_samples, self.presents);
        self.p95_processing_ms = p95;
        self.p99_processing_ms = p99;

        self.cpu = if self.presents > 0 {
            let p = self.presents as f64;
            Timings {
                prepare_ms: self.cpu_total.prepare_ms / p,
                depth_ms: self.cpu_total.depth_ms / p,
                present_ms: self.cpu_total.present_ms / p,
                menu_ms: self.cpu_total.menu_ms / p,
                capture_age_ms: self.cpu_total.capture_age_ms / p,
                capacity_wait_ms: self.cpu_total.capacity_wait_ms / p,
            }
        } else {
            Timings::default()
        };

        self.gpu_ready = self.gpu_count != 0;
        self.gpu = if self.gpu_count > 0 {
            let g = self.gpu_count as f64;
            GpuTimings {
                prepare_ms: self.gpu_total.prepare_ms / g,
                depth_ms: self.gpu_total.depth_ms / g,
                effects_present_ms: self.gpu_total.effects_present_ms / g,
            }
        } else {
            GpuTimings::default()
        };
        self.gpu_ms = self.gpu.prepare_ms + self.gpu.depth_ms + self.gpu.effects_present_ms;
        self.peak_gpu_ms = self.gpu_peak_ms;

        let (gp95, gp99) = percentiles(&self.gpu_samples, self.gpu_count);
        self.p95_gpu_ms = gp95;
        self.p99_gpu_ms = gp99;

        self.depth_fps = self.depth_count as f64 / seconds;
        self.depth_observed_ms = if self.depth_count > 0 {
            self.depth_total_ms / self.depth_count as f64
        } else {
            0.0
        };
        self.depth_worker_ms = if self.depth_count > 0 {
            self.depth_worker_total_ms / self.depth_count as f64
        } else {
            0.0
        };
        self.peak_depth_observed_ms = self.depth_peak_ms;
        self.capacity_waits = self.capacity_wait_count;

        self.history[self.history_next] = Sample {
            at: now,
            capture_fps: self.capture_fps,
            program_fps: self.program_fps,
            fresh_fps: self.fresh_fps,
        };
        self.history_next = (self.history_next + 1) % HISTORY_SAMPLES;
        self.history_count = (self.history_count + 1).min(HISTORY_SAMPLES);
        self.ready = true;

        self.sample_start = now;
        self.capture_start = captured_frames;
        self.presents = 0;
        self.fresh_frames = 0;
        self.processing_total_ms = 0.0;
        self.processing_peak_ms = 0.0;
        self.cpu_total = Timings::default();
        self.gpu_total = GpuTimings::default();
        self.gpu_count = 0;
        self.depth_count = 0;
        self.capacity_wait_count = 0;
        self.gpu_peak_ms = 0.0;
        self.depth_total_ms = 0.0;
        self.depth_worker_total_ms = 0.0;
        self.depth_peak_ms = 0.0;

        true
    }

    pub fn lost_fps(&self) -> f64 {
        (self.capture_fps - self.fresh_fps).max(0.0)
    }

    pub fn loss_percent(&self) -> f64 {
        if self.capture_fps > 0.0 {
            self.lost_fps() / self.capture_fps * 100.0
        } else {
            0.0
        }
    }

    pub fn repeated_fps(&self) -> f64 {
        (self.program_fps - self.fresh_fps).max(0.0)
    }

    pub fn history_size(&self) -> usize {
        self.history_count
    }

    pub fn history_at(&self, index: usize) -> &Sample {
        let offset =
            (self.history_next + HISTORY_SAMPLES - self.history_count + index) % HISTORY_SAMPLES;
        &self.history[offset]
    }
}

fn percentiles(samples: &[f64; TIMING_SAMPLES], count: u64) -> (f64, f64) {
    let used = (count as usize).min(TIMING_SAMPLES);
    if used == 0 {
        return (0.0, 0.0);
    }
    let mut sorted = [0.0; TIMING_SAMPLES];
    sorted[..used].copy_from_slice(&samples[..used]);
    sorted[..used].sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let p95_idx = (used * 95).div_ceil(100).saturating_sub(1);
    let p99_idx = (used * 99).div_ceil(100).saturating_sub(1);
    (sorted[p95_idx], sorted[p99_idx])
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECOND: i64 = 1_000_000_000;

    fn shown(
        limit: &mut FrameLimit,
        frames: i32,
        rate: i32,
        frame_time: impl Fn(i32) -> i64,
    ) -> i32 {
        let mut count = 0;
        for frame in 0..frames {
            if limit.allow(frame_time(frame), SECOND / rate as i64) {
                count += 1;
            }
        }
        count
    }

    #[test]
    fn frame_limiter_matches_unishade_exact_behavior() {
        let steady = |rate: i32| move |frame: i32| SECOND + frame as i64 * SECOND / rate as i64;

        let mut fast = FrameLimit::new();
        assert!((shown(&mut fast, 240, 60, steady(240)) - 60).abs() <= 1);

        let mut uneven = FrameLimit::new();
        assert!((shown(&mut uneven, 144, 60, steady(144)) - 60).abs() <= 1);

        let mut slow = FrameLimit::new();
        assert_eq!(shown(&mut slow, 30, 60, steady(30)), 30);

        let mut quantized = FrameLimit::new();
        let on_refresh =
            |frame: i32| SECOND + ((frame * 144) as f64 / 60.0).ceil() as i64 * SECOND / 144;
        assert_eq!(shown(&mut quantized, 60, 60, on_refresh), 60);

        let mut paused = FrameLimit::new();
        shown(&mut paused, 240, 60, steady(240));
        let resumed = |frame: i32| 10 * SECOND + frame as i64 * SECOND / 240;
        assert!(shown(&mut paused, 24, 60, resumed) <= 7);

        let mut capacity = FrameLimit::new();
        assert!(capacity.ready(SECOND, SECOND / 60));
        assert!(capacity.ready(SECOND + 1, SECOND / 60));
        assert!(capacity.allow(SECOND + 1, SECOND / 60));
        assert!(!capacity.ready(SECOND + 2, SECOND / 60));
    }

    #[test]
    fn frame_statistics_records_and_updates() {
        let start = Instant::now();
        let mut stats = FrameStatistics::new(start, 0);

        stats.record_present(
            1,
            10.0,
            Timings {
                prepare_ms: 2.0,
                ..Default::default()
            },
        );
        stats.record_present(
            2,
            12.0,
            Timings {
                prepare_ms: 3.0,
                ..Default::default()
            },
        );
        stats.record_capacity_wait();
        stats.record_gpu(GpuTimings {
            prepare_ms: 1.0,
            depth_ms: 2.0,
            effects_present_ms: 3.0,
        });
        stats.record_depth(5.0, 4.0);

        let later = start + std::time::Duration::from_millis(1100);
        assert!(stats.update(later, 2));

        assert!(stats.ready);
        assert!(stats.capture_fps > 0.0);
        assert_eq!(stats.capacity_waits, 1);
        assert!(stats.gpu_ready);
        assert_eq!(stats.history_size(), 1);
        assert_eq!(stats.loss_percent(), 0.0);
    }
}
