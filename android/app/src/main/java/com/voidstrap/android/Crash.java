package com.voidstrap.android;

import android.os.Handler;
import android.os.Looper;
import android.os.Message;
import android.util.Log;

import androidx.annotation.NonNull;

import java.util.concurrent.Callable;

public final class Crash {
    private static final String TAG = "Voidstrap";

    private Crash() {
    }

    public static String describe(Throwable t) {
        if (t == null) return "unknown";
        Throwable root = t;
        while (root.getCause() != null && root.getCause() != root) root = root.getCause();
        String message = root.getMessage();
        String name = root.getClass().getSimpleName();
        return message == null || message.isEmpty() ? name : name + ", " + message;
    }

    public static void report(String where, Throwable t) {
        try {
            Log.e(TAG, "Recovered from a failure in " + where, t);
            ModLog.add("recovered from a failure in " + where + ": " + describe(t));
        } catch (Throwable ignored) {
        }
    }

    public static void run(String where, Runnable body) {
        if (body == null) return;
        try {
            body.run();
        } catch (Throwable t) {
            report(where, t);
        }
    }

    public static <T> T call(String where, Callable<T> body, T fallback) {
        if (body == null) return fallback;
        try {
            T v = body.call();
            return v == null ? fallback : v;
        } catch (Throwable t) {
            report(where, t);
            return fallback;
        }
    }

    public static boolean onMain() {
        return Looper.myLooper() == Looper.getMainLooper();
    }

    public static Runnable wrap(String where, Runnable body) {
        return new Guarded(where, body);
    }

    private static final class Guarded implements Runnable {
        private final String where;
        private final Runnable body;

        Guarded(String where, Runnable body) {
            this.where = where;
            this.body = body;
        }

        @Override
        public void run() {
            Crash.run(where, body);
        }
    }

    public static final class SafeHandler extends Handler {
        private final String where;

        public SafeHandler(Looper looper, String where) {
            super(looper);
            this.where = where;
        }

        @Override
        public void dispatchMessage(@NonNull Message msg) {
            try {
                super.dispatchMessage(msg);
            } catch (Throwable t) {
                report(where, t);
            }
        }
    }

    public static void install() {
        try {
            Thread.setDefaultUncaughtExceptionHandler(new LastResort(Thread.getDefaultUncaughtExceptionHandler()));
        } catch (Throwable t) {
            report("crash handler install", t);
        }
    }

    public static void guardMainLoop() {
        try {
            new Handler(Looper.getMainLooper()).post(new LoopGuard());
        } catch (Throwable t) {
            report("main loop guard", t);
        }
    }

    private static final class LastResort implements Thread.UncaughtExceptionHandler {
        private final Thread.UncaughtExceptionHandler previous;

        LastResort(Thread.UncaughtExceptionHandler previous) {
            this.previous = previous;
        }

        @Override
        public void uncaughtException(@NonNull Thread thread, @NonNull Throwable error) {
            report("thread " + thread.getName(), error);
            if (thread == Looper.getMainLooper().getThread() && previous != null) previous.uncaughtException(thread, error);
        }
    }

    private static final class LoopGuard implements Runnable {
        @Override
        public void run() {
            while (true) {
                try {
                    Looper.loop();
                } catch (Throwable t) {
                    report("main loop", t);
                }
            }
        }
    }
}
