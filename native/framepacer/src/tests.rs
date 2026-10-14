use crate::align::Aligner;
use crate::cadence::{self, Cadence, CadenceInput};
use crate::quality::{self, Quality};

fn feed(cadence: &mut Cadence, dt: f64) -> crate::cadence::CadenceResult {
    cadence.update(&CadenceInput { dt, src_is_capture: 1, ..Default::default() })
}

#[test]
fn steady_sixty_settles_on_sixty() {
    let mut c = Cadence::default();
    let mut last = Default::default();
    for _ in 0..60 {
        last = feed(&mut c, 1000.0 / 60.0);
    }
    assert!((c.ema_interval_ms - 1000.0 / 60.0).abs() < 1e-9);
    assert!((last.base_fps - 60.0).abs() < 1e-6);
    assert_eq!(last.reset_history, 0);
}

#[test]
fn sixty_on_two_forty_generates_three() {
    let mut phase = 0.0;
    assert_eq!(cadence::target_total(1000.0 / 60.0, 240.0, 1, false, &mut phase), 4);
    assert_eq!(cadence::target_total(1000.0 / 60.0, 144.0, 1, false, &mut phase), 2);
    assert_eq!(cadence::target_total(1000.0 / 30.0, 144.0, 1, false, &mut phase), 4);
}

#[test]
fn jitter_stays_close_to_true_rate() {
    let mut c = Cadence::default();
    let mut seed = 12345u64;
    for _ in 0..600 {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        let jitter = ((seed >> 33) as f64 / (1u64 << 31) as f64) * 6.0 - 3.0;
        feed(&mut c, 1000.0 / 60.0 + jitter);
    }
    assert!((c.ema_interval_ms - 1000.0 / 60.0).abs() < 1.0, "ema {}", c.ema_interval_ms);
}

#[test]
fn single_spike_is_ignored() {
    let mut c = Cadence::default();
    for _ in 0..30 {
        feed(&mut c, 1000.0 / 60.0);
    }
    feed(&mut c, 80.0);
    assert!((c.ema_interval_ms - 1000.0 / 60.0).abs() < 0.01, "ema {}", c.ema_interval_ms);
}

#[test]
fn dropped_capture_is_normalized() {
    let mut c = Cadence::default();
    for _ in 0..30 {
        feed(&mut c, 1000.0 / 60.0);
    }
    let r = c.update(&CadenceInput {
        dt: 2000.0 / 60.0,
        cap_ms: 1000.0 / 60.0,
        drained_queued_capture: 1,
        skipped_since_last_use: 1,
        src_is_capture: 1,
        ..Default::default()
    });
    assert_eq!(r.reset_history, 1);
    assert!((c.ema_interval_ms - 1000.0 / 60.0).abs() < 0.01);
}

#[test]
fn low_frame_cap_locks_cadence() {
    let mut c = Cadence::default();
    for _ in 0..5 {
        c.update(&CadenceInput { dt: 93.0, cap_ms: 100.0, fps_cap: 10, src_is_capture: 1, ..Default::default() });
    }
    assert!((c.ema_interval_ms - 100.0).abs() < 1e-9);
}

#[test]
fn sustained_rate_change_is_followed() {
    let mut c = Cadence::default();
    for _ in 0..30 {
        feed(&mut c, 1000.0 / 60.0);
    }
    for _ in 0..120 {
        feed(&mut c, 1000.0 / 40.0);
    }
    assert!((c.ema_interval_ms - 25.0).abs() < 0.5, "ema {}", c.ema_interval_ms);
}

#[test]
fn aligned_presents_move_forward_on_the_vblank_grid() {
    let mut a = Aligner { last_aligned_ms: 0.0, vb_period_ms: 1000.0 / 240.0, vb_base_ms: 3.0 };
    let mut target = 100.0;
    let mut previous = 0.0;
    for i in 0..200 {
        let aligned = a.align(target, 1000.0 / 240.0, 240.0);
        assert!(aligned > previous, "step {} went backwards", i);
        if i == 0 {
            assert!(aligned >= target - 1.5);
            let phase = (aligned + (1000.0 / 240.0 * 0.25f64).min(1.5) - 3.0) / (1000.0 / 240.0);
            assert!((phase - phase.round()).abs() < 1e-6);
        }
        previous = aligned;
        target = aligned + 1000.0 / 240.0;
    }
}

