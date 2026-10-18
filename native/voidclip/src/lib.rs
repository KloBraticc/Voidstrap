pub mod audio;
pub mod ffmpeg;
pub mod pipeline;
pub mod ring;
pub mod session;
pub mod ts;

use pipeline::Config;
use session::{Session, Status};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;
use std::sync::{Mutex, OnceLock};

struct GlobalState {
    session: Option<Session>,
    last_error: String,
}

fn state() -> &'static Mutex<GlobalState> {
    static STATE: OnceLock<Mutex<GlobalState>> = OnceLock::new();
    STATE.get_or_init(|| {
        Mutex::new(GlobalState {
            session: None,
            last_error: String::new(),
        })
    })
}

unsafe fn read_utf8<'a>(ptr: *const u8, len: usize) -> Result<&'a str, String> {
    if ptr.is_null() {
        return Err("null string pointer".into());
    }
    let bytes = slice::from_raw_parts(ptr, len);
    std::str::from_utf8(bytes).map_err(|e| e.to_string())
}

unsafe fn write_owned_bytes(bytes: Vec<u8>, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    if out_ptr.is_null() || out_len.is_null() {
        return 0;
    }
    let boxed = bytes.into_boxed_slice();
    let len = boxed.len();
    let raw = Box::into_raw(boxed) as *mut u8;
    *out_ptr = raw;
    *out_len = len;
    1
}

fn set_error(msg: impl Into<String>) -> i32 {
    if let Ok(mut g) = state().lock() {
        g.last_error = msg.into();
    }
    0
}

#[no_mangle]
pub extern "C" fn vc_version() -> u32 {
    1
}

#[no_mangle]
pub unsafe extern "C" fn vc_start(config_ptr: *const u8, config_len: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let json = match read_utf8(config_ptr, config_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        let cfg: Config = match serde_json::from_str(json) {
            Ok(c) => c,
            Err(e) => return set_error(format!("invalid config json: {e}")),
        };
        let mut g = match state().lock() {
            Ok(g) => g,
            Err(_) => return 0,
        };
        if let Some(mut old) = g.session.take() {
            old.stop();
        }
        match Session::start(cfg) {
            Ok(sess) => {
                g.last_error.clear();
                g.session = Some(sess);
                1
            }
            Err(e) => {
                g.last_error = e;
                0
            }
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn vc_stop() -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = state().lock() {
            if let Some(mut s) = g.session.take() {
                s.stop();
            }
            1
        } else {
            0
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_reconfigure(config_ptr: *const u8, config_len: usize) -> i32 {
    vc_start(config_ptr, config_len)
}

#[no_mangle]
pub unsafe extern "C" fn vc_save(output_ptr: *const u8, output_len: usize, seconds: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let output = match read_utf8(output_ptr, output_len) {
            Ok(s) => s.to_owned(),
            Err(e) => return set_error(e),
        };
        let mut g = match state().lock() {
            Ok(g) => g,
            Err(_) => return 0,
        };
        let res = match g.session.as_ref() {
            Some(s) => s.save(&output, seconds),
            None => Err("Clipper is not running".into()),
        };
        match res {
            Ok(()) => {
                g.last_error.clear();
                1
            }
            Err(e) => {
                g.last_error = e;
                0
            }
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_record_start(temp_ptr: *const u8, temp_len: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let temp_path = match read_utf8(temp_ptr, temp_len) {
            Ok(s) => s.to_owned(),
            Err(e) => return set_error(e),
        };
        let mut g = match state().lock() {
            Ok(g) => g,
            Err(_) => return 0,
        };
        let res = match g.session.as_ref() {
            Some(s) => s.start_recording(&temp_path),
            None => Err("Clipper is not running".into()),
        };
        match res {
            Ok(()) => {
                g.last_error.clear();
                1
            }
            Err(e) => {
                g.last_error = e;
                0
            }
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_record_stop(output_ptr: *const u8, output_len: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let output = match read_utf8(output_ptr, output_len) {
            Ok(s) => s.to_owned(),
            Err(e) => return set_error(e),
        };
        let mut g = match state().lock() {
            Ok(g) => g,
            Err(_) => return 0,
        };
        let res = match g.session.as_ref() {
            Some(s) => s.stop_recording(&output),
            None => Err("Clipper is not running".into()),
        };
        match res {
            Ok(()) => {
                g.last_error.clear();
                1
            }
            Err(e) => {
                g.last_error = e;
                0
            }
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_trim(
    ffmpeg_ptr: *const u8,
    ffmpeg_len: usize,
    input_ptr: *const u8,
    input_len: usize,
    output_ptr: *const u8,
    output_len: usize,
    start_ms: u64,
    end_ms: u64,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let ffmpeg_bin = match read_utf8(ffmpeg_ptr, ffmpeg_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        let input = match read_utf8(input_ptr, input_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        let output = match read_utf8(output_ptr, output_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        match session::trim(ffmpeg_bin, input, output, start_ms, end_ms) {
            Ok(()) => 1,
            Err(e) => set_error(e),
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_thumbnail(
    ffmpeg_ptr: *const u8,
    ffmpeg_len: usize,
    input_ptr: *const u8,
    input_len: usize,
    output_ptr: *const u8,
    output_len: usize,
    time_ms: u64,
    width: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let ffmpeg_bin = match read_utf8(ffmpeg_ptr, ffmpeg_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        let input = match read_utf8(input_ptr, input_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        let output = match read_utf8(output_ptr, output_len) {
            Ok(s) => s,
            Err(e) => return set_error(e),
        };
        match session::thumbnail(ffmpeg_bin, input, output, time_ms, width) {
            Ok(()) => 1,
            Err(e) => set_error(e),
        }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_probe_duration_ms(
    ffmpeg_ptr: *const u8,
    ffmpeg_len: usize,
    input_ptr: *const u8,
    input_len: usize,
) -> i64 {
    catch_unwind(AssertUnwindSafe(|| {
        let ffmpeg_bin = match read_utf8(ffmpeg_ptr, ffmpeg_len) {
            Ok(s) => s,
            Err(_) => return -1,
        };
        let input = match read_utf8(input_ptr, input_len) {
            Ok(s) => s,
            Err(_) => return -1,
        };
        ffmpeg::duration_ms(ffmpeg_bin, input)
    }))
    .unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn vc_status_json(out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let status = match state().lock() {
            Ok(g) => match g.session.as_ref() {
                Some(s) => {
                    let mut st = s.status();
                    if st.last_error.is_empty() && !g.last_error.is_empty() {
                        st.last_error = g.last_error.clone();
                    }
                    st
                }
                None => Status {
                    running: false,
                    last_error: g.last_error.clone(),
                    ..Default::default()
                },
            },
            Err(_) => Status::default(),
        };
        let json = serde_json::to_vec(&status).unwrap_or_else(|_| b"{}".to_vec());
        write_owned_bytes(json, out_ptr, out_len)
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_last_error(out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let err = match state().lock() {
            Ok(g) => {
                if !g.last_error.is_empty() {
                    g.last_error.clone()
                } else if let Some(s) = g.session.as_ref() {
                    s.status().last_error
                } else {
                    String::new()
                }
            }
            Err(_) => String::new(),
        };
        write_owned_bytes(err.into_bytes(), out_ptr, out_len)
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn vc_free(ptr: *mut u8, len: usize) {
    if ptr.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let slice = slice::from_raw_parts_mut(ptr, len);
        drop(Box::from_raw(slice as *mut [u8]));
    }));
}
