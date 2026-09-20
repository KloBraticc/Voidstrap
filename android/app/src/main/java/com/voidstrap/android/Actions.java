package com.voidstrap.android;

import android.app.Dialog;
import android.content.Intent;
import android.net.Uri;
import android.provider.Settings;
import android.view.View;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.appcompat.app.AppCompatActivity;

import com.google.android.material.dialog.MaterialAlertDialogBuilder;

public final class Actions {
    private Actions() {
    }

    public static void launch(AppCompatActivity a, Deeplink d, String name) {
        Launcher.launch(a, d, name, r -> {
            if (!a.isFinishing() && !a.isDestroyed()) report(a, d, name, r);
        });
    }

    private static void report(AppCompatActivity a, Deeplink d, String name, Launcher.Result r) {
        String pkg = Targets.selected(a);
        String appName = a.getString(Targets.nameRes(pkg));
        switch (r) {
            case HANDED_OFF:
            case BUSY:
                return;
            case NOT_INSTALLED:
                Ui.say(a, a.getString(d == null ? R.string.install_opening_plain : R.string.install_opening_pending, appName));
                Launcher.openStore(a, pkg);
                return;
            case DISABLED:
                Ui.alert(a)
                        .setTitle(a.getString(R.string.disabled_title, appName))
                        .setMessage(R.string.disabled_body)
                        .setPositiveButton(R.string.common_open_settings, (dlg, w) -> openAppSettings(a, pkg))
                        .setNegativeButton(R.string.common_cancel, null)
                        .show();
                return;
            case DESTINATION_UNSUPPORTED:
                Ui.alert(a)
                        .setTitle(R.string.unsupported_title)
                        .setMessage(a.getString(R.string.unsupported_body, appName))
                        .setPositiveButton(a.getString(R.string.common_open_app, appName), (dlg, w) -> launch(a, null, name))
                        .setNegativeButton(R.string.common_cancel, null)
                        .show();
                return;
            default:
                Ui.say(a, Launcher.message(r));
        }
    }

    public static void openInRoblox(AppCompatActivity a, String url) {
        openInRoblox(a, url, url);
    }

    public static void openInRoblox(AppCompatActivity a, String appUrl, String url) {
        String pkg = Targets.selected(a);
        if (Targets.resolve(a, pkg).ready()) {
            try {
                a.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(appUrl)).setPackage(pkg).addCategory(Intent.CATEGORY_BROWSABLE));
                return;
            } catch (android.content.ActivityNotFoundException ignored) {
            }
        }
        if (!Targets.resolve(a, pkg).installed) {
            Ui.say(a, a.getString(R.string.install_opening_plain, a.getString(Targets.nameRes(pkg))));
            Launcher.openStore(a, pkg);
            return;
        }
        Ui.openWeb(a, url);
    }

    public static void openAppSettings(AppCompatActivity a, String pkg) {
        try {
            a.startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + pkg)));
        } catch (RuntimeException e) {
            Ui.say(a, R.string.launch_failed);
        }
    }

    public static void bindTarget(View button, boolean chevron) {
        android.content.Context c = button.getContext();
        String pkg = Targets.selected(c);
        Targets.State s = Targets.resolve(c, pkg);
        ImageView icon = button.findViewById(R.id.row_icon);
        if (s.icon != null) {
            icon.setImageTintList(null);
            icon.setImageDrawable(s.icon);
        } else {
            icon.setImageResource(R.drawable.ic_games);
        }
        ((TextView) button.findViewById(R.id.row_title)).setText(Targets.nameRes(pkg));
        ((TextView) button.findViewById(R.id.row_detail)).setText(status(c, s));
        button.findViewById(R.id.row_chevron).setVisibility(chevron ? View.VISIBLE : View.GONE);
        button.setContentDescription(c.getString(R.string.target_button_description, c.getString(Targets.nameRes(pkg)), status(c, s)));
    }

    public static String status(android.content.Context c, Targets.State s) {
        if (!s.installed) return c.getString(R.string.target_not_installed);
        if (!s.enabled) return c.getString(R.string.target_disabled);
        if (!s.launchable) return c.getString(R.string.target_no_launcher, s.versionName);
        return c.getString(R.string.target_version, s.versionName == null ? "?" : s.versionName);
    }

    public static void targetSheet(AppCompatActivity a) {
        LinearLayout list = new LinearLayout(a);
        list.setOrientation(LinearLayout.VERTICAL);
        Dialog[] holder = new Dialog[1];
        String selected = Targets.selected(a);
        for (String pkg : Targets.offered(a)) {
            Targets.State s = Targets.resolve(a, pkg);
            Row row = Row.inflate(list);
            row.set(R.drawable.ic_games, a.getString(Targets.nameRes(pkg)), status(a, s) + "\n" + pkg);
            if (s.icon != null) {
                row.icon.setImageTintList(null);
                row.icon.setImageDrawable(s.icon);
                row.icon.getLayoutParams().width = Ui.dp(a, 32);
                row.icon.getLayoutParams().height = Ui.dp(a, 32);
            }
            row.radio.setVisibility(View.VISIBLE);
            row.radio.setChecked(pkg.equals(selected));
            row.view.setSelected(pkg.equals(selected));
            row.view.setContentDescription(a.getString(pkg.equals(selected) ? R.string.target_row_selected : R.string.target_row, a.getString(Targets.nameRes(pkg)), status(a, s)));
            row.view.setOnClickListener(v -> {
                Store.get(a).putSetting("target", pkg);
                if (holder[0] != null) holder[0].dismiss();
            });
            list.addView(row.view);
        }
        Targets.State sel = Targets.resolve(a, selected);
        if (!sel.installed) {
            com.google.android.material.button.MaterialButton get = new com.google.android.material.button.MaterialButton(a);
            get.setText(a.getString(R.string.install_get, a.getString(Targets.nameRes(selected))));
            get.setIconResource(R.drawable.ic_arrow_download);
            get.setOnClickListener(v -> Launcher.openStore(a, selected));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
            lp.topMargin = Ui.dp(a, 12);
            list.addView(get, lp);
        }
        holder[0] = Ui.surface(a, R.string.target_sheet_title, R.string.target_sheet_body, list);
        holder[0].show();
    }
}
