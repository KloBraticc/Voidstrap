package com.voidstrap.android;

import android.app.Dialog;
import android.content.Context;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.provider.Settings;
import android.view.View;
import android.widget.LinearLayout;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;

import org.json.JSONObject;

public final class FlagSync {
    private static final long SETUP_POLL_MS = 1000;
    private static boolean asked;

    public interface Done {
        void run(boolean reported);
    }

    private FlagSync() {
    }

    private static boolean seen(Context c) {
        return "1".equals(Store.get(c).setting("helperSeen", "0"));
    }

    public static String status(Context c) {
        switch (FlagWriter.mode(c)) {
            case HELPER:
                return c.getString(Helper.uid() == 0 ? R.string.flags_status_helper_root : R.string.flags_status_helper);
            case ROOT:
                return c.getString(R.string.flags_status_root);
            default:
                return c.getString(seen(c) ? R.string.flags_status_stopped : R.string.flags_status_none);
        }
    }

    public static void bindStatus(AppCompatActivity a, android.widget.TextView t) {
        FlagWriter.Mode mode = FlagWriter.mode(a);
        boolean ready = mode == FlagWriter.Mode.HELPER || mode == FlagWriter.Mode.ROOT;
        if (mode == FlagWriter.Mode.HELPER && !seen(a)) Store.get(a).putSetting("helperSeen", "1");
        int text;
        switch (mode) {
            case HELPER: text = R.string.flags_short_helper; break;
            case ROOT: text = R.string.flags_short_root; break;
            default: text = seen(a) ? R.string.flags_short_stopped : R.string.flags_short_none;
        }
        t.setText(text);
        android.graphics.drawable.Drawable icon = androidx.core.content.ContextCompat.getDrawable(a, ready ? R.drawable.ic_checkmark_circle : R.drawable.ic_warning);
        if (icon != null) {
            icon = icon.mutate();
            int size = Ui.dp(a, 16);
            icon.setBounds(0, 0, size, size);
            icon.setTint(a.getColor(ready ? R.color.vs_success : R.color.vs_caution));
        }
        t.setCompoundDrawablesRelative(icon, null, null, null);
        t.setContentDescription(status(a));
        t.setOnClickListener(v -> setup(a));
    }

    private static boolean debuggingOn(Context c) {
        return Settings.Global.getInt(c.getContentResolver(), Settings.Global.ADB_ENABLED, 0) == 1
                || Settings.Global.getInt(c.getContentResolver(), "adb_wifi_enabled", 0) == 1;
    }

