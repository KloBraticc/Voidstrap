use super::huff::{BitRead, Table, decode_symbol};
use super::{Error, MAX_ENTRIES, MAX_EXTRACTED, RAR_DAMAGED, RAR_MEMORY, RAR_PASSWORD, RAR_SPLIT, Sink, too_big, too_many};
use crc32fast::Hasher;
use std::fs::File;
use std::io::{ErrorKind, Read, Seek, SeekFrom};
use std::path::Path;

const NC: usize = 306;
const DCB: usize = 64;
const DCX: usize = 80;
const LDC: usize = 16;
const RC: usize = 44;
const BC: usize = 20;
const QUICK_BITS: u32 = 10;
const MAX_SIZE: usize = 0x8000;
const UNPACK_MAX_WRITE: usize = 0x40_0000;
const MAX_INC_LZ_MATCH: usize = 0x1001 + 3;
const MAX_FILTER_BLOCK: u32 = 0x40_0000;
const MAX_FILTERS: usize = 8192;
const MAX_WINDOW: u64 = 128 * 1024 * 1024;
const FILTER_DELTA: u32 = 0;
const FILTER_E8: u32 = 1;
const FILTER_E8E9: u32 = 2;
const FILTER_ARM: u32 = 3;
const FILTER_NONE: u32 = 99;

fn damaged() -> Error {
    Error::rejected(RAR_DAMAGED)
}

struct Item {
    name: String,
    directory: bool,
    unpacked: u64,
    data_start: u64,
    packed: u64,
    has_crc: bool,
    crc: u32,
    method: u32,
    solid: bool,
    extra_dist: bool,
    dict: u64,
    redirect: bool,
}

pub fn decode(path: &Path, sink: &mut dyn Sink) -> Result<(), Error> {
    let items = scan(path)?;
    let mut total = 0u64;
    let mut dict = 0u64;
    for it in &items {
        if it.directory || it.redirect {
            continue;
        }
        total = total.saturating_add(it.unpacked);
        if it.method != 0 {
            dict = dict.max(it.dict);
        }
    }
    let mut window: u64 = 1 << 18;
    let want = dict.min(total.max(1 << 16));
    while window < want {
        window <<= 1;
    }
    if dict > 0 && window > MAX_WINDOW {
        return Err(Error::rejected(RAR_MEMORY));
    }
    let mut decoder = if dict > 0 { Some(Decoder::new(window as usize)) } else { None };
    let mut f = File::open(path)?;
    let mut buf = vec![0u8; 65536];
    for it in &items {
        if it.directory || it.redirect {
            continue;
        }
        sink.entry(&it.name, it.unpacked as i64)?;
        let mut crc = Hasher::new();
        if it.method == 0 {
            f.seek(SeekFrom::Start(it.data_start))?;
            let mut left = it.packed;
            while left > 0 {
                let want = (left as usize).min(buf.len());
                let n = f.read(&mut buf[..want])?;
                if n == 0 {
                    return Err(damaged());
                }
                crc.update(&buf[..n]);
                sink.data(&buf[..n])?;
                left -= n as u64;
            }
        } else {
            let d = decoder.as_mut().ok_or_else(damaged)?;
            d.unpack(&mut f, it, &mut Out { sink, crc: &mut crc })?;
        }
        if it.has_crc && crc.finalize() != it.crc {
            return Err(damaged());
        }
        sink.end()?;
    }
    Ok(())
}

