use super::{CHUNK, Error, MAX_ENTRIES, MAX_EXTRACTED, SEVENZ_DAMAGED, Sink, too_big, too_many};
use sevenz_rust2::{ArchiveReader, EncoderMethod, Password};
use std::path::Path;

const PASSWORD: &str = "The 7z package is password protected.";
const MEMORY: &str = "The 7z package needs more memory than this device allows.";
const MAX_MEMORY: u64 = 256 * 1024 * 1024;

fn map(e: sevenz_rust2::Error) -> Error {
    match e {
        sevenz_rust2::Error::PasswordRequired => Error::rejected(PASSWORD),
        sevenz_rust2::Error::MaxMemLimited { .. } => Error::rejected(MEMORY),
        _ => Error::rejected(SEVENZ_DAMAGED),
    }
}

fn memory(id: &[u8], props: &[u8]) -> Result<u64, Error> {
    let le = |o: usize| -> Result<u64, Error> {
        props
            .get(o..o + 4)
            .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as u64)
            .ok_or_else(|| Error::rejected(SEVENZ_DAMAGED))
    };
    Ok(match id {
        EncoderMethod::ID_LZMA | EncoderMethod::ID_PPMD => le(1)?,
        EncoderMethod::ID_LZMA2 => {
            let bits = *props.first().ok_or_else(|| Error::rejected(SEVENZ_DAMAGED))? as u64;
            if bits > 40 {
                return Err(Error::rejected(SEVENZ_DAMAGED));
            }
            if bits == 40 { 0xFFFF_FFFF } else { (2 | (bits & 1)) << (bits / 2 + 11) }
        }
        _ => 0,
    })
}

pub fn decode(path: &Path, sink: &mut dyn Sink) -> Result<(), Error> {
    let mut reader = ArchiveReader::open(path, Password::empty()).map_err(map)?;
    let archive = reader.archive();
    for block in &archive.blocks {
        for coder in &block.coders {
            let id = coder.encoder_method_id();
            if id == EncoderMethod::ID_AES256_SHA256 {
                return Err(Error::rejected(PASSWORD));
            }
            if memory(id, coder.properties())? > MAX_MEMORY {
                return Err(Error::rejected(MEMORY));
            }
        }
    }
    let mut count = 0;
    for e in &archive.files {
        if e.is_directory || e.is_anti_item {
            continue;
        }
        if e.size > MAX_EXTRACTED {
            return Err(too_big());
        }
        count += 1;
        if count > MAX_ENTRIES {
            return Err(too_many());
        }
    }
    let mut failure = None;
    let mut buf = vec![0u8; CHUNK];
    let result = reader.for_each_entries(|entry, data| {
        if entry.is_directory || entry.is_anti_item {
            return Ok(true);
        }
        let step = (|| -> Result<(), Error> {
            sink.entry(&entry.name, if entry.has_stream { entry.size as i64 } else { 0 })?;
            loop {
                let n = data.read(&mut buf).map_err(|_| Error::rejected(SEVENZ_DAMAGED))?;
                if n == 0 {
                    break;
                }
                sink.data(&buf[..n])?;
            }
            sink.end()
        })();
        match step {
            Ok(()) => Ok(true),
            Err(e) => {
                failure = Some(e);
                Ok(false)
            }
        }
    });
    if let Some(e) = failure {
        return Err(e);
    }
    result.map_err(map)
}
