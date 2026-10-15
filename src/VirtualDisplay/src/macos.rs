use crate::config::Config;
use objc2::{
    Encode, Encoding, msg_send,
    rc::{Retained, autoreleasepool},
    runtime::{AnyClass, AnyObject},
};
use std::{
    ffi::{CStr, CString, c_void},
    fs::{File, OpenOptions},
    io::Write,
    os::{fd::AsRawFd, unix::fs::OpenOptionsExt},
    ptr,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

type Ref = *mut c_void;

#[repr(C)]
#[derive(Clone, Copy)]
struct Point {
    x: f64,
    y: f64,
}

unsafe impl Encode for Point {
    const ENCODING: Encoding = Encoding::Struct("CGPoint", &[Encoding::Double, Encoding::Double]);
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
    fn CGDisplayMirrorsDisplay(display: u32) -> u32;
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
}

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    static kCFRunLoopDefaultMode: Ref;
    fn CFRelease(value: Ref);
    fn CFArrayGetCount(array: Ref) -> isize;
    fn CFArrayGetValueAtIndex(array: Ref, index: isize) -> Ref;
    fn CFRunLoopGetCurrent() -> Ref;
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

const DISPLAY_NAME: &CStr = c"Voidstrap Virtual Display";
const POLL_SECONDS: f64 = 0.5;
const MODE_RETRY: Duration = Duration::from_secs(2);

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

fn status(message: std::fmt::Arguments<'_>) -> Result<(), String> {
    let mut output = std::io::stdout().lock();
    writeln!(output, "{message}")
        .and_then(|_| output.flush())
        .map_err(|error| error.to_string())
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

fn refresh_rate(display: u32) -> f64 {
    let current = unsafe { CGDisplayCopyDisplayMode(display) };
    if current.is_null() {
        return 0.0;
    }
    let current = OwnedRef(current);
    unsafe { CGDisplayModeGetRefreshRate(current.0) }
}

fn refresh_rates(target: u32) -> Vec<f64> {
    let mut rates = vec![60.0, 120.0, target as f64];
    rates.sort_by(f64::total_cmp);
    rates.dedup();
    rates
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
    application: Retained<AnyObject>,
    physical: u32,
    run_loop: Ref,
    active: bool,
    ready: bool,
    refresh: f64,
    mode_requested: Option<Instant>,
    error: Option<String>,
    stopped: Arc<AtomicBool>,
    started: Instant,
}

impl Session {
    fn create(config: Config, stopped: Arc<AtomicBool>) -> Result<Self, String> {
        let started = Instant::now();
        if unsafe {
            libc::dlopen(
                c"/System/Library/Frameworks/AppKit.framework/AppKit".as_ptr(),
                libc::RTLD_LAZY | libc::RTLD_LOCAL,
            )
        }
        .is_null()
        {
            return Err("The macOS display framework is unavailable".into());
        }
        let application: Retained<AnyObject> =
            unsafe { msg_send![class(c"NSApplication")?, sharedApplication] };
        unsafe {
            let _: bool = msg_send![&*application, setActivationPolicy: 1isize];
        }
        let physical = unsafe { CGMainDisplayID() };
        if physical == 0 {
            return Err("No active main display".into());
        }

        let (width, height) = config
            .size
            .map(|(w, h)| (w as usize, h as usize))
            .unwrap_or((0, 0));
        let rates = refresh_rates(config.refresh);
        let name = CString::from(DISPLAY_NAME);
        if unsafe {
            virtualdisplay::init(name.as_ptr(), width, height, rates.as_ptr(), rates.len())
        } != 0
        {
            return Err("The virtual display settings were rejected".into());
        }
        let mut session = Self {
            application,
            physical,
            run_loop: unsafe { CFRunLoopGetCurrent() },
            active: false,
            ready: false,
            refresh: config.refresh as f64,
            mode_requested: None,
            error: None,
            stopped,
            started,
        };
        if virtualdisplay::start_display() != 0 {
            return Err("macOS refused to create the virtual display".into());
        }
        session.active = true;
        Ok(session)
    }

    fn poll(&mut self) -> Result<(), String> {
        if self.stopped.load(Ordering::Acquire) {
            self.stop();
            return Ok(());
        }
        if self.ready {
            return Ok(());
        }
        let main = unsafe { CGMainDisplayID() };
        if main == 0 || main == self.physical {
            return Ok(());
        }
        if unsafe { CGDisplayMirrorsDisplay(self.physical) } != main {
            return Ok(());
        }
        let refresh = refresh_rate(main);
        if (refresh - self.refresh).abs() >= 0.5 {
            if self
                .mode_requested
                .is_none_or(|requested| requested.elapsed() >= MODE_RETRY)
            {
                self.mode_requested = Some(Instant::now());
                self.select_requested_mode(main)?;
            }
            return Ok(());
        }
        self.ready = true;
        status(format_args!(
            "{{\"ready\":true,\"physical\":{},\"virtual\":{},\"refresh\":{},\"startup_us\":{}}}",
            self.physical,
            main,
            refresh,
            self.started.elapsed().as_micros()
        ))
    }

    fn select_requested_mode(&self, display: u32) -> Result<(), String> {
        let modes = unsafe { CGDisplayCopyAllDisplayModes(display, ptr::null_mut()) };
        if modes.is_null() {
            return Ok(());
        }
        let modes = OwnedRef(modes);
        for index in 0..unsafe { CFArrayGetCount(modes.0) } {
            let mode = unsafe { CFArrayGetValueAtIndex(modes.0, index) };
            if (unsafe { CGDisplayModeGetRefreshRate(mode) } - self.refresh).abs() >= 0.5 {
                continue;
            }
            let transaction = Transaction::begin()?;
            check(
                unsafe {
                    CGConfigureDisplayWithDisplayMode(transaction.0, display, mode, ptr::null_mut())
                },
                "Select virtual display mode",
            )?;
            check(
                unsafe { CGConfigureDisplayMirrorOfDisplay(transaction.0, self.physical, display) },
                "Keep virtual display mirroring",
            )?;
            return transaction.commit();
        }
        Ok(())
    }

    fn restore(&mut self) {
        if !self.active {
            return;
        }
        self.active = false;
        virtualdisplay::end_display();
    }

    fn stop(&self) {
        unsafe {
            let _: () = msg_send![&*self.application, stop: ptr::null_mut::<AnyObject>()];
            if let Some(event_class) = AnyClass::get(c"NSEvent") {
                let event: Option<Retained<AnyObject>> = msg_send![event_class, otherEventWithType: 15usize, location: Point { x: 0.0, y: 0.0 }, modifierFlags: 0usize, timestamp: 0.0f64, windowNumber: 0isize, context: ptr::null_mut::<AnyObject>(), subtype: 0i16, data1: 0isize, data2: 0isize];
                if let Some(event) = event {
                    let _: () = msg_send![&*self.application, postEvent: &*event, atStart: true];
                }
            }
            CFRunLoopStop(self.run_loop);
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.restore();
    }
}

unsafe extern "C" fn perform(context: Ref) {
    autoreleasepool(|_| {
        let session = unsafe { &mut **(context as *mut *mut Session) };
        if let Err(error) = session.poll() {
            session.error = Some(error);
            session.stop();
        }
    });
}

unsafe extern "C" fn tick(timer: Ref, context: Ref) {
    unsafe { perform(context) };
    let session = unsafe { &**(context as *const *mut Session) };
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
        status(format_args!(
            "{{\"display\":{},\"width\":{},\"height\":{},\"pixel_width\":{},\"pixel_height\":{},\"refresh\":{},\"mirror\":{}}}",
            display,
            unsafe { CGDisplayModeGetWidth(mode.0) },
            unsafe { CGDisplayModeGetHeight(mode.0) },
            unsafe { CGDisplayModeGetPixelWidth(mode.0) },
            unsafe { CGDisplayModeGetPixelHeight(mode.0) },
            unsafe { CGDisplayModeGetRefreshRate(mode.0) },
            unsafe { CGDisplayMirrorsDisplay(display) }
        ))?;
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
    if config.watch_stdin {
        let mut input = libc::pollfd {
            fd: libc::STDIN_FILENO,
            events: libc::POLLIN,
            revents: 0,
        };
        let available = unsafe { libc::poll(&mut input, 1, 0) };
        if available < 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        if available > 0 && input.revents != 0 {
            status(format_args!("{{\"restored\":true}}"))?;
            return Ok(());
        }
    }
    let stopped = Arc::new(AtomicBool::new(false));
    let mut session_pointer: *mut Session = ptr::null_mut();
    let context = ptr::addr_of_mut!(session_pointer).cast();
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
        run_loop: unsafe { CFRunLoopGetCurrent() },
    });
    let monitor = StopMonitor::start(&wake, stopped.clone(), config.watch_stdin)?;
    if stopped.load(Ordering::Acquire) {
        status(format_args!("{{\"restored\":true}}"))?;
        return Ok(());
    }
    let mut session = Box::new(Session::create(config, stopped)?);
    unsafe { ptr::write(context.cast::<*mut Session>(), &mut *session) };
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
            CFAbsoluteTimeGetCurrent() + POLL_SECONDS,
            POLL_SECONDS,
            0,
            0,
            tick,
            &mut timer_context,
        )
    };
    if timer.is_null() {
        drop(monitor);
        unsafe { CFRunLoopRemoveSource(session.run_loop, source.0, kCFRunLoopDefaultMode) };
        return Err("Could not create the display polling timer".into());
    }
    unsafe { CFRunLoopAddTimer(session.run_loop, timer, kCFRunLoopDefaultMode) };
    let application = Retained::as_ptr(&session.application);
    unsafe {
        let _: () = msg_send![&*application, run];
    }
    drop(monitor);
    unsafe {
        CFRunLoopRemoveSource(session.run_loop, source.0, kCFRunLoopDefaultMode);
        CFRunLoopTimerInvalidate(timer);
        CFRelease(timer);
    }
    session.restore();
    if let Some(error) = session.error.take() {
        return Err(error);
    }
    status(format_args!("{{\"restored\":true}}"))?;
    Ok(())
}