fn scan(path: &Path) -> Result<Vec<Item>, Error> {
    let mut items = Vec::new();
    let mut f = File::open(path)?;
    let mut sig = [0u8; 8];
    exact(&mut f, &mut sig)?;
    if &sig[..4] != b"Rar!" || sig[6] != 1 || sig[7] != 0 {
        return Err(Error::rejected("The package is not a rar 5 archive."));
    }
    let length = f.metadata()?.len();
    let mut pos = 8u64;
    while pos + 7 <= length {
        f.seek(SeekFrom::Start(pos + 4))?;
        let (header_size, size_len) = read_vint(&mut f)?;
        if header_size == 0 || header_size > 2 * 1024 * 1024 {
            return Err(damaged());
        }
        let header_start = pos + 4 + size_len;
        let mut header = vec![0u8; header_size as usize];
        f.seek(SeekFrom::Start(header_start))?;
        exact(&mut f, &mut header)?;
        let mut p = 0usize;
        let kind = vint(&header, &mut p)?;
        let flags = vint(&header, &mut p)?;
        let extra_size = if flags & 1 != 0 { vint(&header, &mut p)? } else { 0 };
        let data_size = if flags & 2 != 0 { vint(&header, &mut p)? } else { 0 };
        let data_start = header_start + header_size;
        if kind == 1 {
            let archive_flags = vint(&header, &mut p)?;
            if archive_flags & 1 != 0 {
                return Err(Error::rejected(RAR_SPLIT));
            }
        } else if kind == 2 {
            let file_flags = vint(&header, &mut p)?;
            let unpacked = vint(&header, &mut p)?;
            vint(&header, &mut p)?;
            if file_flags & 2 != 0 {
                p += 4;
            }
            let mut has_crc = false;
            let mut crc = 0u32;
            if file_flags & 4 != 0 {
                has_crc = true;
                let b = header.get(p..p + 4).ok_or_else(damaged)?;
                crc = u32::from_le_bytes([b[0], b[1], b[2], b[3]]);
                p += 4;
            }
            let info = vint(&header, &mut p)?;
            vint(&header, &mut p)?;
            let name_length = vint(&header, &mut p)? as usize;
            let raw = header.get(p..p.checked_add(name_length).ok_or_else(damaged)?).ok_or_else(damaged)?;
            let name = String::from_utf8_lossy(raw).into_owned();
            let version = info & 0x3F;
            let solid = info & 0x40 != 0;
            let method = ((info >> 7) & 7) as u32;
            let power = (info >> 10) & if version == 0 { 0xF } else { 0x1F };
            let mut dict = 0x20000u64 << power;
            let mut extra_dist = false;
            if version == 1 {
                let fraction = (info >> 15) & 0x1F;
                dict += dict / 32 * fraction;
                extra_dist = info & 0x10_0000 == 0;
            } else if version != 0 {
                return Err(Error::rejected("This rar package uses a newer format than Voidstrap supports."));
            }
            if flags & 0x08 != 0 || flags & 0x10 != 0 {
                return Err(Error::rejected(RAR_SPLIT));
            }
            if file_flags & 8 != 0 {
                return Err(damaged());
            }
            let mut redirect = false;
            if extra_size > 0 {
                let mut q = header.len().checked_sub(extra_size as usize).ok_or_else(damaged)?;
                while q < header.len() {
                    let rec_size = vint(&header, &mut q)?;
                    let rec_start = q;
                    let rec_type = vint(&header, &mut q)?;
                    if rec_type == 1 {
                        return Err(Error::rejected(RAR_PASSWORD));
                    }
                    if rec_type == 5 {
                        redirect = true;
                    }
                    q = rec_start.saturating_add(rec_size as usize);
                }
            }
            if unpacked > MAX_EXTRACTED {
                return Err(too_big());
            }
            items.push(Item {
                name,
                directory: file_flags & 1 != 0,
                unpacked,
                data_start,
                packed: data_size,
                has_crc,
                crc,
                method,
                solid,
                extra_dist,
                dict,
                redirect,
            });
            if items.len() > MAX_ENTRIES {
                return Err(too_many());
            }
        } else if kind == 4 {
            return Err(Error::rejected(RAR_PASSWORD));
        } else if kind == 5 {
            break;
        }
        pos = data_start.checked_add(data_size).ok_or_else(damaged)?;
    }
    Ok(items)
}

fn exact(f: &mut File, buf: &mut [u8]) -> Result<(), Error> {
    f.read_exact(buf).map_err(|e| if e.kind() == ErrorKind::UnexpectedEof { damaged() } else { e.into() })
}

fn read_vint(f: &mut File) -> Result<(u64, u64), Error> {
    let mut v = 0u64;
    for i in 0..10 {
        let mut b = [0u8; 1];
        exact(f, &mut b)?;
        v |= ((b[0] & 0x7F) as u64) << (7 * i);
        if b[0] & 0x80 == 0 {
            return Ok((v, i as u64 + 1));
        }
    }
    Err(damaged())
}

