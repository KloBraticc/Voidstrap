use crate::config::Config;
use objc2::{
    Encode, Encoding, msg_send,
    rc::{Allocated, Retained, autoreleasepool},
    runtime::{AnyClass, AnyObject},
};
use std::{
    ffi::{CStr, c_void},
    fs::{File, OpenOptions},
    io::Write,
    os::{fd::AsRawFd, unix::fs::OpenOptionsExt},
    ptr,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Instant,
};

type Ref = *mut c_void;

#[repr(C)]
#[derive(Clone, Copy)]
struct Size {
    width: f64,
    height: f64,
}

unsafe impl Encode for Size {
    const ENCODING: Encoding = Encoding::Struct("CGSize", &[Encoding::Double, Encoding::Double]);
}

#[repr(C)]
struct SourceContext {
    version: isize,
    info: Ref,
    retain: Option<unsafe extern "C" fn(Ref) -> Ref>,
    release: Option<unsafe extern "C" fn(Ref)>,
    description: Option<unsafe extern "C" fn(Ref) -> Ref>,
    equal: Option<unsafe extern "C" fn(Ref, Ref) -> u8>,
    hash: Option<unsafe extern "C" fn(Ref) -> usize>,
    schedule: Option<unsafe extern "C" fn(Ref, Ref, Ref)>,
    cancel: Option<unsafe extern "C" fn(Ref, Ref, Ref)>,
    perform: Option<unsafe extern "C" fn(Ref)>,
}

#[repr(C)]
struct TimerContext {
    version: isize,
    info: Ref,
    retain: Option<unsafe extern "C" fn(Ref) -> Ref>,
    release: Option<unsafe extern "C" fn(Ref)>,
    description: Option<unsafe extern "C" fn(Ref) -> Ref>,
}

#[link(name = "CoreGraphics", kind = "framework")]
unsafe extern "C" {
    fn CGMainDisplayID() -> u32;
    fn CGDisplayIsActive(display: u32) -> u32;
    fn CGDisplayIsOnline(display: u32) -> u32;
    fn CGDisplayIsInMirrorSet(display: u32) -> u32;
    fn CGDisplayMirrorsDisplay(display: u32) -> u32;
    fn CGDisplayScreenSize(display: u32) -> Size;
    fn CGDisplayCopyDisplayMode(display: u32) -> Ref;
    fn CGDisplayCopyAllDisplayModes(display: u32, options: Ref) -> Ref;
    fn CGDisplayModeGetWidth(mode: Ref) -> usize;
    fn CGDisplayModeGetHeight(mode: Ref) -> usize;
    fn CGDisplayModeGetPixelWidth(mode: Ref) -> usize;
    fn CGDisplayModeGetPixelHeight(mode: Ref) -> usize;
    fn CGDisplayModeGetRefreshRate(mode: Ref) -> f64;
    fn CGBeginDisplayConfiguration(config: *mut Ref) -> i32;
    fn CGConfigureDisplayMirrorOfDisplay(config: Ref, display: u32, master: u32) -> i32;
    fn CGConfigureDisplayWithDisplayMode(config: Ref, display: u32, mode: Ref, options: Ref)
    -> i32;
    fn CGCompleteDisplayConfiguration(config: Ref, option: u32) -> i32;
    fn CGCancelDisplayConfiguration(config: Ref) -> i32;
    fn CGDisplayRegisterReconfigurationCallback(
        callback: unsafe extern "C" fn(u32, u32, Ref),
        context: Ref,
    ) -> i32;
    fn CGDisplayRemoveReconfigurationCallback(
        callback: unsafe extern "C" fn(u32, u32, Ref),
        context: Ref,
    ) -> i32;
}

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    static kCFRunLoopDefaultMode: Ref;
    fn CFRelease(value: Ref);
    fn CFArrayGetCount(array: Ref) -> isize;
    fn CFArrayGetValueAtIndex(array: Ref, index: isize) -> Ref;
    fn CFRunLoopGetCurrent() -> Ref;
    fn CFRunLoopRun();
    fn CFRunLoopStop(run_loop: Ref);
    fn CFRunLoopWakeUp(run_loop: Ref);
    fn CFRunLoopSourceCreate(allocator: Ref, order: isize, context: *mut SourceContext) -> Ref;
    fn CFRunLoopAddSource(run_loop: Ref, source: Ref, mode: Ref);
    fn CFRunLoopRemoveSource(run_loop: Ref, source: Ref, mode: Ref);
    fn CFRunLoopSourceSignal(source: Ref);
    fn CFAbsoluteTimeGetCurrent() -> f64;
    fn CFRunLoopTimerCreate(
        allocator: Ref,
        fire: f64,
        interval: f64,
        flags: usize,
        order: isize,
        callback: unsafe extern "C" fn(Ref, Ref),
        context: *mut TimerContext,
    ) -> Ref;
    fn CFRunLoopAddTimer(run_loop: Ref, timer: Ref, mode: Ref);
    fn CFRunLoopTimerInvalidate(timer: Ref);
}

