pub const PACKET: usize = 188;

pub fn pid(p: &[u8]) -> u16 {
    (((p[1] & 0x1f) as u16) << 8) | p[2] as u16
}

pub fn unit_start(p: &[u8]) -> bool {
    p[1] & 0x40 != 0
}

fn payload_offset(p: &[u8]) -> Option<usize> {
    match (p[3] >> 4) & 3 {
        1 => Some(4),
        3 => {
            let offset = 5 + p[4] as usize;
            if offset < PACKET {
                Some(offset)
            } else {
                None
            }
        }
        _ => None,
    }
}

pub fn random_access(p: &[u8]) -> bool {
    let control = (p[3] >> 4) & 3;
    (control == 2 || control == 3) && p[4] > 0 && p[5] & 0x40 != 0
}

fn section(p: &[u8]) -> Option<&[u8]> {
    if !unit_start(p) {
        return None;
    }
    let offset = payload_offset(p)?;
    let start = offset + 1 + p[offset] as usize;
    if start + 3 > PACKET {
        return None;
    }
    let s = &p[start..];
    let length = ((((s[1] & 0x0f) as usize) << 8) | s[2] as usize) + 3;
    if length > s.len() {
        return None;
    }
    Some(&s[..length])
}

pub fn pmt_pid(p: &[u8]) -> Option<u16> {
    let s = section(p)?;
    if s[0] != 0 || s.len() < 12 {
        return None;
    }
    let end = s.len() - 4;
    let mut i = 8;
    while i + 4 <= end {
        let program = ((s[i] as u16) << 8) | s[i + 1] as u16;
        let pid = (((s[i + 2] & 0x1f) as u16) << 8) | s[i + 3] as u16;
        if program != 0 {
            return Some(pid);
        }
        i += 4;
    }
    None
}

pub fn video_pid(p: &[u8]) -> Option<u16> {
    let s = section(p)?;
    if s[0] != 2 || s.len() < 16 {
        return None;
    }
    let end = s.len() - 4;
    let info = (((s[10] & 0x0f) as usize) << 8) | s[11] as usize;
    let mut i = 12 + info;
    while i + 5 <= end {
        let kind = s[i];
        let pid = (((s[i + 1] & 0x1f) as u16) << 8) | s[i + 2] as u16;
        let es = (((s[i + 3] & 0x0f) as usize) << 8) | s[i + 4] as usize;
        if matches!(kind, 0x01 | 0x02 | 0x10 | 0x1b | 0x24) {
            return Some(pid);
        }
        i += 5 + es;
    }
    None
}
