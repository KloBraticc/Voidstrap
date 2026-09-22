package com.voidstrap.android;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

public final class ShortcutActivity extends Activity {
    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        Intent in = getIntent();
        String link = in == null ? null : Crash.call("shortcut link", () -> in.getStringExtra(Shortcuts.EXTRA_LINK), null);
        Deeplink d = Deeplink.parse(link);
        if (d == null) {
            done(Launcher.Result.FAILED, R.string.shortcut_broken);
            return;
        }
        Crash.run("shortcut usage", () -> androidx.core.content.pm.ShortcutManagerCompat.reportShortcutUsed(this, d.stableId()));
        String name = Crash.call("shortcut name", () -> in.getStringExtra(Shortcuts.EXTRA_NAME), null);
        Launcher.launch(this, d, name, r -> done(r, Launcher.message(r)));
    }

    private void done(Launcher.Result r, int problem) {
        if (r != Launcher.Result.HANDED_OFF && r != Launcher.Result.BUSY) {
            Crash.run("shortcut fallback", () -> getApplicationContext().startActivity(new Intent(this, MainActivity.class)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP)
                    .putExtra(MainActivity.EXTRA_PROBLEM, problem)));
        }
        finish();
    }
}