fn vint(b: &[u8], p: &mut usize) -> Result<u64, Error> {
    let mut v = 0u64;
    for i in 0..10 {
        let x = *b.get(*p).ok_or_else(damaged)?;
        *p += 1;
        v |= ((x & 0x7F) as u64) << (7 * i);
        if x & 0x80 == 0 {
            return Ok(v);
        }
    }
    Err(damaged())
}

struct Out<'a> {
    sink: &'a mut dyn Sink,
    crc: &'a mut Hasher,
}

struct Bits {
    buf: Vec<u8>,
    addr: isize,
    bit: isize,
}

impl BitRead for Bits {
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

impl Bits {
    fn getbits32(&self) -> u32 {
        let a = self.addr as usize;
        let mut b = u32::from_be_bytes([self.buf[a], self.buf[a + 1], self.buf[a + 2], self.buf[a + 3]]);
        b = b.wrapping_shl(self.bit as u32);
        b | ((self.buf[a + 4] as u32) >> (8 - self.bit))
    }
}

struct Filter {
    block_start: usize,
    block_length: u32,
    kind: u32,
    channels: usize,
    next_window: bool,
}

struct Decoder {
    window: Vec<u8>,
    win_size: usize,
    win_mask: usize,
    bits: Bits,
    read_top: isize,
    read_border: isize,
    unp_ptr: usize,
    wr_ptr: usize,
    write_border: usize,
    old_dist: [i64; 4],
    last_length: u32,
    tables_read: bool,
    ld: Table,
    dd: Table,
    ldd: Table,
    rd: Table,
    bd: Table,
    filters: Vec<Filter>,
    block_start: isize,
    block_size: isize,
    block_bit_size: isize,
    last_block: bool,
    table_present: bool,
    extra_dist: bool,
    packed_left: u64,
    dest_size: u64,
    written: u64,
}

impl Decoder {
    fn new(size: usize) -> Decoder {
        Decoder {
            window: vec![0; size],
            win_size: size,
            win_mask: size - 1,
            bits: Bits { buf: vec![0; MAX_SIZE + 64], addr: 0, bit: 0 },
            read_top: 0,
            read_border: 0,
            unp_ptr: 0,
            wr_ptr: 0,
            write_border: 0,
            old_dist: [-1; 4],
            last_length: 0,
            tables_read: false,
            ld: Table::new(),
            dd: Table::new(),
            ldd: Table::new(),
            rd: Table::new(),
            bd: Table::new(),
            filters: Vec::new(),
            block_start: 0,
            block_size: -1,
            block_bit_size: 0,
            last_block: false,
            table_present: false,
            extra_dist: false,
            packed_left: 0,
            dest_size: 0,
            written: 0,
        }
    }

