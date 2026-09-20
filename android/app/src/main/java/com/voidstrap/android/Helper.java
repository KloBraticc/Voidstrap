package com.voidstrap.android;

import android.content.Context;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.os.SystemClock;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;

public final class Helper {
    private static final long CACHE_MS = 2000;
    private static final long UPGRADE_WAIT_MS = 8000;

    private static volatile int uid = -1;
    private static volatile long checkedAt;

    private Helper() {
    }

    public static String command(Context c) {
        return "adb shell \"content read --uri content://" + StartScriptProvider.authority(c.getPackageName()) + "/" + StartScriptProvider.PATH + " | sh\"";
    }

    public static void keepRootHelper(Context c) {
        if (FlagWriter.rootMode(c) != FlagWriter.Mode.ROOT) return;
        FlagWriter.raw(FlagWriter.Mode.ROOT, HelperServer.launchLine(c.getPackageName(), android.os.Process.myUid()) + "exit 0\n", 30);
        SystemClock.sleep(1500);
        uidNow();
    }

    public static Runnable onChanged;

    private static final java.util.concurrent.atomic.AtomicBoolean refreshing = new java.util.concurrent.atomic.AtomicBoolean();

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
                uid = ping();
                checkedAt = SystemClock.elapsedRealtime();
                Runnable notify = onChanged;
                if (before != uid && notify != null) notify.run();
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

    private static LocalSocket connect() throws IOException {
        LocalSocket s = new LocalSocket();
        try {
            s.connect(new LocalSocketAddress(HelperServer.SOCKET));
            int peer = s.getPeerCredentials().getUid();
            if (peer != 0 && peer != HelperServer.SHELL_UID) throw new IOException("untrusted");
            return s;
        } catch (IOException e) {
            s.close();
            throw e;
        }
    }

    private static int ping() {
        try (LocalSocket s = connect()) {
            s.setSoTimeout(1500);
            DataOutputStream out = new DataOutputStream(s.getOutputStream());
            out.writeInt(HelperServer.VERSION);
            out.writeInt(HelperServer.PING);
            out.flush();
            return new DataInputStream(s.getInputStream()).readInt();
        } catch (IOException e) {
            return -1;
        }
    }

    public interface LineSink {
        void line(String line);
    }

    public static void stream(String target, LineSink sink, java.util.concurrent.atomic.AtomicReference<LocalSocket> handle) throws IOException {
        try (LocalSocket s = connect()) {
            handle.set(s);
            s.setSoTimeout(10000);
            DataOutputStream out = new DataOutputStream(s.getOutputStream());
            out.writeInt(HelperServer.VERSION);
            out.writeInt(HelperServer.STREAM);
            out.writeUTF(target);
            out.flush();
            DataInputStream in = new DataInputStream(s.getInputStream());
            if (in.readInt() != 0) throw new IOException("stale");
            s.setSoTimeout(0);
            while (true) sink.line(in.readUTF());
        } finally {
            handle.set(null);
        }
    }

    public static Integer run(String script, int timeoutSeconds) {
        byte[] body = script.getBytes(StandardCharsets.UTF_8);
        if (body.length > HelperServer.MAX_SCRIPT) return FlagWriter.FAILED;
        long giveUp = SystemClock.elapsedRealtime() + UPGRADE_WAIT_MS;
        while (true) {
            int r;
            try (LocalSocket s = connect()) {
                s.setSoTimeout((timeoutSeconds + 10) * 1000);
                DataOutputStream out = new DataOutputStream(s.getOutputStream());
                out.writeInt(HelperServer.VERSION);
                out.writeInt(timeoutSeconds);
                out.writeInt(body.length);
                out.write(body);
                out.flush();
                r = new DataInputStream(s.getInputStream()).readInt();
            } catch (IOException e) {
                r = HelperServer.STALE;
                if (SystemClock.elapsedRealtime() > giveUp || uid < 0) {
                    checkedAt = 0;
                    return null;
                }
            }
            if (r != HelperServer.STALE) return r;
            if (SystemClock.elapsedRealtime() > giveUp) return null;
            SystemClock.sleep(400);
        }
    }
}
