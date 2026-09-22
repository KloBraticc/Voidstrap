package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

final class Core {
    static final String UNAVAILABLE = "The Voidstrap core could not be loaded on this device.";

    private static final boolean LOADED = load();

    private Core() {
    }

    static boolean loaded() {
        return LOADED;
    }

    static boolean load() {
        try {
            System.loadLibrary("voidstrap_core");
            return true;
        } catch (Throwable t) {
            Crash.report("native core", t);
            return false;
        }
    }

    private static native String call(String op, String args, byte[] data, int fd, AtomicBoolean cancel) throws IOException;

    static JSONObject run(String op, JSONObject args, byte[] data, int fd, AtomicBoolean cancel) throws IOException {
        if (!LOADED) throw new IOException(UNAVAILABLE);
        String out;
        try {
            out = call(op, args == null ? "{}" : args.toString(), data, fd, cancel);
        } catch (IOException e) {
            throw e;
        } catch (RuntimeException | LinkageError | StackOverflowError e) {
            throw new IOException(Crash.describe(e));
        }
        try {
            return new JSONObject(out == null ? "{}" : out);
        } catch (JSONException e) {
            throw new IOException(e);
        }
    }

    static JSONObject run(String op, JSONObject args) throws IOException {
        return run(op, args, null, -1, null);
    }

    static JSONObject args(Object... kv) {
        JSONObject o = new JSONObject();
        if (kv == null) return o;
        for (int i = 0; i + 1 < kv.length; i += 2) {
            if (!(kv[i] instanceof String)) continue;
            Object v = kv[i + 1];
            if (v instanceof java.io.File) v = ((java.io.File) v).getAbsolutePath();
            if (v instanceof List) v = new JSONArray((List<?>) v);
            if (v instanceof Double && !Double.isFinite((Double) v)) v = 0d;
            if (v instanceof Float && !Float.isFinite((Float) v)) v = 0d;
            try {
                o.put((String) kv[i], v);
            } catch (JSONException e) {
                Crash.report("core argument " + kv[i], e);
            }
        }
        return o;
    }

    static JSONObject merge(JSONObject... parts) {
        JSONObject o = new JSONObject();
        if (parts == null) return o;
        for (JSONObject p : parts) {
            if (p == null) continue;
            java.util.Iterator<String> it = p.keys();
            while (it.hasNext()) {
                String k = it.next();
                try {
                    o.put(k, p.get(k));
                } catch (JSONException e) {
                    Crash.report("core argument " + k, e);
                }
            }
        }
        return o;
    }

    static Object value(String op, JSONObject args) throws IOException {
        Object v = run(op, args).opt("v");
        return v == JSONObject.NULL ? null : v;
    }

    static String text(String op, JSONObject args) throws IOException {
        Object v = value(op, args);
        return v == null ? null : v.toString();
    }

    static boolean flag(String op, JSONObject args) {
        try {
            return Boolean.TRUE.equals(value(op, args));
        } catch (IOException e) {
            return false;
        }
    }

    static List<String> strings(Object v) {
        List<String> out = new ArrayList<>();
        if (v instanceof JSONArray) {
            JSONArray a = (JSONArray) v;
            for (int i = 0; i < a.length(); i++) out.add(a.optString(i));
        }
        return out;
    }

    static JSONObject feed(Context c, Object... kv) {
        return merge(args("cache", c.getCacheDir(), "agent", "Voidstrap Android/" + BuildConfig.VERSION_NAME), args(kv));
    }

    static List<JSONObject> objects(Object v) {
        List<JSONObject> out = new ArrayList<>();
        if (v instanceof JSONArray) {
            JSONArray a = (JSONArray) v;
            for (int i = 0; i < a.length(); i++) {
                JSONObject o = a.optJSONObject(i);
                if (o != null) out.add(o);
            }
        }
        return out;
    }

    static String opt(JSONObject o, String key) {
        return o.isNull(key) ? null : o.optString(key);
    }

    static JSONObject roots(Context c, Object... kv) {
        return merge(args("mods", Mods.root(c), "managed", ManagedMods.root(c), "state", ModEngine.stateDir(c), "apk", c.getApplicationInfo().sourceDir), args(kv));
    }

    static JSONObject index(Context c) {
        String pkg = Targets.selected(c);
        java.io.File base = ModEngine.originalApk(c, pkg);
        if (base == null) base = ModEngine.baseApk(c, pkg);
        return args("key", base == null ? "" : ModEngine.identity(c, pkg), "apk", base);
    }

    static JSONObject indexed(Context c, Object... kv) {
        return merge(args(kv), args("index", index(c)));
    }

    static JSONObject full(Context c, Object... kv) {
        return merge(roots(c, kv), args("index", index(c)));
    }
}
