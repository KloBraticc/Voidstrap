use super::{CHUNK, Error, Sink, ZIP_BAD};
use crc32fast::Hasher;
use miniz_oxide::inflate::stream::{InflateState, inflate};
use miniz_oxide::{DataFormat, MZError, MZFlush, MZStatus};
use std::fs::File;
use std::io::{BufRead, BufReader, ErrorKind, Read};
use std::path::Path;

const LOCAL: u32 = 0x0403_4b50;
const DESCRIPTOR: u32 = 0x0807_4b50;
const MAGIC: u64 = 0xFFFF_FFFF;
const CP437: &str = "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■\u{a0}";

fn bad() -> Error {
    Error::rejected(ZIP_BAD)
}

fn exact(r: &mut impl Read, buf: &mut [u8]) -> Result<(), Error> {
    r.read_exact(buf).map_err(|e| if e.kind() == ErrorKind::UnexpectedEof { bad() } else { e.into() })
}

fn u16_at(b: &[u8], o: usize) -> u16 {
    u16::from_le_bytes([b[o], b[o + 1]])
}

fn u32_at(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

fn u64_at(b: &[u8], o: usize) -> u64 {
    u64::from_le_bytes(b[o..o + 8].try_into().unwrap_or([0; 8]))
}

pub fn cp437(raw: &[u8]) -> String {
    let high: Vec<char> = CP437.chars().collect();
    raw.iter().map(|&b| if b < 0x80 { b as char } else { high[(b - 0x80) as usize] }).collect()
}

fn name(raw: &[u8]) -> String {
    match std::str::from_utf8(raw) {
        Ok(s) => s.to_string(),
        Err(_) => cp437(raw),
    }
}

fn zip64(extra: &[u8], size: &mut u64, csize: &mut u64) {
    let mut p = 0;
    while p + 4 <= extra.len() {
        let id = u16_at(extra, p);
        let len = u16_at(extra, p + 2) as usize;
        let body = p + 4;
        if body + len > extra.len() {
            return;
        }
        if id == 1 {
            let mut q = body;
            if *size == MAGIC && q + 8 <= body + len {
                *size = u64_at(extra, q);
                q += 8;
            }
            if *csize == MAGIC && q + 8 <= body + len {
                *csize = u64_at(extra, q);
            }
            return;
        }
        p = body + len;
    }
}

pub fn decode(path: &Path, sink: &mut dyn Sink) -> Result<(), Error> {
    decode_reader(File::open(path)?, sink)
}

pub fn decode_reader(input: impl Read, sink: &mut dyn Sink) -> Result<(), Error> {
    let mut r = BufReader::with_capacity(CHUNK, input);
    let mut out = vec![0u8; CHUNK];
    loop {
        let mut loc = [0u8; 30];
        match r.read_exact(&mut loc) {
            Ok(()) => {}
            Err(e) if e.kind() == ErrorKind::UnexpectedEof => return Ok(()),
            Err(e) => return Err(e.into()),
        }
        if u32_at(&loc, 0) != LOCAL {
            return Ok(());
        }
        let h = &loc[4..];
        let flags = u16_at(&h, 2);
        let method = u16_at(&h, 4);
        let crc = u32_at(&h, 10);
        let mut csize = u32_at(&h, 14) as u64;
        let mut size = u32_at(&h, 18) as u64;
        let mut raw = vec![0u8; u16_at(&h, 22) as usize];
        exact(&mut r, &mut raw)?;
        let mut extra = vec![0u8; u16_at(&h, 24) as usize];
        exact(&mut r, &mut extra)?;
        if flags & 1 != 0 {
            return Err(bad());
        }
        let descriptor = flags & 8 != 0;
        if csize == MAGIC || size == MAGIC {
            zip64(&extra, &mut size, &mut csize);
        }
        let entry = name(&raw);
        let directory = entry.ends_with('/');
        if method != 0 && method != 8 {
            return Err(bad());
        }
        if method == 0 && descriptor {
            return Err(bad());
        }
        if !directory {
            sink.entry(&entry, if descriptor { -1 } else { size as i64 })?;
        }
        let mut hash = Hasher::new();
        let mut written = 0u64;
        let mut consumed = 0u64;
        if method == 0 {
            let mut left = csize;
            while left > 0 {
                let n = (left as usize).min(out.len());
                exact(&mut r, &mut out[..n])?;
                hash.update(&out[..n]);
                if !directory {
                    sink.data(&out[..n])?;
                }
                left -= n as u64;
            }
            written = csize;
            consumed = csize;
        } else {
            let mut state = InflateState::new_boxed(DataFormat::Raw);
            loop {
                let input = r.fill_buf()?;
                let eof = input.is_empty();
                let res = inflate(&mut state, input, &mut out, if eof { MZFlush::Finish } else { MZFlush::None });
                r.consume(res.bytes_consumed);
                consumed += res.bytes_consumed as u64;
                if res.bytes_written > 0 {
                    hash.update(&out[..res.bytes_written]);
                    written += res.bytes_written as u64;
                    if !directory {
                        sink.data(&out[..res.bytes_written])?;
                    }
                }
                match res.status {
                    Ok(MZStatus::StreamEnd) => break,
                    Ok(_) => {
                        if eof && res.bytes_written == 0 {
                            return Err(bad());
                        }
                    }
                    Err(MZError::Buf) => {
                        if eof || (res.bytes_consumed == 0 && res.bytes_written == 0) {
                            return Err(bad());
                        }
                    }
                    Err(_) => return Err(bad()),
                }
            }
        }
        let actual = hash.finalize();
        if descriptor {
            let mut d = [0u8; 4];
            exact(&mut r, &mut d)?;
            let mut first = u32_at(&d, 0);
            if first == DESCRIPTOR {
                exact(&mut r, &mut d)?;
                first = u32_at(&d, 0);
            }
            let big = written > MAGIC || consumed > MAGIC;
            let (dc, ds) = if big {
                let mut s = [0u8; 16];
                exact(&mut r, &mut s)?;
                (u64_at(&s, 0), u64_at(&s, 8))
            } else {
                let mut s = [0u8; 8];
                exact(&mut r, &mut s)?;
                (u32_at(&s, 0) as u64, u32_at(&s, 4) as u64)
            };
            if first != actual || dc != consumed || ds != written {
                return Err(bad());
            }
        } else if crc != actual || size != written || csize != consumed {
            return Err(bad());
        }
        if !directory {
            sink.end()?;
        }
    }
}
