package com.voidstrap.android;

import android.content.Context;
import android.net.Uri;

import androidx.appcompat.app.AppCompatActivity;

import org.json.JSONObject;

public final class Updater {
    public static final String AUTO = "autoUpdate";
    public static final String CHECKED_AT = "updateCheckedAt";
    public static final String SKIPPED = "updateSkipped";

    private static final long AUTO_INTERVAL_MS = 6 * 60 * 60 * 1000L;

    public enum State { IDLE, CHECKING, AVAILABLE, DOWNLOADING, READY, INSTALLING, UP_TO_DATE, FAILED }

    public static final class Release {
        public final String version;
        public final long code;
        public final String url;
        public final String name;
        public final long size;
        public final String notes;
        public final String page;

        Release(JSONObject o) {
            version = o.optString("version", "");
            code = o.optLong("code");
            url = o.optString("url", "");
            name = o.optString("name", "");
            size = o.optLong("size");
            notes = o.optString("notes", "");
            page = o.optString("page", "");
        }

        boolean usable() {
            return !version.isEmpty() && code > 0 && url.startsWith("https://");
        }
    }

    private static volatile State state = State.IDLE;
    private static volatile Release found;
    private static volatile String problem = "";
    private static volatile int progress;
    private static volatile boolean busy;

    private Updater() {
    }

    public static State state() {
        return state;
    }

    public static Release available() {
        return state == State.AVAILABLE || state == State.READY || state == State.DOWNLOADING ? found : null;
    }

    public static String problem() {
        return problem;
    }

    public static int progress() {
        return progress;
    }

    public static boolean selfInstalls() {
        return UpdateSource.selfInstalls();
    }

    public static String channel(Context c) {
        return UpdateSource.label(c);
    }

    public static boolean autoOn(Store s) {
        return !"0".equals(s.setting(AUTO, "1"));
    }

    public static void setAuto(Context c, boolean on) {
        Store.get(c).putSetting(AUTO, on ? null : "0");
    }

    public static long installedCode() {
        return BuildConfig.VERSION_CODE;
    }

    public static String installedVersion() {
        return BuildConfig.VERSION_NAME;
    }

    static void report(State next, String message) {
        state = next;
        problem = message == null ? "" : message;
    }

    static void setProgress(int percent) {
        progress = percent < 0 ? 0 : Math.min(100, percent);
    }

    static void changed(Context c) {
        Store.get(c).changed();
    }

    public static void auto(Context c) {
        if (c == null) return;
        if (UpdateSource.localActive()) return;
        Context app = c.getApplicationContext();
        Store s = Store.get(app);
        if (!autoOn(s)) return;
        if (state == State.CHECKING || state == State.DOWNLOADING || state == State.INSTALLING) return;
        long last = 0;
        try {
            last = Long.parseLong(s.setting(CHECKED_AT, "0"));
        } catch (NumberFormatException ignored) {
        }
        long now = System.currentTimeMillis();
        if (last > 0 && now - last < AUTO_INTERVAL_MS && now >= last) return;
        check(app, false);
    }

    public static void check(Context c, boolean userAsked) {
        if (c == null || busy || UpdateSource.localActive()) return;
        Context app = c.getApplicationContext();
        Store s = Store.get(app);
        busy = true;
        report(State.CHECKING, "");
        setProgress(0);
        changed(app);
        ModLog.add("update check starting on " + UpdateSource.label(app) + ", installed " + installedVersion() + " (" + installedCode() + ")");
        s.work.execute(() -> {
            State next;
            String message = "";
            Release release = null;
            try {
                release = UpdateSource.check(app);
                if (release != null && release.usable() && release.code > installedCode()) {
                    next = State.AVAILABLE;
                } else {
                    next = State.UP_TO_DATE;
                    release = null;
                }
            } catch (Exception e) {
                next = State.FAILED;
                message = Crash.describe(e);
            }
            ModLog.add("update result " + next + (message.isEmpty() ? "" : ", " + message)
                    + (release == null ? "" : ", " + release.version + " (" + release.code + ") " + release.size + " bytes"));
            s.putSetting(CHECKED_AT, String.valueOf(System.currentTimeMillis()));
            Release resultRelease = release;
            State result = next;
            String text = message;
            s.main.post(() -> {
                busy = false;
                if (UpdateSource.localActive()) return;
                found = resultRelease;
                report(result, text);
                changed(app);
                if (result == State.AVAILABLE && !userAsked) announce(app);
            });
        });
    }

    private static void announce(Context app) {
        Release r = found;
        if (r == null) return;
        Store s = Store.get(app);
        if (r.version.equals(s.setting(SKIPPED, ""))) return;
        Notify.toast(app, Notify.GENERAL, app.getString(R.string.update_available_toast, r.version));
    }

    public static void skip(Context c) {
        Release r = found;
        if (r == null) return;
        Store.get(c).putSetting(SKIPPED, r.version);
        report(State.IDLE, "");
        changed(c);
    }

    public static void start(AppCompatActivity a) {
        Release r = found;
        if (a == null || r == null || busy || UpdateSource.localActive()) return;
        busy = true;
        UpdateSource.start(a, r, () -> busy = false);
    }

    public static void resume(AppCompatActivity a) {
        UpdateSource.resume(a);
    }

    public static void openLocal(AppCompatActivity a, Uri uri) {
        UpdateSource.openLocal(a, uri);
    }

    public static String sizeText(Context c, long bytes) {
        return bytes <= 0 ? "" : android.text.format.Formatter.formatShortFileSize(c, bytes);
    }
}