#[link(name = "Foundation", kind = "framework")]
unsafe extern "C" {}

unsafe extern "C" {
    static _dispatch_main_q: u8;
}

struct OwnedRef(Ref);

impl OwnedRef {
    fn checked(value: Ref, message: &str) -> Result<Self, String> {
        if value.is_null() {
            Err(message.into())
        } else {
            Ok(Self(value))
        }
    }
}

impl Drop for OwnedRef {
    fn drop(&mut self) {
        unsafe { CFRelease(self.0) };
    }
}

struct Transaction(Ref);

impl Transaction {
    fn begin() -> Result<Self, String> {
        let mut config = ptr::null_mut();
        check(
            unsafe { CGBeginDisplayConfiguration(&mut config) },
            "Begin display configuration",
        )?;
        if config.is_null() {
            return Err("Display configuration is unavailable".into());
        }
        Ok(Self(config))
    }

    fn commit(mut self) -> Result<(), String> {
        let config = std::mem::replace(&mut self.0, ptr::null_mut());
        check(
            unsafe { CGCompleteDisplayConfiguration(config, 0) },
            "Apply display configuration",
        )
    }
}

impl Drop for Transaction {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { CGCancelDisplayConfiguration(self.0) };
        }
    }
}

fn check(code: i32, action: &str) -> Result<(), String> {
    if code == 0 {
        Ok(())
    } else {
        Err(format!("{action} failed: {code}"))
    }
}

fn class(name: &CStr) -> Result<&'static AnyClass, String> {
    AnyClass::get(name).ok_or_else(|| format!("macOS does not provide {}", name.to_string_lossy()))
}

fn new_object(name: &CStr) -> Result<Retained<AnyObject>, String> {
    let value: Option<Retained<AnyObject>> = unsafe { msg_send![class(name)?, new] };
    value.ok_or_else(|| format!("Could not create {}", name.to_string_lossy()))
}

fn mode(width: usize, height: usize, refresh: f64) -> Result<Retained<AnyObject>, String> {
    let allocated: Allocated<AnyObject> =
        unsafe { msg_send![class(c"CGVirtualDisplayMode")?, alloc] };
    let value: Option<Retained<AnyObject>> =
        unsafe { msg_send![allocated, initWithWidth: width, height: height, refreshRate: refresh] };
    value.ok_or_else(|| "Could not create a virtual display mode".into())
}

struct Wake {
    source: Ref,
    run_loop: Ref,
}

impl Wake {
    fn signal(&self) {
        unsafe {
            CFRunLoopSourceSignal(self.source);
            CFRunLoopWakeUp(self.run_loop);
        }
    }
}

unsafe extern "C" fn display_changed(_: u32, flags: u32, context: Ref) {
    if flags & 1 == 0 {
        unsafe { &*(context as *const Wake) }.signal();
    }
}

struct StopMonitor {
    queue: File,
    worker: Option<thread::JoinHandle<()>>,
}

