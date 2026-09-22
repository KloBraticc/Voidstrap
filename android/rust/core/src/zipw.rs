use miniz_oxide::MZFlush;
use miniz_oxide::deflate::core::{CompressorOxide, create_comp_flags_from_zip_params};
use miniz_oxide::deflate::stream::deflate;
use std::fs::File;
use std::io::{self, BufWriter, Write};

pub struct Writer {
    out: BufWriter<File>,
    pos: u64,
    central: Vec<u8>,
    count: u32,
    time: (u16, u16),
}

fn dos_now() -> (u16, u16) {
    let [y, mo, d, h, mi, s] = crate::java::utc_parts(crate::java::now_ms());
    (((h << 11) | (mi << 5) | (s / 2)) as u16, (((y - 1980).max(0) << 9) | (mo << 5) | d) as u16)
}

impl Writer {
    pub fn new(out: File) -> Writer {
        Writer { out: BufWriter::with_capacity(1 << 16, out), pos: 0, central: Vec::new(), count: 0, time: dos_now() }
    }

    fn record(&mut self, name: &str, method: u16, crc: u32, compressed: &[u8], size: u32) -> io::Result<()> {
        let n = name.as_bytes();
        let mut h = Vec::with_capacity(30 + n.len());
        h.extend_from_slice(&0x0403_4b50u32.to_le_bytes());
        for v in [20u16, 0x0800, method, self.time.0, self.time.1] {
            h.extend_from_slice(&v.to_le_bytes());
        }
        h.extend_from_slice(&crc.to_le_bytes());
        h.extend_from_slice(&(compressed.len() as u32).to_le_bytes());
        h.extend_from_slice(&size.to_le_bytes());
        h.extend_from_slice(&(n.len() as u16).to_le_bytes());
        h.extend_from_slice(&0u16.to_le_bytes());
        h.extend_from_slice(n);
        self.out.write_all(&h)?;
        self.out.write_all(compressed)?;
        let c = &mut self.central;
        c.extend_from_slice(&0x0201_4b50u32.to_le_bytes());
        for v in [20u16, 20, 0x0800, method, self.time.0, self.time.1] {
            c.extend_from_slice(&v.to_le_bytes());
        }
        c.extend_from_slice(&crc.to_le_bytes());
        c.extend_from_slice(&(compressed.len() as u32).to_le_bytes());
        c.extend_from_slice(&size.to_le_bytes());
        for v in [n.len() as u16, 0, 0, 0, 0] {
            c.extend_from_slice(&v.to_le_bytes());
        }
        c.extend_from_slice(&(if name.ends_with('/') { 0x10u32 } else { 0 }).to_le_bytes());
        c.extend_from_slice(&(self.pos as u32).to_le_bytes());
        c.extend_from_slice(n);
        self.pos += h.len() as u64 + compressed.len() as u64;
        self.count += 1;
        if self.pos > 0xFFFF_FFFE || self.count > 0xFFFE {
            return Err(io::Error::other("zip64 is not supported"));
        }
        Ok(())
    }

    pub fn dir(&mut self, name: &str) -> io::Result<()> {
        self.record(name, 0, 0, &[], 0)
    }

    pub fn file(&mut self, name: &str, data: &[u8]) -> io::Result<()> {
        if data.len() > 0xFFFF_FFFE {
            return Err(io::Error::other("entry too large"));
        }
        let crc = crc32fast::hash(data);
        let packed = miniz_oxide::deflate::compress_to_vec(data, 6);
        if packed.len() < data.len() {
            self.record(name, 8, crc, &packed, data.len() as u32)
        } else {
            self.record(name, 0, crc, data, data.len() as u32)
        }
    }

    pub fn finish(mut self) -> io::Result<()> {
        let size = self.central.len() as u32;
        let central = std::mem::take(&mut self.central);
        self.out.write_all(&central)?;
        let mut end = Vec::with_capacity(22);
        end.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
        for v in [0u16, 0, self.count as u16, self.count as u16] {
            end.extend_from_slice(&v.to_le_bytes());
        }
        end.extend_from_slice(&size.to_le_bytes());
        end.extend_from_slice(&(self.pos as u32).to_le_bytes());
        end.extend_from_slice(&0u16.to_le_bytes());
        self.out.write_all(&end)?;
        let f = self.out.into_inner().map_err(|e| e.into_error())?;
        f.sync_all().or(Ok(()))
    }
}

fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], body: &[u8]) {
    out.extend_from_slice(&(body.len() as u32).to_be_bytes());
    let mut crc = crc32fast::Hasher::new();
    crc.update(kind);
    crc.update(body);
    out.extend_from_slice(kind);
    out.extend_from_slice(body);
    out.extend_from_slice(&crc.finalize().to_be_bytes());
}

pub fn blank_png(w: u32, h: u32) -> Option<Vec<u8>> {
    if w == 0 || h == 0 || w > 16384 || h > 16384 {
        return None;
    }
    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&w.to_be_bytes());
    ihdr.extend_from_slice(&h.to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);
    let row = vec![0u8; 1 + w as usize * 4];
    let mut z = CompressorOxide::new(create_comp_flags_from_zip_params(9, 15, 0));
    let mut idat = Vec::new();
    let mut buf = vec![0u8; 1 << 16];
    for y in 0..h {
        let flush = if y + 1 == h { MZFlush::Finish } else { MZFlush::None };
        let mut input: &[u8] = &row;
        loop {
            let r = deflate(&mut z, input, &mut buf, flush);
            idat.extend_from_slice(&buf[..r.bytes_written]);
            input = &input[r.bytes_consumed..];
            match r.status {
                Ok(miniz_oxide::MZStatus::StreamEnd) => break,
                Ok(_) if input.is_empty() && (flush == MZFlush::None || r.bytes_written == 0 && r.bytes_consumed == 0) => break,
                Ok(_) => {}
                Err(_) => return None,
            }
        }
    }
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];
    chunk(&mut out, b"IHDR", &ihdr);
    chunk(&mut out, b"IDAT", &idat);
    chunk(&mut out, b"IEND", &[]);
    Some(out)
}
