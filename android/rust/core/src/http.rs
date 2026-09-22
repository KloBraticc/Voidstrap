use crate::jni::{array_len, delete_local, get_bytes, get_string, new_bytes, new_string, new_strings, object_at};
use jni_sys::{JNI_EDETACHED, JNI_OK, JNI_VERSION_1_6, JNIEnv, JavaVM, jboolean, jint, jintArray, jmethodID, jobject, jvalue};
use std::ffi::c_void;
use std::ptr;
use std::sync::atomic::{AtomicPtr, Ordering};

static VM: AtomicPtr<JavaVM> = AtomicPtr::new(ptr::null_mut());
static CLASS: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static METHOD: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static HTML: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());

pub unsafe fn on_load(vm: *mut JavaVM) {
    VM.store(vm, Ordering::SeqCst);
    unsafe {
        let mut raw: *mut c_void = ptr::null_mut();
        let Some(get_env) = (**vm).GetEnv else {
            return;
        };
        if get_env(vm, &mut raw, JNI_VERSION_1_6) != JNI_OK {
            return;
        }
        let env = raw as *mut JNIEnv;
        let f = &**env;
        let (Some(find), Some(global), Some(method), Some(clear)) = (f.FindClass, f.NewGlobalRef, f.GetStaticMethodID, f.ExceptionClear) else {
            return;
        };
        let cls = find(env, c"com/voidstrap/android/Http".as_ptr());
        if cls.is_null() {
            clear(env);
            return;
        }
        let m = method(env, cls, c"request".as_ptr(), c"(Ljava/lang/String;Ljava/lang/String;[Ljava/lang/String;[BIIZ)[Ljava/lang/Object;".as_ptr());
        if m.is_null() {
            clear(env);
            delete_local(env, cls);
            return;
        }
        let h = method(env, cls, c"html".as_ptr(), c"(Ljava/lang/String;Z)Ljava/lang/String;".as_ptr());
        if h.is_null() {
            clear(env);
        }
        let g = global(env, cls);
        delete_local(env, cls);
        HTML.store(h as *mut c_void, Ordering::SeqCst);
        METHOD.store(m as *mut c_void, Ordering::SeqCst);
        CLASS.store(g as *mut c_void, Ordering::SeqCst);
    }
}

pub struct Response {
    pub code: i32,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Response {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }

    pub fn text(&self) -> String {
        String::from_utf8_lossy(&self.body).into_owned()
    }
}

struct Attached {
    vm: *mut JavaVM,
    env: *mut JNIEnv,
    detach: bool,
}

impl Drop for Attached {
    fn drop(&mut self) {
        if self.detach {
            unsafe {
                if let Some(d) = (**self.vm).DetachCurrentThread {
                    d(self.vm);
                }
            }
        }
    }
}

fn attach() -> Option<Attached> {
    let vm = VM.load(Ordering::SeqCst);
    if vm.is_null() {
        return None;
    }
    unsafe {
        let mut raw: *mut c_void = ptr::null_mut();
        match ((**vm).GetEnv?)(vm, &mut raw, JNI_VERSION_1_6) {
            JNI_OK => Some(Attached { vm, env: raw as *mut JNIEnv, detach: false }),
            JNI_EDETACHED => {
                if ((**vm).AttachCurrentThread?)(vm, &mut raw, ptr::null_mut()) != JNI_OK {
                    return None;
                }
                Some(Attached { vm, env: raw as *mut JNIEnv, detach: true })
            }
            _ => None,
        }
    }
}

pub fn request(method: &str, url: &str, headers: &[(&str, &str)], body: Option<&[u8]>, timeout_ms: i32, limit: i32, follow: bool) -> Option<Response> {
    if !url.starts_with("https://") {
        return None;
    }
    let cls = CLASS.load(Ordering::SeqCst) as jobject;
    let m = METHOD.load(Ordering::SeqCst) as jmethodID;
    if cls.is_null() || m.is_null() {
        return None;
    }
    let a = attach()?;
    let env = a.env;
    unsafe {
        let f = &**env;
        let (Some(push), Some(pop), Some(call), Some(check), Some(clear), Some(ints)) =
            (f.PushLocalFrame, f.PopLocalFrame, f.CallStaticObjectMethodA, f.ExceptionCheck, f.ExceptionClear, f.GetIntArrayRegion)
        else {
            return None;
        };
        if push(env, 32) != JNI_OK {
            return None;
        }
        let flat: Vec<String> = headers.iter().flat_map(|(k, v)| [k.to_string(), v.to_string()]).collect();
        let args = [
            jvalue { l: new_string(env, method) },
            jvalue { l: new_string(env, url) },
            jvalue { l: new_strings(env, &flat) },
            jvalue { l: body.map_or(ptr::null_mut(), |b| new_bytes(env, b)) },
            jvalue { i: timeout_ms as jint },
            jvalue { i: limit as jint },
            jvalue { z: follow as jboolean },
        ];
        let result = call(env, cls, m, args.as_ptr());
        if check(env) != 0 {
            clear(env);
            pop(env, ptr::null_mut());
            return None;
        }
        if result.is_null() {
            pop(env, ptr::null_mut());
            return None;
        }
        let mut code = [0 as jint; 1];
        ints(env, object_at(env, result, 0) as jintArray, 0, 1, code.as_mut_ptr());
        let pairs = object_at(env, result, 1);
        let mut headers = Vec::new();
        let n = array_len(env, pairs);
        let mut i = 0;
        while i + 1 < n {
            let k = object_at(env, pairs, i);
            let v = object_at(env, pairs, i + 1);
            headers.push((get_string(env, k).unwrap_or_default(), get_string(env, v).unwrap_or_default()));
            delete_local(env, k);
            delete_local(env, v);
            i += 2;
        }
        let body = get_bytes(env, object_at(env, result, 2)).unwrap_or_default();
        pop(env, ptr::null_mut());
        Some(Response { code: code[0], headers, body })
    }
}

pub fn html(text: &str, keep_lines: bool) -> String {
    let cls = CLASS.load(Ordering::SeqCst) as jobject;
    let m = HTML.load(Ordering::SeqCst) as jmethodID;
    let fallback = || text.to_string();
    if cls.is_null() || m.is_null() {
        return fallback();
    }
    let Some(a) = attach() else {
        return fallback();
    };
    let env = a.env;
    unsafe {
        let f = &**env;
        let (Some(push), Some(pop), Some(call), Some(check), Some(clear)) = (f.PushLocalFrame, f.PopLocalFrame, f.CallStaticObjectMethodA, f.ExceptionCheck, f.ExceptionClear) else {
            return fallback();
        };
        if push(env, 8) != JNI_OK {
            return fallback();
        }
        let args = [jvalue { l: new_string(env, text) }, jvalue { z: keep_lines as jboolean }];
        let result = call(env, cls, m, args.as_ptr());
        let out = if check(env) != 0 {
            clear(env);
            None
        } else {
            get_string(env, result)
        };
        pop(env, ptr::null_mut());
        out.unwrap_or_else(fallback)
    }
}

pub fn encode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'.' | b'-' | b'*' | b'_' => out.push(b as char),
            b' ' => out.push('+'),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}
