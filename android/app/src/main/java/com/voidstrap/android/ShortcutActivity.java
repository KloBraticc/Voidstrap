package com.voidstrap.android;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

public final class ShortcutActivity extends Activity {
    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        Intent in = getIntent();
        Deeplink d = Deeplink.parse(in.getStringExtra(Shortcuts.EXTRA_LINK));
        if (d == null) {
            done(Launcher.Result.FAILED, R.string.shortcut_broken);
            return;
        }
        androidx.core.content.pm.ShortcutManagerCompat.reportShortcutUsed(this, d.stableId());
        Launcher.launch(this, d, in.getStringExtra(Shortcuts.EXTRA_NAME), r -> done(r, Launcher.message(r)));
    }

    private void done(Launcher.Result r, int problem) {
        if (r != Launcher.Result.HANDED_OFF && r != Launcher.Result.BUSY) {
            getApplicationContext().startActivity(new Intent(this, MainActivity.class)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP)
                    .putExtra(MainActivity.EXTRA_PROBLEM, problem));
        }
        finish();
    }
}
