package com.voidstrap.android;

import android.content.Context;
import android.os.SystemClock;
import android.webkit.CookieManager;

final class RobloxLogin {
    enum Source { VOIDSTRAP, NONE }

    static final String LOGIN_URL = "https://www.roblox.com/login";
    private static final String NAME = ".ROBLOSECURITY";
    private static final long CACHE_MS = 5 * 60_000;

    private static String cached;
    private static Source cachedSource = Source.NONE;
    private static long cachedAt;
    private static boolean rejected;

    private RobloxLogin() {
    }

    static synchronized void invalidate() {
        cached = null;
        cachedSource = Source.NONE;
        cachedAt = 0;
        rejected = false;
    }

    static synchronized void reject() {
        cached = null;
        cachedSource = Source.NONE;
        cachedAt = 0;
        rejected = true;
    }

    static synchronized Source source(Context c) {
        cookie(c);
        return cachedSource;
    }

    static synchronized Source quick(Context c) {
        return source(c);
    }

    static synchronized String cookie(Context c) {
        if (rejected) return null;
        long now = SystemClock.elapsedRealtime();
        if (cachedAt != 0 && now - cachedAt < CACHE_MS) return cached;
        String value = fromWebView();
        cached = value;
        cachedSource = value != null ? Source.VOIDSTRAP : Source.NONE;
        cachedAt = now;
        return value;
    }

    static String fromWebView() {
        try {
            String all = CookieManager.getInstance().getCookie("https://www.roblox.com");
            if (all == null) return null;
            for (String part : all.split(";")) {
                String p = part.trim();
                if (p.startsWith(NAME + "=")) {
                    String v = p.substring(NAME.length() + 1);
                    return v.isEmpty() ? null : v;
                }
            }
        } catch (RuntimeException ignored) {
        }
        return null;
    }

    static void signOut() {
        try {
            CookieManager.getInstance().removeAllCookies(null);
            CookieManager.getInstance().flush();
        } catch (RuntimeException ignored) {
        }
        invalidate();
    }
}
