pub const LARGEST_TABLE: usize = 306;
const QUICK_MAX: usize = 1 << 10;

pub trait BitRead {
    fn getbits(&self) -> u32;
    fn addbits(&mut self, bits: isize);
}

pub struct Table {
    decode_len: [u32; 16],
    decode_pos: [u32; 16],
    decode_num: [u32; LARGEST_TABLE + 64],
    quick_len: [u32; QUICK_MAX],
    quick_num: [u32; QUICK_MAX],
    max_num: u32,
    quick_bits: u32,
}

impl Table {
    pub fn new() -> Table {
        Table {
            decode_len: [0; 16],
            decode_pos: [0; 16],
            decode_num: [0; LARGEST_TABLE + 64],
            quick_len: [0; QUICK_MAX],
            quick_num: [0; QUICK_MAX],
            max_num: 0,
            quick_bits: 0,
        }
    }

    pub fn make(&mut self, lengths: &[u8], size: usize, quick_bits: u32) {
        self.max_num = size as u32;
        let mut count = [0u32; 16];
        for &l in &lengths[..size] {
            count[(l & 0xF) as usize] += 1;
        }
        count[0] = 0;
        self.decode_num.fill(0);
        self.decode_pos[0] = 0;
        self.decode_len[0] = 0;
        let mut upper = 0u32;
        for i in 1..16 {
            upper = upper.wrapping_add(count[i]);
            let left = upper.wrapping_shl(16 - i as u32);
            upper = upper.wrapping_mul(2);
            self.decode_len[i] = left;
            self.decode_pos[i] = self.decode_pos[i - 1].wrapping_add(count[i - 1]);
        }
        let mut copy = self.decode_pos;
        for (i, &l) in lengths[..size].iter().enumerate() {
            let len = (l & 0xF) as usize;
            if len != 0 {
                let last = copy[len] as usize;
                if last < self.decode_num.len() {
                    self.decode_num[last] = i as u32;
                }
                copy[len] = copy[len].wrapping_add(1);
            }
        }
        self.quick_bits = quick_bits;
        let quick_size = 1usize << quick_bits;
        let mut cur = 1usize;
        for code in 0..quick_size {
            let field = (code as u32) << (16 - quick_bits);
            while cur < 16 && field >= self.decode_len[cur] {
                cur += 1;
            }
            self.quick_len[code] = cur as u32;
            let dist = field.wrapping_sub(self.decode_len[cur - 1]) >> (16 - cur as u32);
            let pos = self.decode_pos.get(cur).map(|p| p.wrapping_add(dist) as usize);
            self.quick_num[code] = match pos {
                Some(p) if cur < 16 && p < size => self.decode_num[p],
                _ => 0,
            };
        }
    }
}

pub fn decode_symbol<B: BitRead>(b: &mut B, t: &Table) -> usize {
    let field = b.getbits() & 0xFFFE;
    if field < t.decode_len[t.quick_bits as usize] {
        let code = (field >> (16 - t.quick_bits)) as usize;
        b.addbits(t.quick_len[code] as isize);
        return t.quick_num[code] as usize;
    }
    let mut bits = 15usize;
    for i in (t.quick_bits as usize + 1)..15 {
        if field < t.decode_len[i] {
            bits = i;
            break;
        }
    }
    b.addbits(bits as isize);
    let dist = field.wrapping_sub(t.decode_len[bits - 1]) >> (16 - bits as u32);
    let mut pos = t.decode_pos[bits].wrapping_add(dist);
    if pos >= t.max_num {
        pos = 0;
    }
    t.decode_num[pos as usize] as usize
}
