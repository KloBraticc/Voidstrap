#![allow(clippy::missing_safety_doc)]

use aho_corasick::AhoCorasick;
use std::collections::VecDeque;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::panic::{catch_unwind, AssertUnwindSafe};

const CHUNK: usize = 64 * 1024;
const MAX_LINE: usize = 4 * 1024 * 1024;

pub enum Next {
    Line(Vec<u8>),
    Idle,
    Restarted,
}

pub struct Tail {
    file: File,
    markers: AhoCorasick,
    position: u64,
    buffer: Vec<u8>,
    skipping: bool,
    ready: VecDeque<Vec<u8>>,
}

fn first_break(bytes: &[u8]) -> Option<usize> {
    memchr::memchr2(b'\n', b'\r', bytes)
}

fn last_break(bytes: &[u8]) -> Option<usize> {
    memchr::memrchr2(b'\n', b'\r', bytes)
}

impl Tail {
    pub fn open(path: &str, markers: &[&str]) -> std::io::Result<Tail> {
        let file = File::open(path)?;
        let markers = AhoCorasick::builder()
            .match_kind(aho_corasick::MatchKind::LeftmostFirst)
            .build(markers.iter().filter(|marker| !marker.is_empty()))
            .map_err(std::io::Error::other)?;
        Ok(Tail {
            file,
            markers,
            position: 0,
            buffer: Vec::with_capacity(CHUNK * 2),
            skipping: false,
            ready: VecDeque::new(),
        })
    }

    pub fn poll(&mut self) -> std::io::Result<Next> {
        loop {
            if let Some(line) = self.ready.pop_front() {
                return Ok(Next::Line(line));
            }
            let carried = self.buffer.len();
            self.buffer.resize(carried + CHUNK, 0);
            let read = match self.file.read(&mut self.buffer[carried..]) {
                Ok(read) => read,
                Err(error) => {
                    self.buffer.truncate(carried);
                    return Err(error);
                }
            };
            self.buffer.truncate(carried + read);
            if read == 0 {
                let length = self.file.metadata()?.len();
                if length < self.position {
                    self.file.seek(SeekFrom::Start(0))?;
                    self.position = 0;
                    self.buffer.clear();
                    self.skipping = false;
                    return Ok(Next::Restarted);
                }
                return Ok(Next::Idle);
            }
            self.position += read as u64;
            self.scan(carried);
        }
    }

    fn scan(&mut self, carried: usize) {
        let mut start = 0;
        if self.skipping {
            match first_break(&self.buffer[carried..]) {
                Some(offset) => {
                    self.skipping = false;
                    start = carried + offset + 1;
                }
                None => {
                    self.buffer.clear();
                    return;
                }
            }
        }
        let complete = match last_break(&self.buffer[start..]) {
            Some(offset) => start + offset + 1,
            None => start,
        };
        let mut from = start;
        while from < complete {
            let Some(hit) = self.markers.find(&self.buffer[from..complete]) else {
                break;
            };
            let at = from + hit.start();
            let line_start = last_break(&self.buffer[from..at]).map_or(from, |offset| from + offset + 1);
            let line_end = first_break(&self.buffer[at..complete]).map_or(complete, |offset| at + offset);
            self.ready.push_back(self.buffer[line_start..line_end].to_vec());
            from = line_end + 1;
        }
        self.buffer.drain(..complete);
        if self.buffer.len() > MAX_LINE {
            self.buffer.clear();
            self.skipping = true;
        }
    }
}

pub struct Handle {
    tail: Tail,
    pending: Option<Vec<u8>>,
}

#[no_mangle]
pub extern "C" fn voidlog_version() -> u32 {
    1
}

#[no_mangle]
pub unsafe extern "C" fn voidlog_open(path: *const u8, path_length: usize, markers: *const u8, markers_length: usize) -> *mut Handle {
    if path.is_null() || markers.is_null() {
        return std::ptr::null_mut();
    }
    catch_unwind(AssertUnwindSafe(|| {
        let path = std::str::from_utf8(std::slice::from_raw_parts(path, path_length)).ok()?;
        let markers = std::str::from_utf8(std::slice::from_raw_parts(markers, markers_length)).ok()?;
        let list: Vec<&str> = markers.split('\n').collect();
        let tail = Tail::open(path, &list).ok()?;
        Some(Box::into_raw(Box::new(Handle { tail, pending: None })))
    }))
    .ok()
    .flatten()
    .unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn voidlog_next(handle: *mut Handle, buffer: *mut u8, capacity: usize, length: *mut usize) -> i32 {
    if handle.is_null() || length.is_null() || (buffer.is_null() && capacity > 0) {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let handle = &mut *handle;
        let line = match handle.pending.take() {
            Some(line) => line,
            None => match handle.tail.poll() {
                Ok(Next::Line(line)) => line,
                Ok(Next::Idle) => return 0,
                Ok(Next::Restarted) => return 2,
                Err(_) => return -1,
            },
        };
        *length = line.len();
        if line.len() > capacity {
            handle.pending = Some(line);
            return 3;
        }
        std::ptr::copy_nonoverlapping(line.as_ptr(), buffer, line.len());
        1
    }))
    .unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn voidlog_close(handle: *mut Handle) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(handle))));
}
