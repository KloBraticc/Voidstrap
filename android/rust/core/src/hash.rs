use std::io::Read;
use std::path::Path;

pub trait Digest {
    fn update(&mut self, data: &[u8]);
    fn finish(self) -> Vec<u8>;
}

struct Blocks {
    buf: [u8; 64],
    fill: usize,
    len: u64,
}

impl Blocks {
    fn new() -> Blocks {
        Blocks { buf: [0; 64], fill: 0, len: 0 }
    }

    fn feed(&mut self, mut data: &[u8], mut block: impl FnMut(&[u8; 64])) {
        self.len += data.len() as u64;
        while !data.is_empty() {
            let n = (64 - self.fill).min(data.len());
            self.buf[self.fill..self.fill + n].copy_from_slice(&data[..n]);
            self.fill += n;
            data = &data[n..];
            if self.fill == 64 {
                block(&self.buf);
                self.fill = 0;
            }
        }
    }

    fn pad(mut self, big_endian: bool, mut block: impl FnMut(&[u8; 64])) {
        let bits = self.len.wrapping_mul(8);
        let mut tail = vec![0x80u8];
        while (self.fill + tail.len()) % 64 != 56 {
            tail.push(0);
        }
        tail.extend_from_slice(&if big_endian { bits.to_be_bytes() } else { bits.to_le_bytes() });
        let len = self.len;
        self.feed(&tail, &mut block);
        self.len = len;
    }
}

pub struct Sha1 {
    h: [u32; 5],
    blocks: Blocks,
}

impl Sha1 {
    pub fn new() -> Sha1 {
        Sha1 { h: [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0], blocks: Blocks::new() }
    }

    fn block(h: &mut [u32; 5], b: &[u8; 64]) {
        let mut w = [0u32; 80];
        for i in 0..16 {
            w[i] = u32::from_be_bytes([b[4 * i], b[4 * i + 1], b[4 * i + 2], b[4 * i + 3]]);
        }
        for i in 16..80 {
            w[i] = (w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16]).rotate_left(1);
        }
        let [mut a, mut bb, mut c, mut d, mut e] = *h;
        for (i, wi) in w.iter().enumerate() {
            let (f, k) = match i {
                0..=19 => ((bb & c) | (!bb & d), 0x5A827999),
                20..=39 => (bb ^ c ^ d, 0x6ED9EBA1),
                40..=59 => ((bb & c) | (bb & d) | (c & d), 0x8F1BBCDC),
                _ => (bb ^ c ^ d, 0xCA62C1D6),
            };
            let t = a.rotate_left(5).wrapping_add(f).wrapping_add(e).wrapping_add(k).wrapping_add(*wi);
            e = d;
            d = c;
            c = bb.rotate_left(30);
            bb = a;
            a = t;
        }
        for (x, y) in h.iter_mut().zip([a, bb, c, d, e]) {
            *x = x.wrapping_add(y);
        }
    }
}

impl Digest for Sha1 {
    fn update(&mut self, data: &[u8]) {
        let h = &mut self.h;
        self.blocks.feed(data, |b| Sha1::block(h, b));
    }

    fn finish(mut self) -> Vec<u8> {
        let h = &mut self.h;
        self.blocks.pad(true, |b| Sha1::block(h, b));
        self.h.iter().flat_map(|v| v.to_be_bytes()).collect()
    }
}

pub struct Md5 {
    h: [u32; 4],
    blocks: Blocks,
}

const S: [u32; 64] = [
    7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11,
    16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
];

const K: [u32; 64] = [
    0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501, 0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122,
    0xfd987193, 0xa679438e, 0x49b40821, 0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8, 0x21e1cde6, 0xc33707d6,
    0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a, 0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60,
    0xbebfbc70, 0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665, 0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
    0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1, 0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
];

impl Md5 {
    pub fn new() -> Md5 {
        Md5 { h: [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476], blocks: Blocks::new() }
    }

    fn block(h: &mut [u32; 4], b: &[u8; 64]) {
        let m: Vec<u32> = (0..16).map(|i| u32::from_le_bytes([b[4 * i], b[4 * i + 1], b[4 * i + 2], b[4 * i + 3]])).collect();
        let [mut a, mut bb, mut c, mut d] = *h;
        for i in 0..64 {
            let (f, g) = match i {
                0..=15 => ((bb & c) | (!bb & d), i),
                16..=31 => ((d & bb) | (!d & c), (5 * i + 1) % 16),
                32..=47 => (bb ^ c ^ d, (3 * i + 5) % 16),
                _ => (c ^ (bb | !d), (7 * i) % 16),
            };
            let t = d;
            d = c;
            c = bb;
            bb = bb.wrapping_add(a.wrapping_add(f).wrapping_add(K[i]).wrapping_add(m[g]).rotate_left(S[i]));
            a = t;
        }
        for (x, y) in h.iter_mut().zip([a, bb, c, d]) {
            *x = x.wrapping_add(y);
        }
    }
}

impl Digest for Md5 {
    fn update(&mut self, data: &[u8]) {
        let h = &mut self.h;
        self.blocks.feed(data, |b| Md5::block(h, b));
    }

    fn finish(mut self) -> Vec<u8> {
        let h = &mut self.h;
        self.blocks.pad(false, |b| Md5::block(h, b));
        self.h.iter().flat_map(|v| v.to_le_bytes()).collect()
    }
}

pub fn file<D: Digest>(mut d: D, path: &Path, prefix: &[u8]) -> Option<Vec<u8>> {
    let mut f = std::fs::File::open(path).ok()?;
    d.update(prefix);
    let mut buf = vec![0u8; 65536];
    loop {
        match f.read(&mut buf) {
            Ok(0) => return Some(d.finish()),
            Ok(n) => d.update(&buf[..n]),
            Err(_) => return None,
        }
    }
}

pub fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}
