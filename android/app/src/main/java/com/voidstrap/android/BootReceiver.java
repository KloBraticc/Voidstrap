package com.voidstrap.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent == null ? null : intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action) && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) return;
        Context app = context.getApplicationContext();
        PendingResult pending = goAsync();
        Thread t = new Thread(() -> {
            try {
                Crash.run("boot helper", () -> Helper.keepRootHelper(app));
                Crash.run("boot mods", () -> ModEngine.onBoot(app));
            } finally {
                Crash.run("boot finish", pending::finish);
            }
        }, "voidstrap-mods-boot");
        t.setDaemon(true);
        t.start();
    }
}
