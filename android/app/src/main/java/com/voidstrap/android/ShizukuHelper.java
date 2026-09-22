package com.voidstrap.android;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.IBinder;
import android.os.Parcel;
import android.os.SystemClock;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import rikka.shizuku.Shizuku;

public final class ShizukuHelper {
    public static final String MANAGER = "moe.shizuku.privileged.api";
    private static final String USED = "shizukuUsed";
    private static final int REQUEST = 4201;
    private static final long START_TIMEOUT_MS = 20_000;

    public enum State { NOT_INSTALLED, NOT_RUNNING, TOO_OLD, NEEDS_PERMISSION, READY }

    public interface Result {
        void done(boolean started, int message);
    }

    private ShizukuHelper() {
    }

    public static State state(Context c) {
        boolean alive = Crash.call("shizuku ping", Shizuku::pingBinder, false);
        if (!alive) return installed(c) ? State.NOT_RUNNING : State.NOT_INSTALLED;
        if (Crash.call("shizuku version", Shizuku::isPreV11, true)) return State.TOO_OLD;
        boolean granted = Crash.call("shizuku permission", () -> Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED, false);
        return granted ? State.READY : State.NEEDS_PERMISSION;
    }

    public static boolean installed(Context c) {
        try {
            c.getPackageManager().getPackageInfo(MANAGER, 0);
            return true;
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }

    public static void openManager(Context c) {
        Intent launch = c.getPackageManager().getLaunchIntentForPackage(MANAGER);
        try {
            if (launch != null) {
                c.startActivity(launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                return;
            }
            c.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=" + MANAGER)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (RuntimeException e) {
            try {
                c.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://shizuku.rikka.app/")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            } catch (RuntimeException ignored) {
            }
        }
    }

    public static void start(Context c, Result result) {
        Context app = c.getApplicationContext();
        Store store = Store.get(app);
        State s = state(app);
        if (s == State.NOT_INSTALLED || s == State.NOT_RUNNING) {
            openManager(c);
            result.done(false, s == State.NOT_INSTALLED ? R.string.shizuku_not_installed : R.string.shizuku_not_running);
            return;
        }
        if (s == State.TOO_OLD) {
            result.done(false, R.string.shizuku_too_old);
            return;
        }
        if (s == State.NEEDS_PERMISSION) {
            Shizuku.OnRequestPermissionResultListener[] listener = new Shizuku.OnRequestPermissionResultListener[1];
            listener[0] = (code, grant) -> {
                if (code != REQUEST) return;
                Shizuku.removeRequestPermissionResultListener(listener[0]);
                if (grant == PackageManager.PERMISSION_GRANTED) launch(app, store, result);
                else result.done(false, R.string.shizuku_denied);
            };
            Shizuku.addRequestPermissionResultListener(listener[0]);
            try {
                Shizuku.requestPermission(REQUEST);
            } catch (RuntimeException e) {
                Shizuku.removeRequestPermissionResultListener(listener[0]);
                result.done(false, R.string.shizuku_failed);
            }
            return;
        }
        launch(app, store, result);
    }

    private static void launch(Context app, Store store, Result result) {
        store.work.execute(() -> {
            boolean ok = startBlocking(app);
            if (ok) store.putSetting(USED, "1");
            store.main.post(() -> result.done(ok, ok ? R.string.shizuku_started : R.string.shizuku_failed));
        });
    }

    public static void keepHelper(Context c) {
        Context app = c.getApplicationContext();
        if (!"1".equals(Store.get(app).setting(USED, "0")) || Helper.uidNow() >= 0) return;
        if (state(app) != State.READY) return;
        startBlocking(app);
    }

    private static boolean startBlocking(Context app) {
        Shizuku.UserServiceArgs args = new Shizuku.UserServiceArgs(new ComponentName(app.getPackageName(), ShizukuStarter.class.getName()))
                .daemon(false)
                .processNameSuffix("starter")
                .debuggable(BuildConfig.DEBUG)
                .version(BuildConfig.VERSION_CODE);
        AtomicReference<IBinder> binder = new AtomicReference<>();
        CountDownLatch connected = new CountDownLatch(1);
        ServiceConnection connection = new ServiceConnection() {
            @Override
            public void onServiceConnected(ComponentName name, IBinder service) {
                binder.set(service);
                connected.countDown();
            }

            @Override
            public void onServiceDisconnected(ComponentName name) {
            }
        };
        try {
            Store.get(app).main.post(() -> Crash.run("shizuku bind", () -> Shizuku.bindUserService(args, connection)));
            if (!connected.await(START_TIMEOUT_MS, TimeUnit.MILLISECONDS) || binder.get() == null) return false;
            int exit = transact(binder.get(), app);
            if (exit != 0) return false;
            long deadline = SystemClock.elapsedRealtime() + 8000;
            while (SystemClock.elapsedRealtime() < deadline) {
                if (Helper.uidNow() >= 0) return true;
                SystemClock.sleep(400);
            }
            return false;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return false;
        } finally {
            Store.get(app).main.post(() -> Crash.run("shizuku unbind", () -> Shizuku.unbindUserService(args, connection, true)));
        }
    }

    private static int transact(IBinder service, Context app) {
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(ShizukuStarter.DESCRIPTOR);
            data.writeString(app.getPackageName());
            data.writeString(String.valueOf(android.os.Process.myUid()));
            data.writeString(Helper.token(app));
            service.transact(ShizukuStarter.START, data, reply, 0);
            reply.readException();
            return reply.readInt();
        } catch (android.os.RemoteException | RuntimeException e) {
            Crash.report("shizuku start", e);
            return -1;
        } finally {
            data.recycle();
            reply.recycle();
        }
    }
}