#[test]
fn vrr_skips_grid_snapping() {
    let mut a = Aligner { last_aligned_ms: 0.0, vb_period_ms: 8.7, vb_base_ms: 0.0 };
    assert_eq!(a.align(123.456, 4.0, 144.0), 123.456);
}

#[test]
fn no_vblank_info_passes_through() {
    let mut a = Aligner::default();
    assert_eq!(a.align(50.0, 4.0, 240.0), 50.0);
}

#[test]
fn heavy_misses_lower_quality_then_stability_restores_it() {
    let mut q = Quality { fg_quality: 2, auto_generated_limit: quality::AUTO_GENERATED_CEILING, six_load_limit: 5, ..Default::default() };
    let mut now = 100.0;
    for i in 0..120 {
        q.record_slot(i % 2 == 0, now);
    }
    let events = q.consider(0, false, 0, 0, now);
    assert_eq!(events, quality::EVENT_LOWER_QUALITY_PACING);
    assert_eq!(q.fg_quality, 1);
    for _ in 0..4 {
        now += 3.0;
        for _ in 0..120 {
            q.record_slot(false, now);
        }
        q.consider(0, false, 0, 0, now);
    }
    assert_eq!(q.fg_quality, 2);
}

#[test]
fn capture_backpressure_lowers_quality_and_rate_limits() {
    let mut q = Quality { fg_quality: 2, auto_generated_limit: 7, six_load_limit: 5, ..Default::default() };
    assert_eq!(q.consider(0, true, 1, 0, 10.0), quality::EVENT_LOWER_QUALITY_CAPTURE);
    assert_eq!(q.consider(0, true, 1, 0, 11.0), 0);
    assert_eq!(q.consider(0, true, 1, 0, 13.5), quality::EVENT_FAST_FLOW_CAPTURE);
    assert_eq!(q.consider(0, true, 1, 0, 17.0), quality::EVENT_AUTO_LOWER_CAPTURE);
    assert_eq!(q.auto_generated_limit, 6);
}

#[test]
fn source_regression_needs_a_held_drop() {
    let mut q = Quality::default();
    assert_eq!(q.source_health(120.0, 1.0), 0);
    assert_eq!(q.source_health(60.0, 2.0), 0);
    assert_eq!(q.source_health(60.0, 4.0), 0);
    assert_eq!(q.source_health(60.0, 5.5), 2);
    assert_eq!(q.source_health(60.0, 6.0), 1);
    assert_eq!(q.source_health(119.0, 7.0), 0);
    assert_eq!(q.source_regressed, 0);
}

#[test]
fn fractional_cadence_averages_out() {
    let mut phase = 0.0;
    let mut sum = 0;
    for _ in 0..1000 {
        sum += cadence::next_total(20.0, 144.0, &mut phase);
    }
    assert!((sum as f64 / 1000.0 - 2.88).abs() < 0.01, "average {}", sum as f64 / 1000.0);
}

#[test]
fn helpers_match_expected_values() {
    assert_eq!(cadence::normalize_interval(33.3, 16.6), 16.65);
    assert_eq!(cadence::normalize_interval(20.0, 16.6), 20.0);
    assert!(cadence::is_source_stutter(80.0, 16.6));
    assert!(!cadence::is_source_stutter(60.0, 16.6));
    assert!(cadence::is_slot_overdue(10.0, 20.0, 4.0));
    assert_eq!(cadence::first_unshown_slot(0, 4), 1);
    assert_eq!(cadence::output_slot_ms(16.0, 240.0, 4, false), 4.166666666666667);
    assert_eq!(cadence::median_interval(&[5.0, 1.0, 3.0]), 3.0);
    assert_eq!(cadence::max_generated_for_cadence(1, 0.5), 3);
    assert_eq!(cadence::required_cadence_samples(70.0), 1);
}

