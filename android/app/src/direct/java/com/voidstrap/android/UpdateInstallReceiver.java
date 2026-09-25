package com.voidstrap.android;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.os.Build;

import androidx.appcompat.app.AppCompatActivity;
import androidx.core.app.NotificationCompat;
import androidx.core.app.NotificationManagerCompat;
import androidx.core.content.IntentCompat;

public final class UpdateInstallReceiver extends BroadcastReceiver {
    private static final String CHANNEL = "app_updates";
    private static final int NOTIFICATION = 8414;
    private static Intent pendingConfirmation;

    @Override
    public void onReceive(Context context, Intent intent) {
        if (context == null || intent == null) return;
        Context app = context.getApplicationContext();
        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent.class);
            if (confirm == null) {
                fail(app, app.getString(R.string.update_error_no_prompt));
                return;
            }
            confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            pendingConfirmation = confirm;
            AppCompatActivity active = Updater.foreground();
            if (active != null) {
                try {
                    active.startActivity(confirm);
                    pendingConfirmation = null;
                    return;
                } catch (RuntimeException e) {
                    Crash.report("install prompt", e);
                }
            }
            notifyForConfirmation(app, confirm);
            return;
        }
        if (status == PackageInstaller.STATUS_SUCCESS) {
            pendingConfirmation = null;
            NotificationManagerCompat.from(app).cancel(NOTIFICATION);
            ModLog.add("update installed");
            Updater.report(Updater.State.IDLE, "");
            Updater.changed(app);
            return;
        }
        fail(app, reason(app, status, message));
    }

    private static void fail(Context app, String message) {
        pendingConfirmation = null;
        NotificationManagerCompat.from(app).cancel(NOTIFICATION);
        ModLog.add("update failed: " + message);
        Updater.report(Updater.State.FAILED, message);
        Updater.changed(app);
        Notify.toast(app, Notify.GENERAL, app.getString(R.string.update_failed_toast, message));
    }

    static void resume(AppCompatActivity a) {
        Intent confirm = pendingConfirmation;
        if (confirm == null) return;
        try {
            a.startActivity(confirm);
            pendingConfirmation = null;
            Updater.report(Updater.State.INSTALLING, "");
            Updater.changed(a);
            NotificationManagerCompat.from(a).cancel(NOTIFICATION);
        } catch (RuntimeException e) {
            Crash.report("install prompt", e);
            fail(a, a.getString(R.string.update_error_no_prompt));
        }
    }

    private static void notifyForConfirmation(Context app, Intent confirm) {
        if (!NotificationManagerCompat.from(app).areNotificationsEnabled()
                || Build.VERSION.SDK_INT >= 33 && app.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            Updater.report(Updater.State.READY, app.getString(R.string.update_open_to_install));
            Updater.changed(app);
            return;
        }
        try {
            NotificationManager manager = (NotificationManager) app.getSystemService(Context.NOTIFICATION_SERVICE);
            if (manager == null) throw new IllegalStateException(app.getString(R.string.update_error_no_prompt));
            if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(new NotificationChannel(
                    CHANNEL, app.getString(R.string.update_channel_notification), NotificationManager.IMPORTANCE_HIGH));
            if (Build.VERSION.SDK_INT >= 26 && manager.getNotificationChannel(CHANNEL).getImportance() == NotificationManager.IMPORTANCE_NONE) {
                Updater.report(Updater.State.READY, app.getString(R.string.update_open_to_install));
                Updater.changed(app);
                return;
            }
            PendingIntent open = PendingIntent.getActivity(app, NOTIFICATION, confirm,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            NotificationManagerCompat.from(app).notify(NOTIFICATION, new NotificationCompat.Builder(app, CHANNEL)
                    .setSmallIcon(R.drawable.ic_arrow_sync)
                    .setContentTitle(app.getString(R.string.update_confirm_title))
                    .setContentText(app.getString(R.string.update_confirm_body))
                    .setContentIntent(open)
                    .setAutoCancel(true)
                    .build());
            Updater.report(Updater.State.INSTALLING, app.getString(R.string.update_open_to_install));
            Updater.changed(app);
        } catch (RuntimeException e) {
            Crash.report("install notification", e);
            Updater.report(Updater.State.READY, app.getString(R.string.update_open_to_install));
            Updater.changed(app);
        }
    }

    private static String reason(Context app, int status, String message) {
        switch (status) {
            case PackageInstaller.STATUS_FAILURE_ABORTED:
                return app.getString(R.string.update_error_cancelled);
            case PackageInstaller.STATUS_FAILURE_BLOCKED:
                return app.getString(R.string.update_error_blocked);
            case PackageInstaller.STATUS_FAILURE_CONFLICT:
                return app.getString(R.string.update_error_conflict);
            case PackageInstaller.STATUS_FAILURE_INCOMPATIBLE:
                return app.getString(R.string.update_error_incompatible);
            case PackageInstaller.STATUS_FAILURE_INVALID:
                return app.getString(R.string.update_error_invalid);
            case PackageInstaller.STATUS_FAILURE_STORAGE:
                return app.getString(R.string.update_error_storage);
            default:
                return message == null || message.isEmpty() ? app.getString(R.string.update_error_generic) : message;
        }
    }
}
