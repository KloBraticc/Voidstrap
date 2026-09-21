package com.voidstrap.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class PinResultReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String name = intent.getStringExtra(Shortcuts.EXTRA_NAME);
        Notify.toast(context, Notify.SHORTCUTS, context.getString(R.string.shortcut_added, name == null ? "" : name));
    }
}
