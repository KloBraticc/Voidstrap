use crate::num::{clamp, max, round, to_int};

pub const RING: usize = 15;
const OUTLIER_HIGH: f64 = 2.5;
const OUTLIER_LOW: f64 = 0.4;
const TRIM_FROM: usize = 5;

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Cadence {
    pub ring: [f64; RING],
    pub head: i32,
    pub count: i32,
    pub outlier_run: i32,
    pub reserved: i32,
    pub ema_interval_ms: f64,
    pub source_interval_ms: f64,
    pub consume_ratio_ema: f64,
}

impl Default for Cadence {
    fn default() -> Self {
        Cadence {
            ring: [0.0; RING],
            head: 0,
            count: 0,
            outlier_run: 0,
            reserved: 0,
            ema_interval_ms: 0.0,
            source_interval_ms: 0.0,
            consume_ratio_ema: 1.0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CadenceInput {
    pub dt: f64,
    pub cap_ms: f64,
    pub capture_gap_ema: f64,
    pub skipped_since_last_use: i64,
    pub fps_cap: i32,
    pub drained_queued_capture: i32,
    pub src_is_capture: i32,
    pub reserved: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct CadenceResult {
    pub base_fps: f64,
    pub source_fps: f64,
    pub reset_history: i32,
    pub reserved: i32,
}

pub fn max_generated_for(mode: i32) -> i32 {
    const MAX_GENERATED: [i32; 2] = [0, 7];
    MAX_GENERATED[crate::num::clamp_i32(mode, 0, 1) as usize]
}

pub fn max_generated_for_cadence(mode: i32, consume_ratio: f64) -> i32 {
    let maximum = max_generated_for(mode);
    if maximum <= 0 || consume_ratio >= 0.9 {
        return maximum;
    }
    crate::num::clamp_i32(to_int((maximum as f64 * consume_ratio).floor()), 0, maximum)
}

pub fn required_cadence_samples(frame_ms: f64) -> i32 {
    if frame_ms >= 60.0 {
        1
    } else {
        2
    }
}

pub fn next_total(frame_ms: f64, refresh_hz: f64, phase: &mut f64) -> i32 {
    let mut slots = frame_ms * refresh_hz / 1000.0;
    let nearest = round(slots);
    if nearest >= 1.0 && (slots - nearest).abs() <= 0.03 {
        slots = nearest;
    }
    *phase += slots;
    let total = to_int(*phase).max(1);
    *phase = clamp(*phase - total as f64, 0.0, 1.0);
    total
}

pub fn target_total(frame_ms: f64, refresh_hz: f64, mode: i32, uncap: bool, phase: &mut f64) -> i32 {
    let maximum = max_generated_for_cadence(mode, 1.0) + 1;
    if mode == 1 && !uncap {
        let slots = frame_ms * refresh_hz / 1000.0;
        let stable_total = to_int((slots + 0.02).floor());
        *phase = 0.0;
        return crate::num::clamp_i32(stable_total, 1, maximum);
    }
    if uncap {
        maximum
    } else {
        maximum.min(next_total(frame_ms, refresh_hz, phase))
    }
}

pub fn first_unshown_slot(filled: i32, total: i32) -> i32 {
    crate::num::clamp_i32(filled.wrapping_add(1), 1, total)
}

pub fn normalize_interval(measured_ms: f64, expected_ms: f64) -> f64 {
    if expected_ms <= 4.0 {
        return measured_ms;
    }
    let multiple = round(measured_ms / expected_ms);
    if multiple < 2.0 || multiple > 4.0 {
        return measured_ms;
    }
    let normalized = measured_ms / multiple;
    if (normalized - expected_ms).abs() <= expected_ms * 0.15 {
        normalized
    } else {
        measured_ms
    }
}

pub fn median_interval(samples: &[f64]) -> f64 {
    if samples.is_empty() {
        return 0.0;
    }
    let count = samples.len().min(RING);
    let mut sorted = [0.0f64; RING];
    sorted[..count].copy_from_slice(&samples[..count]);
    sorted[..count].sort_by(|a, b| a.total_cmp(b));
    sorted[count / 2]
}

pub fn trimmed_mean(samples: &[f64]) -> f64 {
    if samples.is_empty() {
        return 0.0;
    }
    let count = samples.len().min(RING);
    let mut sorted = [0.0f64; RING];
    sorted[..count].copy_from_slice(&samples[..count]);
    sorted[..count].sort_by(|a, b| a.total_cmp(b));
    let kept = if count >= TRIM_FROM { &sorted[1..count - 1] } else { &sorted[..count] };
    kept.iter().sum::<f64>() / kept.len() as f64
}

pub fn is_slot_overdue(target_ms: f64, now_ms: f64, slot_ms: f64) -> bool {
    target_ms < now_ms - max(slot_ms, 2.0)
}

pub fn is_source_stutter(elapsed_ms: f64, expected_ms: f64) -> bool {
    expected_ms > 4.0 && elapsed_ms >= max(80.0, expected_ms * 4.0)
}

pub fn output_slot_ms(source_interval_ms: f64, refresh_hz: f64, total: i32, uncap: bool) -> f64 {
    let refresh_interval_ms = 1000.0 / max(1.0, refresh_hz);
    if source_interval_ms > 0.5 && total > 0 {
        if uncap {
            source_interval_ms / total as f64
        } else {
            max(refresh_interval_ms, source_interval_ms / total as f64)
        }
    } else {
        refresh_interval_ms
    }
}

impl Cadence {
    pub fn stable_interval_ms(&self) -> f64 {
        if self.count == 0 {
            return self.ema_interval_ms;
        }
        let count = (self.count.max(0) as usize).min(RING);
        trimmed_mean(&self.ring[..count])
    }

    pub fn update(&mut self, input: &CadenceInput) -> CadenceResult {
        let measured_dt = clamp(input.dt, 2.0, 400.0);
        let drained = input.drained_queued_capture != 0;
        let normalization_ms = if !drained {
            0.0
        } else if self.count >= 3 && self.ema_interval_ms > 4.0 {
            self.ema_interval_ms
        } else if input.cap_ms > 4.0 {
            input.cap_ms
        } else {
            0.0
        };
        let mut clamped_dt = if normalization_ms > 4.0 {
            normalize_interval(measured_dt, normalization_ms)
        } else {
            measured_dt
        };
        let cadence_locked = input.fps_cap >= 5 && input.fps_cap <= 15;
        if cadence_locked {
            clamped_dt = input.cap_ms;
        }
        let normalized_dropped_gap = drained && (clamped_dt - measured_dt).abs() > 0.01;
        if normalized_dropped_gap
            && self.ema_interval_ms > 0.0
            && (self.ema_interval_ms - clamped_dt).abs() > clamped_dt * 0.25
        {
            self.count = 0;
            self.head = 0;
            self.outlier_run = 0;
        }
        let reset_history = !cadence_locked && (clamped_dt - measured_dt).abs() > 0.01;
        if self.count >= 3 && self.ema_interval_ms > 4.0 {
            let outlier = clamped_dt > self.ema_interval_ms * OUTLIER_HIGH || clamped_dt < self.ema_interval_ms * OUTLIER_LOW;
            if outlier && self.outlier_run < 5 {
                self.outlier_run += 1;
                clamped_dt = self.ema_interval_ms;
            } else {
                self.outlier_run = 0;
            }
        }
        let head = (self.head.max(0) as usize) % RING;
        self.ring[head] = clamped_dt;
        self.head = ((head + 1) % RING) as i32;
        if self.count < RING as i32 {
            self.count += 1;
        }
        let stable = self.stable_interval_ms();
        if self.count < 2 {
            self.ema_interval_ms = if input.cap_ms > 4.0 { input.cap_ms } else { stable };
        } else if self.count == 2 || self.ema_interval_ms <= 0.0 {
            self.ema_interval_ms = stable;
        } else {
            self.ema_interval_ms = self.ema_interval_ms * 0.85 + stable * 0.15;
        }
        if self.ema_interval_ms <= 0.5 && input.capture_gap_ema > 0.5 {
            self.ema_interval_ms = input.capture_gap_ema;
        }
        let base_fps = if self.ema_interval_ms > 0.5 { 1000.0 / self.ema_interval_ms } else { 0.0 };
        let true_interval_ms = if input.src_is_capture != 0 && input.skipped_since_last_use > 0 {
            clamp(measured_dt / (input.skipped_since_last_use.wrapping_add(1)) as f64, 2.0, 400.0)
        } else {
            self.ema_interval_ms
        };
        self.source_interval_ms = if self.source_interval_ms <= 0.5 {
            true_interval_ms
        } else {
            self.source_interval_ms * 0.85 + true_interval_ms * 0.15
        };
        let source_fps = if self.source_interval_ms > 0.5 { 1000.0 / self.source_interval_ms } else { base_fps };
        let consume_ratio = if source_fps > 0.5 && base_fps > 0.5 {
            clamp(base_fps / source_fps, 0.05, 1.0)
        } else {
            1.0
        };
        self.consume_ratio_ema = if self.consume_ratio_ema <= 0.0 {
            consume_ratio
        } else {
            self.consume_ratio_ema * 0.8 + consume_ratio * 0.2
        };
        CadenceResult {
            base_fps,
            source_fps,
            reset_history: reset_history as i32,
            reserved: 0,
        }
    }
}
