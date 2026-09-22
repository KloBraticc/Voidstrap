use crc32fast::Hasher;
use miniz_oxide::inflate::stream::{InflateState, inflate};
use miniz_oxide::{DataFormat, MZFlush, MZStatus};
use std::collections::HashMap;
use std::fs::File;
use std::io::{self, BufWriter, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

const LOCAL: u32 = 0x0403_4b50;
const CENTRAL: u32 = 0x0201_4b50;
const END: u32 = 0x0605_4b50;
const UTF8: u16 = 0x0800;
const DOS_TIME: u16 = 0;
const DOS_DATE: u16 = 0x21;
const LIMIT: u64 = 64 * 1024 * 1024;

pub struct Entry {
    pub name: String,
    pub method: u16,
    pub compressed: u64,
    pub size: u64,
    pub central_offset: usize,
    pub central_length: usize,
    pub local_offset: u64,
}

pub struct Directory {
    pub central_start: u64,
    pub central: Vec<u8>,
    pub entries: Vec<Entry>,
}

pub enum Source {
    File(PathBuf),
    Bytes(Vec<u8>),
}

impl Source {
    fn open(&self) -> io::Result<Box<dyn Read + '_>> {
        Ok(match self {
            Source::File(p) => Box::new(File::open(p)?),
            Source::Bytes(b) => Box::new(b.as_slice()),
        })
    }

    fn length(&self) -> io::Result<u64> {
        Ok(match self {
            Source::File(p) => std::fs::metadata(p).map(|m| m.len()).unwrap_or(0),
            Source::Bytes(b) => b.len() as u64,
        })
    }
}

fn fail(message: &str) -> io::Error {
    io::Error::other(message.to_string())
}

fn le16(b: &[u8], o: usize) -> u16 {
    u16::from_le_bytes([b[o], b[o + 1]])
}

