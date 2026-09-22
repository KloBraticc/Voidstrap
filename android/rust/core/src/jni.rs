use crate::archive::Error;
use crate::watcher::{Data, Event, Watcher};
use jni_sys::{JNIEnv, jboolean, jbyte, jbyteArray, jclass, jint, jlong, jlongArray, jobject, jobjectArray, jsize, jstring};
use std::ffi::CString;
use std::panic::{self, AssertUnwindSafe};
use std::ptr;

const REJECTED: &str = "com/voidstrap/android/ModArchives$Rejected";
const IO: &str = "java/io/IOException";
const STATE: &str = "java/lang/IllegalStateException";

pub unsafe fn get_string(env: *mut JNIEnv, s: jstring) -> Option<String> {
    if s.is_null() {
        return None;
    }
    unsafe {
        let f = &**env;
        let len = (f.GetStringLength?)(env, s);
        let chars = (f.GetStringChars?)(env, s, ptr::null_mut());
        if chars.is_null() {
            return None;
        }
        let out = String::from_utf16_lossy(std::slice::from_raw_parts(chars, len.max(0) as usize));
        (f.ReleaseStringChars?)(env, s, chars);
        Some(out)
    }
}

pub unsafe fn new_string(env: *mut JNIEnv, s: &str) -> jstring {
    let units: Vec<u16> = s.encode_utf16().collect();
    unsafe {
        match (**env).NewString {
            Some(new) => new(env, units.as_ptr(), units.len() as jsize),
            None => ptr::null_mut(),
        }
    }
}

pub unsafe fn get_bytes(env: *mut JNIEnv, array: jbyteArray) -> Option<Vec<u8>> {
    if array.is_null() {
        return None;
    }
    unsafe {
        let f = &**env;
        let len = (f.GetArrayLength?)(env, array).max(0) as usize;
        let mut out = vec![0u8; len];
        (f.GetByteArrayRegion?)(env, array, 0, len as jsize, out.as_mut_ptr() as *mut jbyte);
        Some(out)
    }
}

pub unsafe fn new_bytes(env: *mut JNIEnv, data: &[u8]) -> jbyteArray {
    unsafe {
        let f = &**env;
        let (Some(new), Some(set)) = (f.NewByteArray, f.SetByteArrayRegion) else {
            return ptr::null_mut();
        };
        let array = new(env, data.len() as jsize);
        if !array.is_null() {
            set(env, array, 0, data.len() as jsize, data.as_ptr() as *const jbyte);
        }
        array
    }
}

pub unsafe fn throw(env: *mut JNIEnv, class: &str, message: &str) {
    let (Ok(c), Ok(m)) = (CString::new(class), CString::new(message.replace('\0', " "))) else {
        return;
    };
    unsafe {
        let f = &**env;
        let (Some(find), Some(throw_new), Some(delete)) = (f.FindClass, f.ThrowNew, f.DeleteLocalRef) else {
            return;
        };
        let cls = find(env, c.as_ptr());
        if cls.is_null() {
            return;
        }
        throw_new(env, cls, m.as_ptr());
        delete(env, cls);
    }
}

pub unsafe fn throw_error(env: *mut JNIEnv, e: &Error) {
    unsafe {
        match e {
            Error::Rejected(m) => throw(env, REJECTED, m),
            Error::Io(m) => throw(env, IO, m),
        }
    }
}

pub fn guard<T>(env: *mut JNIEnv, fallback: T, body: impl FnOnce() -> T) -> T {
    match panic::catch_unwind(AssertUnwindSafe(body)) {
        Ok(v) => v,
        Err(_) => {
            unsafe { throw(env, STATE, "The native core failed.") };
            fallback
        }
    }
}

pub unsafe fn new_strings(env: *mut JNIEnv, items: &[String]) -> jobjectArray {
    unsafe {
        let f = &**env;
        let (Some(find), Some(new_array), Some(set), Some(delete)) = (f.FindClass, f.NewObjectArray, f.SetObjectArrayElement, f.DeleteLocalRef) else {
            return ptr::null_mut();
        };
        let cls = find(env, c"java/lang/String".as_ptr());
        if cls.is_null() {
            return ptr::null_mut();
        }
        let array = new_array(env, items.len() as jsize, cls, ptr::null_mut());
        delete(env, cls);
        if array.is_null() {
            return ptr::null_mut();
        }
        for (i, item) in items.iter().enumerate() {
            let s = new_string(env, item);
            set(env, array, i as jsize, s);
            delete(env, s);
        }
        array
    }
}

fn data_fields(d: &Data) -> Vec<String> {
    vec![
        d.place_id.to_string(),
        d.job_id.clone(),
        d.universe_id.to_string(),
        d.user_id.to_string(),
        d.server_type.to_string(),
        d.server_address.clone(),
        d.joined.to_string(),
        if d.teleport { "1" } else { "0" }.to_string(),
        d.launch_data.clone(),
    ]
}