    fn unpack(&mut self, f: &mut File, it: &Item, out: &mut Out) -> Result<(), Error> {
        self.extra_dist = it.extra_dist;
        f.seek(SeekFrom::Start(it.data_start))?;
        self.packed_left = it.packed;
        self.dest_size = it.unpacked;
        self.written = 0;
        if !it.solid {
            self.old_dist = [-1; 4];
            self.last_length = 0;
            self.unp_ptr = 0;
            self.wr_ptr = 0;
            self.write_border = self.win_size.min(UNPACK_MAX_WRITE) & self.win_mask;
            self.tables_read = false;
        }
        self.filters.clear();
        self.bits.addr = 0;
        self.bits.bit = 0;
        self.read_top = 0;
        self.read_border = 0;
        self.block_size = -1;
        self.block_start = 0;
        self.bits.buf.fill(0);
        if !self.read_buf(f)? {
            return Ok(());
        }
        if !self.read_block_header(f)? || !self.read_tables(f)? || !self.tables_read {
            return Ok(());
        }
        loop {
            self.unp_ptr &= self.win_mask;
            if self.bits.addr >= self.read_border {
                let mut done = false;
                while self.bits.addr > self.block_start + self.block_size - 1
                    || (self.bits.addr == self.block_start + self.block_size - 1 && self.bits.bit >= self.block_bit_size)
                {
                    if self.last_block {
                        done = true;
                        break;
                    }
                    if !self.read_block_header(f)? || !self.read_tables(f)? {
                        return Ok(());
                    }
                }
                if done || !self.read_buf(f)? {
                    break;
                }
            }
            if (self.write_border.wrapping_sub(self.unp_ptr) & self.win_mask) < MAX_INC_LZ_MATCH && self.write_border != self.unp_ptr {
                self.write_buf(out)?;
                if self.written > self.dest_size {
                    return Ok(());
                }
            }
            let main_slot = decode_symbol(&mut self.bits, &self.ld);
            if main_slot < 256 {
                self.window[self.unp_ptr] = main_slot as u8;
                self.unp_ptr += 1;
                continue;
            }
            if main_slot >= 262 {
                let mut length = self.slot_to_length(main_slot - 262);
                let dist_slot = decode_symbol(&mut self.bits, &self.dd);
                let mut distance: i64 = 1;
                let d_bits: u32;
                if dist_slot < 4 {
                    d_bits = 0;
                    distance += dist_slot as i64;
                } else {
                    d_bits = (dist_slot / 2 - 1) as u32;
                    distance += ((2 | (dist_slot & 1)) as i64) << d_bits;
                }
                if d_bits > 0 {
                    if d_bits >= 4 {
                        if d_bits > 4 {
                            if d_bits > 36 {
                                let hi = d_bits - 36;
                                distance += (((self.bits.getbits32() as u64) >> (32 - hi)) << 36) as i64;
                                self.bits.addbits(hi as isize);
                                distance += ((self.bits.getbits32() as u64) << 4) as i64;
                                self.bits.addbits(32);
                            } else {
                                distance += (((self.bits.getbits32() as u64) >> (36 - d_bits)) << 4) as i64;
                                self.bits.addbits(d_bits as isize - 4);
                            }
                        }
                        distance += decode_symbol(&mut self.bits, &self.ldd) as i64;
                    } else {
                        distance += ((self.bits.getbits32() as u64) >> (32 - d_bits)) as i64;
                        self.bits.addbits(d_bits as isize);
                    }
                }
                if distance > 0x100 {
                    length += 1;
                    if distance > 0x2000 {
                        length += 1;
                        if distance > 0x40000 {
                            length += 1;
                        }
                    }
                }
                self.old_dist = [distance, self.old_dist[0], self.old_dist[1], self.old_dist[2]];
                self.last_length = length;
                self.copy_string(length, distance)?;
                continue;
            }
            if main_slot == 256 {
                let flt = self.read_filter(f)?;
                self.add_filter(flt, out)?;
                continue;
            }
            if main_slot == 257 {
                if self.last_length != 0 {
                    self.copy_string(self.last_length, self.old_dist[0])?;
                }
                continue;
            }
            let dist_num = main_slot - 258;
            let distance = self.old_dist[dist_num];
            for i in (1..=dist_num).rev() {
                self.old_dist[i] = self.old_dist[i - 1];
            }
            self.old_dist[0] = distance;
            let length_slot = decode_symbol(&mut self.bits, &self.rd);
            let length = self.slot_to_length(length_slot);
            self.last_length = length;
            self.copy_string(length, distance)?;
        }
        self.write_buf(out)
    }

    fn copy_string(&mut self, length: u32, distance: i64) -> Result<(), Error> {
        if distance <= 0 || distance > self.win_size as i64 {
            return Err(damaged());
        }
        let mask = self.win_mask;
        let mut src = ((self.unp_ptr as i64 - distance) & mask as i64) as usize;
        let mut dst = self.unp_ptr;
        for _ in 0..length {
            self.window[dst] = self.window[src];
            src = (src + 1) & mask;
            dst = (dst + 1) & mask;
        }
        self.unp_ptr = dst;
        Ok(())
    }

    fn slot_to_length(&mut self, slot: usize) -> u32 {
        let l_bits: u32;
        let mut length = 2u32;
        if slot < 8 {
            l_bits = 0;
            length += slot as u32;
        } else {
            l_bits = (slot / 4 - 1) as u32;
            length += ((4 | (slot & 3)) as u32) << l_bits;
        }
        if l_bits > 0 {
            length += self.bits.getbits() >> (16 - l_bits);
            self.bits.addbits(l_bits as isize);
        }
        length
    }