impl StopMonitor {
    fn start(wake: &Wake, stopped: Arc<AtomicBool>, stdin: bool) -> Result<Self, String> {
        use std::os::fd::FromRawFd;
        let fd = unsafe { libc::kqueue() };
        if fd < 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        let queue = unsafe { File::from_raw_fd(fd) };
        let mut changes = Vec::with_capacity(5);
        for signal in [libc::SIGINT, libc::SIGTERM, libc::SIGHUP] {
            unsafe {
                libc::signal(signal, libc::SIG_IGN);
            }
            changes.push(event(
                signal as usize,
                libc::EVFILT_SIGNAL,
                libc::EV_ADD | libc::EV_ENABLE,
                0,
            ));
        }
        changes.push(event(
            1,
            libc::EVFILT_USER,
            libc::EV_ADD | libc::EV_CLEAR,
            0,
        ));
        if stdin {
            changes.push(event(
                0,
                libc::EVFILT_READ,
                libc::EV_ADD | libc::EV_ENABLE,
                0,
            ));
        }
        if unsafe {
            libc::kevent(
                fd,
                changes.as_ptr(),
                changes.len() as i32,
                ptr::null_mut(),
                0,
                ptr::null(),
            )
        } < 0
        {
            return Err(std::io::Error::last_os_error().to_string());
        }
        let source = wake.source as usize;
        let run_loop = wake.run_loop as usize;
        let worker = thread::Builder::new()
            .name("display lifecycle".into())
            .spawn(move || {
                let mut result = event(0, 0, 0, 0);
                loop {
                    let count =
                        unsafe { libc::kevent(fd, ptr::null(), 0, &mut result, 1, ptr::null()) };
                    if count < 0
                        && std::io::Error::last_os_error().raw_os_error() == Some(libc::EINTR)
                    {
                        continue;
                    }
                    if count > 0 && result.filter == libc::EVFILT_USER {
                        return;
                    }
                    stopped.store(true, Ordering::Release);
                    Wake {
                        source: source as Ref,
                        run_loop: run_loop as Ref,
                    }
                    .signal();
                    return;
                }
            })
            .map_err(|error| error.to_string())?;
        Ok(Self {
            queue,
            worker: Some(worker),
        })
    }
}

