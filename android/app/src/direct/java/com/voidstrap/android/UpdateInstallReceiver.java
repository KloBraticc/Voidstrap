package com.voidstrap.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;

public final class UpdateInstallReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (context == null || intent == null) return;
        Context app = context.getApplicationContext();
        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirm = intent.getParcelableExtra(Intent.EXTRA_INTENT);
            if (confirm == null) {
                fail(app, app.getString(R.string.update_error_no_prompt));
                return;
            }
            confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            Crash.run("install prompt", () -> app.startActivity(confirm));
            return;
        }
        if (status == PackageInstaller.STATUS_SUCCESS) {
            ModLog.add("update installed");
            Updater.report(Updater.State.IDLE, "");
            Updater.changed(app);
            return;
        }
        fail(app, reason(app, status, message));
    }

    private static void fail(Context app, String message) {
        ModLog.add("update failed: " + message);
        Updater.report(Updater.State.FAILED, message);
        Updater.changed(app);
        Notify.toast(app, Notify.GENERAL, app.getString(R.string.update_failed_toast, message));
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
