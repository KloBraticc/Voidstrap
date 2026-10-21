
pub const DEPTH_SIZES: [u32; 4] = [924, 518, 392, 266];
pub const DEFAULT_DEPTH_SIZE: u32 = 518;

pub fn normalize_depth_size(size: u32) -> u32 {
    if !(140..=DEPTH_SIZES[0]).contains(&size) {
        DEFAULT_DEPTH_SIZE
    } else {
        size / 14 * 14
    }
}

pub fn depth_model_dimensions(
    source_width: u32,
    source_height: u32,
    detail: u32,
) -> Option<(u32, u32)> {
    if source_width == 0 || source_height == 0 {
        return None;
    }

    let detail = normalize_depth_size(detail);
    let (long_side, short_side) = if source_width >= source_height {
        (source_width, source_height)
    } else {
        (source_height, source_width)
    };
    let scaled_short = (detail as f64 * short_side as f64 / long_side as f64 / 14.0).round();
    let short_side = ((scaled_short as u32).saturating_mul(14)).max(14);
    Some(if source_width >= source_height {
        (detail, short_side)
    } else {
        (short_side, detail)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn depth_quality_validation_matches_legacy_behavior() {
        assert_eq!(normalize_depth_size(924), 924);
        assert_eq!(normalize_depth_size(518), 518);
        assert_eq!(normalize_depth_size(517), 504);
        assert_eq!(normalize_depth_size(139), DEFAULT_DEPTH_SIZE);
        assert_eq!(normalize_depth_size(925), DEFAULT_DEPTH_SIZE);
    }

    #[test]
    fn model_dimensions_preserve_orientation_and_align_to_fourteen() {
        assert_eq!(depth_model_dimensions(1920, 1080, 518), Some((518, 294)));
        assert_eq!(depth_model_dimensions(1080, 1920, 518), Some((294, 518)));
        assert_eq!(depth_model_dimensions(1, 100_000, 266), Some((14, 266)));
        assert_eq!(depth_model_dimensions(0, 1080, 518), None);
        assert_eq!(depth_model_dimensions(1920, 0, 518), None);
    }
}
