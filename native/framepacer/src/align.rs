use crate::num::{clamp, max, min, round, to_int};

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Aligner {
    pub last_aligned_ms: f64,
    pub vb_period_ms: f64,
    pub vb_base_ms: f64,
}

impl Aligner {
    pub fn align(&mut self, target_ms: f64, slot_ms: f64, refresh_hz: f64) -> f64 {
        if self.vb_period_ms <= 0.0 {
            return target_ms;
        }
        let nominal_slot_ms = 1000.0 / max(1.0, refresh_hz);
        let period_ratio = self.vb_period_ms / max(0.1, nominal_slot_ms);
        let likely_vrr = (period_ratio - round(period_ratio)).abs() > 0.15
            || (refresh_hz >= 48.0 && (self.vb_period_ms - nominal_slot_ms).abs() > 1.5);
        if likely_vrr {
            return target_ms;
        }
        let mut grid = self.vb_period_ms;
        if slot_ms < self.vb_period_ms * 0.9 {
            let sub = to_int(round(self.vb_period_ms / max(0.1, slot_ms))).max(1);
            grid = self.vb_period_ms / sub as f64;
        }
        let lead = min(1.5, grid * 0.25);
        let aligned = if self.last_aligned_ms <= 0.0 {
            self.vb_base_ms + ((target_ms + lead - self.vb_base_ms) / grid).ceil() * grid - lead
        } else {
            let nearest = self.vb_base_ms + round((target_ms + lead - self.vb_base_ms) / grid) * grid - lead;
            let mut aligned = target_ms + clamp(nearest - target_ms, -0.12, 0.12);
            if aligned <= self.last_aligned_ms + grid * 0.5 {
                aligned = self.last_aligned_ms + grid;
            }
            aligned
        };
        self.last_aligned_ms = aligned;
        aligned
    }
}
