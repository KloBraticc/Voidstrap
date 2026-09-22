package com.voidstrap.android;

import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;

import androidx.appcompat.app.AppCompatActivity;

import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.atomic.AtomicBoolean;

final class UpdateSource {
    static final String ACTION_STATUS = "com.voidstrap.android.UPDATE_STATUS";
    private static final String DIR = "updates";
    private static final int REQUEST = 8412;

    private UpdateSource() {
    }

    static boolean selfInstalls() {
        return true;
    }

    static String label(Context c) {
        return c.getString(R.string.update_channel_releases);
    }

    static Updater.Release check(Context app) throws IOException {
        JSONObject r = Core.run("update.latest", Core.args(
                "agent", "Voidstrap Android/" + BuildConfig.VERSION_NAME,
                "flavor", "direct",
                "installed", Updater.installedCode(),
                "prerelease", false));
        ModLog.add("update check: " + r.optInt("seen") + " releases, newest tag " + r.optString("newestTag", "none") + ", " + r.optString("reason", ""));
        if (!r.optBoolean("found")) return null;
        JSONObject release = r.optJSONObject("release");
        return release == null ? null : new Updater.Release(release);
    }

    static boolean canInstall(Context c) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return true;
        return c.getPackageManager().canRequestPackageInstalls();
    }

    static void askForPermission(AppCompatActivity a) {
        Intent i = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + a.getPackageName()));
        try {
            a.startActivity(i);
        } catch (RuntimeException e) {
            Crash.report("install permission", e);
            Ui.say(a, R.string.update_permission_failed);
        }
    }

    static File staged(Context c, Updater.Release r) {
        File dir = new File(c.getCacheDir(), DIR);
        return new File(dir, "voidstrap-" + r.version + ".apk");
    }

    static void start(AppCompatActivity a, Updater.Release r, Runnable done) {
        Context app = a.getApplicationContext();
        Store store = Store.get(app);
        if (!canInstall(a)) {
            done.run();
            Updater.report(Updater.State.AVAILABLE, a.getString(R.string.update_needs_permission));
            Updater.changed(app);
            askForPermission(a);
            return;
        }
        Updater.report(Updater.State.DOWNLOADING, "");
        Updater.setProgress(0);
        Updater.changed(app);
        store.work.execute(() -> {
            File apk = staged(app, r);
            String failure = null;
            try {
                if (!ready(apk, r)) download(app, r, apk);
            } catch (IOException | RuntimeException e) {
                failure = Crash.describe(e);
            }
            String message = failure;
            store.main.post(() -> {
                if (message != null) {
                    done.run();
                    Updater.report(Updater.State.FAILED, message);
                    Updater.changed(app);
                    return;
                }
                Updater.report(Updater.State.INSTALLING, "");
                Updater.changed(app);
                store.work.execute(() -> {
                    String problem = commit(app, apk);
                    store.main.post(() -> {
                        done.run();
                        if (problem != null) {
                            Updater.report(Updater.State.FAILED, problem);
                            Updater.changed(app);
                        }
                    });
                });
            });
        });
    }

    static void resume(AppCompatActivity a) {
    }

    private static boolean ready(File apk, Updater.Release r) {
        return apk.isFile() && apk.length() == r.size && r.size > 0;
    }

    private static void download(Context app, Updater.Release r, File apk) throws IOException {
        File dir = apk.getParentFile();
        if (dir != null) Net.ensureDir(dir, "mkdir");
        clean(dir, apk.getName());
        AtomicBoolean cancel = new AtomicBoolean();
        ModLog.add("update download " + r.name + " from " + r.url);
        Net.downloadTracked(r.url, apk, Math.max(r.size, 1) + 4096, cancel, percent -> {
            Updater.setProgress(percent);
            Updater.changed(app);
        });
        ModLog.add("update downloaded " + apk.length() + " bytes");
        if (r.size > 0 && apk.length() != r.size) {
            apk.delete();
            throw new IOException(app.getString(R.string.update_error_size));
        }
        String bad = verify(app, apk, r);
        if (bad != null) {
            apk.delete();
            throw new IOException(bad);
        }
    }

    static String verify(Context app, File apk, Updater.Release r) {
        android.content.pm.PackageInfo info = app.getPackageManager().getPackageArchiveInfo(apk.getPath(), 0);
        if (info == null) return app.getString(R.string.update_error_not_apk);
        if (!app.getPackageName().equals(info.packageName)) return app.getString(R.string.update_error_other_app, String.valueOf(info.packageName));
        long code = androidx.core.content.pm.PackageInfoCompat.getLongVersionCode(info);
        if (code != r.code) return app.getString(R.string.update_error_version, String.valueOf(code), String.valueOf(r.code));
        if (code <= Updater.installedCode()) return app.getString(R.string.update_error_older);
        return null;
    }

    private static void clean(File dir, String keep) {
        if (dir == null) return;
        File[] files = dir.listFiles();
        if (files == null) return;
        for (File f : files) if (!f.getName().equals(keep)) f.delete();
    }

    private static String commit(Context app, File apk) {
        PackageInstaller installer = app.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        params.setAppPackageName(app.getPackageName());
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) params.setInstallReason(android.content.pm.PackageManager.INSTALL_REASON_USER);
        int id = -1;
        try {
            params.setSize(apk.length());
            id = installer.createSession(params);
            ModLog.add("update install session " + id + " for " + apk.length() + " bytes");
            try (PackageInstaller.Session session = installer.openSession(id)) {
                try (InputStream in = new java.io.FileInputStream(apk); OutputStream out = session.openWrite("base.apk", 0, apk.length())) {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                    session.fsync(out);
                }
                session.commit(statusSender(app, id));
            }
            return null;
        } catch (IOException | RuntimeException e) {
            if (id >= 0) {
                try {
                    installer.abandonSession(id);
                } catch (RuntimeException ignored) {
                }
            }
            return Crash.describe(e);
        }
    }

    private static android.content.IntentSender statusSender(Context app, int id) {
        Intent intent = new Intent(ACTION_STATUS).setPackage(app.getPackageName());
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) flags |= PendingIntent.FLAG_MUTABLE;
        return PendingIntent.getBroadcast(app, REQUEST + id, intent, flags).getIntentSender();
    }
}
