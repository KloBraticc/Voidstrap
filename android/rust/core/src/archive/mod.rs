mod huff;
mod rar4;
mod rar5;
mod sevenz;
mod zip;

use std::fs::File;
use std::io::Read;
use std::panic::{self, AssertUnwindSafe};
use std::path::Path;

pub const MAX_EXTRACTED: u64 = 1024 * 1024 * 1024;
pub const MAX_ENTRIES: usize = 6000;
pub const CHUNK: usize = 64 * 1024;

pub const NOT_ARCHIVE: &str = "The package is not a zip, rar or 7z archive.";
pub const ZIP_BAD: &str = "The package is not a readable zip archive.";
pub const RAR_DAMAGED: &str = "The rar package is damaged.";
pub const RAR_PASSWORD: &str = "The rar package is password protected.";
pub const RAR_SPLIT: &str = "Split rar packages are not supported. Use a single file package.";
pub const RAR_MEMORY: &str = "The rar package needs more memory than this device allows.";
pub const SEVENZ_DAMAGED: &str = "The 7z package is damaged.";

#[derive(Debug, PartialEq)]
pub enum Error {
    Rejected(String),
    Io(String),
}

impl Error {
    pub fn rejected(message: &str) -> Self {
        Error::Rejected(message.to_string())
    }
}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Error::Io(e.to_string())
    }
}

pub fn too_many() -> Error {
    Error::Rejected(format!("The package contains more than {MAX_ENTRIES} files."))
}

pub fn too_big() -> Error {
    Error::Rejected(format!("The package expands to more than {} MB.", MAX_EXTRACTED / 1048576))
}

pub trait Sink {
    fn entry(&mut self, name: &str, size: i64) -> Result<(), Error>;
    fn data(&mut self, bytes: &[u8]) -> Result<(), Error>;
    fn end(&mut self) -> Result<(), Error>;
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Kind {
    Zip,
    Rar4,
    Rar5,
    SevenZip,
    Unknown,
}

pub fn kind(path: &Path) -> Kind {
    let mut h = [0u8; 8];
    let Ok(mut f) = File::open(path) else {
        return Kind::Unknown;
    };
    let mut n = 0;
    while n < h.len() {
        match f.read(&mut h[n..]) {
            Ok(0) | Err(_) => break,
            Ok(read) => n += read,
        }
    }
    if n < 6 {
        return Kind::Unknown;
    }
    if h[0] == b'P' && h[1] == b'K' && matches!(h[2], 3 | 5 | 7) {
        return Kind::Zip;
    }
    if &h[..6] == b"Rar!\x1a\x07" {
        return if h[6] == 1 { Kind::Rar5 } else { Kind::Rar4 };
    }
    if h[..6] == [b'7', b'z', 0xBC, 0xAF, 0x27, 0x1C] {
        return Kind::SevenZip;
    }
    Kind::Unknown
}

pub fn zip_reader(input: impl Read, sink: &mut dyn Sink) -> Result<(), Error> {
    panic::catch_unwind(AssertUnwindSafe(|| zip::decode_reader(input, sink))).unwrap_or_else(|_| Err(Error::rejected(ZIP_BAD)))
}

pub fn decode(path: &Path, sink: &mut dyn Sink) -> Result<(), Error> {
    let kind = kind(path);
    let run = panic::catch_unwind(AssertUnwindSafe(|| match kind {
        Kind::Zip => zip::decode(path, sink),
        Kind::Rar5 => rar5::decode(path, sink),
        Kind::Rar4 => rar4::decode(path, sink),
        Kind::SevenZip => sevenz::decode(path, sink),
        Kind::Unknown => Err(Error::rejected(NOT_ARCHIVE)),
    }));
    run.unwrap_or_else(|_| {
        Err(Error::rejected(match kind {
            Kind::Zip => ZIP_BAD,
            Kind::SevenZip => SEVENZ_DAMAGED,
            _ => RAR_DAMAGED,
        }))
    })
}
