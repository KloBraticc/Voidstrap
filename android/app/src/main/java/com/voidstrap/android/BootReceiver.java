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
                Helper.keepRootHelper(app);
                ModEngine.onBoot(app);
            } finally {
                pending.finish();
            }
        }, "voidstrap-mods-boot");
        t.start();
    }
}
