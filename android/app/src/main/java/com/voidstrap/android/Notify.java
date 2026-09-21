package com.voidstrap.android;

import android.app.Activity;
import android.content.Context;
import android.widget.Toast;

public final class Notify {
    public static final String POPUP = "ntPopup";
    public static final String JOIN_TIME = "ntJoinTime";
    public static final String SMART_ALERTS = "ntSmartAlerts";
    public static final String TRACK_GAME = "ntTrackGame";
    public static final String LENGTH = "ntLength";
    public static final String LAUNCH = "ntLaunch";
    public static final String SMART = "ntSmart";
    public static final String FLAGS = "ntFlags";
    public static final String MODS = "ntMods";
    public static final String LIBRARY = "ntLibrary";
    public static final String SHORTCUTS = "ntShortcuts";
    public static final String COPY = "ntCopy";
    public static final String GENERAL = "ntGeneral";

    static final long[] JOIN_TIMES = {-1, 5000, 15000, 30000, 0};
    static final int[] LENGTHS = {1500, 2750, 5000, 8000};
    static final int DEFAULT_LENGTH = 1;

    private Notify() {
    }

    public static boolean on(Store s, String key) {
        return key == null || !"0".equals(s.setting(key, "1"));
    }

    public static void set(Store s, String key, boolean value) {
        s.putSetting(key, value ? "1" : "0");
    }

    static int index(Store s, String key, int count, int fallback) {
        try {
            int i = Integer.parseInt(s.setting(key, String.valueOf(fallback)));
            return i >= 0 && i < count ? i : fallback;
        } catch (NumberFormatException e) {
            return fallback;
        }
    }

    static long joinTimeout(Store s, long automatic) {
        long t = JOIN_TIMES[index(s, JOIN_TIME, JOIN_TIMES.length, 0)];
        return t < 0 ? automatic : t;
    }

    static int length(Store s) {
        return LENGTHS[index(s, LENGTH, LENGTHS.length, DEFAULT_LENGTH)];
    }

    public static void say(Activity a, String key, CharSequence text) {
        if (on(Store.get(a), key)) Ui.say(a, text);
    }

    public static void say(Activity a, String key, int res) {
        say(a, key, a.getString(res));
    }

    public static void toast(Context c, String key, CharSequence text) {
        Store s = Store.get(c);
        if (on(s, key)) Toast.makeText(c.getApplicationContext(), text, length(s) > LENGTHS[0] ? Toast.LENGTH_LONG : Toast.LENGTH_SHORT).show();
    }

    public static void toast(Context c, String key, int res) {
        toast(c, key, c.getString(res));
    }
}
