#![allow(clippy::missing_safety_doc)]

mod discovery;
mod transport;

use std::collections::VecDeque;
use std::io::{Read, Write};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread::JoinHandle;

pub const MAX_FRAME: usize = 16 * 1024;
const MAX_PENDING: usize = 64;

pub struct Frame {
    pub opcode: u32,
    pub data: Vec<u8>,
}

struct Shared {
    connected: AtomicBool,
    stop: AtomicBool,
    frames: Mutex<VecDeque<Frame>>,
}

pub struct Connection {
    shared: Arc<Shared>,
    writer: Mutex<transport::Stream>,
    reader: Option<JoinHandle<()>>,
    pub pipe: i32,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    match mutex.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    }
}

impl Connection {
    pub fn open(pipe: i32) -> Option<Connection> {
        let (stream, index) = discovery::connect(pipe)?;
        let shared = Arc::new(Shared {
            connected: AtomicBool::new(true),
            stop: AtomicBool::new(false),
            frames: Mutex::new(VecDeque::new()),
        });
        let mut source = stream.try_clone().ok()?;
        let state = shared.clone();
        let reader = std::thread::Builder::new()
            .name("voidrpc".into())
            .stack_size(128 * 1024)
            .spawn(move || read_loop(&mut source, &state))
            .ok()?;
        Some(Connection { shared, writer: Mutex::new(stream), reader: Some(reader), pipe: index })
    }

    pub fn is_connected(&self) -> bool {
        self.shared.connected.load(Ordering::Acquire)
    }

    pub fn write(&self, opcode: u32, data: &[u8]) -> bool {
        if data.len() > MAX_FRAME || !self.is_connected() {
            return false;
        }
        let mut buffer = Vec::with_capacity(8 + data.len());
        buffer.extend_from_slice(&opcode.to_le_bytes());
        buffer.extend_from_slice(&(data.len() as u32).to_le_bytes());
        buffer.extend_from_slice(data);
        let mut writer = lock(&self.writer);
        let ok = writer.write_all(&buffer).and_then(|_| writer.flush()).is_ok();
        if !ok {
            self.shared.connected.store(false, Ordering::Release);
        }
        ok
    }

    pub fn read(&self) -> Option<Frame> {
        lock(&self.shared.frames).pop_front()
    }
}

impl Drop for Connection {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::Release);
        self.shared.connected.store(false, Ordering::Release);
        lock(&self.writer).shutdown();
        if let Some(reader) = self.reader.take() {
            let _ = reader.join();
        }
    }
}

fn read_loop(stream: &mut transport::Stream, shared: &Shared) {
    let mut header = [0u8; 8];
    loop {
        if !fill(stream, &mut header, shared) {
            break;
        }
        let opcode = u32::from_le_bytes([header[0], header[1], header[2], header[3]]);
        let length = u32::from_le_bytes([header[4], header[5], header[6], header[7]]) as usize;
        if length > MAX_FRAME {
            break;
        }
        let mut data = vec![0u8; length];
        if !fill(stream, &mut data, shared) {
            break;
        }
        let mut frames = lock(&shared.frames);
        if frames.len() >= MAX_PENDING {
            break;
        }
        frames.push_back(Frame { opcode, data });
    }
    shared.connected.store(false, Ordering::Release);
}

fn fill(stream: &mut transport::Stream, buffer: &mut [u8], shared: &Shared) -> bool {
    let mut offset = 0;
    while offset < buffer.len() {
        if shared.stop.load(Ordering::Acquire) {
            return false;
        }
        match stream.wait_readable(&shared.stop) {
            Ok(true) => {}
            _ => return false,
        }
        match stream.read(&mut buffer[offset..]) {
            Ok(0) => return false,
            Ok(read) => offset += read,
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => {}
            Err(_) => return false,
        }
    }
    true
}

#[no_mangle]
pub unsafe extern "C" fn voidrpc_connect(pipe: i32, connected_pipe: *mut i32) -> *mut Connection {
    catch_unwind(|| match Connection::open(pipe) {
        Some(connection) => {
            if !connected_pipe.is_null() {
                *connected_pipe = connection.pipe;
            }
            Box::into_raw(Box::new(connection))
        }
        None => std::ptr::null_mut(),
    })
    .unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn voidrpc_is_connected(connection: *const Connection) -> i32 {
    if connection.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| (*connection).is_connected() as i32)).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn voidrpc_write(connection: *const Connection, opcode: u32, data: *const u8, length: u32) -> i32 {
    if connection.is_null() || (data.is_null() && length > 0) {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let bytes = if length == 0 { &[][..] } else { std::slice::from_raw_parts(data, length as usize) };
        (*connection).write(opcode, bytes) as i32
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn voidrpc_read(connection: *const Connection, opcode: *mut u32, buffer: *mut u8, capacity: u32, length: *mut u32) -> i32 {
    if connection.is_null() || opcode.is_null() || length.is_null() || buffer.is_null() || (capacity as usize) < MAX_FRAME {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let connection = &*connection;
        match connection.read() {
            Some(frame) => {
                std::ptr::copy_nonoverlapping(frame.data.as_ptr(), buffer, frame.data.len());
                *opcode = frame.opcode;
                *length = frame.data.len() as u32;
                1
            }
            None if connection.is_connected() => 0,
            None => -1,
        }
    }))
    .unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn voidrpc_close(connection: *mut Connection) {
    if connection.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(connection))));
}

#[no_mangle]
pub extern "C" fn voidrpc_find_pipe() -> i32 {
    catch_unwind(discovery::find_pipe).unwrap_or(-1)
}

#[no_mangle]
pub extern "C" fn voidrpc_version() -> u32 {
    1
}