impl Drop for StopMonitor {
    fn drop(&mut self) {
        let change = event(1, libc::EVFILT_USER, 0, libc::NOTE_TRIGGER);
        unsafe {
            libc::kevent(
                self.queue.as_raw_fd(),
                &change,
                1,
                ptr::null_mut(),
                0,
                ptr::null(),
            );
        }
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn event(ident: usize, filter: i16, flags: u16, fflags: u32) -> libc::kevent {
    libc::kevent {
        ident,
        filter,
        flags,
        fflags,
        data: 0,
        udata: ptr::null_mut(),
    }
}

struct Session {
    display: Retained<AnyObject>,
    original: OwnedRef,
    physical: u32,
    virtual_id: u32,
    refresh: f64,
    run_loop: Ref,
    changed: bool,
    ready: bool,
    timeout: bool,
    error: Option<String>,
    stopped: Arc<AtomicBool>,
    started: Instant,
}

impl Session {
    fn create(config: Config, stopped: Arc<AtomicBool>) -> Result<Self, String> {
        let started = Instant::now();
        let physical = unsafe { CGMainDisplayID() };
        if physical == 0 || unsafe { CGDisplayIsOnline(physical) } == 0 {
            return Err("No active main display".into());
        }
        if unsafe { CGDisplayIsInMirrorSet(physical) } != 0 {
            return Err(
                "Stop existing screen mirroring before starting the virtual display".into(),
            );
        }
        let original = OwnedRef::checked(
            unsafe { CGDisplayCopyDisplayMode(physical) },
            "The main display mode is unavailable",
        )?;
        let (width, height) = config
            .size
            .map(|(w, h)| (w as usize, h as usize))
            .unwrap_or_else(|| unsafe {
                (
                    CGDisplayModeGetWidth(original.0),
                    CGDisplayModeGetHeight(original.0),
                )
            });
        let (pixels_wide, pixels_high) = if config.size.is_none() {
            unsafe {
                (
                    CGDisplayModeGetPixelWidth(original.0),
                    CGDisplayModeGetPixelHeight(original.0),
                )
            }
        } else {
            (width, height)
        };
        if width == 0
            || height == 0
            || pixels_wide > u32::MAX as usize
            || pixels_high > u32::MAX as usize
        {
            return Err("The main display dimensions are invalid".into());
        }
        let descriptor = new_object(c"CGVirtualDisplayDescriptor")?;
        let name: Retained<AnyObject> = unsafe {
            msg_send![class(c"NSString")?, stringWithUTF8String: c"Voidstrap Virtual Display".as_ptr()]
        };
        unsafe {
            let _: () = msg_send![&*descriptor, setName: &*name];
            let _: () = msg_send![&*descriptor, setMaxPixelsWide: pixels_wide as u32];
            let _: () = msg_send![&*descriptor, setMaxPixelsHigh: pixels_high as u32];
            let _: () =
                msg_send![&*descriptor, setSizeInMillimeters: CGDisplayScreenSize(physical)];
            let _: () = msg_send![&*descriptor, setVendorID: 0x5653u32];
            let _: () = msg_send![&*descriptor, setProductID: 0x240u32];
            let _: () = msg_send![&*descriptor, setSerialNum: 1u32];
            let _: () =
                msg_send![&*descriptor, setDispatchQueue: ptr::addr_of!(_dispatch_main_q) as Ref];
        }
        let allocated: Allocated<AnyObject> =
            unsafe { msg_send![class(c"CGVirtualDisplay")?, alloc] };
        let display: Option<Retained<AnyObject>> =
            unsafe { msg_send![allocated, initWithDescriptor: &*descriptor] };
        let display =
            display.ok_or_else(|| "macOS refused to create the virtual display".to_string())?;
        let settings = new_object(c"CGVirtualDisplaySettings")?;
        let fast = mode(width, height, config.refresh as f64)?;
        let fallback = mode(width, height, 60.0)?;
        let objects = [Retained::as_ptr(&fast), Retained::as_ptr(&fallback)];
        let modes: Retained<AnyObject> = unsafe {
            msg_send![class(c"NSArray")?, arrayWithObjects: objects.as_ptr(), count: objects.len()]
        };
        unsafe {
            let _: () = msg_send![&*settings, setHiDPI: u32::from(config.size.is_none() && pixels_wide > width)];
            let _: () = msg_send![&*settings, setModes: &*modes];
            let applied: bool = msg_send![&*display, applySettings: &*settings];
            if !applied {
                return Err("macOS refused the virtual display modes".into());
            }
        }
        let virtual_id: u32 = unsafe { msg_send![&*display, displayID] };
        if virtual_id == 0 {
            return Err("macOS returned an invalid virtual display identifier".into());
        }
        Ok(Self {
            display,
            original,
            physical,
            virtual_id,
            refresh: config.refresh as f64,
            run_loop: unsafe { CFRunLoopGetCurrent() },
            changed: false,
            ready: false,
            timeout: false,
            error: None,
            stopped,
            started,
        })
    }

    fn update(&mut self) -> Result<(), String> {
        if self.stopped.load(Ordering::Acquire) {
            unsafe { CFRunLoopStop(self.run_loop) };
            return Ok(());
        }
        if unsafe { CGDisplayIsOnline(self.physical) } == 0 {
            return Err("The main display was disconnected".into());
        }
        if self.ready {
            if unsafe { CGDisplayMirrorsDisplay(self.physical) } != self.virtual_id {
                return Err("Virtual display mirroring ended".into());
            }
            return Ok(());
        }
        if self.timeout {
            return Err("Timed out waiting for the virtual display".into());
        }
        if unsafe { CGDisplayIsActive(self.virtual_id) } == 0 {
            return Ok(());
        }
        if !self.changed {
            let modes = unsafe { CGDisplayCopyAllDisplayModes(self.virtual_id, ptr::null_mut()) };
            if modes.is_null() {
                return Ok(());
            }
            let modes = OwnedRef(modes);
            let mut selected = ptr::null_mut();
            for index in 0..unsafe { CFArrayGetCount(modes.0) } {
                let mode = unsafe { CFArrayGetValueAtIndex(modes.0, index) };
                if (unsafe { CGDisplayModeGetRefreshRate(mode) } - self.refresh).abs() < 0.5 {
                    selected = mode;
                    break;
                }
            }
            if selected.is_null() {
                return Ok(());
            }
            let transaction = Transaction::begin()?;
            check(
                unsafe {
                    CGConfigureDisplayWithDisplayMode(
                        transaction.0,
                        self.virtual_id,
                        selected,
                        ptr::null_mut(),
                    )
                },
                "Select virtual display mode",
            )?;
            check(
                unsafe {
                    CGConfigureDisplayMirrorOfDisplay(transaction.0, self.physical, self.virtual_id)
                },
                "Enable virtual display mirroring",
            )?;
            self.changed = true;
            transaction.commit()?;
        }
        if unsafe { CGDisplayMirrorsDisplay(self.physical) } == self.virtual_id {
            let current = OwnedRef::checked(
                unsafe { CGDisplayCopyDisplayMode(self.virtual_id) },
                "Virtual display mode is unavailable",
            )?;
            let refresh = unsafe { CGDisplayModeGetRefreshRate(current.0) };
            if (refresh - self.refresh).abs() >= 0.5 {
                return Err("The requested refresh rate was not applied".into());
            }
            self.ready = true;
            println!(
                "{{\"ready\":true,\"physical\":{},\"virtual\":{},\"refresh\":{},\"startup_us\":{}}}",
                self.physical,
                self.virtual_id,
                refresh,
                self.started.elapsed().as_micros()
            );
            std::io::stdout()
                .flush()
                .map_err(|error| error.to_string())?;
        }
        Ok(())
    }

    fn restore(&mut self) -> Result<(), String> {
        if !self.changed {
            return Ok(());
        }
        if unsafe { CGDisplayIsOnline(self.physical) } == 0 {
            self.changed = false;
            return Ok(());
        }
        let mirror = unsafe { CGDisplayMirrorsDisplay(self.physical) };
        if mirror != 0 && mirror != self.virtual_id {
            self.changed = false;
            return Ok(());
        }
        let transaction = Transaction::begin()?;
        check(
            unsafe { CGConfigureDisplayMirrorOfDisplay(transaction.0, self.physical, 0) },
            "Restore display mirroring",
        )?;
        check(
            unsafe {
                CGConfigureDisplayWithDisplayMode(
                    transaction.0,
                    self.physical,
                    self.original.0,
                    ptr::null_mut(),
                )
            },
            "Restore original display mode",
        )?;
        transaction.commit()?;
        self.changed = false;
        Ok(())
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        if let Err(error) = self.restore() {
            eprintln!("Virtual display restoration: {error}");
        }
        let _ = &self.display;
    }
}

unsafe extern "C" fn perform(context: Ref) {
    autoreleasepool(|_| {
        let session = unsafe { &mut *(context as *mut Session) };
        if let Err(error) = session.update() {
            session.error = Some(error);
            unsafe { CFRunLoopStop(session.run_loop) };
        }
    });
}

unsafe extern "C" fn timeout(timer: Ref, context: Ref) {
    let session = unsafe { &mut *(context as *mut Session) };
    session.timeout = !session.ready && session.started.elapsed().as_secs() >= 10;
    unsafe { perform(context) };
    if session.ready || session.error.is_some() || session.stopped.load(Ordering::Acquire) {
        unsafe { CFRunLoopTimerInvalidate(timer) };
    }
}

pub fn run(config: Config) -> Result<(), String> {
    autoreleasepool(|_| run_inner(config))
}

fn run_inner(config: Config) -> Result<(), String> {
    if config.probe {
        let display = unsafe { CGMainDisplayID() };
        let mode = OwnedRef::checked(
            unsafe { CGDisplayCopyDisplayMode(display) },
            "No main display mode",
        )?;
        println!(
            "{{\"display\":{},\"width\":{},\"height\":{},\"pixel_width\":{},\"pixel_height\":{},\"refresh\":{},\"mirror\":{}}}",
            display,
            unsafe { CGDisplayModeGetWidth(mode.0) },
            unsafe { CGDisplayModeGetHeight(mode.0) },
            unsafe { CGDisplayModeGetPixelWidth(mode.0) },
            unsafe { CGDisplayModeGetPixelHeight(mode.0) },
            unsafe { CGDisplayModeGetRefreshRate(mode.0) },
            unsafe { CGDisplayMirrorsDisplay(display) }
        );
        return Ok(());
    }
    let lock_path = std::env::temp_dir()
        .join(format!("voidstrap-virtualdisplay-{}.lock", unsafe {
            libc::getuid()
        }));
    let lock = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .mode(0o600)
        .custom_flags(libc::O_NOFOLLOW)
        .open(lock_path)
        .map_err(|error| error.to_string())?;
    if unsafe { libc::flock(lock.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 {
        return Err("A virtual display session is already running".into());
    }
    let stopped = Arc::new(AtomicBool::new(false));
    let mut session = Box::new(Session::create(config, stopped.clone())?);
    let context = (&mut *session as *mut Session).cast();
    let mut source_context = SourceContext {
        version: 0,
        info: context,
        retain: None,
        release: None,
        description: None,
        equal: None,
        hash: None,
        schedule: None,
        cancel: None,
        perform: Some(perform),
    };
    let source = OwnedRef::checked(
        unsafe { CFRunLoopSourceCreate(ptr::null_mut(), 0, &mut source_context) },
        "Could not create a display event source",
    )?;
    let wake = Box::new(Wake {
        source: source.0,
        run_loop: session.run_loop,
    });
    let wake_context = (&*wake as *const Wake).cast_mut().cast();
    let monitor = StopMonitor::start(&wake, stopped, config.watch_stdin)?;
    check(
        unsafe { CGDisplayRegisterReconfigurationCallback(display_changed, wake_context) },
        "Watch display changes",
    )?;
    unsafe { CFRunLoopAddSource(session.run_loop, source.0, kCFRunLoopDefaultMode) };
    let mut timer_context = TimerContext {
        version: 0,
        info: context,
        retain: None,
        release: None,
        description: None,
    };
    let timer = unsafe {
        CFRunLoopTimerCreate(
            ptr::null_mut(),
            CFAbsoluteTimeGetCurrent() + 0.5,
            0.5,
            0,
            0,
            timeout,
            &mut timer_context,
        )
    };
    if timer.is_null() {
        drop(monitor);
        unsafe {
            CGDisplayRemoveReconfigurationCallback(display_changed, wake_context);
            CFRunLoopRemoveSource(session.run_loop, source.0, kCFRunLoopDefaultMode);
        }
        return Err("Could not create the display startup deadline".into());
    }
    unsafe { CFRunLoopAddTimer(session.run_loop, timer, kCFRunLoopDefaultMode) };
    wake.signal();
    unsafe { CFRunLoopRun() };
    drop(monitor);
    unsafe {
        CGDisplayRemoveReconfigurationCallback(display_changed, wake_context);
        CFRunLoopRemoveSource(session.run_loop, source.0, kCFRunLoopDefaultMode);
        if !timer.is_null() {
            CFRunLoopTimerInvalidate(timer);
            CFRelease(timer);
        }
    }
    session.restore()?;
    if let Some(error) = session.error.take() {
        return Err(error);
    }
    println!("{{\"restored\":true}}");
    Ok(())
}
