const MIN_UNITS: i64 = 16;
const MAX_UNITS: i64 = 16384;

struct Table {
    tag: [u8; 4],
    data: Vec<u8>,
}

struct Bounds {
    min_x: i32,
    min_y: i32,
    max_x: i32,
    max_y: i32,
}

fn round(v: f64) -> i32 {
    (v + 0.5).floor() as i64 as i32
}

fn u16_at(b: &[u8], o: usize) -> i32 {
    ((b[o] as i32) << 8) | b[o + 1] as i32
}

fn s16_at(b: &[u8], o: usize) -> i32 {
    u16_at(b, o) as i16 as i32
}

fn u32_at(b: &[u8], o: usize) -> u32 {
    u32::from_be_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

fn w16(b: &mut [u8], o: usize, v: i32) {
    b[o] = (v >> 8) as u8;
    b[o + 1] = v as u8;
}

fn w32(b: &mut [u8], o: usize, v: u32) {
    b[o..o + 4].copy_from_slice(&v.to_be_bytes());
}

fn ws(out: &mut Vec<u8>, v: i32) {
    out.push((v >> 8) as u8);
    out.push(v as u8);
}

fn sat(v: i32) -> i32 {
    v.clamp(i16::MIN as i32, i16::MAX as i32)
}

pub fn collection(data: &[u8]) -> bool {
    data.len() >= 4 && &data[..4] == b"ttcf"
}

pub fn scale(data: &[u8], scale: f64) -> Vec<u8> {
    if (scale - 1.0).abs() < 0.0001 {
        return data.to_vec();
    }
    let attempt = std::panic::catch_unwind(|| {
        if let Some(mut tables) = read_tables(data) {
            if let Some(rebuilt) = scale_outlines(&mut tables, scale) {
                return Some(rebuilt);
            }
        }
        let mut copy = data.to_vec();
        scale_em(&mut copy, scale).then_some(copy)
    });
    attempt.ok().flatten().unwrap_or_else(|| data.to_vec())
}

fn read_tables(data: &[u8]) -> Option<Vec<Table>> {
    if data.len() < 12 || collection(data) {
        return None;
    }
    let count = u16_at(data, 4) as usize;
    if count == 0 || count > 512 || 12 + count * 16 > data.len() {
        return None;
    }
    let mut tables = Vec::with_capacity(count);
    for i in 0..count {
        let entry = 12 + i * 16;
        let offset = u32_at(data, entry + 8) as u64;
        let length = u32_at(data, entry + 12) as u64;
        if offset + length > data.len() as u64 {
            return None;
        }
        tables.push(Table {
            tag: [data[entry], data[entry + 1], data[entry + 2], data[entry + 3]],
            data: data[offset as usize..(offset + length) as usize].to_vec(),
        });
    }
    Some(tables)
}

fn find(tables: &[Table], tag: &[u8; 4]) -> Option<usize> {
    tables.iter().position(|t| &t.tag == tag)
}

fn scale_outlines(tables: &mut Vec<Table>, scale: f64) -> Option<Vec<u8>> {
    let head = find(tables, b"head")?;
    let maxp = find(tables, b"maxp")?;
    let loca = find(tables, b"loca")?;
    let glyf = find(tables, b"glyf")?;
    let hhea = find(tables, b"hhea");
    let hmtx = find(tables, b"hmtx");
    if tables[head].data.len() < 54 || tables[maxp].data.len() < 6 {
        return None;
    }
    let glyphs = u16_at(&tables[maxp].data, 4) as usize;
    let loc_format = s16_at(&tables[head].data, 50);
    if glyphs == 0 {
        return None;
    }
    let mut offsets = vec![0i64; glyphs + 1];
    {
        let l = &tables[loca].data;
        if loc_format == 0 {
            if l.len() < (glyphs + 1) * 2 {
                return None;
            }
            for (i, o) in offsets.iter_mut().enumerate() {
                *o = u16_at(l, i * 2) as i64 * 2;
            }
        } else {
            if l.len() < (glyphs + 1) * 4 {
                return None;
            }
            for (i, o) in offsets.iter_mut().enumerate() {
                *o = u32_at(l, i * 4) as i32 as i64;
            }
        }
    }
    let mut out = Vec::new();
    let mut next = vec![0u32; glyphs + 1];
    let mut b = Bounds { min_x: i32::MAX, min_y: i32::MAX, max_x: i32::MIN, max_y: i32::MIN };
    {
        let g = &tables[glyf].data;
        for i in 0..glyphs {
            next[i] = out.len() as u32;
            let start = offsets[i];
            let length = offsets[i + 1] - start;
            if length <= 0 || start < 0 || start + length > g.len() as i64 {
                continue;
            }
            let scaled = scale_glyph(&g[start as usize..(start + length) as usize], scale, &mut b)?;
            out.extend_from_slice(&scaled);
            while out.len() % 4 != 0 {
                out.push(0);
            }
        }
    }
    next[glyphs] = out.len() as u32;
    tables[glyf].data = out;
    let mut new_loca = vec![0u8; (glyphs + 1) * 4];
    for (i, n) in next.iter().enumerate() {
        w32(&mut new_loca, i * 4, *n);
    }
    tables[loca].data = new_loca;
    let h = &mut tables[head].data;
    w16(h, 50, 1);
    if b.min_x <= b.max_x {
        w16(h, 36, sat(b.min_x));
        w16(h, 38, sat(b.min_y));
        w16(h, 40, sat(b.max_x));
        w16(h, 42, sat(b.max_y));
    }
    if let (Some(hhea), Some(hmtx)) = (hhea, hmtx) {
        if tables[hhea].data.len() >= 36 {
            let metrics = u16_at(&tables[hhea].data, 34) as usize;
            let m = &mut tables[hmtx].data;
            for i in 0..metrics {
                let at = i * 4;
                if at + 4 > m.len() {
                    break;
                }
                let advance = u16_at(m, at);
                let bearing = s16_at(m, at + 2);
                w16(m, at, round(advance as f64 * scale).clamp(0, 65535));
                w16(m, at + 2, sat(round(bearing as f64 * scale)));
            }
        }
    }
    Some(rebuild(tables))
}

fn scale_glyph(g: &[u8], scale: f64, b: &mut Bounds) -> Option<Vec<u8>> {
    if g.len() < 10 {
        return None;
    }
    let contours = s16_at(g, 0);
    let mut out = Vec::new();
    let gx_min = sat(round(s16_at(g, 2) as f64 * scale));
    let gy_min = sat(round(s16_at(g, 4) as f64 * scale));
    let gx_max = sat(round(s16_at(g, 6) as f64 * scale));
    let gy_max = sat(round(s16_at(g, 8) as f64 * scale));
    b.min_x = b.min_x.min(gx_min);
    b.min_y = b.min_y.min(gy_min);
    b.max_x = b.max_x.max(gx_max);
    b.max_y = b.max_y.max(gy_max);
    ws(&mut out, contours);
    ws(&mut out, gx_min);
    ws(&mut out, gy_min);
    ws(&mut out, gx_max);
    ws(&mut out, gy_max);
    if contours >= 0 { simple(g, contours as usize, scale, out) } else { composite(g, scale, out) }
}

fn simple(g: &[u8], contours: usize, scale: f64, mut out: Vec<u8>) -> Option<Vec<u8>> {
    let mut p = 10;
    if p + contours * 2 + 2 > g.len() {
        return None;
    }
    let mut points = 0usize;
    for _ in 0..contours {
        let end = u16_at(g, p) as usize;
        out.extend_from_slice(&g[p..p + 2]);
        p += 2;
        points = end + 1;
    }
    let instructions = u16_at(g, p) as usize;
    p += 2;
    if p + instructions > g.len() {
        return None;
    }
    p += instructions;
    ws(&mut out, 0);
    if points == 0 {
        return Some(out);
    }
    let mut flags = vec![0u8; points];
    let mut index = 0;
    while index < points {
        let flag = *g.get(p)?;
        p += 1;
        flags[index] = flag;
        index += 1;
        if flag & 0x08 != 0 {
            let repeat = *g.get(p)? as usize;
            p += 1;
            let mut r = 0;
            while r < repeat && index < points {
                flags[index] = flag;
                index += 1;
                r += 1;
            }
        }
    }
    let dx = deltas(g, &mut p, &flags, 0x02, 0x10)?;
    let dy = deltas(g, &mut p, &flags, 0x04, 0x20)?;
    let mut ax = vec![0i32; points];
    let mut ay = vec![0i32; points];
    let (mut rx, mut ry) = (0i32, 0i32);
    for i in 0..points {
        rx = rx.wrapping_add(dx[i]);
        ry = ry.wrapping_add(dy[i]);
        ax[i] = round(rx as f64 * scale);
        ay[i] = round(ry as f64 * scale);
    }
    for f in &flags {
        out.push(f & 0x01);
    }
    let mut prev = 0i32;
    for x in &ax {
        ws(&mut out, sat(x.wrapping_sub(prev)));
        prev = *x;
    }
    prev = 0;
    for y in &ay {
        ws(&mut out, sat(y.wrapping_sub(prev)));
        prev = *y;
    }
    Some(out)
}

fn deltas(g: &[u8], p: &mut usize, flags: &[u8], short_bit: u8, same_bit: u8) -> Option<Vec<i32>> {
    let mut d = vec![0i32; flags.len()];
    for (i, &flag) in flags.iter().enumerate() {
        if flag & short_bit != 0 {
            let v = *g.get(*p)? as i32;
            *p += 1;
            d[i] = if flag & same_bit != 0 { v } else { -v };
        } else if flag & same_bit != 0 {
            d[i] = 0;
        } else {
            if *p + 2 > g.len() {
                return None;
            }
            d[i] = s16_at(g, *p);
            *p += 2;
        }
    }
    Some(d)
}

fn composite(g: &[u8], scale: f64, mut out: Vec<u8>) -> Option<Vec<u8>> {
    let mut p = 10;
    loop {
        if p + 4 > g.len() {
            return None;
        }
        let flags = u16_at(g, p);
        let glyph = u16_at(g, p + 2);
        p += 4;
        let words = flags & 0x0001 != 0;
        let xy = flags & 0x0002 != 0;
        let (mut a1, mut a2);
        if words {
            if p + 4 > g.len() {
                return None;
            }
            a1 = s16_at(g, p);
            a2 = s16_at(g, p + 2);
            p += 4;
        } else {
            if p + 2 > g.len() {
                return None;
            }
            a1 = if xy { g[p] as i8 as i32 } else { g[p] as i32 };
            a2 = if xy { g[p + 1] as i8 as i32 } else { g[p + 1] as i32 };
            p += 2;
        }
        if xy {
            a1 = round(a1 as f64 * scale);
            a2 = round(a2 as f64 * scale);
        }
        ws(&mut out, (flags | 0x0001) & !0x0100);
        ws(&mut out, glyph);
        ws(&mut out, sat(a1));
        ws(&mut out, sat(a2));
        let transform = if flags & 0x0008 != 0 {
            2
        } else if flags & 0x0040 != 0 {
            4
        } else if flags & 0x0080 != 0 {
            8
        } else {
            0
        };
        if transform > 0 {
            if p + transform > g.len() {
                return None;
            }
            out.extend_from_slice(&g[p..p + transform]);
            p += transform;
        }
        if flags & 0x0020 == 0 {
            break;
        }
    }
    Some(out)
}

fn rebuild(tables: &mut [Table]) -> Vec<u8> {
    tables.sort_by(|a, b| a.tag.cmp(&b.tag));
    let count = tables.len();
    let dir_size = 12 + count * 16;
    let total = dir_size + tables.iter().map(|t| (t.data.len() + 3) & !3).sum::<usize>();
    let mut out = vec![0u8; total];
    w32(&mut out, 0, 0x0001_0000);
    w16(&mut out, 4, count as i32);
    let mut power = 1usize;
    let mut exponent = 0;
    while power * 2 <= count {
        power *= 2;
        exponent += 1;
    }
    w16(&mut out, 6, (power * 16) as i32);
    w16(&mut out, 8, exponent);
    w16(&mut out, 10, (count * 16) as i32 - (power * 16) as i32);
    let mut offset = dir_size;
    let mut head_offset = None;
    for (i, t) in tables.iter_mut().enumerate() {
        let entry = 12 + i * 16;
        out[entry..entry + 4].copy_from_slice(&t.tag);
        if &t.tag == b"head" {
            head_offset = Some(offset);
            w32(&mut t.data, 8, 0);
        }
        out[offset..offset + t.data.len()].copy_from_slice(&t.data);
        let padded = (t.data.len() + 3) & !3;
        let sum = checksum(&out[offset..offset + padded]);
        w32(&mut out, entry + 4, sum);
        w32(&mut out, entry + 8, offset as u32);
        w32(&mut out, entry + 12, t.data.len() as u32);
        offset += padded;
    }
    if let Some(h) = head_offset {
        let adjust = 0xB1B0_AFBAu32.wrapping_sub(checksum(&out));
        w32(&mut out, h + 8, adjust);
    }
    out
}

fn checksum(d: &[u8]) -> u32 {
    let mut sum = 0u32;
    let mut i = 0;
    while i + 4 <= d.len() {
        sum = sum.wrapping_add(u32_at(d, i));
        i += 4;
    }
    if i < d.len() {
        let mut tail = 0u32;
        for k in 0..4 {
            tail = (tail << 8) | d.get(i + k).copied().unwrap_or(0) as u32;
        }
        sum = sum.wrapping_add(tail);
    }
    sum
}

fn scale_em(data: &mut [u8], scale: f64) -> bool {
    if read_tables(data).is_none() {
        return false;
    }
    let count = u16_at(data, 4) as usize;
    for i in 0..count {
        let entry = 12 + i * 16;
        if &data[entry..entry + 4] != b"head" {
            continue;
        }
        let offset = u32_at(data, entry + 8) as usize;
        if offset + 20 > data.len() {
            return false;
        }
        let current = u16_at(data, offset + 18);
        if current == 0 {
            return false;
        }
        let scaled = ((current as f64 / scale + 0.5).floor() as i64).clamp(MIN_UNITS, MAX_UNITS);
        w16(data, offset + 18, scaled as i32);
        w32(data, offset + 8, 0);
        return true;
    }
    false
}