unsafe fn watcher<'a>(handle: jlong) -> Option<&'a mut Watcher> {
    unsafe { (handle as *mut Watcher).as_mut() }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_create(env: *mut JNIEnv, _: jclass) -> jlong {
    guard(env, 0, || Box::into_raw(Box::new(Watcher::default())) as jlong)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_destroy(env: *mut JNIEnv, _: jclass, handle: jlong) {
    guard(env, (), || {
        if handle != 0 {
            drop(unsafe { Box::from_raw(handle as *mut Watcher) });
        }
    })
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_feed(env: *mut JNIEnv, _: jclass, handle: jlong, line: jstring, now: jlong) -> jobjectArray {
    guard(env, ptr::null_mut(), || unsafe {
        let (Some(w), Some(line)) = (watcher(handle), get_string(env, line)) else {
            return ptr::null_mut();
        };
        let fields = match w.feed(&line, now) {
            None => return ptr::null_mut(),
            Some(Event::Joined(d)) => [vec!["joined".to_string()], data_fields(&d)].concat(),
            Some(Event::Left(d)) => [vec!["left".to_string()], data_fields(&d)].concat(),
            Some(Event::Menu) => vec!["menu".to_string()],
            Some(Event::Log(kind, text)) => vec!["log".to_string(), kind.to_string(), text],
            Some(Event::Rpc(command, body)) => vec!["rpc".to_string(), command, body],
        };
        new_strings(env, &fields)
    })
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_reset(env: *mut JNIEnv, _: jclass, handle: jlong) -> jobjectArray {
    guard(env, ptr::null_mut(), || unsafe {
        match watcher(handle).and_then(|w| w.reset()) {
            Some(d) => new_strings(env, &data_fields(&d)),
            None => ptr::null_mut(),
        }
    })
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_current(env: *mut JNIEnv, _: jclass, handle: jlong) -> jobjectArray {
    guard(env, ptr::null_mut(), || unsafe {
        match watcher(handle).and_then(|w| w.current()) {
            Some(d) => new_strings(env, &data_fields(&d)),
            None => ptr::null_mut(),
        }
    })
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_inGame(env: *mut JNIEnv, _: jclass, handle: jlong) -> jboolean {
    guard(env, 0, || unsafe { watcher(handle).is_some_and(|w| w.in_game()) as jboolean })
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ActivityWatcher_userId(env: *mut JNIEnv, _: jclass, handle: jlong) -> jlong {
    guard(env, 0, || unsafe { watcher(handle).map_or(0, |w| w.user_id()) })
}

pub unsafe fn new_longs(env: *mut JNIEnv, values: &[i64]) -> jlongArray {
    unsafe {
        let f = &**env;
        let (Some(new), Some(set)) = (f.NewLongArray, f.SetLongArrayRegion) else {
            return ptr::null_mut();
        };
        let array = new(env, values.len() as jsize);
        if !array.is_null() {
            set(env, array, 0, values.len() as jsize, values.as_ptr());
        }
        array
    }
}

pub unsafe fn object_at(env: *mut JNIEnv, array: jobjectArray, i: usize) -> jobject {
    unsafe {
        match (**env).GetObjectArrayElement {
            Some(get) if !array.is_null() => get(env, array, i as jsize),
            _ => ptr::null_mut(),
        }
    }
}

pub unsafe fn array_len(env: *mut JNIEnv, array: jobjectArray) -> usize {
    unsafe {
        match (**env).GetArrayLength {
            Some(len) if !array.is_null() => len(env, array).max(0) as usize,
            _ => 0,
        }
    }
}

pub unsafe fn delete_local(env: *mut JNIEnv, obj: jobject) {
    unsafe {
        if let (false, Some(delete)) = (obj.is_null(), (**env).DeleteLocalRef) {
            delete(env, obj);
        }
    }
}

unsafe fn cancel_checker(env: *mut JNIEnv, cancel: jobject) -> impl FnMut() -> bool {
    let method = unsafe {
        let f = &**env;
        if cancel.is_null() {
            ptr::null_mut()
        } else {
            match (f.GetObjectClass, f.GetMethodID) {
                (Some(class_of), Some(method_id)) => {
                    let cls = class_of(env, cancel);
                    let m = method_id(env, cls, c"get".as_ptr(), c"()Z".as_ptr());
                    delete_local(env, cls);
                    m
                }
                _ => ptr::null_mut(),
            }
        }
    };
    move || unsafe {
        if method.is_null() {
            return false;
        }
        match (**env).CallBooleanMethodA {
            Some(call) => call(env, cancel, method, ptr::null()) != 0,
            None => false,
        }
    }
}

fn io_error(env: *mut JNIEnv, e: std::io::Error) {
    unsafe { throw(env, IO, &e.to_string()) };
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ApkPatcher_readNative(env: *mut JNIEnv, _: jclass, path: jstring) -> jobjectArray {
    guard(env, ptr::null_mut(), || unsafe {
        let Some(path) = get_string(env, path) else {
            return ptr::null_mut();
        };
        match crate::apk::read(std::path::Path::new(&path)) {
            Ok(dir) => {
                let names: Vec<String> = dir.entries.iter().map(|e| e.name.clone()).collect();
                let mut fields = vec![dir.central_start as i64];
                for e in &dir.entries {
                    fields.extend_from_slice(&[e.method as i64, e.compressed as i64, e.size as i64, e.local_offset as i64]);
                }
                let f = &**env;
                let (Some(find), Some(new_array), Some(set)) = (f.FindClass, f.NewObjectArray, f.SetObjectArrayElement) else {
                    return ptr::null_mut();
                };
                let cls = find(env, c"java/lang/Object".as_ptr());
                let out = new_array(env, 2, cls, ptr::null_mut());
                delete_local(env, cls);
                let n = new_strings(env, &names);
                let l = new_longs(env, &fields);
                set(env, out, 0, n);
                set(env, out, 1, l);
                delete_local(env, n);
                delete_local(env, l);
                out
            }
            Err(e) => {
                io_error(env, e);
                ptr::null_mut()
            }
        }
    })
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_ApkPatcher_patchNative(
    env: *mut JNIEnv,
    _: jclass,
    base: jstring,
    out: jstring,
    names: jobjectArray,
    paths: jobjectArray,
    bytes: jobjectArray,
    cancel: jobject,
) {
    guard(env, (), || unsafe {
        let (Some(base), Some(out)) = (get_string(env, base), get_string(env, out)) else {
            return;
        };
        let mut changes = Vec::new();
        for i in 0..array_len(env, names) {
            let n = object_at(env, names, i);
            let p = object_at(env, paths, i);
            let b = object_at(env, bytes, i);
            let name = get_string(env, n).unwrap_or_default();
            let source = match get_string(env, p) {
                Some(path) => crate::apk::Source::File(path.into()),
                None => crate::apk::Source::Bytes(get_bytes(env, b).unwrap_or_default()),
            };
            delete_local(env, n);
            delete_local(env, p);
            delete_local(env, b);
            changes.push((name, source));
        }
        let mut cancelled = cancel_checker(env, cancel);
        if let Err(e) = crate::apk::patch(std::path::Path::new(&base), std::path::Path::new(&out), changes, &mut cancelled) {
            io_error(env, e);
        }
    })
}

#[cfg(unix)]
unsafe fn take_fd(fd: jint) -> Option<std::fs::File> {
    use std::os::fd::FromRawFd;
    (fd >= 0).then(|| unsafe { std::fs::File::from_raw_fd(fd) })
}

#[cfg(not(unix))]
unsafe fn take_fd(_fd: jint) -> Option<std::fs::File> {
    None
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_Core_call(
    env: *mut JNIEnv,
    _: jclass,
    op: jstring,
    args: jstring,
    data: jbyteArray,
    fd: jint,
    cancel: jobject,
) -> jstring {
    let file = unsafe { take_fd(fd) };
    guard(env, ptr::null_mut(), || unsafe {
        let (Some(op), Some(args)) = (get_string(env, op), get_string(env, args)) else {
            return ptr::null_mut();
        };
        let a: serde_json::Value = serde_json::from_str(&args).unwrap_or(serde_json::Value::Null);
        let bytes = get_bytes(env, data);
        let mut cancelled = cancel_checker(env, cancel);
        match crate::bridge::call(&op, &a, bytes.as_deref(), file, &mut cancelled) {
            Ok(v) => new_string(env, &v.to_string()),
            Err(e) => {
                throw_error(env, &e);
                ptr::null_mut()
            }
        }
    })
}

unsafe fn pending_exception(env: *mut JNIEnv) -> bool {
    unsafe { (**env).ExceptionCheck.is_some_and(|check| check(env) != 0) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_voidstrap_android_Helper_streamNative(
    env: *mut JNIEnv,
    _: jclass,
    token: jstring,
    version: jint,
    target: jstring,
    sink: jobject,
    cancel: jobject,
) {
    guard(env, (), || unsafe {
        let (Some(token), Some(target)) = (get_string(env, token), get_string(env, target)) else {
            return;
        };
        let f = &**env;
        let (Some(class_of), Some(method_id), Some(call)) = (f.GetObjectClass, f.GetMethodID, f.CallVoidMethodA) else {
            return;
        };
        let cls = class_of(env, sink);
        let method = method_id(env, cls, c"line".as_ptr(), c"(Ljava/lang/String;)V".as_ptr());
        delete_local(env, cls);
        if method.is_null() {
            return;
        }
        let mut cancelled = cancel_checker(env, cancel);
        let mut on_line = |line: String| -> bool {
            let s = new_string(env, &line);
            let arg = jni_sys::jvalue { l: s };
            call(env, sink, method, &arg);
            delete_local(env, s);
            !pending_exception(env)
        };
        let result = crate::shell::stream(&token, version, &target, &mut cancelled, &mut on_line);
        if let Err(e) = result
            && !pending_exception(env)
        {
            throw(env, IO, &e.to_string());
        }
    })
}

#[unsafe(no_mangle)]
pub extern "system" fn JNI_OnLoad(vm: *mut jni_sys::JavaVM, _: *mut std::ffi::c_void) -> jint {
    unsafe { crate::http::on_load(vm) };
    jni_sys::JNI_VERSION_1_6
}
