package com.voidstrap.android;

import android.content.Context;
import android.os.SystemClock;

import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

public final class Helper {
    static {
        Core.load();
    }

    private static final long CACHE_MS = 2000;

    private static volatile int uid = -1;
    private static volatile long checkedAt;
    private static volatile String token = "";

    private Helper() {
    }

    public static synchronized String token(Context c) {
        if (c == null) return token;
        Store store = Store.get(c);
        String saved = store.setting("helperToken", "");
        if (saved.length() != 32) {
            byte[] b = new byte[16];
            new java.security.SecureRandom().nextBytes(b);
            StringBuilder sb = new StringBuilder(32);
            for (byte x : b) sb.append(Character.forDigit((x >> 4) & 0xF, 16)).append(Character.forDigit(x & 0xF, 16));
            saved = sb.toString();
            store.putSetting("helperToken", saved);
        }
        token = saved;
        return saved;
    }

    public static String command(Context c) {
        return "adb shell \"" + deviceCommand(c) + "\"";
    }

    public static String deviceCommand(Context c) {
        return "content read --uri content://" + StartScriptProvider.authority(c.getPackageName()) + "/" + StartScriptProvider.PATH + " | sh";
    }

    public static void keepRootHelper(Context c) {
        if (FlagWriter.rootMode(c) != FlagWriter.Mode.ROOT) return;
        FlagWriter.raw(FlagWriter.Mode.ROOT, HelperServer.launchLine(c.getPackageName(), android.os.Process.myUid(), token(c)) + "exit 0\n", 30);
        SystemClock.sleep(1500);
        uidNow();
    }

    public static Runnable onChanged;

    private static final AtomicBoolean refreshing = new AtomicBoolean();

    public static int uid() {
        long now = SystemClock.elapsedRealtime();
        if (now - checkedAt < CACHE_MS) return uid;
        if (android.os.Looper.myLooper() == android.os.Looper.getMainLooper()) {
            refreshAsync();
            return uid;
        }
        uid = ping();
        checkedAt = SystemClock.elapsedRealtime();
        return uid;
    }

    private static void refreshAsync() {
        if (!refreshing.compareAndSet(false, true)) return;
        Thread t = new Thread(() -> {
            try {
                int before = uid;
                uid = Crash.call("helper ping", Helper::ping, -1);
                checkedAt = SystemClock.elapsedRealtime();
                Runnable notify = onChanged;
                if (before != uid && notify != null) Crash.run("helper state", notify);
            } finally {
                refreshing.set(false);
            }
        }, "helper-ping");
        t.setDaemon(true);
        t.start();
    }

    public static int uidNow() {
        checkedAt = 0;
        return uid();
    }

    public static boolean running() {
        return uid() >= 0;
    }

    private static int ping() {
        try {
            Object v = Core.value("shell.ping", Core.args("token", token, "version", HelperServer.VERSION));
            return v instanceof Number ? ((Number) v).intValue() : -1;
        } catch (IOException e) {
            return -1;
        }
    }

    public interface LineSink {
        void line(String line);
    }

    public static void stream(String target, LineSink sink, AtomicBoolean cancel) throws IOException {
        if (!Core.loaded()) throw new IOException(Core.UNAVAILABLE);
        try {
            streamNative(token, HelperServer.VERSION, target, sink, cancel);
        } catch (IOException e) {
            throw e;
        } catch (RuntimeException | LinkageError e) {
            throw new IOException(Crash.describe(e));
        }
    }

    public static Integer run(String script, int timeoutSeconds) {
        if (script == null) return null;
        try {
            Object v = Core.value("shell.run", Core.args("token", token, "version", HelperServer.VERSION, "script", script, "timeout", timeoutSeconds, "uid", uid));
            if (v instanceof Number) return ((Number) v).intValue();
        } catch (IOException ignored) {
        }
        checkedAt = 0;
        return null;
    }

    private static native void streamNative(String token, int version, String target, LineSink sink, AtomicBoolean cancel) throws IOException;
}
