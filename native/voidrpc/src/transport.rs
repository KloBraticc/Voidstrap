use std::io::{self, Read, Write};
use std::sync::atomic::AtomicBool;
use std::time::Duration;

#[cfg(unix)]
pub struct Stream(std::os::unix::net::UnixStream);

#[cfg(unix)]
impl Stream {
    pub fn connect(path: &str) -> Option<Stream> {
        let stream = std::os::unix::net::UnixStream::connect(path).ok()?;
        stream.set_write_timeout(Some(Duration::from_secs(1))).ok()?;
        Some(Stream(stream))
    }

    pub fn probe(path: &str) -> bool {
        std::os::unix::net::UnixStream::connect(path).is_ok()
    }

    pub fn try_clone(&self) -> io::Result<Stream> {
        self.0.try_clone().map(Stream)
    }

    pub fn shutdown(&self) {
        let _ = self.0.shutdown(std::net::Shutdown::Both);
    }

    pub fn wait_readable(&mut self, _stop: &AtomicBool) -> io::Result<bool> {
        Ok(true)
    }
}

#[cfg(unix)]
impl Read for Stream {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        self.0.read(buffer)
    }
}

#[cfg(unix)]
impl Write for Stream {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        self.0.write(buffer)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.0.flush()
    }
}

#[cfg(windows)]
pub struct Stream(std::sync::Arc<std::fs::File>);

#[cfg(windows)]
#[link(name = "kernel32")]
extern "system" {
    fn PeekNamedPipe(pipe: isize, buffer: *mut u8, size: u32, read: *mut u32, available: *mut u32, left: *mut u32) -> i32;
}

#[cfg(windows)]
impl Stream {
    pub fn connect(path: &str) -> Option<Stream> {
        std::fs::OpenOptions::new().read(true).write(true).open(path).ok().map(|file| Stream(std::sync::Arc::new(file)))
    }

    pub fn probe(path: &str) -> bool {
        std::path::Path::new(path).exists()
    }

    pub fn try_clone(&self) -> io::Result<Stream> {
        Ok(Stream(self.0.clone()))
    }

    pub fn shutdown(&self) {}

    pub fn wait_readable(&mut self, stop: &AtomicBool) -> io::Result<bool> {
        use std::os::windows::io::AsRawHandle;
        use std::sync::atomic::Ordering;
        let handle = self.0.as_raw_handle() as isize;
        loop {
            if stop.load(Ordering::Acquire) {
                return Ok(false);
            }
            let mut available = 0u32;
            if unsafe { PeekNamedPipe(handle, std::ptr::null_mut(), 0, std::ptr::null_mut(), &mut available, std::ptr::null_mut()) } == 0 {
                return Err(io::Error::last_os_error());
            }
            if available > 0 {
                return Ok(true);
            }
            std::thread::sleep(Duration::from_millis(25));
        }
    }
}

#[cfg(windows)]
impl Read for Stream {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        (&*self.0).read(buffer)
    }
}

#[cfg(windows)]
impl Write for Stream {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        (&*self.0).write(buffer)
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