    private static void openDeveloperOptions(AppCompatActivity a) {
        try {
            a.startActivity(new Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS));
        } catch (RuntimeException e) {
            try {
                a.startActivity(new Intent(Settings.ACTION_DEVICE_INFO_SETTINGS));
            } catch (RuntimeException ignored) {
            }
        }
    }

    public static void ask(androidx.fragment.app.Fragment f) {
        if (asked) return;
        Store.get(f.requireContext()).main.postDelayed(() -> {
            if (asked || !f.isResumed() || f.isHidden()) return;
            FlagWriter.Mode m = FlagWriter.mode(f.requireContext());
            if (m == FlagWriter.Mode.NONE) setup((AppCompatActivity) f.requireActivity());
        }, 800);
    }

    public static void setup(AppCompatActivity a) {
        asked = true;
        if (FlagWriter.mode(a) != FlagWriter.Mode.NONE) {
            Ui.say(a, status(a));
            return;
        }
        Store store = Store.get(a);
        String command = Helper.command(a);
        LinearLayout list = new LinearLayout(a);
        list.setOrientation(LinearLayout.VERTICAL);
        Row debugging = Row.inflate(list);
        Row start = Row.inflate(list);
        list.addView(debugging.view);
        list.addView(start.view);
        Dialog[] sheet = new Dialog[1];
        Runnable bind = () -> {
            boolean running = Helper.uidNow() >= 0;
            boolean adb = running || debuggingOn(a);
            step(a, debugging, R.drawable.ic_bug, R.string.setup_debugging, R.string.setup_debugging_body, adb, true, () -> openDeveloperOptions(a));
            step(a, start, R.drawable.ic_play, R.string.setup_run, 0, running, true, () -> {
                if (command == null) return;
                Ui.copy(a, a.getString(R.string.setup_run), command);
                Notify.say(a, Notify.COPY, R.string.setup_copied);
            });
            start.detail.setText(running ? a.getString(R.string.setup_run_done)
                    : command == null ? a.getString(R.string.setup_run_unavailable) : a.getString(R.string.setup_run_body, command));
            start.detail.setVisibility(View.VISIBLE);
            if (running && sheet[0] != null && sheet[0].isShowing()) {
                store.putSetting("helperSeen", "1");
                store.changed();
                sheet[0].dismiss();
            }
        };
        Runnable[] poll = new Runnable[1];
        poll[0] = () -> {
            bind.run();
            if (sheet[0] != null && sheet[0].isShowing()) store.main.postDelayed(poll[0], SETUP_POLL_MS);
        };
        bind.run();
        sheet[0] = Ui.surface(a, R.string.setup_title, R.string.setup_body, list, () -> store.main.removeCallbacks(poll[0]));
        sheet[0].show();
        store.main.postDelayed(poll[0], SETUP_POLL_MS);
    }

    private static void step(Context c, Row row, int icon, int title, int body, boolean done, boolean available, Runnable action) {
        boolean actionable = !done && available;
        row.set(done ? R.drawable.ic_checkmark_circle : icon, c.getString(title), body == 0 ? "" : c.getString(body));
        row.icon.setImageTintList(ColorStateList.valueOf(done ? c.getColor(R.color.vs_success) : Ui.attr(c, R.attr.vsTextSecondary)));
        row.detail.setMaxLines(4);
        row.chevron.setVisibility(actionable ? View.VISIBLE : View.GONE);
        row.view.setOnClickListener(v -> action.run());
        row.view.setClickable(actionable);
        row.view.setFocusable(actionable);
        row.view.setAlpha(done || available ? 1f : 0.5f);
    }

    public static void apply(AppCompatActivity a, boolean quiet, @Nullable Done done) {
        FlagWriter.Mode mode = FlagWriter.mode(a);
        if (mode == FlagWriter.Mode.HELPER || mode == FlagWriter.Mode.ROOT) {
            write(a, mode, quiet, done);
            return;
        }
        if (!quiet) setup(a);
        if (done != null) done.run(false);
    }

    public static void remove(AppCompatActivity a) {
        FlagWriter.Mode mode = FlagWriter.mode(a);
        if (mode != FlagWriter.Mode.HELPER && mode != FlagWriter.Mode.ROOT) {
            setup(a);
            return;
        }
        Store store = Store.get(a);
        store.work.execute(() -> {
            int r = FlagWriter.remove(mode);
            store.main.post(() -> report(a, r, R.string.flags_sync_removed));
        });
    }

    private static void write(AppCompatActivity a, FlagWriter.Mode mode, boolean quiet, @Nullable Done done) {
        Store store = Store.get(a);
        JSONObject values = store.flags.active().valuesJson();
        int count = values.length();
        String json = values.toString();
        if (FlagWriter.tooLarge(json)) {
            Ui.say(a, R.string.flags_too_large);
            if (done != null) done.run(true);
            return;
        }
        store.work.execute(() -> {
            int r = count == 0 ? FlagWriter.remove(mode) : FlagWriter.write(mode, json);
            boolean readable = r == FlagWriter.OK && FlagWriter.readable(json);
            store.main.post(() -> {
                if (a.isFinishing() || a.isDestroyed()) return;
                if (r == FlagWriter.OK && !readable) Ui.say(a, R.string.flags_sync_unreadable);
                else if (r == FlagWriter.OK && count == 0) Notify.say(a, Notify.FLAGS, R.string.flags_sync_removed);
                else if (r == FlagWriter.OK) Notify.say(a, Notify.FLAGS, a.getResources().getQuantityString(quiet ? R.plurals.flags_sync_saved : R.plurals.flags_sync_applied, count, count));
                else report(a, r, 0);
                if (done != null) done.run(true);
            });
        });
    }

    private static void report(AppCompatActivity a, int result, int okText) {
        if (a.isFinishing() || a.isDestroyed()) return;
        if (result == FlagWriter.OK) Notify.say(a, Notify.FLAGS, okText);
        else if (result == FlagWriter.DENIED) Ui.say(a, R.string.flags_sync_denied);
        else Ui.say(a, R.string.flags_sync_failed);
    }

    public static void rootRow(LinearLayout parent, AppCompatActivity a) {
        Context c = a;
        Store store = Store.get(a);
        boolean rooted = FlagWriter.rootAvailable();
        boolean enabled = FlagWriter.rootEnabled(c);
        com.google.android.material.materialswitch.MaterialSwitch toggle = new com.google.android.material.materialswitch.MaterialSwitch(c);
        toggle.setChecked(enabled);
        toggle.setContentDescription(c.getString(R.string.settings_root));
        int summary = enabled ? R.string.settings_root_on : rooted ? R.string.settings_root_body : R.string.settings_root_untested;
        LinearLayout row = SettingRows.row(parent, c.getString(R.string.settings_root), c.getString(summary), toggle);
        row.setOnClickListener(x -> {
            if (toggle.isEnabled()) toggle.toggle();
        });
        toggle.setOnCheckedChangeListener((b, on) -> {
            if (!on) {
                FlagWriter.setRootEnabled(c, false);
                store.changed();
                return;
            }
            if (FlagWriter.rootEnabled(c)) return;
            toggle.setEnabled(false);
            com.google.android.material.snackbar.Snackbar waiting = Ui.make(a, c.getString(R.string.settings_root_waiting));
            waiting.setDuration(com.google.android.material.snackbar.Snackbar.LENGTH_INDEFINITE);
            waiting.show();
            Context app = c.getApplicationContext();
            store.work.execute(() -> {
                int r = FlagWriter.testRoot();
                if (r == FlagWriter.OK) {
                    FlagWriter.setRootEnabled(app, true);
                    Helper.keepRootHelper(app);
                }
                store.main.post(() -> {
                    if (r != FlagWriter.OK) FlagWriter.setRootEnabled(app, false);
                    waiting.dismiss();
                    store.changed();
                    if (a.isFinishing() || a.isDestroyed()) return;
                    toggle.setEnabled(true);
                    if (r != FlagWriter.OK) toggle.setChecked(false);
                    if (r == FlagWriter.OK) Notify.say(a, Notify.GENERAL, R.string.settings_root_ready);
                    else rootFailed(a);
                });
            });
        });
    }

    private static void rootFailed(AppCompatActivity a) {
        boolean rooted = FlagWriter.rootAvailable();
        boolean helper = FlagWriter.mode(a) != FlagWriter.Mode.NONE;
        int message = rooted ? R.string.settings_root_denied : helper ? R.string.settings_root_missing_helper : R.string.settings_root_missing;
        com.google.android.material.dialog.MaterialAlertDialogBuilder b = Ui.alert(a)
                .setTitle(R.string.settings_root_denied_title)
                .setMessage(message)
                .setPositiveButton(R.string.common_ok, null);
        if (!rooted && !helper) b.setNeutralButton(R.string.compat_setup, (d, w) -> setup(a));
        b.show();
    }
}
