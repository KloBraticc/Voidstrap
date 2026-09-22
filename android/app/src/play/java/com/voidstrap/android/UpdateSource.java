package com.voidstrap.android;

import android.content.Context;

import androidx.appcompat.app.AppCompatActivity;

import com.google.android.play.core.appupdate.AppUpdateInfo;
import com.google.android.play.core.appupdate.AppUpdateManager;
import com.google.android.play.core.appupdate.AppUpdateManagerFactory;
import com.google.android.play.core.appupdate.AppUpdateOptions;
import com.google.android.play.core.install.InstallState;
import com.google.android.play.core.install.InstallStateUpdatedListener;
import com.google.android.play.core.install.model.AppUpdateType;
import com.google.android.play.core.install.model.InstallStatus;
import com.google.android.play.core.install.model.UpdateAvailability;

import org.json.JSONObject;

import java.io.IOException;
import java.util.concurrent.TimeUnit;

final class UpdateSource {
    private static final int REQUEST = 8413;

    private static AppUpdateManager manager;
    private static InstallStateUpdatedListener listener;

    private UpdateSource() {
    }

    static boolean selfInstalls() {
        return false;
    }

    static String label(Context c) {
        return c.getString(R.string.update_channel_play);
    }

    private static synchronized AppUpdateManager manager(Context c) {
        if (manager == null) manager = AppUpdateManagerFactory.create(c.getApplicationContext());
        return manager;
    }

    static Updater.Release check(Context app) throws IOException {
        AppUpdateInfo info = await(app);
        ModLog.add("update check: google play availability " + (info == null ? "unknown" : String.valueOf(info.updateAvailability())));
        if (info == null || info.updateAvailability() != UpdateAvailability.UPDATE_AVAILABLE) return null;
        int code = info.availableVersionCode();
        if (code <= Updater.installedCode()) return null;
        JSONObject o = new JSONObject();
        try {
            o.put("version", name(code));
            o.put("code", code);
            o.put("url", "https://play.google.com/store/apps/details?id=" + app.getPackageName());
            o.put("size", info.totalBytesToDownload());
            o.put("page", "https://play.google.com/store/apps/details?id=" + app.getPackageName());
        } catch (org.json.JSONException e) {
            throw new IOException(Crash.describe(e));
        }
        return new Updater.Release(o);
    }

    private static AppUpdateInfo await(Context app) throws IOException {
        try {
            return com.google.android.gms.tasks.Tasks.await(manager(app).getAppUpdateInfo(), 20, TimeUnit.SECONDS);
        } catch (Exception e) {
            throw new IOException(app.getString(R.string.update_error_play));
        }
    }

    static String name(long code) {
        StringBuilder sb = new StringBuilder();
        long left = code;
        for (int i = 0; i < 4; i++) {
            sb.insert(0, left % 100);
            left /= 100;
            if (left == 0) break;
            sb.insert(0, '.');
        }
        return sb.toString();
    }

    static void start(AppCompatActivity a, Updater.Release r, Runnable done) {
        Context app = a.getApplicationContext();
        Store store = Store.get(app);
        store.work.execute(() -> {
            AppUpdateInfo info = null;
            String failure = null;
            try {
                info = await(app);
            } catch (IOException e) {
                failure = Crash.describe(e);
            }
            AppUpdateInfo ready = info;
            String message = failure;
            store.main.post(() -> {
                done.run();
                if (ready == null || ready.updateAvailability() != UpdateAvailability.UPDATE_AVAILABLE) {
                    Updater.report(Updater.State.FAILED, message == null ? app.getString(R.string.update_error_play) : message);
                    Updater.changed(app);
                    return;
                }
                listen(app);
                int type = ready.isUpdateTypeAllowed(AppUpdateType.FLEXIBLE) ? AppUpdateType.FLEXIBLE : AppUpdateType.IMMEDIATE;
                if (!ready.isUpdateTypeAllowed(type)) {
                    Updater.report(Updater.State.FAILED, app.getString(R.string.update_error_play));
                    Updater.changed(app);
                    return;
                }
                Updater.report(Updater.State.DOWNLOADING, "");
                Updater.changed(app);
                try {
                    manager(app).startUpdateFlowForResult(ready, a, AppUpdateOptions.newBuilder(type).build(), REQUEST);
                } catch (android.content.IntentSender.SendIntentException | RuntimeException e) {
                    Updater.report(Updater.State.FAILED, Crash.describe(e));
                    Updater.changed(app);
                }
            });
        });
    }

    private static synchronized void listen(Context app) {
        if (listener != null) return;
        listener = new InstallStateUpdatedListener() {
            @Override
            public void onStateUpdate(InstallState state) {
                if (state.installStatus() == InstallStatus.DOWNLOADING) {
                    long total = state.totalBytesToDownload();
                    Updater.setProgress(total > 0 ? (int) (state.bytesDownloaded() * 100 / total) : 0);
                    Updater.report(Updater.State.DOWNLOADING, "");
                } else if (state.installStatus() == InstallStatus.DOWNLOADED) {
                    Updater.report(Updater.State.READY, "");
                } else if (state.installStatus() == InstallStatus.INSTALLED) {
                    Updater.report(Updater.State.IDLE, "");
                } else if (state.installStatus() == InstallStatus.FAILED) {
                    Updater.report(Updater.State.FAILED, app.getString(R.string.update_error_play));
                }
                Updater.changed(app);
            }
        };
        manager(app).registerListener(listener);
    }

    static void resume(AppCompatActivity a) {
        if (a == null) return;
        Context app = a.getApplicationContext();
        Crash.run("play update resume", () -> manager(app).getAppUpdateInfo().addOnSuccessListener(info -> Crash.run("play update finish", () -> {
            if (info.installStatus() == InstallStatus.DOWNLOADED) {
                Updater.report(Updater.State.READY, "");
                Updater.changed(app);
                manager(app).completeUpdate();
            } else if (info.updateAvailability() == UpdateAvailability.DEVELOPER_TRIGGERED_UPDATE_IN_PROGRESS) {
                try {
                    manager(app).startUpdateFlowForResult(info, a, AppUpdateOptions.newBuilder(AppUpdateType.IMMEDIATE).build(), REQUEST);
                } catch (android.content.IntentSender.SendIntentException e) {
                    Crash.report("play update resume", e);
                }
            }
        })));
    }
}
