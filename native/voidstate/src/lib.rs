#![allow(clippy::missing_safety_doc)]

use std::fs::File;
use std::io::Read;
use std::panic::{catch_unwind, AssertUnwindSafe};

pub const MAGIC: u32 = 0x3154_5356;
pub const MAX_DEPTH: usize = 63;

pub const STATUS_OK: i32 = 0;
pub const STATUS_MISSING: i32 = 1;
pub const STATUS_IO: i32 = 2;
pub const STATUS_TOO_LARGE: i32 = 3;
pub const STATUS_INVALID: i32 = 4;
pub const STATUS_PANIC: i32 = 5;

pub const TAG_NULL: u32 = 0;
pub const TAG_FALSE: u32 = 1;
pub const TAG_TRUE: u32 = 2;
pub const TAG_NUMBER: u32 = 3;
pub const TAG_STRING: u32 = 4;
pub const TAG_OBJECT: u32 = 5;
pub const TAG_ARRAY: u32 = 6;

struct Parser<'a> {
    src: &'a [u8],
    pos: usize,
    arena: Vec<u8>,
    tokens: Vec<u32>,
}

type Step = Result<(), ()>;

impl Parser<'_> {
    fn skip_trivia(&mut self) -> Step {
        loop {
            match self.src.get(self.pos) {
                Some(b' ' | b'\t' | b'\n' | b'\r') => self.pos += 1,
                Some(b'/') => match self.src.get(self.pos + 1) {
                    Some(b'/') => {
                        self.pos += 2;
                        while let Some(&c) = self.src.get(self.pos) {
                            self.pos += 1;
                            if c == b'\n' {
                                break;
                            }
                        }
                    }
                    Some(b'*') => {
                        self.pos += 2;
                        loop {
                            match self.src.get(self.pos) {
                                None => return Err(()),
                                Some(b'*') if self.src.get(self.pos + 1) == Some(&b'/') => {
                                    self.pos += 2;
                                    break;
                                }
                                Some(_) => self.pos += 1,
                            }
                        }
                    }
                    _ => return Err(()),
                },
                _ => return Ok(()),
            }
        }
    }

    fn literal(&mut self, word: &[u8], tag: u32) -> Step {
        if self.src[self.pos..].starts_with(word) {
            self.pos += word.len();
            self.tokens.push(tag);
            Ok(())
        } else {
            Err(())
        }
    }

    fn digits(&mut self) -> usize {
        let start = self.pos;
        while matches!(self.src.get(self.pos), Some(b'0'..=b'9')) {
            self.pos += 1;
        }
        self.pos - start
    }

    fn number(&mut self) -> Step {
        let start = self.pos;
        if self.src.get(self.pos) == Some(&b'-') {
            self.pos += 1;
        }
        match self.src.get(self.pos) {
            Some(b'0') => self.pos += 1,
            Some(b'1'..=b'9') => {
                self.digits();
            }
            _ => return Err(()),
        }
        if self.src.get(self.pos) == Some(&b'.') {
            self.pos += 1;
            if self.digits() == 0 {
                return Err(());
            }
        }
        if matches!(self.src.get(self.pos), Some(b'e' | b'E')) {
            self.pos += 1;
            if matches!(self.src.get(self.pos), Some(b'+' | b'-')) {
                self.pos += 1;
            }
            if self.digits() == 0 {
                return Err(());
            }
        }
        self.tokens.extend_from_slice(&[TAG_NUMBER, start as u32, (self.pos - start) as u32]);
        Ok(())
    }

    fn hex4(&mut self) -> Result<u32, ()> {
        let bytes = self.src.get(self.pos..self.pos + 4).ok_or(())?;
        let mut value = 0u32;
        for &b in bytes {
            value = value * 16 + (b as char).to_digit(16).ok_or(())?;
        }
        self.pos += 4;
        Ok(value)
    }

    fn escape(&mut self) -> Step {
        self.pos += 1;
        let escape = *self.src.get(self.pos).ok_or(())?;
        self.pos += 1;
        let simple = match escape {
            b'"' => b'"',
            b'\\' => b'\\',
            b'/' => b'/',
            b'b' => 0x08,
            b'f' => 0x0C,
            b'n' => b'\n',
            b'r' => b'\r',
            b't' => b'\t',
            b'u' => {
                let high = self.hex4()?;
                let code = if (0xD800..0xDC00).contains(&high) {
                    if self.src.get(self.pos..self.pos + 2) != Some(&b"\\u"[..]) {
                        return Err(());
                    }
                    self.pos += 2;
                    let low = self.hex4()?;
                    if !(0xDC00..0xE000).contains(&low) {
                        return Err(());
                    }
                    0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00)
                } else if (0xDC00..0xE000).contains(&high) {
                    return Err(());
                } else {
                    high
                };
                let ch = char::from_u32(code).ok_or(())?;
                let mut buffer = [0u8; 4];
                self.arena.extend_from_slice(ch.encode_utf8(&mut buffer).as_bytes());
                return Ok(());
            }
            _ => return Err(()),
        };
        self.arena.push(simple);
        Ok(())
    }

    fn string(&mut self) -> Step {
        self.pos += 1;
        let start = self.pos;
        loop {
            match self.src.get(self.pos) {
                None => return Err(()),
                Some(b'"') => {
                    self.tokens.extend_from_slice(&[TAG_STRING, start as u32, (self.pos - start) as u32]);
                    self.pos += 1;
                    return Ok(());
                }
                Some(b'\\') => break,
                Some(&c) if c < 0x20 => return Err(()),
                Some(_) => self.pos += 1,
            }
        }
        let offset = self.src.len() + self.arena.len();
        self.arena.extend_from_slice(&self.src[start..self.pos]);
        loop {
            match self.src.get(self.pos) {
                None => return Err(()),
                Some(b'"') => {
                    self.pos += 1;
                    let length = self.src.len() + self.arena.len() - offset;
                    self.tokens.extend_from_slice(&[TAG_STRING, offset as u32, length as u32]);
                    return Ok(());
                }
                Some(b'\\') => self.escape()?,
                Some(&c) if c < 0x20 => return Err(()),
                Some(&c) => {
                    self.arena.push(c);
                    self.pos += 1;
                }
            }
        }
    }

    fn container(&mut self, depth: usize, object: bool) -> Step {
        if depth >= MAX_DEPTH {
            return Err(());
        }
        let raw_start = self.pos;
        self.pos += 1;
        let head = self.tokens.len();
        self.tokens.extend_from_slice(&[if object { TAG_OBJECT } else { TAG_ARRAY }, 0, raw_start as u32, 0]);
        let close = if object { b'}' } else { b']' };
        let mut count = 0u32;
        loop {
            self.skip_trivia()?;
            if self.src.get(self.pos) == Some(&close) {
                self.pos += 1;
                break;
            }
            if object {
                if self.src.get(self.pos) != Some(&b'"') {
                    return Err(());
                }
                self.string()?;
                self.skip_trivia()?;
                if self.src.get(self.pos) != Some(&b':') {
                    return Err(());
                }
                self.pos += 1;
                self.skip_trivia()?;
            }
            self.value(depth + 1)?;
            count += 1;
            self.skip_trivia()?;
            match self.src.get(self.pos) {
                Some(b',') => self.pos += 1,
                Some(&c) if c == close => {
                    self.pos += 1;
                    break;
                }
                _ => return Err(()),
            }
        }
        self.tokens[head + 1] = count;
        self.tokens[head + 3] = (self.pos - raw_start) as u32;
        Ok(())
    }

    fn value(&mut self, depth: usize) -> Step {
        match self.src.get(self.pos) {
            Some(b'{') => self.container(depth, true),
            Some(b'[') => self.container(depth, false),
            Some(b'"') => self.string(),
            Some(b't') => self.literal(b"true", TAG_TRUE),
            Some(b'f') => self.literal(b"false", TAG_FALSE),
            Some(b'n') => self.literal(b"null", TAG_NULL),
            Some(b'-' | b'0'..=b'9') => self.number(),
            _ => Err(()),
        }
    }
}

