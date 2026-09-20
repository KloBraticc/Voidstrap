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

import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.tabs.TabLayout;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Locale;

public final class SettingsFragment extends Page {
    private static final String STATE_SECTION = "settingsSection";

    private View target;
    private TextView helperDetail;
    private View[] pages;
    private TabLayout tabs;
    private int section;

    private ActivityResultLauncher<String> exportLauncher;
    private ActivityResultLauncher<String[]> importLauncher;

    public SettingsFragment() {
        super(R.layout.fragment_settings);
    }

    @Override
    public void onCreate(@Nullable Bundle saved) {
        super.onCreate(saved);
        exportLauncher = registerForActivityResult(new ActivityResultContracts.CreateDocument("application/json"), this::onExport);
        importLauncher = registerForActivityResult(new ActivityResultContracts.OpenDocument(), this::onImport);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        pages = new View[]{v.findViewById(R.id.settings_page_appearance), v.findViewById(R.id.settings_page_roblox), v.findViewById(R.id.settings_page_backup), v.findViewById(R.id.settings_page_about)};
        tabs = v.findViewById(R.id.settings_tabs);
        int[] labels = {R.string.settings_appearance, R.string.settings_roblox, R.string.settings_backup, R.string.settings_about};
        int[] icons = {R.drawable.ic_options, R.drawable.ic_games, R.drawable.ic_folder, R.drawable.ic_info};
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
        LinearLayout appearance = v.findViewById(R.id.appearance_rows);
        String currentTheme = store.setting("theme", "system");
        SettingRows.choice(appearance, getString(R.string.settings_theme), null, getResources().getStringArray(R.array.settings_themes), VoidstrapApp.themeIndex(currentTheme), i -> {
            String value = VoidstrapApp.THEMES[i];
            if (value.equals(store.setting("theme", "system"))) return;
            store.putSetting("theme", value);
            ThemeFade.restart(requireActivity(), R.id.nav_settings);
        });
        if (!VoidstrapApp.colorTheme(currentTheme)) {
            boolean dynamic = Build.VERSION.SDK_INT >= 31;
            String[] accents = dynamic
                    ? new String[]{getString(R.string.settings_accent_system), getString(R.string.settings_accent_brand)}
                    : new String[]{getString(R.string.settings_accent_brand)};
            int accentIndex = dynamic && !"brand".equals(store.setting("accent", "system")) ? 0 : accents.length - 1;
            SettingRows.choice(appearance, getString(R.string.settings_accent), dynamic ? null : getString(R.string.settings_accent_note), accents, accentIndex, i -> {
                String value = dynamic && i == 0 ? "system" : "brand";
                if (value.equals(store.setting("accent", "system"))) return;
                store.putSetting("accent", value);
                ThemeFade.restart(requireActivity(), R.id.nav_settings);
            });
        }
        com.google.android.material.materialswitch.MaterialSwitch voidRpc = new com.google.android.material.materialswitch.MaterialSwitch(requireContext());
        voidRpc.setChecked(AppPresence.enabled(store));
        voidRpc.setContentDescription(getString(R.string.settings_void_rpc));
        SettingRows.row(appearance, getString(R.string.settings_void_rpc), getString(R.string.settings_void_rpc_body), voidRpc).setOnClickListener(x -> voidRpc.toggle());
        voidRpc.setOnCheckedChangeListener((b, on) -> store.putSetting(AppPresence.SETTING, on ? "1" : "0"));
        LinearLayout roblox = v.findViewById(R.id.roblox_rows);
        Row helper = Row.inflate(roblox);
        helper.set(R.drawable.ic_shield_checkmark, getString(R.string.settings_helper), FlagSync.status(requireContext()));
        helper.detail.setMaxLines(4);
        helper.chevron.setVisibility(View.VISIBLE);
        helper.view.setOnClickListener(x -> FlagSync.setup(host()));
        helperDetail = helper.detail;
        roblox.addView(helper.view);
        rootToggle(roblox);
        if (!Ui.wide(requireContext())) row(roblox, R.drawable.ic_add, R.string.nav_integrations, R.string.integrations_open_body, () -> ((MainActivity) host()).select(R.id.nav_integrations));
        if (!Ui.wide(requireContext())) row(roblox, R.drawable.ic_globe, R.string.matchmaker_title, R.string.matchmaker_open_body, () -> ((MainActivity) host()).select(R.id.nav_matchmaker));
        row(roblox, R.drawable.ic_history, R.string.settings_clear_history, R.string.settings_clear_history_body, () ->
                new MaterialAlertDialogBuilder(requireContext())
                        .setTitle(R.string.history_clear_title)
                        .setMessage(R.string.history_clear_body)
                        .setPositiveButton(R.string.common_clear, (d, w) -> {
                            store.clearHistory();
                            Ui.say(host(), R.string.history_cleared);
                        })
                        .setNegativeButton(R.string.common_cancel, null)
                        .show());
        v.findViewById(R.id.backup_export).setOnClickListener(x -> exportLauncher.launch("voidstrap-android-backup.json"));
        v.findViewById(R.id.backup_import).setOnClickListener(x -> importLauncher.launch(new String[]{"application/json", "text/plain", "application/octet-stream"}));
        LinearLayout about = v.findViewById(R.id.about_rows);
        row(about, R.drawable.ic_shield_checkmark, R.string.settings_compat, R.string.settings_compat_body, this::showCompatibility);
        row(about, R.drawable.ic_bug, R.string.settings_diagnostics, R.string.settings_diagnostics_body, this::showDiagnostics);
        row(about, R.drawable.ic_document, R.string.settings_licenses, R.string.settings_licenses_body, this::showLicenses);
        row(about, R.drawable.ic_globe, R.string.settings_website, R.string.settings_website_body, () -> Ui.openWeb(requireContext(), getString(R.string.url_website)));
        if (BuildConfig.DIRECT_UPDATES) {
            row(about, R.drawable.ic_arrow_sync, R.string.settings_updates, R.string.settings_updates_body, () -> Ui.openWeb(requireContext(), getString(R.string.url_releases)));
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
        Row row = Row.inflate(parent);
        row.set(icon, getString(title), getString(detail));
        row.chevron.setVisibility(View.VISIBLE);
        row.view.setOnClickListener(x -> r.run());
        parent.addView(row.view);
    }

    private void rootToggle(LinearLayout parent) {
        Context c = requireContext();
        boolean rooted = FlagWriter.rootAvailable();
        com.google.android.material.materialswitch.MaterialSwitch toggle = new com.google.android.material.materialswitch.MaterialSwitch(c);
        toggle.setChecked(rooted && FlagWriter.rootEnabled(c));
        toggle.setEnabled(rooted);
        toggle.setContentDescription(getString(R.string.settings_root));
        LinearLayout row = SettingRows.row(parent, getString(R.string.settings_root), getString(rooted ? R.string.settings_root_body : R.string.settings_root_missing), toggle);
        if (rooted) row.setOnClickListener(x -> {
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
            Context app = c.getApplicationContext();
            store.work.execute(() -> {
                int r = FlagWriter.testRoot();
                if (r == FlagWriter.OK) {
                    FlagWriter.setRootEnabled(app, true);
                    Helper.keepRootHelper(app);
                }
                store.main.post(() -> {
                    if (r != FlagWriter.OK) FlagWriter.setRootEnabled(app, false);
                    store.changed();
                    if (!isAdded()) return;
                    toggle.setEnabled(true);
                    if (r != FlagWriter.OK) toggle.setChecked(false);
                    Ui.say(host(), r == FlagWriter.OK ? R.string.settings_root_ready : R.string.settings_root_denied);
                });
            });
        });
    }

    @Override
    protected void refresh() {
        Actions.bindTarget(target, true);
        if (helperDetail != null) helperDetail.setText(FlagSync.status(requireContext()));
    }

    private void showCompatibility() {
        String[] names = getResources().getStringArray(R.array.compat_names);
        String[] states = getResources().getStringArray(R.array.compat_states);
        String[] notes = getResources().getStringArray(R.array.compat_notes);
        Context c = requireContext();
        LinearLayout list = new LinearLayout(c);
        list.setOrientation(LinearLayout.VERTICAL);
        android.app.Dialog[] sheet = new android.app.Dialog[1];
        for (int i = 0; i < names.length; i++) {
            String state = states[i];
            String note = notes[i];
            boolean setup = false;
            if (state.equals("pin")) {
                state = Shortcuts.pinSupported(c) ? "yes" : "partial";
                if (state.equals("partial")) note = getString(R.string.compat_pin_unsupported);
            } else if (state.equals("flags")) {
                FlagWriter.Mode mode = FlagWriter.mode(c);
                setup = mode != FlagWriter.Mode.HELPER && mode != FlagWriter.Mode.ROOT;
                state = setup ? "partial" : "yes";
                if (setup) note = FlagSync.status(c);
            } else if (state.equals("matchmaker")) {
                boolean ready = RobloxLogin.quick(c) != RobloxLogin.Source.NONE;
                state = ready ? "yes" : "partial";
                if (!ready) note = getString(R.string.matchmaker_login_none);
            } else if (state.equals("activity")) {
                FlagWriter.Mode mode = FlagWriter.mode(c);
                setup = mode == FlagWriter.Mode.NONE;
                state = setup ? "partial" : "yes";
                if (setup) note = FlagSync.status(c);
            } else if (state.equals("mods")) {
                boolean rooted = FlagWriter.rootMode(c) != FlagWriter.Mode.NONE;
                state = rooted ? "yes" : FlagWriter.rootAvailable() ? "partial" : "no";
                if (!rooted) note = getString(FlagWriter.rootAvailable() ? R.string.mods_state_root_off : R.string.mods_state_no_root);
            }
            Row row = Row.inflate(list);
            boolean yes = state.equals("yes");
            boolean partial = state.equals("partial");
            row.set(yes ? R.drawable.ic_checkmark_circle : partial ? R.drawable.ic_info : R.drawable.ic_dismiss_circle, names[i], note);
            row.icon.setImageTintList(android.content.res.ColorStateList.valueOf(c.getColor(yes ? R.color.vs_success : partial ? R.color.vs_caution : R.color.vs_critical)));
            row.badge.setVisibility(View.VISIBLE);
            row.badge.setText(setup ? R.string.compat_setup : yes ? R.string.compat_available : partial ? R.string.compat_partial : R.string.compat_unavailable);
            if (setup) {
                row.chevron.setVisibility(View.VISIBLE);
                row.view.setOnClickListener(x -> {
                    sheet[0].dismiss();
                    FlagSync.setup(host());
                });
            } else {
                row.view.setClickable(false);
            }
            row.detail.setMaxLines(6);
            list.addView(row.view);
        }
        sheet[0] = Ui.surface(host(), R.string.settings_compat, R.string.compat_intro, list);
        sheet[0].show();
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
        android.view.Display disp = c.getSystemService(android.hardware.display.DisplayManager.class).getDisplay(android.view.Display.DEFAULT_DISPLAY);
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
            Ui.say(host(), R.string.diagnostics_copied);
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
                if (isAdded()) Ui.say(host(), error == 0 ? R.string.backup_exported : error);
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
                    new MaterialAlertDialogBuilder(requireContext()).setTitle(R.string.backup_invalid_title).setMessage(R.string.backup_invalid_body).setPositiveButton(R.string.common_ok, null).show();
                    return;
                }
                int games = backup.optJSONArray("library") == null ? 0 : backup.optJSONArray("library").length();
                JSONObject flags = backup.optJSONObject("flags");
                int profiles = flags == null || flags.optJSONArray("profiles") == null ? 0 : flags.optJSONArray("profiles").length();
                new MaterialAlertDialogBuilder(requireContext())
                        .setTitle(R.string.backup_import_title)
                        .setMessage(getString(R.string.backup_import_body, games, profiles))
                        .setPositiveButton(R.string.common_import, (d, w) -> {
                            try {
                                store.restore(backup);
                                Ui.say(host(), R.string.backup_imported);
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