    fn read_buf(&mut self, f: &mut File) -> Result<bool, Error> {
        let mut data_size = self.read_top - self.bits.addr;
        if data_size < 0 {
            return Ok(false);
        }
        self.block_start -= self.bits.addr;
        if self.bits.addr > (MAX_SIZE / 2) as isize {
            if data_size > 0 {
                let a = self.bits.addr as usize;
                self.bits.buf.copy_within(a..a + data_size as usize, 0);
            }
            self.bits.addr = 0;
            self.read_top = data_size;
        } else {
            data_size = self.read_top;
        }
        let mut read: isize = 0;
        if MAX_SIZE as isize != data_size && self.packed_left > 0 {
            let want = ((MAX_SIZE as isize - data_size) as u64).min(self.packed_left) as usize;
            let start = data_size as usize;
            let n = f.read(&mut self.bits.buf[start..start + want])?;
            if n == 0 {
                read = -1;
            } else {
                read = n as isize;
                self.packed_left -= n as u64;
            }
        }
        if read > 0 {
            self.read_top += read;
        }
        let top = self.read_top as usize;
        self.bits.buf[top..].fill(0);
        self.read_border = self.read_top - 30;
        self.block_start += self.bits.addr;
        if self.block_size != -1 {
            self.read_border = self.read_border.min(self.block_start + self.block_size - 1);
        }
        Ok(read != -1)
    }

    fn read_block_header(&mut self, f: &mut File) -> Result<bool, Error> {
        if self.bits.addr > self.read_top - 7 && !self.read_buf(f)? {
            return Ok(false);
        }
        self.bits.addbits((8 - self.bits.bit) & 7);
        let flags = self.bits.getbits() >> 8;
        self.bits.addbits(8);
        let byte_count = ((flags >> 3) & 3) + 1;
        if byte_count == 4 {
            return Ok(false);
        }
        self.block_bit_size = (flags & 7) as isize + 1;
        let saved = self.bits.getbits() >> 8;
        self.bits.addbits(8);
        let mut size = 0u32;
        for i in 0..byte_count {
            size += (self.bits.getbits() >> 8) << (i * 8);
            self.bits.addbits(8);
        }
        let check = (0x5A ^ flags ^ size ^ (size >> 8) ^ (size >> 16)) & 0xFF;
        if check != saved {
            return Ok(false);
        }
        self.block_size = size as isize;
        self.block_start = self.bits.addr;
        self.read_border = self.read_border.min(self.block_start + self.block_size - 1);
        self.last_block = flags & 0x40 != 0;
        self.table_present = flags & 0x80 != 0;
        Ok(true)
    }

    fn read_tables(&mut self, f: &mut File) -> Result<bool, Error> {
        if !self.table_present {
            return Ok(true);
        }
        if self.bits.addr > self.read_top - 25 && !self.read_buf(f)? {
            return Ok(false);
        }
        let mut bit_length = [0u8; BC];
        let mut i = 0;
        while i < BC {
            let length = self.bits.getbits() >> 12;
            self.bits.addbits(4);
            if length == 15 {
                let mut zero = self.bits.getbits() >> 12;
                self.bits.addbits(4);
                if zero == 0 {
                    bit_length[i] = 15;
                } else {
                    zero += 2;
                    while zero > 0 && i < BC {
                        bit_length[i] = 0;
                        i += 1;
                        zero -= 1;
                    }
                    i -= 1;
                }
            } else {
                bit_length[i] = length as u8;
            }
            i += 1;
        }
        self.bd.make(&bit_length, BC, QUICK_BITS - 3);
        let dc = if self.extra_dist { DCX } else { DCB };
        let size = NC + dc + LDC + RC;
        let mut table = vec![0u8; size];
        let mut i = 0;
        while i < size {
            if self.bits.addr > self.read_top - 5 && !self.read_buf(f)? {
                return Ok(false);
            }
            let number = decode_symbol(&mut self.bits, &self.bd);
            if number < 16 {
                table[i] = number as u8;
                i += 1;
            } else if number < 18 {
                let mut n = if number == 16 {
                    let v = (self.bits.getbits() >> 13) + 3;
                    self.bits.addbits(3);
                    v
                } else {
                    let v = (self.bits.getbits() >> 9) + 11;
                    self.bits.addbits(7);
                    v
                };
                if i == 0 {
                    return Ok(false);
                }
                while n > 0 && i < size {
                    table[i] = table[i - 1];
                    i += 1;
                    n -= 1;
                }
            } else {
                let mut n = if number == 18 {
                    let v = (self.bits.getbits() >> 13) + 3;
                    self.bits.addbits(3);
                    v
                } else {
                    let v = (self.bits.getbits() >> 9) + 11;
                    self.bits.addbits(7);
                    v
                };
                while n > 0 && i < size {
                    table[i] = 0;
                    i += 1;
                    n -= 1;
                }
            }
        }
        self.tables_read = true;
        if self.bits.addr > self.read_top {
            return Ok(false);
        }
        self.ld.make(&table, NC, QUICK_BITS);
        self.dd.make(&table[NC..], dc, QUICK_BITS - 3);
        self.ldd.make(&table[NC + dc..], LDC, QUICK_BITS - 3);
        self.rd.make(&table[NC + dc + LDC..], RC, QUICK_BITS - 3);
        Ok(true)
    }

