package com.voidstrap.android;

import android.app.ActivityManager;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatDelegate;
import androidx.core.app.NotificationManagerCompat;

import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.tabs.TabLayout;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public final class SettingsFragment extends Page {
    private static final String STATE_SECTION = "settingsSection";
    static final int SECTION_ABOUT = 4;
    static int pendingSection = -1;

    private View target;
    private TextView helperDetail;
    private View[] pages;
    private TabLayout tabs;
    private int section;

    private ActivityResultLauncher<String> exportLauncher;
    private ActivityResultLauncher<String[]> importLauncher;
    private ActivityResultLauncher<String> notifyPermission;
    private ActivityResultLauncher<String[]> fontLauncher;
    private LinearLayout appearance;
    private final List<MaterialSwitch> notifySwitches = new ArrayList<>();
    private TextView notifyStatus;
    private View trackingOff;
    private Row updateRow;

    public SettingsFragment() {
        super(R.layout.fragment_settings);
    }

    @Override
    public void onCreate(@Nullable Bundle saved) {
        super.onCreate(saved);
        exportLauncher = registerForActivityResult(new ActivityResultContracts.CreateDocument("application/json"), this::onExport);
        importLauncher = registerForActivityResult(new ActivityResultContracts.OpenDocument(), this::onImport);
        fontLauncher = registerForActivityResult(new ActivityResultContracts.OpenDocument(), this::onFont);
        notifyPermission = registerForActivityResult(new ActivityResultContracts.RequestPermission(), granted -> {
            if (!isAdded()) return;
            bindNotifications();
            com.google.android.material.snackbar.Snackbar bar = granted ? null : Ui.make(host(), getString(R.string.integrations_notify_blocked));
            if (bar != null) bar.setAction(R.string.notify_open, x -> openNotificationSettings()).show();
        });
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        pages = new View[]{v.findViewById(R.id.settings_page_appearance), v.findViewById(R.id.settings_page_notifications), v.findViewById(R.id.settings_page_roblox), v.findViewById(R.id.settings_page_backup), v.findViewById(R.id.settings_page_about)};
        tabs = v.findViewById(R.id.settings_tabs);
        int[] labels = {R.string.settings_appearance, R.string.settings_notifications, R.string.settings_roblox, R.string.settings_backup, R.string.settings_about};
        int[] icons = {R.drawable.ic_options, R.drawable.ic_alert, R.drawable.ic_games, R.drawable.ic_folder, R.drawable.ic_info};
        for (int i = 0; i < labels.length; i++) tabs.addTab(tabs.newTab().setText(labels[i]).setIcon(icons[i]));
        section = saved == null ? 0 : Math.max(0, Math.min(pages.length - 1, saved.getInt(STATE_SECTION, 0)));
        TabLayout.Tab initial = tabs.getTabAt(section);
        if (initial != null) initial.select();
        showSection(section);
        tabs.addOnTabSelectedListener(new TabLayout.OnTabSelectedListener() {
            @Override
            public void onTabSelected(TabLayout.Tab tab) {
                showSection(tab.getPosition());
            }

            @Override
            public void onTabUnselected(TabLayout.Tab tab) {
            }

            @Override
            public void onTabReselected(TabLayout.Tab tab) {
                ScrollPane page = (ScrollPane) pages[tab.getPosition()];
                page.smoothScrollTo(0, 0);
            }
        });
        target = v.findViewById(R.id.settings_target);
        target.setOnClickListener(x -> Actions.targetSheet(host()));
        appearance = v.findViewById(R.id.appearance_rows);
        buildAppearance();
        notifications(v.findViewById(R.id.notification_sections));
        LinearLayout roblox = v.findViewById(R.id.roblox_rows);
        Row helper = Row.inflate(roblox);
        helper.set(R.drawable.ic_shield_checkmark, getString(R.string.settings_helper), FlagSync.status(requireContext()));
        helper.detail.setMaxLines(4);
        helper.chevron.setVisibility(View.VISIBLE);
        helper.view.setOnClickListener(x -> FlagSync.setup(host()));
        helperDetail = helper.detail;
        roblox.addView(helper.view);
        FlagSync.rootRow(roblox, host());
        if (!Ui.wide(requireContext())) row(roblox, R.drawable.ic_add, R.string.nav_integrations, R.string.integrations_open_body, () -> ((MainActivity) host()).select(R.id.nav_integrations));
        if (!Ui.wide(requireContext()) && SmartJoin.available()) row(roblox, R.drawable.ic_globe, SmartJoin.title(requireContext()), SmartJoin.openBody(requireContext()), () -> ((MainActivity) host()).select(R.id.nav_smart));
        row(roblox, R.drawable.ic_history, R.string.settings_clear_history, R.string.settings_clear_history_body, () ->
                Ui.alert(requireContext())
                        .setTitle(R.string.history_clear_title)
                        .setMessage(R.string.history_clear_body)
                        .setPositiveButton(R.string.common_clear, (d, w) -> {
                            store.clearHistory();
                            Notify.say(host(), Notify.GENERAL, R.string.history_cleared);
                        })
                        .setNegativeButton(R.string.common_cancel, null)
                        .show());
        v.findViewById(R.id.backup_export).setOnClickListener(x -> exportLauncher.launch("voidstrap-android-backup.json"));
        v.findViewById(R.id.backup_import).setOnClickListener(x -> importLauncher.launch(new String[]{"application/json", "text/plain", "application/octet-stream"}));
        LinearLayout about = v.findViewById(R.id.about_rows);
        row(about, R.drawable.ic_bug, R.string.settings_diagnostics, R.string.settings_diagnostics_body, this::showDiagnostics);
        row(about, R.drawable.ic_document, R.string.settings_licenses, R.string.settings_licenses_body, this::showLicenses);
        row(about, R.drawable.ic_globe, R.string.settings_website, R.string.settings_website_body, () -> Ui.openWeb(requireContext(), getString(R.string.url_website)));
        buildUpdates(about);
        if (BuildConfig.DIRECT_UPDATES) {
            row(about, R.drawable.ic_globe, R.string.settings_updates, R.string.settings_updates_body, () -> Ui.openWeb(requireContext(), getString(R.string.url_releases)));
        }
        ((TextView) v.findViewById(R.id.version)).setText(getString(R.string.settings_version, BuildConfig.VERSION_NAME, getString(BuildConfig.DIRECT_UPDATES ? R.string.edition_direct : R.string.edition_play)));
    }

    @Override
    public void onSaveInstanceState(@NonNull Bundle out) {
        super.onSaveInstanceState(out);
        out.putInt(STATE_SECTION, section);
    }

    private void showSection(int index) {
        section = index;
        for (int i = 0; i < pages.length; i++) pages[i].setVisibility(i == index ? View.VISIBLE : View.GONE);
    }

    private void row(LinearLayout parent, int icon, int title, int detail, Runnable r) {
        row(parent, icon, getString(title), getString(detail), r);
    }

    private void row(LinearLayout parent, int icon, String title, String detail, Runnable r) {
        Row row = Row.inflate(parent);
        row.set(icon, title, detail);
        row.chevron.setVisibility(View.VISIBLE);
        row.view.setOnClickListener(x -> r.run());
        parent.addView(row.view);
    }

    private void buildAppearance() {
        appearance.removeAllViews();
        SettingRows.appearance(appearance, requireActivity(), R.id.nav_settings);
        String[] names = getResources().getStringArray(R.array.settings_fonts);
        CharSequence[] labels = new CharSequence[names.length];
        for (int i = 0; i < names.length; i++) {
            String family = AppFont.FAMILIES[i].isEmpty() ? "sans-serif" : AppFont.FAMILIES[i];
            android.text.SpannableString label = new android.text.SpannableString(names[i]);
            if (!AppFont.CUSTOM.equals(family)) label.setSpan(new android.text.style.TypefaceSpan(family), 0, label.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            labels[i] = label;
        }
        SettingRows.choice(appearance, getString(R.string.settings_font), getString(R.string.settings_font_body), labels, AppFont.index(store), i -> {
            if (AppFont.CUSTOM.equals(AppFont.FAMILIES[i])) {
                fontLauncher.launch(AppFont.TYPES);
                return;
            }
            store.putSetting(AppFont.SETTING, AppFont.FAMILIES[i]);
            ThemeFade.restart(requireActivity(), R.id.nav_settings);
        });
        MaterialSwitch voidRpc = new MaterialSwitch(requireContext());
        voidRpc.setChecked(AppPresence.enabled(store));
        voidRpc.setContentDescription(getString(R.string.settings_void_rpc));
        SettingRows.row(appearance, getString(R.string.settings_void_rpc), getString(R.string.settings_void_rpc_body), voidRpc).setOnClickListener(x -> voidRpc.toggle());
        voidRpc.setOnCheckedChangeListener((b, on) -> store.putSetting(AppPresence.SETTING, on ? "1" : "0"));
    }

    private void onFont(Uri uri) {
        if (uri == null) {
            if (getView() != null) buildAppearance();
            return;
        }
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            boolean ok = AppFont.install(app, uri);
            store.main.post(() -> {
                if (!isAdded() || getView() == null) return;
                if (!ok) {
                    buildAppearance();
                    Ui.say(host(), R.string.settings_font_invalid);
                    return;
                }
                AppFont.reload();
                store.putSetting(AppFont.SETTING, AppFont.CUSTOM);
                ThemeFade.restart(requireActivity(), R.id.nav_settings);
            });
        });
    }

    private void notifications(LinearLayout root) {
        Context c = requireContext();
        notifySwitches.clear();
        LinearLayout system = SettingRows.section(root, getString(R.string.notify_system));
        LinearLayout allow = SettingRows.row(system, getString(R.string.notify_allow), getString(R.string.notify_blocked), null);
        notifyStatus = (TextView) ((LinearLayout) allow.getChildAt(0)).getChildAt(1);
        allow.setOnClickListener(x -> {
            if (Build.VERSION.SDK_INT >= 33 && !NotificationManagerCompat.from(c).areNotificationsEnabled()) notifyPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS);
            else openNotificationSettings();
        });
        SettingRows.row(system, getString(R.string.notify_channels), getString(R.string.notify_channels_body), null).setOnClickListener(x -> openNotificationSettings());
        trackingOff = caption(system, R.string.notify_tracking_off);
        notifySwitch(system, getString(R.string.integrations_notify), getString(R.string.integrations_notify_body), Integrations.NOTIFY);
        notifySwitch(system, getString(R.string.notify_location), getString(R.string.notify_location_body), Integrations.LOCATION);
        notifySwitch(system, getString(R.string.notify_popup), getString(R.string.notify_popup_body), Notify.POPUP);
        SettingRows.choice(system, getString(R.string.notify_join_time), null, getResources().getStringArray(R.array.notify_join_times), Notify.index(store, Notify.JOIN_TIME, Notify.JOIN_TIMES.length, 0), i -> store.putSetting(Notify.JOIN_TIME, String.valueOf(i)));
        String alerts = SmartJoin.alertsTitle(c);
        if (alerts != null) notifySwitch(system, alerts, SmartJoin.alertsBody(c), Notify.SMART_ALERTS);
        notifySwitch(system, getString(R.string.notify_track_game), getString(R.string.notify_track_game_body), Notify.TRACK_GAME);

        LinearLayout inApp = SettingRows.section(root, getString(R.string.notify_in_app));
        caption(inApp, R.string.notify_in_app_body);
        SettingRows.choice(inApp, getString(R.string.notify_length), null, getResources().getStringArray(R.array.notify_lengths), Notify.index(store, Notify.LENGTH, Notify.LENGTHS.length, Notify.DEFAULT_LENGTH), i -> store.putSetting(Notify.LENGTH, String.valueOf(i)));
        notifySwitch(inApp, getString(R.string.notify_launch), getString(R.string.notify_launch_body), Notify.LAUNCH);
        if (SmartJoin.available()) notifySwitch(inApp, getString(R.string.notify_smart, SmartJoin.title(c)), getString(R.string.notify_smart_body), Notify.SMART);
        notifySwitch(inApp, getString(R.string.notify_flags), getString(R.string.notify_flags_body), Notify.FLAGS);
        notifySwitch(inApp, getString(R.string.notify_mods), getString(R.string.notify_mods_body), Notify.MODS);
        notifySwitch(inApp, getString(R.string.notify_library), getString(R.string.notify_library_body), Notify.LIBRARY);
        notifySwitch(inApp, getString(R.string.notify_shortcuts), getString(R.string.notify_shortcuts_body), Notify.SHORTCUTS);
        notifySwitch(inApp, getString(R.string.notify_copy), getString(R.string.notify_copy_body), Notify.COPY);
        notifySwitch(inApp, getString(R.string.notify_general), getString(R.string.notify_general_body), Notify.GENERAL);
    }

    private TextView caption(LinearLayout parent, int text) {
        Context c = parent.getContext();
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        t.setText(text);
        t.setPadding(Ui.dp(c, 16), Ui.dp(c, 8), Ui.dp(c, 16), Ui.dp(c, 4));
        parent.addView(t);
        return t;
    }

    private void notifySwitch(LinearLayout parent, String title, String body, String key) {
        MaterialSwitch toggle = new MaterialSwitch(requireContext());
        toggle.setChecked(Notify.on(store, key));
        toggle.setContentDescription(title);
        toggle.setTag(key);
        SettingRows.row(parent, title, body, toggle).setOnClickListener(x -> toggle.toggle());
        toggle.setOnCheckedChangeListener((b, on) -> {
            if (Notify.on(store, key) == on) return;
            Notify.set(store, key, on);
            if (on && Integrations.NOTIFY.equals(key) && Build.VERSION.SDK_INT >= 33 && !NotificationManagerCompat.from(requireContext()).areNotificationsEnabled()) notifyPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS);
        });
        notifySwitches.add(toggle);
    }

    private void openNotificationSettings() {
        Notify.openSettings(host());
    }

    private void bindNotifications() {
        if (notifyStatus == null) return;
        notifyStatus.setText(NotificationManagerCompat.from(requireContext()).areNotificationsEnabled() ? R.string.notify_allowed : R.string.notify_blocked);
        trackingOff.setVisibility(Integrations.on(store, Integrations.TRACKING) ? View.GONE : View.VISIBLE);
        for (MaterialSwitch s : notifySwitches) {
            boolean on = Notify.on(store, (String) s.getTag());
            if (s.isChecked() != on) s.setChecked(on);
        }
    }

    @Override
    protected void refresh() {
        if (pendingSection >= 0 && tabs != null) {
            TabLayout.Tab tab = tabs.getTabAt(pendingSection);
            pendingSection = -1;
            if (tab != null) tab.select();
        }
        Actions.bindTarget(target, true);
        if (helperDetail != null) helperDetail.setText(FlagSync.status(requireContext()));
        bindNotifications();
        bindUpdate();
    }

    private void buildUpdates(LinearLayout about) {
        updateRow = Row.inflate(about);
        updateRow.chevron.setVisibility(View.VISIBLE);
        updateRow.view.setOnClickListener(x -> onUpdateRow());
        about.addView(updateRow.view);
        MaterialSwitch auto = new MaterialSwitch(requireContext());
        auto.setChecked(Updater.autoOn(store));
        auto.setContentDescription(getString(R.string.update_auto));
        SettingRows.row(about, getString(R.string.update_auto), getString(R.string.update_auto_body), auto).setOnClickListener(x -> auto.toggle());
        auto.setOnCheckedChangeListener((b, on) -> {
            if (Updater.autoOn(store) == on) return;
            Updater.setAuto(requireContext(), on);
            if (on) Updater.auto(requireContext());
        });
        bindUpdate();
    }

    private void onUpdateRow() {
        Updater.State s = Updater.state();
        if (s == Updater.State.CHECKING || s == Updater.State.DOWNLOADING || s == Updater.State.INSTALLING) return;
        if (Updater.available() != null) Updater.start(host());
        else Updater.check(requireContext(), true);
    }

    private void bindUpdate() {
        if (updateRow == null || !isAdded()) return;
        Context c = requireContext();
        Updater.State s = Updater.state();
        Updater.Release r = Updater.available();
        String title;
        String detail;
        if (s == Updater.State.CHECKING) {
            title = getString(R.string.update_checking);
            detail = Updater.channel(c);
        } else if (s == Updater.State.DOWNLOADING) {
            title = getString(R.string.update_downloading, Updater.progress());
            detail = r == null ? Updater.channel(c) : r.version;
        } else if (s == Updater.State.INSTALLING) {
            title = getString(R.string.update_installing);
            detail = r == null ? Updater.channel(c) : r.version;
        } else if (s == Updater.State.READY) {
            title = getString(R.string.update_restart);
            detail = r == null ? Updater.channel(c) : r.version;
        } else if (r != null) {
            title = getString(R.string.update_ready_title, r.version);
            detail = getString(R.string.update_ready_body, Updater.sizeText(c, r.size), Updater.channel(c));
        } else if (s == Updater.State.FAILED) {
            title = getString(R.string.update_check);
            detail = getString(R.string.update_failed, Updater.problem());
        } else {
            title = getString(R.string.update_check);
            detail = lastChecked(c);
        }
        updateRow.set(R.drawable.ic_arrow_sync, title, detail);
    }

    private String lastChecked(Context c) {
        long at = 0;
        try {
            at = Long.parseLong(store.setting(Updater.CHECKED_AT, "0"));
        } catch (NumberFormatException ignored) {
        }
        if (Updater.state() == Updater.State.UP_TO_DATE) return getString(R.string.update_current, Updater.installedVersion());
        if (at <= 0) return getString(R.string.update_never_checked, Updater.installedVersion());
        return getString(R.string.update_checked_ago, Updater.installedVersion(), Ui.ago(c, at));
    }

    private String diagnostics() {
        Context c = requireContext();
        StringBuilder sb = new StringBuilder();
        sb.append("Voidstrap for Android ").append(BuildConfig.VERSION_NAME).append(" (").append(BuildConfig.VERSION_CODE).append(") ")
                .append(BuildConfig.FLAVOR).append(' ').append(BuildConfig.BUILD_TYPE).append('\n');
        sb.append("Android ").append(Build.VERSION.RELEASE).append(" API ").append(Build.VERSION.SDK_INT).append('\n');
        sb.append("Device ").append(Build.MANUFACTURER).append(' ').append(Build.MODEL).append('\n');
        sb.append("ABIs ").append(String.join(", ", Build.SUPPORTED_ABIS)).append('\n');
        ActivityManager am = (ActivityManager) c.getSystemService(Context.ACTIVITY_SERVICE);
        sb.append("Low RAM ").append(am != null && am.isLowRamDevice()).append('\n');
        sb.append("Screen ").append(getResources().getConfiguration().screenWidthDp).append('x').append(getResources().getConfiguration().screenHeightDp).append(" dp\n");
        sb.append("Locale ").append(Locale.getDefault().toLanguageTag()).append('\n');
        sb.append("Selected target ").append(Targets.selected(c)).append('\n');
        for (String p : Targets.ALL) {
            Targets.State s = Targets.resolve(c, p);
            sb.append("  ").append(p).append(": ");
            if (!s.installed) sb.append("not installed");
            else sb.append(s.versionName).append(" (").append(s.versionCode).append(")").append(s.enabled ? "" : " disabled").append(s.launchable ? "" : " no launcher");
            sb.append('\n');
        }
        sb.append("Helper uid ").append(Helper.uidNow()).append(", USB debugging ").append(android.provider.Settings.Global.getInt(c.getContentResolver(), android.provider.Settings.Global.ADB_ENABLED, 0) == 1).append('\n');
        sb.append("Root ").append(FlagWriter.rootAvailable()).append(", use root ").append(FlagWriter.rootEnabled(c)).append(", flag mode ").append(FlagWriter.mode(c)).append('\n');
        java.io.File flagFile = new java.io.File(FlagWriter.PATH);
        sb.append("Flags file ").append(flagFile.exists() ? flagFile.length() + " bytes, readable " + flagFile.canRead() + ", matches profile " + FlagWriter.readable(store.flags.active().valuesJson().toString()) : "absent").append('\n');
        android.hardware.display.DisplayManager dm = c.getSystemService(android.hardware.display.DisplayManager.class);
        android.view.Display disp = dm == null ? null : dm.getDisplay(android.view.Display.DEFAULT_DISPLAY);
        sb.append("Display refresh ").append(disp == null ? 0 : disp.getRefreshRate()).append(" Hz, max ").append(FlagWriter.maxRefresh(c)).append(" Hz\n");
        sb.append("Pinned games ").append(store.library.size()).append('\n');
        sb.append("Flag profiles ").append(store.flags.profiles.size()).append(", active entries ").append(store.flags.active().values.size()).append('\n');
        sb.append("Workspace items ").append(Mods.count(Mods.root(c))).append(", My Mods ").append(ManagedMods.load(c).size()).append('\n');
        String target = Targets.selected(c);
        java.io.File base = ModEngine.baseApk(c, target);
        sb.append("Mods ").append(ModEngine.enabled(c) ? "on" : "off").append(", loaded ").append(base != null && ModEngine.mountedFor(target, base.getAbsolutePath())).append(", files ").append(ModEngine.collect(c).size()).append(", root mode ").append(FlagWriter.rootMode(c)).append('\n');
        if (!store.history.isEmpty()) {
            Store.Launch l = store.history.get(0);
            sb.append("Last launch ").append(l.result).append(" via ").append(l.target).append(' ').append(Ui.ago(c, l.time)).append('\n');
        }
        sb.append("Updates ").append(Updater.autoOn(store) ? "automatic" : "manual").append(" via ").append(Updater.channel(c))
                .append(", state ").append(Updater.state());
        Updater.Release pending = Updater.available();
        if (pending != null) sb.append(", offered ").append(pending.version);
        if (!Updater.problem().isEmpty()) sb.append(", last problem ").append(Updater.problem());
        sb.append('\n');
        sb.append("Mod activity\n").append(ModLog.dump());
        return sb.toString();
    }

    private void showDiagnostics() {
        String report = diagnostics();
        LinearLayout box = new LinearLayout(requireContext());
        box.setOrientation(LinearLayout.VERTICAL);
        TextView t = new TextView(requireContext());
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Mono);
        t.setTextIsSelectable(true);
        t.setText(report);
        box.addView(t);
        TextView note = new TextView(requireContext());
        note.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
        note.setText(R.string.diagnostics_note);
        note.setPadding(0, Ui.dp(requireContext(), 12), 0, 0);
        box.addView(note);
        com.google.android.material.button.MaterialButton copy = new com.google.android.material.button.MaterialButton(requireContext(), null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
        copy.setText(R.string.common_copy);
        copy.setIconResource(R.drawable.ic_copy);
        copy.setOnClickListener(x -> {
            Ui.copy(requireContext(), getString(R.string.settings_diagnostics), report);
            Notify.say(host(), Notify.COPY, R.string.diagnostics_copied);
        });
        com.google.android.material.button.MaterialButton share = new com.google.android.material.button.MaterialButton(requireContext(), null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
        share.setText(R.string.common_share);
        share.setIconResource(R.drawable.ic_share);
        share.setOnClickListener(x -> startActivity(Intent.createChooser(new Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, report), getString(R.string.common_share))));
        LinearLayout buttons = new LinearLayout(requireContext());
        buttons.setPadding(0, Ui.dp(requireContext(), 12), 0, 0);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMarginEnd(Ui.dp(requireContext(), 8));
        buttons.addView(copy, lp);
        buttons.addView(share);
        box.addView(buttons);
        Ui.surface(host(), R.string.settings_diagnostics, R.string.diagnostics_intro, box).show();
    }

    private void showLicenses() {
        TextView t = new TextView(requireContext());
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        t.setTextIsSelectable(true);
        t.setText(R.string.licenses_text);
        Ui.surface(host(), R.string.settings_licenses, 0, t).show();
    }

    private void onExport(Uri uri) {
        if (uri == null) return;
        String json;
        try {
            json = store.backup().toString(2);
        } catch (JSONException e) {
            Ui.say(host(), R.string.export_failed);
            return;
        }
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            int error = Ui.writeText(app, uri, json);
            store.main.post(() -> {
                if (isAdded()) Notify.say(host(), error == 0 ? Notify.GENERAL : null, error == 0 ? R.string.backup_exported : error);
            });
        });
    }

    private void onImport(Uri uri) {
        if (uri == null) return;
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            JSONObject parsed = null;
            try (InputStream in = app.getContentResolver().openInputStream(uri)) {
                if (in == null) throw new IOException("stream");
                parsed = new JSONObject(Flags.readBounded(in));
                if (!"voidstrap.android.backup".equals(parsed.optString("format"))) parsed = null;
            } catch (IOException | JSONException | Flags.FormatException | RuntimeException ignored) {
            }
            JSONObject backup = parsed;
            store.main.post(() -> {
                if (!isAdded()) return;
                if (backup == null) {
                    Ui.alert(requireContext()).setTitle(R.string.backup_invalid_title).setMessage(R.string.backup_invalid_body).setPositiveButton(R.string.common_ok, null).show();
                    return;
                }
                int games = backup.optJSONArray("library") == null ? 0 : backup.optJSONArray("library").length();
                JSONObject flags = backup.optJSONObject("flags");
                int profiles = flags == null || flags.optJSONArray("profiles") == null ? 0 : flags.optJSONArray("profiles").length();
                Ui.alert(requireContext())
                        .setTitle(R.string.backup_import_title)
                        .setMessage(getString(R.string.backup_import_body, games, profiles))
                        .setPositiveButton(R.string.common_import, (d, w) -> {
                            try {
                                store.restore(backup);
                                Notify.say(host(), Notify.GENERAL, R.string.backup_imported);
                                ThemeFade.restart(requireActivity(), R.id.nav_settings);
                            } catch (JSONException e) {
                                Ui.say(host(), R.string.backup_invalid_title);
                            }
                        })
                        .setNegativeButton(R.string.common_cancel, null)
                        .show();
            });
        });
    }
}
