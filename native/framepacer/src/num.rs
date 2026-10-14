pub fn clamp(value: f64, low: f64, high: f64) -> f64 {
    if value < low {
        low
    } else if value > high {
        high
    } else {
        value
    }
}

pub fn clamp_i32(value: i32, low: i32, high: i32) -> i32 {
    if value < low {
        low
    } else if value > high {
        high
    } else {
        value
    }
}

pub fn max(a: f64, b: f64) -> f64 {
    if a != b {
        if !a.is_nan() {
            return if b < a { a } else { b };
        }
        return a;
    }
    if b.is_sign_negative() {
        a
    } else {
        b
    }
}

pub fn min(a: f64, b: f64) -> f64 {
    if a != b && !a.is_nan() {
        return if a < b { a } else { b };
    }
    if a.is_sign_negative() {
        a
    } else {
        b
    }
}

pub fn round(value: f64) -> f64 {
    value.round_ties_even()
}

pub fn to_int(value: f64) -> i32 {
    if value.is_nan() || value >= 2147483648.0 || value <= -2147483649.0 {
        i32::MIN
    } else {
        value as i32
    }
}