    fn read_filter(&mut self, f: &mut File) -> Result<Filter, Error> {
        if self.bits.addr > self.read_top - 16 {
            self.read_buf(f)?;
        }
        let block_start = self.read_filter_data() as usize;
        let mut block_length = self.read_filter_data();
        if block_length > MAX_FILTER_BLOCK {
            block_length = 0;
        }
        let kind = self.bits.getbits() >> 13;
        self.bits.addbits(3);
        let mut channels = 0;
        if kind == FILTER_DELTA {
            channels = (self.bits.getbits() >> 11) as usize + 1;
            self.bits.addbits(5);
        }
        Ok(Filter { block_start, block_length, kind, channels, next_window: false })
    }

    fn read_filter_data(&mut self) -> u32 {
        let count = (self.bits.getbits() >> 14) + 1;
        self.bits.addbits(2);
        let mut data = 0u32;
        for i in 0..count {
            data = data.wrapping_add((self.bits.getbits() >> 8) << (i * 8));
            self.bits.addbits(8);
        }
        data
    }

    fn add_filter(&mut self, mut f: Filter, out: &mut Out) -> Result<(), Error> {
        if self.filters.len() >= MAX_FILTERS {
            self.write_buf(out)?;
            if self.filters.len() >= MAX_FILTERS {
                self.filters.clear();
            }
        }
        f.next_window = self.wr_ptr != self.unp_ptr && (self.wr_ptr.wrapping_sub(self.unp_ptr) & self.win_mask) <= f.block_start;
        f.block_start = f.block_start.wrapping_add(self.unp_ptr) & self.win_mask;
        self.filters.push(f);
        Ok(())
    }

    fn write_buf(&mut self, out: &mut Out) -> Result<(), Error> {
        let mask = self.win_mask;
        let mut write_size = self.unp_ptr.wrapping_sub(self.wr_ptr) & mask;
        for i in 0..self.filters.len() {
            if self.filters[i].kind == FILTER_NONE {
                continue;
            }
            if self.filters[i].next_window {
                if (self.filters[i].block_start.wrapping_sub(self.wr_ptr) & mask) <= write_size {
                    self.filters[i].next_window = false;
                }
                continue;
            }
            let bs = self.filters[i].block_start;
            let bl = self.filters[i].block_length as usize;
            if (bs.wrapping_sub(self.wr_ptr) & mask) < write_size {
                if self.wr_ptr != bs {
                    self.write_area(self.wr_ptr, bs, out)?;
                    self.wr_ptr = bs;
                    write_size = self.unp_ptr.wrapping_sub(self.wr_ptr) & mask;
                }
                if bl <= write_size {
                    let be = (bs + bl) & mask;
                    let mut mem = vec![0u8; bl];
                    if bs < be || be == 0 {
                        mem.copy_from_slice(&self.window[bs..bs + bl]);
                    } else {
                        let first = self.win_size - bs;
                        mem[..first].copy_from_slice(&self.window[bs..]);
                        mem[first..].copy_from_slice(&self.window[..be]);
                    }
                    let filtered = self.apply_filter(mem, &self.filters[i]);
                    self.filters[i].kind = FILTER_NONE;
                    if let Some(data) = filtered {
                        self.write_data(&data, out)?;
                    }
                    self.wr_ptr = be;
                    write_size = self.unp_ptr.wrapping_sub(self.wr_ptr) & mask;
                } else {
                    self.write_border = bs;
                    return Ok(());
                }
            }
        }
        self.filters.retain(|f| f.kind != FILTER_NONE);
        self.write_area(self.wr_ptr, self.unp_ptr, out)?;
        self.wr_ptr = self.unp_ptr;
        self.write_border = (self.unp_ptr + self.win_size.min(UNPACK_MAX_WRITE)) & mask;
        if self.write_border == self.unp_ptr
            || (self.wr_ptr != self.unp_ptr
                && (self.wr_ptr.wrapping_sub(self.unp_ptr) & mask) < (self.write_border.wrapping_sub(self.unp_ptr) & mask))
        {
            self.write_border = self.wr_ptr;
        }
        Ok(())
    }

