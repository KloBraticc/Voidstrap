package com.voidstrap.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.widget.Toast;

public final class PinResultReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String name = intent.getStringExtra(Shortcuts.EXTRA_NAME);
        Toast.makeText(context, context.getString(R.string.shortcut_added, name == null ? "" : name), Toast.LENGTH_SHORT).show();
    }
}
