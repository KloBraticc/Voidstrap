use super::huff::{BitRead, Table, decode_symbol};
use super::{Error, MAX_ENTRIES, MAX_EXTRACTED, RAR_PASSWORD, RAR_SPLIT, Sink, too_big, too_many};
use crc32fast::Hasher;
use ppmd_rust::Ppmd7aDecoder;
use std::cell::RefCell;
use std::fs::File;
use std::io::{ErrorKind, Read, Seek, SeekFrom};
use std::path::Path;
use std::rc::Rc;

const BAD: &str = "The rar package is damaged or uses an unsupported feature.";
const MAX_SIZE: usize = 0x8000;
const NC30: usize = 299;
const DC30: usize = 60;
const LDC30: usize = 17;
const RC30: usize = 28;
const BC30: usize = 20;
const TABLE30: usize = NC30 + DC30 + LDC30 + RC30;
const QUICK: u32 = 10;
const WINDOW: usize = 0x40_0000;
const MASK: usize = WINDOW - 1;
const MAX3_LZ_MATCH: usize = 0x101;
const LOW_DIST_REP_COUNT: u32 = 16;
const MAX3_UNPACK_FILTERS: usize = 8192;
const MAX3_UNPACK_CHANNELS: u32 = 1024;
const VM_MEMSIZE: usize = 0x40000;
const VM_MEMMASK: usize = VM_MEMSIZE - 1;
const MAX_NAME: usize = 2048;
const LDECODE: [u32; 28] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
const LBITS: [u32; 28] = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
const SDDECODE: [u32; 8] = [0, 4, 8, 16, 32, 64, 128, 192];
const SDBITS: [u32; 8] = [2, 2, 3, 4, 5, 6, 6, 6];
const DBIT_COUNTS: [u32; 19] = [4, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 14, 0, 12];

fn bad() -> Error {
    Error::rejected(BAD)
}

fn le16(b: &[u8], o: usize) -> u16 {
    u16::from_le_bytes([b[o], b[o + 1]])
}

fn le32(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

fn exact(f: &mut File, buf: &mut [u8]) -> Result<(), Error> {
    f.read_exact(buf).map_err(|e| if e.kind() == ErrorKind::UnexpectedEof { bad() } else { e.into() })
}

pub fn decode(path: &Path, sink: &mut dyn Sink) -> Result<(), Error> {
    let mut f = File::open(path)?;
    let length = f.metadata()?.len();
    let mut mark = [0u8; 7];
    exact(&mut f, &mut mark)?;
    if &mark[..6] != b"Rar!\x1a\x07" {
        return Err(bad());
    }
    let mut pos = 7u64;
    let mut count = 0usize;
    let mut unpack: Option<Unpack> = None;
    let mut buf = vec![0u8; 65536];
    while pos + 7 <= length {
        f.seek(SeekFrom::Start(pos))?;
        let mut base = [0u8; 7];
        exact(&mut f, &mut base)?;
        let kind = base[2];
        let flags = le16(&base, 3);
        let head_size = le16(&base, 5) as u64;
        if head_size < 7 {
            return Err(bad());
        }
        let mut h = vec![0u8; head_size as usize - 7];
        exact(&mut f, &mut h)?;
        let mut data_size = 0u64;
        match kind {
            0x73 => {
                if flags & 0x0080 != 0 {
                    return Err(Error::rejected(RAR_PASSWORD));
                }
            }
            0x74 | 0x7A => {
                if h.len() < 25 {
                    return Err(bad());
                }
                let mut pack = le32(&h, 0) as u64;
                let mut unp = le32(&h, 4) as u64;
                let file_crc = le32(&h, 9);
                let unp_ver = h[17];
                let method = h[18];
                let name_size = le16(&h, 19) as usize;
                let mut off = 25;
                if flags & 0x100 != 0 {
                    if h.len() < 33 {
                        return Err(bad());
                    }
                    pack |= (le32(&h, 25) as u64) << 32;
                    unp |= (le32(&h, 29) as u64) << 32;
                    off = 33;
                }
                data_size = pack;
                if kind == 0x74 && flags & 0xE0 != 0xE0 {
                    let raw = h.get(off..off + name_size).ok_or_else(bad)?;
                    if flags & 0x04 != 0 {
                        return Err(Error::rejected(RAR_PASSWORD));
                    }
                    if flags & 0x03 != 0 {
                        return Err(Error::rejected(RAR_SPLIT));
                    }
                    if unp > MAX_EXTRACTED {
                        return Err(too_big());
                    }
                    count += 1;
                    if count > MAX_ENTRIES {
                        return Err(too_many());
                    }
                    let name = file_name(raw, flags & 0x200 != 0).replace('\\', "/");
                    let data_start = pos + head_size;
                    sink.entry(&name, unp as i64)?;
                    let mut crc = Hasher::new();
                    if method == 0x30 {
                        f.seek(SeekFrom::Start(data_start))?;
                        let mut left = pack;
                        while left > 0 {
                            let want = (left as usize).min(buf.len());
                            let n = f.read(&mut buf[..want])?;
                            if n == 0 {
                                return Err(bad());
                            }
                            crc.update(&buf[..n]);
                            sink.data(&buf[..n])?;
                            left -= n as u64;
                        }
                    } else if (0x31..=0x35).contains(&method) && (unp_ver == 29 || unp_ver == 36) {
                        if unpack.is_none() {
                            unpack = Some(Unpack::new(File::open(path)?));
                        }
                        let u = unpack.as_mut().ok_or_else(bad)?;
                        u.extract(data_start, pack, unp, flags & 0x10 != 0, &mut Out { sink, crc: &mut crc })?;
                    } else {
                        return Err(bad());
                    }
                    if crc.finalize() != file_crc {
                        return Err(bad());
                    }
                    sink.end()?;
                }
            }
            0x7B => break,
            _ => {
                if flags & 0x8000 != 0 {
                    if h.len() < 4 {
                        return Err(bad());
                    }
                    data_size = le32(&h, 0) as u64;
                }
            }
        }
        pos = pos.checked_add(head_size).and_then(|p| p.checked_add(data_size)).ok_or_else(bad)?;
    }
    Ok(())
}

fn file_name(raw: &[u8], unicode: bool) -> String {
    if unicode {
        if let Some(z) = raw.iter().position(|&b| b == 0) {
            return decode_unicode(&raw[..z], &raw[z + 1..]);
        }
        return String::from_utf8_lossy(raw).into_owned();
    }
    match std::str::from_utf8(raw) {
        Ok(s) => s.to_string(),
        Err(_) => super::zip::cp437(raw),
    }
}

fn decode_unicode(name: &[u8], enc: &[u8]) -> String {
    let mut out: Vec<u16> = Vec::new();
    let mut ep = 0;
    let high = if ep < enc.len() {
        ep += 1;
        enc[0] as u16
    } else {
        0
    };
    let mut flags = 0u8;
    let mut flag_bits = 0;
    while ep < enc.len() && out.len() < MAX_NAME {
        if flag_bits == 0 {
            flags = enc[ep];
            ep += 1;
            flag_bits = 8;
        }
        match flags >> 6 {
            0 => {
                if ep >= enc.len() {
                    break;
                }
                out.push(enc[ep] as u16);
                ep += 1;
            }
            1 => {
                if ep >= enc.len() {
                    break;
                }
                out.push(enc[ep] as u16 + (high << 8));
                ep += 1;
            }
            2 => {
                if ep + 1 >= enc.len() {
                    break;
                }
                out.push(enc[ep] as u16 + ((enc[ep + 1] as u16) << 8));
                ep += 2;
            }
            _ => {
                if ep >= enc.len() {
                    break;
                }
                let mut length = enc[ep] as usize;
                ep += 1;
                if length & 0x80 != 0 {
                    if ep >= enc.len() {
                        break;
                    }
                    let correction = enc[ep];
                    ep += 1;
                    length = (length & 0x7F) + 2;
                    while length > 0 && out.len() < MAX_NAME {
                        let c = name.get(out.len()).copied().unwrap_or(0).wrapping_add(correction);
                        out.push(c as u16 + (high << 8));
                        length -= 1;
                    }
                } else {
                    length += 2;
                    while length > 0 && out.len() < MAX_NAME && out.len() < name.len() {
                        out.push(name[out.len()] as u16);
                        length -= 1;
                    }
                }
            }
        }
        flags <<= 2;
        flag_bits -= 2;
    }
    if let Some(z) = out.iter().position(|&c| c == 0) {
        out.truncate(z);
    }
    String::from_utf16_lossy(&out)
}

struct Out<'a> {
    sink: &'a mut dyn Sink,
    crc: &'a mut Hasher,
}