#[test]
fn roblox_skipping_frames_is_paced_at_its_real_rate() {
    let mut c = Cadence::default();
    let mut resets = 0;
    for i in 0..300 {
        let dt = if i % 3 == 2 { 25.0 } else { 12.5 };
        let r = c.update(&CadenceInput { dt, cap_ms: 12.5, fps_cap: 80, src_is_capture: 1, ..Default::default() });
        resets += r.reset_history;
    }
    assert_eq!(resets, 0);
    assert!((c.ema_interval_ms - 50.0 / 3.0).abs() < 1.0, "ema {}", c.ema_interval_ms);
    let mut phase = 0.0;
    assert_eq!(cadence::target_total(c.ema_interval_ms, 240.0, 1, false, &mut phase), 3);
}

#[test]
fn real_dropped_captures_are_still_normalized() {
    let mut c = Cadence::default();
    for _ in 0..30 {
        c.update(&CadenceInput { dt: 12.5, cap_ms: 12.5, fps_cap: 80, src_is_capture: 1, ..Default::default() });
    }
    let r = c.update(&CadenceInput { dt: 25.0, cap_ms: 12.5, fps_cap: 80, drained_queued_capture: 1, skipped_since_last_use: 1, src_is_capture: 1, ..Default::default() });
    assert_eq!(r.reset_history, 1);
    assert!((c.ema_interval_ms - 12.5).abs() < 0.01);
}

#[test]
fn pacing_reductions_wait_for_their_effect() {
    let mut q = Quality { fg_quality: 1, auto_generated_limit: 7, six_load_limit: 5, ..Default::default() };
    let mut now = 100.0;
    let mut reductions = 0;
    for _ in 0..8 {
        for i in 0..120 {
            q.record_slot(i % 2 == 0, now);
        }
        if q.consider(1, false, 1, 2, now) == quality::EVENT_AUTO_LOWER_PACING {
            reductions += 1;
        }
        now += 0.5;
    }
    assert_eq!(reductions, 1);
}

#[test]
fn auto_ceiling_drops_below_what_is_in_use() {
    let mut q = Quality { fg_quality: 0, auto_generated_limit: 7, six_load_limit: 5, ..Default::default() };
    for i in 0..120 {
        q.record_slot(i % 2 == 0, 50.0);
    }
    assert_eq!(q.consider(0, false, 1, 2, 50.0), quality::EVENT_AUTO_LOWER_PACING);
    assert_eq!(q.auto_generated_limit, 1);
}

#[test]
fn dropped_capture_below_the_cap_uses_the_measured_rate() {
    let mut c = Cadence::default();
    for _ in 0..30 {
        c.update(&CadenceInput { dt: 50.0 / 3.0, cap_ms: 12.5, fps_cap: 80, src_is_capture: 1, ..Default::default() });
    }
    let count = c.count;
    c.update(&CadenceInput { dt: 100.0 / 3.0, cap_ms: 12.5, fps_cap: 80, drained_queued_capture: 1, skipped_since_last_use: 1, src_is_capture: 1, ..Default::default() });
    assert_eq!(c.count, count);
    assert!((c.ema_interval_ms - 50.0 / 3.0).abs() < 0.01, "ema {}", c.ema_interval_ms);
}

#[test]
fn skipping_every_other_capture_still_measures_the_source_rate() {
    let mut c = Cadence::default();
    for _ in 0..20 {
        c.update(&CadenceInput { dt: 1000.0 / 55.0, cap_ms: 12.5, fps_cap: 80, src_is_capture: 1, ..Default::default() });
    }
    for _ in 0..60 {
        c.update(&CadenceInput { dt: 2000.0 / 55.0, cap_ms: 12.5, fps_cap: 80, drained_queued_capture: 1, skipped_since_last_use: 1, src_is_capture: 1, ..Default::default() });
    }
    assert!((c.ema_interval_ms - 1000.0 / 55.0).abs() < 0.5, "ema {}", c.ema_interval_ms);
}
