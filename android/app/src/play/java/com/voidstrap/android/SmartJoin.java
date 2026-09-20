package com.voidstrap.android;

import android.content.Context;

import java.util.List;

final class SmartJoin {
    interface Alert {
        void show(String text);
    }

    private SmartJoin() {
    }

    static boolean available() {
        return false;
    }

    static String title(Context c) {
        return c.getString(R.string.app_name);
    }

    static String openBody(Context c) {
        return "";
    }

    static Page page() {
        return null;
    }

    static String[] presence() {
        return null;
    }

    static Deeplink rewrite(Context app, Deeplink destination) {
        return destination;
    }

    static boolean rejoining() {
        return false;
    }

    static void onJoined(Context c, ActivityWatcher.Data data, Alert alert) {
    }

    static void addCompat(Context c, List<String[]> rows) {
    }
}