    fn write_area(&mut self, start: usize, end: usize, out: &mut Out) -> Result<(), Error> {
        if end < start {
            let window = std::mem::take(&mut self.window);
            let r = self.write_data(&window[start..], out).and_then(|_| self.write_data(&window[..end], out));
            self.window = window;
            r
        } else {
            let window = std::mem::take(&mut self.window);
            let r = self.write_data(&window[start..end], out);
            self.window = window;
            r
        }
    }

    fn write_data(&mut self, data: &[u8], out: &mut Out) -> Result<(), Error> {
        if self.written >= self.dest_size {
            self.written += data.len() as u64;
            return Ok(());
        }
        let left = self.dest_size - self.written;
        let n = (data.len() as u64).min(left) as usize;
        if n > 0 {
            out.crc.update(&data[..n]);
            out.sink.data(&data[..n])?;
        }
        self.written += data.len() as u64;
        Ok(())
    }

    fn apply_filter(&self, mut data: Vec<u8>, f: &Filter) -> Option<Vec<u8>> {
        let size = data.len();
        match f.kind {
            FILTER_E8 | FILTER_E8E9 => {
                let file_offset = self.written as u32 as u64;
                const FILE_SIZE: i64 = 0x100_0000;
                let cmp2 = if f.kind == FILTER_E8E9 { 0xE9 } else { 0xE8 };
                let mut p = 0usize;
                let mut cur = 0usize;
                while cur + 4 < size {
                    let b = data[p];
                    p += 1;
                    cur += 1;
                    if b == 0xE8 || b == cmp2 {
                        let offset = ((cur as u64 + file_offset) % FILE_SIZE as u64) as i64;
                        let addr = i32::from_le_bytes([data[p], data[p + 1], data[p + 2], data[p + 3]]);
                        if addr < 0 {
                            if (addr as i64 + offset) & 0x8000_0000 == 0 {
                                data[p..p + 4].copy_from_slice(&addr.wrapping_add(FILE_SIZE as i32).to_le_bytes());
                            }
                        } else if addr.wrapping_sub(FILE_SIZE as i32) < 0 {
                            data[p..p + 4].copy_from_slice(&((addr as i64 - offset) as i32).to_le_bytes());
                        }
                        p += 4;
                        cur += 4;
                    }
                }
                Some(data)
            }
            FILTER_ARM => {
                let file_offset = self.written as u32 as u64;
                let mut cur = 0usize;
                while cur + 3 < size {
                    if data[cur + 3] == 0xEB {
                        let mut offset = data[cur] as i32 + data[cur + 1] as i32 * 0x100 + data[cur + 2] as i32 * 0x10000;
                        offset = offset.wrapping_sub(((file_offset + cur as u64) / 4) as i32);
                        data[cur] = offset as u8;
                        data[cur + 1] = (offset >> 8) as u8;
                        data[cur + 2] = (offset >> 16) as u8;
                    }
                    cur += 4;
                }
                Some(data)
            }
            FILTER_DELTA => {
                let channels = f.channels;
                let mut src = 0usize;
                let mut dst = vec![0u8; size];
                for ch in 0..channels {
                    let mut prev = 0u8;
                    let mut d = ch;
                    while d < size {
                        prev = prev.wrapping_sub(data[src]);
                        dst[d] = prev;
                        src += 1;
                        d += channels;
                    }
                }
                Some(dst)
            }
            _ => None,
        }
    }
}
