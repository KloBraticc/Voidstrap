pub const AUTO_GENERATED_CEILING: i32 = 7;
pub const MINIMUM_SIX_GENERATED_LOAD: i32 = 2;
const SOURCE_REGRESSION_RATIO: f64 = 0.7;
const SOURCE_REGRESSION_FLOOR: f64 = 30.0;
const SOURCE_PEAK_DECAY: f64 = 0.002;
const SOURCE_REGRESSION_HOLD_SEC: f64 = 3.0;
const PACING_REDUCE_COOLDOWN_SEC: f64 = 2.0;

pub const EVENT_FAST_FLOW_CAPTURE: u32 = 1;
pub const EVENT_LOWER_QUALITY_CAPTURE: u32 = 2;
pub const EVENT_SIX_LOWER_CAPTURE: u32 = 3;
pub const EVENT_AUTO_LOWER_CAPTURE: u32 = 4;
pub const EVENT_AUTO_RESTORE_QUIET: u32 = 5;
pub const EVENT_FAST_FLOW_PACING: u32 = 6;
pub const EVENT_LOWER_QUALITY_PACING: u32 = 7;
pub const EVENT_SIX_LOWER_PACING: u32 = 8;
pub const EVENT_AUTO_LOWER_PACING: u32 = 9;
pub const EVENT_AUTO_RESTORE_STABLE: u32 = 10;
pub const EVENT_SIX_RESTORE_STABLE: u32 = 11;
pub const EVENT_RAISE_QUALITY_TOP: u32 = 12;
pub const EVENT_RAISE_QUALITY: u32 = 13;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Quality {
    pub fg_quality: i32,
    pub auto_generated_limit: i32,
    pub six_load_limit: i32,
    pub slot_window: i32,
    pub miss_window: i32,
    pub stable_slots: i32,
    pub source_regressed: i32,
    pub reserved: i32,
    pub last_change_sec: f64,
    pub backpressure_until_sec: f64,
    pub last_miss_sec: f64,
    pub source_fps_peak: f64,
    pub source_regressed_since_sec: f64,
    pub missed_slots_total: i64,
}

fn lowered_auto_limit(limit: i32, current_generated: i32) -> i32 {
    let effective = if current_generated > 0 { limit.min(current_generated) } else { limit };
    (effective - 1).max(1)
}

fn push(events: &mut u32, event: u32) {
    *events = if *events == 0 { event } else { *events | (event << 8) };
}

impl Quality {
    pub fn record_slot(&mut self, missed: bool, now_sec: f64) {
        self.slot_window = self.slot_window.wrapping_add(1);
        if !missed {
            return;
        }
        self.last_miss_sec = now_sec;
        self.miss_window = self.miss_window.wrapping_add(1);
        self.missed_slots_total = self.missed_slots_total.wrapping_add(1);
    }

    pub fn source_health(&mut self, base_fps: f64, now_sec: f64) -> i32 {
        if base_fps < 1.0 {
            return 0;
        }
        if base_fps > self.source_fps_peak {
            self.source_fps_peak = base_fps;
            return 0;
        }
        self.source_fps_peak += (base_fps - self.source_fps_peak) * SOURCE_PEAK_DECAY;
        if self.source_fps_peak < SOURCE_REGRESSION_FLOOR {
            self.source_regressed_since_sec = 0.0;
            self.source_regressed = 0;
            return 0;
        }
        if base_fps >= self.source_fps_peak * SOURCE_REGRESSION_RATIO {
            self.source_regressed_since_sec = 0.0;
            self.source_regressed = 0;
            return 0;
        }
        if self.source_regressed_since_sec <= 0.0 {
            self.source_regressed_since_sec = now_sec;
            return 0;
        }
        if now_sec - self.source_regressed_since_sec < SOURCE_REGRESSION_HOLD_SEC {
            return 0;
        }
        if self.source_regressed == 0 {
            self.source_regressed = 1;
            return 2;
        }
        1
    }