struct Input {
    buf: Vec<u8>,
    addr: isize,
    bit: isize,
    read_top: isize,
    read_border: isize,
    file: File,
    packed_left: u64,
}

impl BitRead for Input {
    fn getbits(&self) -> u32 {
        let a = self.addr as usize;
        let b = (self.buf[a] as u32) << 16 | (self.buf[a + 1] as u32) << 8 | self.buf[a + 2] as u32;
        (b >> (8 - self.bit)) & 0xFFFF
    }

    fn addbits(&mut self, bits: isize) {
        let bits = bits + self.bit;
        self.addr += bits >> 3;
        self.bit = bits & 7;
    }
}

impl Input {
    fn read_buf(&mut self) -> bool {
        let mut data_size = self.read_top - self.addr;
        if data_size < 0 {
            return false;
        }
        if self.addr > (MAX_SIZE / 2) as isize {
            if data_size > 0 {
                let a = self.addr as usize;
                self.buf.copy_within(a..a + data_size as usize, 0);
            }
            self.addr = 0;
            self.read_top = data_size;
        } else {
            data_size = self.read_top;
        }
        let want = ((MAX_SIZE as isize - data_size) as u64).min(self.packed_left) as usize;
        let mut read = 0usize;
        if want > 0 {
            let start = data_size as usize;
            match self.file.read(&mut self.buf[start..start + want]) {
                Ok(n) => {
                    read = n;
                    self.packed_left -= n as u64;
                }
                Err(_) => return false,
            }
        }
        self.read_top += read as isize;
        let top = self.read_top as usize;
        self.buf[top..].fill(0);
        self.read_border = self.read_top - 30;
        true
    }

    fn get_char(&mut self) -> u8 {
        if self.addr > (MAX_SIZE - 30) as isize {
            self.read_buf();
            if self.addr >= MAX_SIZE as isize {
                return 0;
            }
        }
        let c = self.buf[self.addr as usize];
        self.addr += 1;
        c
    }
}

struct PpmIn(Rc<RefCell<Input>>);

impl Read for PpmIn {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let mut inp = self.0.borrow_mut();
        for b in buf.iter_mut() {
            *b = inp.get_char();
        }
        Ok(buf.len())
    }
}

struct CodeInput {
    buf: Vec<u8>,
    addr: isize,
    bit: isize,
}

impl BitRead for CodeInput {
    fn getbits(&self) -> u32 {
        let a = self.addr as usize;
        let b = (self.buf[a] as u32) << 16 | (self.buf[a + 1] as u32) << 8 | self.buf[a + 2] as u32;
        (b >> (8 - self.bit)) & 0xFFFF
    }

    fn addbits(&mut self, bits: isize) {
        let bits = bits + self.bit;
        self.addr += bits >> 3;
        self.bit = bits & 7;
    }
}

