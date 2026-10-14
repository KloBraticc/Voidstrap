pub mod align;
pub mod cadence;
pub mod num;
pub mod quality;

#[cfg(test)]
mod tests;

use align::Aligner;
use cadence::{Cadence, CadenceInput, CadenceResult};
use quality::Quality;

pub const ABI_VERSION: u32 = 2;

#[repr(C)]
pub struct Layout {
    pub abi_version: u32,
    pub cadence_size: u32,
    pub cadence_input_size: u32,
    pub cadence_result_size: u32,
    pub aligner_size: u32,
    pub quality_size: u32,
}

#[no_mangle]
pub extern "C" fn vfp_layout() -> Layout {
    Layout {
        abi_version: ABI_VERSION,
        cadence_size: std::mem::size_of::<Cadence>() as u32,
        cadence_input_size: std::mem::size_of::<CadenceInput>() as u32,
        cadence_result_size: std::mem::size_of::<CadenceResult>() as u32,
        aligner_size: std::mem::size_of::<Aligner>() as u32,
        quality_size: std::mem::size_of::<Quality>() as u32,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vfp_cadence_reset(state: *mut Cadence) {
    if let Some(state) = state.as_mut() {
        *state = Cadence::default();
    }
}

#[no_mangle]
pub unsafe extern "C" fn vfp_cadence_update(state: *mut Cadence, input: *const CadenceInput, result: *mut CadenceResult) -> i32 {
    let (Some(state), Some(input), Some(result)) = (state.as_mut(), input.as_ref(), result.as_mut()) else {
        return 0;
    };
    *result = state.update(input);
    1
}

#[no_mangle]
pub unsafe extern "C" fn vfp_align(state: *mut Aligner, target_ms: f64, slot_ms: f64, refresh_hz: f64) -> f64 {
    match state.as_mut() {
        Some(state) => state.align(target_ms, slot_ms, refresh_hz),
        None => target_ms,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vfp_quality_record_slot(state: *mut Quality, missed: i32, now_sec: f64) {
    if let Some(state) = state.as_mut() {
        state.record_slot(missed != 0, now_sec);
    }
}

#[no_mangle]
pub unsafe extern "C" fn vfp_quality_consider(state: *mut Quality, minimum_quality: i32, capture_backpressure: i32, active_mode: i32, current_generated: i32, now_sec: f64) -> u32 {
    match state.as_mut() {
        Some(state) => state.consider(minimum_quality, capture_backpressure != 0, active_mode, current_generated, now_sec),
        None => 0,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vfp_quality_source_health(state: *mut Quality, base_fps: f64, now_sec: f64) -> i32 {
    match state.as_mut() {
        Some(state) => state.source_health(base_fps, now_sec),
        None => 0,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vfp_target_total(frame_ms: f64, refresh_hz: f64, mode: i32, uncap: i32, phase: *mut f64) -> i32 {
    let mut local = 0.0;
    let phase = match phase.as_mut() {
        Some(phase) => phase,
        None => &mut local,
    };
    cadence::target_total(frame_ms, refresh_hz, mode, uncap != 0, phase)
}

#[no_mangle]
pub extern "C" fn vfp_max_generated_for(mode: i32) -> i32 {
    cadence::max_generated_for(mode)
}

#[no_mangle]
pub extern "C" fn vfp_max_generated_for_cadence(mode: i32, consume_ratio: f64) -> i32 {
    cadence::max_generated_for_cadence(mode, consume_ratio)
}

#[no_mangle]
pub extern "C" fn vfp_required_cadence_samples(frame_ms: f64) -> i32 {
    cadence::required_cadence_samples(frame_ms)
}

#[no_mangle]
pub extern "C" fn vfp_first_unshown_slot(filled: i32, total: i32) -> i32 {
    cadence::first_unshown_slot(filled, total)
}

#[no_mangle]
pub extern "C" fn vfp_normalize_interval(measured_ms: f64, expected_ms: f64) -> f64 {
    cadence::normalize_interval(measured_ms, expected_ms)
}

#[no_mangle]
pub unsafe extern "C" fn vfp_median_interval(samples: *const f64, count: i32) -> f64 {
    if samples.is_null() || count <= 0 {
        return 0.0;
    }
    cadence::median_interval(std::slice::from_raw_parts(samples, count as usize))
}

#[no_mangle]
pub extern "C" fn vfp_is_slot_overdue(target_ms: f64, now_ms: f64, slot_ms: f64) -> i32 {
    cadence::is_slot_overdue(target_ms, now_ms, slot_ms) as i32
}

#[no_mangle]
pub extern "C" fn vfp_is_source_stutter(elapsed_ms: f64, expected_ms: f64) -> i32 {
    cadence::is_source_stutter(elapsed_ms, expected_ms) as i32
}

#[no_mangle]
pub extern "C" fn vfp_output_slot_ms(source_interval_ms: f64, refresh_hz: f64, total: i32, uncap: i32) -> f64 {
    cadence::output_slot_ms(source_interval_ms, refresh_hz, total, uncap != 0)
}