pub fn encode(source: &[u8]) -> Option<Vec<u8>> {
    let source = source.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(source);
    if source.len() > (u32::MAX / 4) as usize || std::str::from_utf8(source).is_err() {
        return None;
    }
    let mut parser = Parser { src: source, pos: 0, arena: Vec::new(), tokens: Vec::with_capacity(source.len() / 4 + 16) };
    parser.skip_trivia().ok()?;
    parser.value(0).ok()?;
    parser.skip_trivia().ok()?;
    if parser.pos != source.len() {
        return None;
    }
    let data_length = source.len() + parser.arena.len();
    let mut output = Vec::with_capacity(12 + data_length + parser.tokens.len() * 4);
    output.extend_from_slice(&MAGIC.to_le_bytes());
    output.extend_from_slice(&(data_length as u32).to_le_bytes());
    output.extend_from_slice(&(parser.tokens.len() as u32).to_le_bytes());
    output.extend_from_slice(source);
    output.extend_from_slice(&parser.arena);
    for token in &parser.tokens {
        output.extend_from_slice(&token.to_le_bytes());
    }
    Some(output)
}

fn load(path: &str, maximum: u64) -> Result<Vec<u8>, i32> {
    let mut file = File::open(path).map_err(|error| match error.kind() {
        std::io::ErrorKind::NotFound => STATUS_MISSING,
        _ => STATUS_IO,
    })?;
    let length = file.metadata().map_err(|_| STATUS_IO)?.len();
    if length > maximum {
        return Err(STATUS_TOO_LARGE);
    }
    let mut source = Vec::with_capacity(length as usize);
    file.by_ref().take(maximum + 1).read_to_end(&mut source).map_err(|_| STATUS_IO)?;
    if source.len() as u64 > maximum {
        return Err(STATUS_TOO_LARGE);
    }
    encode(&source).ok_or(STATUS_INVALID)
}

#[no_mangle]
pub extern "C" fn voidstate_version() -> u32 {
    1
}

#[no_mangle]
pub unsafe extern "C" fn voidstate_load(path: *const u8, path_length: usize, maximum: u64, output: *mut *mut u8, output_length: *mut usize) -> i32 {
    if path.is_null() || output.is_null() || output_length.is_null() {
        return STATUS_INVALID;
    }
    *output = std::ptr::null_mut();
    *output_length = 0;
    let result = catch_unwind(AssertUnwindSafe(|| {
        let path = std::str::from_utf8(std::slice::from_raw_parts(path, path_length)).map_err(|_| STATUS_INVALID)?;
        load(path, maximum)
    }));
    match result {
        Ok(Ok(buffer)) => {
            let boxed = buffer.into_boxed_slice();
            *output_length = boxed.len();
            *output = Box::into_raw(boxed) as *mut u8;
            STATUS_OK
        }
        Ok(Err(status)) => status,
        Err(_) => STATUS_PANIC,
    }
}

#[no_mangle]
pub unsafe extern "C" fn voidstate_free(buffer: *mut u8, length: usize) {
    if !buffer.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(buffer, length)));
    }
}