impl CodeInput {
    fn new(code: &[u8]) -> CodeInput {
        let mut buf = vec![0u8; MAX_SIZE + 8];
        let n = code.len().min(MAX_SIZE);
        buf[..n].copy_from_slice(&code[..n]);
        CodeInput { buf, addr: 0, bit: 0 }
    }

    fn overflow(&self, inc: usize) -> bool {
        self.addr as usize + inc >= MAX_SIZE
    }
}

fn read_data<B: BitRead>(inp: &mut B) -> u32 {
    let mut data = inp.getbits();
    match data & 0xC000 {
        0 => {
            inp.addbits(6);
            (data >> 10) & 0xF
        }
        0x4000 => {
            if data & 0x3C00 == 0 {
                data = 0xFFFF_FF00 | ((data >> 2) & 0xFF);
                inp.addbits(14);
            } else {
                data = (data >> 6) & 0xFF;
                inp.addbits(10);
            }
            data
        }
        0x8000 => {
            inp.addbits(2);
            let d = inp.getbits();
            inp.addbits(16);
            d
        }
        _ => {
            inp.addbits(2);
            let hi = inp.getbits() << 16;
            inp.addbits(16);
            let lo = inp.getbits();
            inp.addbits(16);
            hi | lo
        }
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Std {
    None,
    E8,
    E8E9,
    Itanium,
    Delta,
    Rgb,
    Audio,
}

fn identify(code: &[u8]) -> Std {
    let xor = code[1..].iter().fold(0u8, |a, &b| a ^ b);
    if xor != code[0] {
        return Std::None;
    }
    match (code.len(), crc32fast::hash(code)) {
        (53, 0xAD57_6887) => Std::E8,
        (57, 0x3CD7_E57E) => Std::E8E9,
        (120, 0x3769_893F) => Std::Itanium,
        (29, 0x0E06_077D) => Std::Delta,
        (149, 0x1C2C_5DC8) => Std::Rgb,
        (216, 0xBC85_E701) => Std::Audio,
        _ => Std::None,
    }
}

#[derive(Clone, Copy)]
struct StackFilter {
    block_start: usize,
    block_length: u32,
    next_window: bool,
    init_r: [u32; 7],
    kind: Std,
}

struct Unpack {
    window: Vec<u8>,
    inp: Rc<RefCell<Input>>,
    unp_ptr: usize,
    wr_ptr: usize,
    old_dist: [usize; 4],
    last_length: u32,
    ld: Table,
    dd: Table,
    ldd: Table,
    rd: Table,
    bd: Table,
    old_table: [u8; TABLE30],
    tables_read: bool,
    ppm_block: bool,
    ppm: Option<Ppmd7aDecoder<PpmIn>>,
    esc_char: u32,
    prev_low_dist: u32,
    low_dist_rep: u32,
    filters: Vec<Std>,
    old_filter_lengths: Vec<u32>,
    last_filter: usize,
    stack: Vec<Option<StackFilter>>,
    vm: Vec<u8>,
    written: u64,
    dest_size: u64,
    ddecode: [u32; DC30],
    dbits: [u32; DC30],
}

impl Unpack {
    fn new(file: File) -> Unpack {
        let mut ddecode = [0u32; DC30];
        let mut dbits = [0u32; DC30];
        let mut dist = 0u32;
        let mut slot = 0usize;
        for (bit_length, &n) in DBIT_COUNTS.iter().enumerate() {
            for _ in 0..n {
                ddecode[slot] = dist;
                dbits[slot] = bit_length as u32;
                slot += 1;
                dist += 1 << bit_length;
            }
        }
        Unpack {
            window: vec![0; WINDOW],
            inp: Rc::new(RefCell::new(Input {
                buf: vec![0; MAX_SIZE + 64],
                addr: 0,
                bit: 0,
                read_top: 0,
                read_border: 0,
                file,
                packed_left: 0,
            })),
            unp_ptr: 0,
            wr_ptr: 0,
            old_dist: [usize::MAX; 4],
            last_length: 0,
            ld: Table::new(),
            dd: Table::new(),
            ldd: Table::new(),
            rd: Table::new(),
            bd: Table::new(),
            old_table: [0; TABLE30],
            tables_read: false,
            ppm_block: false,
            ppm: None,
            esc_char: 2,
            prev_low_dist: 0,
            low_dist_rep: 0,
            filters: Vec::new(),
            old_filter_lengths: Vec::new(),
            last_filter: 0,
            stack: Vec::new(),
            vm: vec![0; VM_MEMSIZE + 4],
            written: 0,
            dest_size: 0,
            ddecode,
            dbits,
        }
    }

    fn extract(&mut self, data_start: u64, packed: u64, dest: u64, solid: bool, out: &mut Out) -> Result<(), Error> {
        {
            let mut inp = self.inp.borrow_mut();
            inp.file.seek(SeekFrom::Start(data_start))?;
            inp.packed_left = packed;
            inp.addr = 0;
            inp.bit = 0;
            inp.read_top = 0;
            inp.read_border = 0;
            inp.buf.fill(0);
        }
        self.dest_size = dest;
        if !solid {
            self.old_dist = [usize::MAX; 4];
            self.last_length = 0;
            self.ld = Table::new();
            self.dd = Table::new();
            self.ldd = Table::new();
            self.rd = Table::new();
            self.unp_ptr = 0;
            self.wr_ptr = 0;
            self.tables_read = false;
            self.old_table = [0; TABLE30];
            self.esc_char = 2;
            self.ppm_block = false;
            self.init_filters();
        }
        self.written = 0;
        self.stack.clear();
        self.unpack29(solid, out)
    }

    fn init_filters(&mut self) {
        self.old_filter_lengths.clear();
        self.last_filter = 0;
        self.filters.clear();
        self.stack.clear();
    }

    fn unpack29(&mut self, solid: bool, out: &mut Out) -> Result<(), Error> {
        if !self.inp.borrow_mut().read_buf() {
            return Ok(());
        }
        if (!solid || !self.tables_read) && !self.read_tables() {
            return Ok(());
        }
        loop {
            self.unp_ptr &= MASK;
            {
                let mut inp = self.inp.borrow_mut();
                if inp.addr > inp.read_border && !inp.read_buf() {
                    break;
                }
            }
            if (self.wr_ptr.wrapping_sub(self.unp_ptr) & MASK) < MAX3_LZ_MATCH + 3 && self.wr_ptr != self.unp_ptr {
                self.write_buf(out)?;
                if self.written > self.dest_size {
                    return Ok(());
                }
            }
            if self.ppm_block {
                let ch = self.ppm_char();
                if ch < 0 {
                    self.ppm = None;
                    self.ppm_block = false;
                    break;
                }
                if ch as u32 == self.esc_char {
                    let next = self.safe_ppm_char();
                    if next == 0 {
                        if !self.read_tables() {
                            break;
                        }
                        continue;
                    }
                    if next == -1 || next == 2 {
                        break;
                    }
                    if next == 3 {
                        if !self.read_vm_code_ppm() {
                            break;
                        }
                        continue;
                    }
                    if next == 4 {
                        let mut distance = 0u32;
                        let mut length = 0u32;
                        let mut failed = false;
                        for i in 0..4 {
                            let c = self.safe_ppm_char();
                            if c == -1 {
                                failed = true;
                                break;
                            }
                            if i == 3 {
                                length = c as u32;
                            } else {
                                distance = (distance << 8) + c as u32;
                            }
                        }
                        if failed {
                            break;
                        }
                        self.copy_string(length + 32, distance as usize + 2);
                        continue;
                    }
                    if next == 5 {
                        let length = self.safe_ppm_char();
                        if length == -1 {
                            break;
                        }
                        self.copy_string(length as u32 + 4, 1);
                        continue;
                    }
                }
                self.window[self.unp_ptr] = ch as u8;
                self.unp_ptr += 1;
                continue;
            }
            let number = {
                let mut inp = self.inp.borrow_mut();
                decode_symbol(&mut *inp, &self.ld)
            };
            if number < 256 {
                self.window[self.unp_ptr] = number as u8;
                self.unp_ptr += 1;
                continue;
            }
            if number >= 271 {
                let (mut length, distance) = {
                    let mut inp = self.inp.borrow_mut();
                    let n = number - 271;
                    let mut length = LDECODE[n] + 3;
                    let bits = LBITS[n];
                    if bits > 0 {
                        length += inp.getbits() >> (16 - bits);
                        inp.addbits(bits as isize);
                    }
                    let dist_number = decode_symbol(&mut *inp, &self.dd);
                    let mut distance = self.ddecode[dist_number] + 1;
                    let bits = self.dbits[dist_number];
                    if bits > 0 {
                        if dist_number > 9 {
                            if bits > 4 {
                                distance += (inp.getbits() >> (20 - bits)) << 4;
                                inp.addbits(bits as isize - 4);
                            }
                            if self.low_dist_rep > 0 {
                                self.low_dist_rep -= 1;
                                distance += self.prev_low_dist;
                            } else {
                                let low = decode_symbol(&mut *inp, &self.ldd) as u32;
                                if low == 16 {
                                    self.low_dist_rep = LOW_DIST_REP_COUNT - 1;
                                    distance += self.prev_low_dist;
                                } else {
                                    distance += low;
                                    self.prev_low_dist = low;
                                }
                            }
                        } else {
                            distance += inp.getbits() >> (16 - bits);
                            inp.addbits(bits as isize);
                        }
                    }
                    (length, distance)
                };
                if distance >= 0x2000 {
                    length += 1;
                    if distance >= 0x40000 {
                        length += 1;
                    }
                }
                self.insert_old_dist(distance as usize);
                self.last_length = length;
                self.copy_string(length, distance as usize);
                continue;
            }
            if number == 256 {
                if !self.read_end_of_block() {
                    break;
                }
                continue;
            }
            if number == 257 {
                if !self.read_vm_code() {
                    break;
                }
                continue;
            }
            if number == 258 {
                if self.last_length != 0 {
                    self.copy_string(self.last_length, self.old_dist[0]);
                }
                continue;
            }
            if number < 263 {
                let dist_num = number - 259;
                let distance = self.old_dist[dist_num];
                for i in (1..=dist_num).rev() {
                    self.old_dist[i] = self.old_dist[i - 1];
                }
                self.old_dist[0] = distance;
                let length = {
                    let mut inp = self.inp.borrow_mut();
                    let ln = decode_symbol(&mut *inp, &self.rd);
                    let mut length = LDECODE[ln] + 2;
                    let bits = LBITS[ln];
                    if bits > 0 {
                        length += inp.getbits() >> (16 - bits);
                        inp.addbits(bits as isize);
                    }
                    length
                };
                self.last_length = length;
                self.copy_string(length, distance);
                continue;
            }
            let n = number - 263;
            let mut distance = SDDECODE[n] + 1;
            {
                let mut inp = self.inp.borrow_mut();
                let bits = SDBITS[n];
                if bits > 0 {
                    distance += inp.getbits() >> (16 - bits);
                    inp.addbits(bits as isize);
                }
            }
            self.insert_old_dist(distance as usize);
            self.last_length = 2;
            self.copy_string(2, distance as usize);
        }
        self.write_buf(out)
    }

    fn insert_old_dist(&mut self, d: usize) {
        self.old_dist = [d, self.old_dist[0], self.old_dist[1], self.old_dist[2]];
    }

    fn copy_string(&mut self, length: u32, distance: usize) {
        let mut src = self.unp_ptr.wrapping_sub(distance);
        for _ in 0..length {
            self.window[self.unp_ptr] = self.window[src & MASK];
            src = src.wrapping_add(1);
            self.unp_ptr = (self.unp_ptr + 1) & MASK;
        }
    }

    fn ppm_char(&mut self) -> i32 {
        let Some(p) = self.ppm.as_mut() else {
            return -1;
        };
        let mut b = [0u8; 1];
        match p.read(&mut b) {
            Ok(1) => b[0] as i32,
            _ => -1,
        }
    }

    fn safe_ppm_char(&mut self) -> i32 {
        let c = self.ppm_char();
        if c == -1 {
            self.ppm = None;
            self.ppm_block = false;
        }
        c
    }

    fn ppm_init(&mut self) -> bool {
        let (max_order, max_mb) = {
            let mut inp = self.inp.borrow_mut();
            let mo = inp.get_char() as u32;
            let mb = if mo & 0x20 != 0 { inp.get_char() as u32 } else { 0 };
            if mo & 0x40 != 0 {
                self.esc_char = inp.get_char() as u32;
            }
            (mo, mb)
        };
        if max_order & 0x20 == 0 {
            return self.ppm.as_mut().is_some_and(|p| p.restart().is_ok());
        }
        let mut order = (max_order & 0x1F) + 1;
        if order > 16 {
            order = 16 + (order - 16) * 3;
        }
        if order == 1 {
            self.ppm = None;
            return false;
        }
        match Ppmd7aDecoder::new(PpmIn(self.inp.clone()), order, (max_mb + 1) << 20) {
            Ok(d) => {
                self.ppm = Some(d);
                true
            }
            Err(_) => {
                self.ppm = None;
                false
            }
        }
    }

    fn read_tables(&mut self) -> bool {
        let bitfield = {
            let mut inp = self.inp.borrow_mut();
            if inp.addr > inp.read_top - 25 && !inp.read_buf() {
                return false;
            }
            let b = inp.bit;
            inp.addbits((8 - b) & 7);
            inp.getbits()
        };
        if bitfield & 0x8000 != 0 {
            self.ppm_block = true;
            return self.ppm_init();
        }
        self.ppm_block = false;
        self.prev_low_dist = 0;
        self.low_dist_rep = 0;
        if bitfield & 0x4000 == 0 {
            self.old_table = [0; TABLE30];
        }
        let mut table = [0u8; TABLE30];
        {
            let mut inp = self.inp.borrow_mut();
            inp.addbits(2);
            let mut bit_length = [0u8; BC30];
            let mut i = 0;
            while i < BC30 {
                let length = (inp.getbits() >> 12) as u8;
                inp.addbits(4);
                if length == 15 {
                    let mut zero = (inp.getbits() >> 12) as u8;
                    inp.addbits(4);
                    if zero == 0 {
                        bit_length[i] = 15;
                    } else {
                        zero += 2;
                        while zero > 0 && i < BC30 {
                            bit_length[i] = 0;
                            i += 1;
                            zero -= 1;
                        }
                        i -= 1;
                    }
                } else {
                    bit_length[i] = length;
                }
                i += 1;
            }
            self.bd.make(&bit_length, BC30, QUICK - 3);
            let mut i = 0;
            while i < TABLE30 {
                if inp.addr > inp.read_top - 5 && !inp.read_buf() {
                    return false;
                }
                let number = decode_symbol(&mut *inp, &self.bd);
                if number < 16 {
                    table[i] = (number as u8 + self.old_table[i]) & 0xF;
                    i += 1;
                } else if number < 18 {
                    let mut n = if number == 16 {
                        let v = (inp.getbits() >> 13) + 3;
                        inp.addbits(3);
                        v
                    } else {
                        let v = (inp.getbits() >> 9) + 11;
                        inp.addbits(7);
                        v
                    };
                    if i == 0 {
                        return false;
                    }
                    while n > 0 && i < TABLE30 {
                        table[i] = table[i - 1];
                        i += 1;
                        n -= 1;
                    }
                } else {
                    let mut n = if number == 18 {
                        let v = (inp.getbits() >> 13) + 3;
                        inp.addbits(3);
                        v
                    } else {
                        let v = (inp.getbits() >> 9) + 11;
                        inp.addbits(7);
                        v
                    };
                    while n > 0 && i < TABLE30 {
                        table[i] = 0;
                        i += 1;
                        n -= 1;
                    }
                }
            }
            self.tables_read = true;
            if inp.addr > inp.read_top {
                return false;
            }
        }
        self.ld.make(&table, NC30, QUICK);
        self.dd.make(&table[NC30..], DC30, QUICK - 3);
        self.ldd.make(&table[NC30 + DC30..], LDC30, QUICK - 3);
        self.rd.make(&table[NC30 + DC30 + LDC30..], RC30, QUICK - 3);
        self.old_table = table;
        true
    }

    fn read_end_of_block(&mut self) -> bool {
        let (new_table, new_file) = {
            let mut inp = self.inp.borrow_mut();
            let bf = inp.getbits();
            if bf & 0x8000 != 0 {
                inp.addbits(1);
                (true, false)
            } else {
                inp.addbits(2);
                (bf & 0x4000 != 0, true)
            }
        };
        self.tables_read = !new_table;
        if new_file {
            return false;
        }
        self.read_tables()
    }

    fn read_vm_code(&mut self) -> bool {
        let (first, code) = {
            let mut inp = self.inp.borrow_mut();
            let first = inp.getbits() >> 8;
            inp.addbits(8);
            let mut length = (first & 7) + 1;
            if length == 7 {
                length = (inp.getbits() >> 8) + 7;
                inp.addbits(8);
            } else if length == 8 {
                length = inp.getbits();
                inp.addbits(16);
            }
            if length == 0 {
                return false;
            }
            let length = length as usize;
            let mut code = vec![0u8; length];
            for (i, byte) in code.iter_mut().enumerate() {
                if inp.addr >= inp.read_top - 1 && !inp.read_buf() && i < length - 1 {
                    return false;
                }
                *byte = (inp.getbits() >> 8) as u8;
                inp.addbits(8);
            }
            (first, code)
        };
        self.add_vm_code(first, &code)
    }

    fn read_vm_code_ppm(&mut self) -> bool {
        let first = self.safe_ppm_char();
        if first == -1 {
            return false;
        }
        let first = first as u32;
        let mut length = (first & 7) + 1;
        if length == 7 {
            let b1 = self.safe_ppm_char();
            if b1 == -1 {
                return false;
            }
            length = b1 as u32 + 7;
        } else if length == 8 {
            let b1 = self.safe_ppm_char();
            if b1 == -1 {
                return false;
            }
            let b2 = self.safe_ppm_char();
            if b2 == -1 {
                return false;
            }
            length = b1 as u32 * 256 + b2 as u32;
        }
        if length == 0 {
            return false;
        }
        let mut code = vec![0u8; length as usize];
        for byte in code.iter_mut() {
            let c = self.safe_ppm_char();
            if c == -1 {
                return false;
            }
            *byte = c as u8;
        }
        self.add_vm_code(first, &code)
    }

    fn add_vm_code(&mut self, first: u32, code: &[u8]) -> bool {
        let mut vin = CodeInput::new(code);
        let mut filt_pos;
        if first & 0x80 != 0 {
            filt_pos = read_data(&mut vin) as usize;
            if filt_pos == 0 {
                self.init_filters();
            } else {
                filt_pos -= 1;
            }
        } else {
            filt_pos = self.last_filter;
        }
        if filt_pos > self.filters.len() || filt_pos > self.old_filter_lengths.len() {
            return false;
        }
        self.last_filter = filt_pos;
        let new_filter = filt_pos == self.filters.len();
        let parent = if new_filter {
            if filt_pos > MAX3_UNPACK_FILTERS {
                return false;
            }
            self.filters.push(Std::None);
            self.old_filter_lengths.push(0);
            self.filters.len() - 1
        } else {
            filt_pos
        };
        let total = self.stack.len();
        let kept: Vec<Option<StackFilter>> = self.stack.drain(..).filter(|f| f.is_some()).collect();
        let mut empty = total - kept.len();
        self.stack = kept;
        self.stack.resize(total, None);
        if empty == 0 {
            if self.stack.len() > MAX3_UNPACK_FILTERS {
                return false;
            }
            self.stack.push(None);
            empty = 1;
        }
        let stack_pos = self.stack.len() - empty;
        let mut block_start = read_data(&mut vin);
        if first & 0x40 != 0 {
            block_start = block_start.wrapping_add(258);
        }
        let absolute = (block_start as usize).wrapping_add(self.unp_ptr) & MASK;
        let block_length = if first & 0x20 != 0 {
            let l = read_data(&mut vin);
            self.old_filter_lengths[filt_pos] = l;
            l
        } else {
            self.old_filter_lengths.get(filt_pos).copied().unwrap_or(0)
        };
        let next_window = self.wr_ptr != self.unp_ptr && (self.wr_ptr.wrapping_sub(self.unp_ptr) & MASK) <= block_start as usize;
        let mut init_r = [0u32; 7];
        init_r[4] = block_length;
        if first & 0x10 != 0 {
            let mask = vin.getbits() >> 9;
            vin.addbits(7);
            for (i, r) in init_r.iter_mut().enumerate() {
                if mask & (1 << i) != 0 {
                    *r = read_data(&mut vin);
                }
            }
        }
        self.stack[stack_pos] = Some(StackFilter { block_start: absolute, block_length, next_window, init_r, kind: Std::None });
        if new_filter {
            let size = read_data(&mut vin) as usize;
            if size >= 0x10000 || size == 0 || vin.addr as usize + size > code.len() {
                return false;
            }
            let mut vm_code = vec![0u8; size];
            for byte in vm_code.iter_mut() {
                if vin.overflow(3) {
                    return false;
                }
                *byte = (vin.getbits() >> 8) as u8;
                vin.addbits(8);
            }
            self.filters[parent] = identify(&vm_code);
        }
        if let Some(f) = self.stack[stack_pos].as_mut() {
            f.kind = self.filters[parent];
        }
        true
    }

    fn set_memory(&mut self, start: usize, len: usize) {
        let n = len.min(VM_MEMSIZE);
        if start + n <= WINDOW {
            self.vm[..n].copy_from_slice(&self.window[start..start + n]);
        } else {
            let first = WINDOW - start;
            self.vm[..first].copy_from_slice(&self.window[start..]);
            self.vm[first..n].copy_from_slice(&self.window[..n - first]);
        }
    }

    fn execute(&mut self, f: &StackFilter) -> Result<(usize, usize), Error> {
        let mut r = f.init_r;
        r[6] = self.written as u32;
        let size = r[4] as usize & VM_MEMMASK;
        let ok = match f.kind {
            Std::None => return Err(bad()),
            Std::E8 | Std::E8E9 => filter_e8(&mut self.vm, &r, f.kind == Std::E8E9),
            Std::Itanium => filter_itanium(&mut self.vm, &r),
            Std::Delta => filter_delta(&mut self.vm, &r),
            Std::Rgb => filter_rgb(&mut self.vm, &r),
            Std::Audio => filter_audio(&mut self.vm, &r),
        };
        let offset = match f.kind {
            Std::Delta | Std::Rgb | Std::Audio if 2 * size <= VM_MEMSIZE && ok => size,
            _ => 0,
        };
        Ok((offset, size))
    }

    fn write_buf(&mut self, out: &mut Out) -> Result<(), Error> {
        let mut written_border = self.wr_ptr;
        let mut write_size = self.unp_ptr.wrapping_sub(written_border) & MASK;
        let mut i = 0;
        while i < self.stack.len() {
            let Some(mut flt) = self.stack[i] else {
                i += 1;
                continue;
            };
            if flt.next_window {
                flt.next_window = false;
                self.stack[i] = Some(flt);
                i += 1;
                continue;
            }
            let bs = flt.block_start;
            let bl = flt.block_length as usize;
            if (bs.wrapping_sub(written_border) & MASK) < write_size {
                if written_border != bs {
                    self.write_area(written_border, bs, out)?;
                    written_border = bs;
                    write_size = self.unp_ptr.wrapping_sub(written_border) & MASK;
                }
                if bl <= write_size {
                    let be = (bs + bl) & MASK;
                    self.set_memory(bs, bl);
                    let (mut data_off, mut data_size) = self.execute(&flt)?;
                    self.stack[i] = None;
                    while i + 1 < self.stack.len() {
                        let Some(next) = self.stack[i + 1] else {
                            break;
                        };
                        if next.block_start != bs || next.block_length as usize != data_size || next.next_window {
                            break;
                        }
                        let n = data_size.min(VM_MEMSIZE);
                        self.vm.copy_within(data_off..data_off + n, 0);
                        let (o, s) = self.execute(&next)?;
                        data_off = o;
                        data_size = s;
                        i += 1;
                        self.stack[i] = None;
                    }
                    let vm = std::mem::take(&mut self.vm);
                    let r = self.write_data(&vm[data_off..data_off + data_size], out);
                    self.vm = vm;
                    r?;
                    written_border = be;
                    write_size = self.unp_ptr.wrapping_sub(written_border) & MASK;
                } else {
                    for f in self.stack[i..].iter_mut().flatten() {
                        f.next_window = false;
                    }
                    self.wr_ptr = written_border;
                    return Ok(());
                }
            }
            i += 1;
        }
        self.write_area(written_border, self.unp_ptr, out)?;
        self.wr_ptr = self.unp_ptr;
        Ok(())
    }

    fn write_area(&mut self, start: usize, end: usize, out: &mut Out) -> Result<(), Error> {
        let window = std::mem::take(&mut self.window);
        let r = if end < start {
            self.write_data(&window[start..], out).and_then(|_| self.write_data(&window[..end], out))
        } else {
            self.write_data(&window[start..end], out)
        };
        self.window = window;
        r
    }

    fn write_data(&mut self, data: &[u8], out: &mut Out) -> Result<(), Error> {
        if self.written >= self.dest_size {
            self.written += data.len() as u64;
            return Ok(());
        }
        let n = (data.len() as u64).min(self.dest_size - self.written) as usize;
        if n > 0 {
            out.crc.update(&data[..n]);
            out.sink.data(&data[..n])?;
        }
        self.written += data.len() as u64;
        Ok(())
    }
}

fn filter_e8(mem: &mut [u8], r: &[u32; 7], e9: bool) -> bool {
    let size = r[4];
    let file_offset = r[6];
    if size as usize > VM_MEMSIZE || size < 4 {
        return false;
    }
    const FILE_SIZE: u32 = 0x100_0000;
    let cmp2 = if e9 { 0xE9 } else { 0xE8 };
    let mut p = 0usize;
    let mut cur = 0u32;
    while cur < size - 4 {
        let b = mem[p];
        p += 1;
        cur += 1;
        if b == 0xE8 || b == cmp2 {
            let offset = cur.wrapping_add(file_offset);
            let addr = u32::from_le_bytes([mem[p], mem[p + 1], mem[p + 2], mem[p + 3]]);
            if addr & 0x8000_0000 != 0 {
                if addr.wrapping_add(offset) & 0x8000_0000 == 0 {
                    mem[p..p + 4].copy_from_slice(&addr.wrapping_add(FILE_SIZE).to_le_bytes());
                }
            } else if addr.wrapping_sub(FILE_SIZE) & 0x8000_0000 != 0 {
                mem[p..p + 4].copy_from_slice(&addr.wrapping_sub(offset).to_le_bytes());
            }
            p += 4;
            cur += 4;
        }
    }
    true
}

fn itanium_get(data: &[u8], bit_pos: u32, count: u32) -> u32 {
    let a = (bit_pos / 8) as usize;
    let bit = bit_pos & 7;
    let field = u32::from_le_bytes([data[a], data[a + 1], data[a + 2], data[a + 3]]) >> bit;
    field & (0xFFFF_FFFF >> (32 - count))
}

fn itanium_set(data: &mut [u8], field: u32, bit_pos: u32, count: u32) {
    let a = (bit_pos / 8) as usize;
    let bit = bit_pos & 7;
    let mut and_mask = !((0xFFFF_FFFFu32 >> (32 - count)) << bit);
    let mut field = field << bit;
    for i in 0..4 {
        data[a + i] &= and_mask as u8;
        data[a + i] |= field as u8;
        and_mask = (and_mask >> 8) | 0xFF00_0000;
        field >>= 8;
    }
}

fn filter_itanium(mem: &mut [u8], r: &[u32; 7]) -> bool {
    const MASKS: [u8; 16] = [4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0];
    let size = r[4];
    let mut file_offset = r[6];
    if size as usize > VM_MEMSIZE || size < 21 {
        return false;
    }
    let mut cur = 0u32;
    let mut p = 0usize;
    file_offset >>= 4;
    while cur < size - 21 {
        let byte = (mem[p] & 0x1F) as i32 - 0x10;
        if byte >= 0 {
            let cmd_mask = MASKS[byte as usize];
            if cmd_mask != 0 {
                for i in 0..=2u32 {
                    if cmd_mask & (1 << i) != 0 {
                        let start = i * 41 + 5;
                        let data = &mut mem[p..];
                        if itanium_get(data, start + 37, 4) == 5 {
                            let offset = itanium_get(data, start + 13, 20);
                            itanium_set(data, offset.wrapping_sub(file_offset) & 0xFFFFF, start + 13, 20);
                        }
                    }
                }
            }
        }
        p += 16;
        cur += 16;
        file_offset = file_offset.wrapping_add(1);
    }
    true
}

fn filter_delta(mem: &mut [u8], r: &[u32; 7]) -> bool {
    let size = r[4] as usize;
    let channels = r[0];
    if size > VM_MEMSIZE / 2 || channels > MAX3_UNPACK_CHANNELS || channels == 0 {
        return false;
    }
    let channels = channels as usize;
    let border = size * 2;
    let mut src = 0usize;
    for ch in 0..channels {
        let mut prev = 0u8;
        let mut dest = size + ch;
        while dest < border {
            prev = prev.wrapping_sub(mem[src]);
            mem[dest] = prev;
            src += 1;
            dest += channels;
        }
    }
    true
}

fn filter_rgb(mem: &mut [u8], r: &[u32; 7]) -> bool {
    let size = r[4] as usize;
    let width = r[0].wrapping_sub(3) as usize;
    let pos_r = r[1] as usize;
    if size > VM_MEMSIZE / 2 || size < 3 || width > size || pos_r > 2 {
        return false;
    }
    let mut src = 0usize;
    for ch in 0..3 {
        let mut prev: i32 = 0;
        let mut i = ch;
        while i < size {
            let predicted = if i >= width + 3 {
                let upper = mem[size + i - width] as i32;
                let upper_left = mem[size + i - width - 3] as i32;
                let p = prev + upper - upper_left;
                let pa = (p - prev).abs();
                let pb = (p - upper).abs();
                let pc = (p - upper_left).abs();
                if pa <= pb && pa <= pc {
                    prev
                } else if pb <= pc {
                    upper
                } else {
                    upper_left
                }
            } else {
                prev
            };
            let v = (predicted as u8).wrapping_sub(mem[src]);
            src += 1;
            mem[size + i] = v;
            prev = v as i32;
            i += 3;
        }
    }
    let mut i = pos_r;
    while i + 2 < size {
        let g = mem[size + i + 1];
        mem[size + i] = mem[size + i].wrapping_add(g);
        mem[size + i + 2] = mem[size + i + 2].wrapping_add(g);
        i += 3;
    }
    true
}

fn filter_audio(mem: &mut [u8], r: &[u32; 7]) -> bool {
    let size = r[4] as usize;
    let channels = r[0];
    if size > VM_MEMSIZE / 2 || channels > 128 || channels == 0 {
        return false;
    }
    let channels = channels as usize;
    let mut src = 0usize;
    for ch in 0..channels {
        let mut prev_byte = 0u32;
        let mut prev_delta = 0u32;
        let mut dif = [0u32; 7];
        let (mut d1, mut d2) = (0i32, 0i32);
        let mut d3: i32;
        let (mut k1, mut k2, mut k3) = (0i32, 0i32, 0i32);
        let mut i = ch;
        let mut byte_count = 0u32;
        while i < size {
            d3 = d2;
            d2 = prev_delta.wrapping_sub(d1 as u32) as i32;
            d1 = prev_delta as i32;
            let mut predicted = prev_byte
                .wrapping_mul(8)
                .wrapping_add(k1.wrapping_mul(d1) as u32)
                .wrapping_add(k2.wrapping_mul(d2) as u32)
                .wrapping_add(k3.wrapping_mul(d3) as u32);
            predicted = (predicted >> 3) & 0xFF;
            let cur = mem[src] as u32;
            src += 1;
            predicted = predicted.wrapping_sub(cur);
            mem[size + i] = predicted as u8;
            prev_delta = (predicted.wrapping_sub(prev_byte) as u8 as i8) as i32 as u32;
            prev_byte = predicted;
            let d = (cur as u8 as i8 as i32).wrapping_mul(8);
            dif[0] = dif[0].wrapping_add(d.unsigned_abs());
            dif[1] = dif[1].wrapping_add(d.wrapping_sub(d1).unsigned_abs());
            dif[2] = dif[2].wrapping_add(d.wrapping_add(d1).unsigned_abs());
            dif[3] = dif[3].wrapping_add(d.wrapping_sub(d2).unsigned_abs());
            dif[4] = dif[4].wrapping_add(d.wrapping_add(d2).unsigned_abs());
            dif[5] = dif[5].wrapping_add(d.wrapping_sub(d3).unsigned_abs());
            dif[6] = dif[6].wrapping_add(d.wrapping_add(d3).unsigned_abs());
            if byte_count & 0x1F == 0 {
                let mut min_dif = dif[0];
                let mut num_min = 0;
                dif[0] = 0;
                for j in 1..7 {
                    if dif[j] < min_dif {
                        min_dif = dif[j];
                        num_min = j;
                    }
                    dif[j] = 0;
                }
                match num_min {
                    1 if k1 >= -16 => k1 -= 1,
                    2 if k1 < 16 => k1 += 1,
                    3 if k2 >= -16 => k2 -= 1,
                    4 if k2 < 16 => k2 += 1,
                    5 if k3 >= -16 => k3 -= 1,
                    6 if k3 < 16 => k3 += 1,
                    _ => {}
                }
            }
            i += channels;
            byte_count += 1;
        }
    }
    true
}