fn le32(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

pub fn read(apk: &Path) -> io::Result<Directory> {
    let mut f = File::open(apk)?;
    let length = f.metadata()?.len();
    let tail = length.min(65535 + 22) as usize;
    let mut end = vec![0u8; tail];
    f.seek(SeekFrom::Start(length - tail as u64))?;
    f.read_exact(&mut end)?;
    let at = (0..=tail.saturating_sub(22)).rev().find(|&i| tail >= 22 && le32(&end, i) == END).ok_or_else(|| fail("not a zip"))?;
    let total = le16(&end, at + 10) as usize;
    let size = le32(&end, at + 12) as u64;
    let start = le32(&end, at + 16) as u64;
    if total == 0xFFFF || size == 0xFFFF_FFFF || start == 0xFFFF_FFFF {
        return Err(fail("zip64 is not supported"));
    }
    if start + size > length || size > LIMIT {
        return Err(fail("bad central directory"));
    }
    let mut central = vec![0u8; size as usize];
    f.seek(SeekFrom::Start(start))?;
    f.read_exact(&mut central)?;
    let mut entries: Vec<Entry> = Vec::new();
    let mut index: HashMap<String, usize> = HashMap::new();
    let mut p = 0usize;
    for _ in 0..total {
        if p + 46 > central.len() || le32(&central, p) != CENTRAL {
            return Err(fail("bad central entry"));
        }
        let name_length = le16(&central, p + 28) as usize;
        let extra_length = le16(&central, p + 30) as usize;
        let comment_length = le16(&central, p + 32) as usize;
        let name_bytes = central.get(p + 46..p + 46 + name_length).ok_or_else(|| fail("bad central entry"))?;
        let name = String::from_utf8_lossy(name_bytes).into_owned();
        let record = 46 + name_length + extra_length + comment_length;
        let e = Entry {
            name: name.clone(),
            method: le16(&central, p + 10),
            compressed: le32(&central, p + 20) as u64,
            size: le32(&central, p + 24) as u64,
            central_offset: p,
            central_length: record,
            local_offset: le32(&central, p + 42) as u64,
        };
        match index.get(&name) {
            Some(&i) => entries[i] = e,
            None => {
                index.insert(name, entries.len());
                entries.push(e);
            }
        }
        p += record;
    }
    Ok(Directory { central_start: start, central, entries })
}

pub fn read_entry(apk: &Path, local_offset: u64, method: u16, compressed: u64, size: u64) -> io::Result<Vec<u8>> {
    if size > LIMIT || compressed > LIMIT * 2 {
        return Err(fail("entry too large"));
    }
    let mut f = File::open(apk)?;
    let mut header = [0u8; 30];
    f.seek(SeekFrom::Start(local_offset))?;
    f.read_exact(&mut header)?;
    if le32(&header, 0) != LOCAL {
        return Err(fail("bad local header"));
    }
    let data = local_offset + 30 + le16(&header, 26) as u64 + le16(&header, 28) as u64;
    let mut raw = vec![0u8; compressed as usize];
    f.seek(SeekFrom::Start(data))?;
    f.read_exact(&mut raw)?;
    if method == 0 {
        return Ok(raw);
    }
    if method != 8 {
        return Err(fail("unsupported method"));
    }
    let mut out = vec![0u8; size as usize];
    let mut state = InflateState::new_boxed(DataFormat::Raw);
    let (mut n, mut used) = (0usize, 0usize);
    while n < out.len() {
        let r = inflate(&mut state, &raw[used..], &mut out[n..], MZFlush::None);
        n += r.bytes_written;
        used += r.bytes_consumed;
        match r.status {
            Ok(MZStatus::StreamEnd) => break,
            Ok(_) if r.bytes_written == 0 && r.bytes_consumed == 0 => break,
            Ok(_) => {}
            Err(_) => {
                if r.bytes_written == 0 && r.bytes_consumed == 0 {
                    break;
                }
                if used >= raw.len() && r.bytes_written == 0 {
                    return Err(fail("bad entry data"));
                }
            }
        }
    }
    if n != out.len() {
        return Err(fail("short entry"));
    }
    Ok(out)
}

fn java_order(a: &str, b: &str) -> std::cmp::Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

fn central_record(out: &mut Vec<u8>, name: &[u8], written: (u64, u32, u64), made_by: u16, external: u32) {
    out.extend_from_slice(&CENTRAL.to_le_bytes());
    for v in [made_by, 10, UTF8, 0, DOS_TIME, DOS_DATE] {
        out.extend_from_slice(&v.to_le_bytes());
    }
    out.extend_from_slice(&written.1.to_le_bytes());
    out.extend_from_slice(&(written.2 as u32).to_le_bytes());
    out.extend_from_slice(&(written.2 as u32).to_le_bytes());
    for v in [name.len() as u16, 0, 0, 0, 0] {
        out.extend_from_slice(&v.to_le_bytes());
    }
    out.extend_from_slice(&external.to_le_bytes());
    out.extend_from_slice(&(written.0 as u32).to_le_bytes());
    out.extend_from_slice(name);
}

pub fn patch(base: &Path, out: &Path, mut changes: Vec<(String, Source)>, cancelled: &mut dyn FnMut() -> bool) -> io::Result<()> {
    let dir = read(base)?;
    changes.sort_by(|a, b| java_order(&a.0, &b.0));
    let mut src = File::open(base)?;
    let dst = File::create(out)?;
    let mut body = BufWriter::with_capacity(1 << 16, dst);
    let mut buf = vec![0u8; 1 << 16];
    let mut copied = 0u64;
    while copied < dir.central_start {
        if cancelled() {
            return Err(fail("cancelled"));
        }
        let chunk = (dir.central_start - copied).min(64 * 1024 * 1024);
        let n = io::copy(&mut (&mut src).take(chunk), &mut body)?;
        if n == 0 {
            return Err(fail("copy failed"));
        }
        copied += n;
    }
    let mut pos = dir.central_start;
    let mut written: Vec<(u64, u32, u64)> = Vec::new();
    for (name, source) in &changes {
        if cancelled() {
            return Err(fail("cancelled"));
        }
        let name_bytes = name.as_bytes();
        let length = source.length()?;
        if length > 0xFFFF_FFFE {
            return Err(fail("entry too large"));
        }
        let mut crc = Hasher::new();
        let mut counted = 0u64;
        {
            let mut r = source.open()?;
            loop {
                let n = r.read(&mut buf)?;
                if n == 0 {
                    break;
                }
                crc.update(&buf[..n]);
                counted += n as u64;
            }
        }
        if counted != length {
            return Err(fail("source changed"));
        }
        let crc = crc.finalize();
        let pad = ((4 - ((pos + 30 + name_bytes.len() as u64) % 4)) % 4) as u16;
        let mut h = Vec::with_capacity(30);
        h.extend_from_slice(&LOCAL.to_le_bytes());
        for v in [10u16, UTF8, 0, DOS_TIME, DOS_DATE] {
            h.extend_from_slice(&v.to_le_bytes());
        }
        h.extend_from_slice(&crc.to_le_bytes());
        h.extend_from_slice(&(length as u32).to_le_bytes());
        h.extend_from_slice(&(length as u32).to_le_bytes());
        h.extend_from_slice(&(name_bytes.len() as u16).to_le_bytes());
        h.extend_from_slice(&pad.to_le_bytes());
        body.write_all(&h)?;
        body.write_all(name_bytes)?;
        body.write_all(&vec![0u8; pad as usize])?;
        let mut total = 0u64;
        {
            let mut r = source.open()?;
            loop {
                let n = r.read(&mut buf)?;
                if n == 0 {
                    break;
                }
                body.write_all(&buf[..n])?;
                total += n as u64;
            }
        }
        if total != length {
            return Err(fail("source changed"));
        }
        written.push((pos, crc, length));
        pos += 30 + name_bytes.len() as u64 + pad as u64 + length;
    }
    let central_start = pos;
    let mut index: HashMap<&str, usize> = changes.iter().enumerate().map(|(i, (n, _))| (n.as_str(), i)).collect();
    let mut central = Vec::new();
    let mut count = 0usize;
    for e in &dir.entries {
        match index.remove(e.name.as_str()) {
            None => central.extend_from_slice(&dir.central[e.central_offset..e.central_offset + e.central_length]),
            Some(w) => central_record(
                &mut central,
                e.name.as_bytes(),
                written[w],
                le16(&dir.central, e.central_offset + 4),
                le32(&dir.central, e.central_offset + 38),
            ),
        }
        count += 1;
    }
    for (i, (name, _)) in changes.iter().enumerate() {
        if !index.contains_key(name.as_str()) {
            continue;
        }
        central_record(&mut central, name.as_bytes(), written[i], 0x0314, 0o100644 << 16);
        count += 1;
    }
    if count > 0xFFFE || central_start > 0xFFFF_FFFE {
        return Err(fail("zip64 is not supported"));
    }
    body.write_all(&central)?;
    let mut end = Vec::with_capacity(22);
    end.extend_from_slice(&END.to_le_bytes());
    for v in [0u16, 0, count as u16, count as u16] {
        end.extend_from_slice(&v.to_le_bytes());
    }
    end.extend_from_slice(&(central.len() as u32).to_le_bytes());
    end.extend_from_slice(&(central_start as u32).to_le_bytes());
    end.extend_from_slice(&0u16.to_le_bytes());
    body.write_all(&end)?;
    let dst = body.into_inner().map_err(|e| e.into_error())?;
    dst.sync_all()?;
    Ok(())
}