    pub fn consider(&mut self, minimum_quality: i32, capture_backpressure: bool, active_mode: i32, current_generated: i32, now_sec: f64) -> u32 {
        let mut events = 0u32;
        if capture_backpressure {
            self.backpressure_until_sec = now_sec + 5.0;
            self.stable_slots = 0;
            if now_sec - self.last_change_sec >= 3.0 {
                self.last_change_sec = now_sec;
                if self.fg_quality > minimum_quality {
                    self.fg_quality -= 1;
                    push(&mut events, if self.fg_quality == 0 { EVENT_FAST_FLOW_CAPTURE } else { EVENT_LOWER_QUALITY_CAPTURE });
                } else if active_mode == 5 && self.six_load_limit > MINIMUM_SIX_GENERATED_LOAD {
                    self.six_load_limit -= 1;
                    push(&mut events, EVENT_SIX_LOWER_CAPTURE);
                } else if active_mode == 1 && self.auto_generated_limit > 1 {
                    self.auto_generated_limit = lowered_auto_limit(self.auto_generated_limit, current_generated);
                    push(&mut events, EVENT_AUTO_LOWER_CAPTURE);
                }
            }
            return events;
        }
        if active_mode == 1
            && self.auto_generated_limit < AUTO_GENERATED_CEILING
            && self.source_regressed == 0
            && now_sec - self.last_change_sec >= 12.0
            && now_sec - self.last_miss_sec >= 8.0
            && now_sec >= self.backpressure_until_sec
        {
            self.last_change_sec = now_sec;
            self.auto_generated_limit += 1;
            push(&mut events, EVENT_AUTO_RESTORE_QUIET);
        }
        if self.slot_window < 120 {
            return events;
        }
        let slots = self.slot_window;
        let misses = self.miss_window;
        self.slot_window = 0;
        self.miss_window = 0;
        if misses.wrapping_mul(4) > slots {
            self.stable_slots = 0;
            if now_sec - self.last_change_sec < PACING_REDUCE_COOLDOWN_SEC {
                return events;
            }
            self.last_change_sec = now_sec;
            if self.fg_quality > minimum_quality {
                self.fg_quality -= 1;
                push(&mut events, if self.fg_quality == 0 { EVENT_FAST_FLOW_PACING } else { EVENT_LOWER_QUALITY_PACING });
            } else if active_mode == 5 && self.six_load_limit > MINIMUM_SIX_GENERATED_LOAD {
                self.six_load_limit -= 1;
                push(&mut events, EVENT_SIX_LOWER_PACING);
            } else if active_mode == 1 && self.auto_generated_limit > 1 {
                self.auto_generated_limit = lowered_auto_limit(self.auto_generated_limit, current_generated);
                push(&mut events, EVENT_AUTO_LOWER_PACING);
            }
            return events;
        }
        if misses == 0 {
            self.stable_slots = self.stable_slots.wrapping_add(slots);
        } else {
            self.stable_slots = 0;
        }
        if self.stable_slots < 240 || now_sec - self.last_change_sec < 6.0 || now_sec < self.backpressure_until_sec {
            return events;
        }
        self.stable_slots = 0;
        self.last_change_sec = now_sec;
        if active_mode == 1 {
            if self.auto_generated_limit < AUTO_GENERATED_CEILING && self.source_regressed == 0 {
                self.auto_generated_limit += 1;
                push(&mut events, EVENT_AUTO_RESTORE_STABLE);
            }
            return events;
        }
        if active_mode == 5 && self.six_load_limit < 5 {
            self.six_load_limit += 1;
            push(&mut events, EVENT_SIX_RESTORE_STABLE);
        } else if self.fg_quality < 2 {
            self.fg_quality += 1;
            push(&mut events, if self.fg_quality >= 2 { EVENT_RAISE_QUALITY_TOP } else { EVENT_RAISE_QUALITY });
        }
        events
    }
}
