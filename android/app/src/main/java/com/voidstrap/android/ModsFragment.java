package com.voidstrap.android;

import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.widget.ImageView;
import android.widget.TextView;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AlertDialog;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.tabs.TabLayout;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

public final class ModsFragment extends Page {
    static final String[] ARCHIVE_TYPES = {"application/zip", "application/x-zip-compressed", "application/x-rar-compressed", "application/vnd.rar", "application/rar", "application/x-rar", "application/x-7z-compressed", "application/octet-stream"};
    static final int TAB_CUSTOMIZE = 0;
    static final int TAB_PACKS = 1;
    static final int TAB_LIBRARY = 2;
    static final int TAB_FILES = 3;
    private static final String STATE_TAB = "modsTab";
    private static final String GAMEBANANA = "https://gamebanana.com/games/2879";

    interface Job {
        String run(Context app, AtomicBoolean cancel) throws IOException;
    }

    private int tab;
    private View[] pages;
    private TabLayout tabs;
    private ModsFilesTab files;
    private ModsLibraryTab library;
    private ModsCustomizeTab customize;
    private ModsPacksTab packs;
    private TextView statusText;
    private ImageView statusIcon;
    private MaterialSwitch enabled;
    private MaterialButton apply;
    private MaterialButton root;
    private MaterialButton remove;
    private int statusGeneration;
    private boolean applying;

    private Consumer<Uri> onPick;
    private Consumer<List<Uri>> onPickMany;
    private Consumer<Uri> onSave;
    private String saveMime = "application/octet-stream";
    private ActivityResultLauncher<String[]> pickOne;
    private ActivityResultLauncher<String[]> pickMany;
    private ActivityResultLauncher<String> saveOne;

    public ModsFragment() {
        super(R.layout.fragment_mods);
    }

    @Override
    public void onCreate(@Nullable Bundle saved) {
        super.onCreate(saved);
        pickOne = registerForActivityResult(new ActivityResultContracts.OpenDocument(), uri -> {
            Consumer<Uri> cb = onPick;
            onPick = null;
            if (cb != null && uri != null) cb.accept(uri);
        });
        pickMany = registerForActivityResult(new ActivityResultContracts.OpenMultipleDocuments(), uris -> {
            Consumer<List<Uri>> cb = onPickMany;
            onPickMany = null;
            if (cb != null && uris != null && !uris.isEmpty()) cb.accept(uris);
        });
        saveOne = registerForActivityResult(new ActivityResultContracts.CreateDocument("application/octet-stream") {
            @NonNull
            @Override
            public Intent createIntent(@NonNull Context context, @NonNull String input) {
                return super.createIntent(context, input).setType(saveMime);
            }
        }, uri -> {
            Consumer<Uri> cb = onSave;
            onSave = null;
            if (cb != null && uri != null) cb.accept(uri);
        });
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        statusText = v.findViewById(R.id.mods_status_text);
        statusIcon = v.findViewById(R.id.mods_status_icon);
        enabled = v.findViewById(R.id.mods_enabled);
        apply = v.findViewById(R.id.mods_apply);
        root = v.findViewById(R.id.mods_root);
        remove = v.findViewById(R.id.mods_remove);
        enabled.setChecked(ModEngine.enabled(requireContext()));
        enabled.setOnCheckedChangeListener((b, on) -> {
            ModEngine.setEnabled(requireContext(), on);
            refreshStatus();
        });
        v.findViewById(R.id.mods_status_row).setOnClickListener(x -> enabled.toggle());
        apply.setOnClickListener(x -> applyNow());
        root.setOnClickListener(x -> useRoot());
        remove.setOnClickListener(x -> restore());
        v.findViewById(R.id.mods_gamebanana).setOnClickListener(x -> Ui.openWeb(requireContext(), GAMEBANANA));
        pages = new View[]{v.findViewById(R.id.tab_customize), v.findViewById(R.id.tab_packs), v.findViewById(R.id.tab_library), v.findViewById(R.id.tab_files)};
        customize = new ModsCustomizeTab(this, v);
        packs = new ModsPacksTab(this, pages[TAB_PACKS]);
        library = new ModsLibraryTab(this, pages[TAB_LIBRARY]);
        files = new ModsFilesTab(this, pages[TAB_FILES], saved);
        tabs = v.findViewById(R.id.mods_tabs);
        int[] labels = {R.string.mods_tab_customize, R.string.mods_tab_packs, R.string.mods_tab_library, R.string.mods_tab_files};
        int[] icons = {R.drawable.ic_wrench_screwdriver, R.drawable.ic_arrow_download, R.drawable.ic_apps, R.drawable.ic_folder};
        for (int i = 0; i < labels.length; i++) tabs.addTab(tabs.newTab().setText(labels[i]).setIcon(icons[i]));
        tab = saved == null ? TAB_CUSTOMIZE : saved.getInt(STATE_TAB, TAB_CUSTOMIZE);
        TabLayout.Tab initial = tabs.getTabAt(tab);
        if (initial != null) initial.select();
        showTab(tab);
        tabs.addOnTabSelectedListener(new TabLayout.OnTabSelectedListener() {
            @Override
            public void onTabSelected(TabLayout.Tab t) {
                showTab(t.getPosition());
            }

            @Override
            public void onTabUnselected(TabLayout.Tab t) {
            }

            @Override
            public void onTabReselected(TabLayout.Tab t) {
            }
        });
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> ModMigration.run(app));
    }

    @Override
    public void onSaveInstanceState(@NonNull Bundle out) {
        super.onSaveInstanceState(out);
        out.putInt(STATE_TAB, tab);
        if (files != null) files.save(out);
    }

    @Override
    public void onStop() {
        if (files != null) files.stop();
        super.onStop();
    }

    @Override
    public void onDestroyView() {
        if (files != null) files.destroy();
        if (packs != null) packs.destroy();
        super.onDestroyView();
    }

    @Override
    protected void refresh() {
        refreshStatus();
        refreshTab();
    }

    Store store() {
        return store;
    }

    void changed() {
        if (!isAdded() || getView() == null) return;
        refresh();
    }

    private void showTab(int index) {
        tab = index;
        for (int i = 0; i < pages.length; i++) pages[i].setVisibility(i == index ? View.VISIBLE : View.GONE);
        refreshTab();
        publishPresence();
    }

    void publishPresence() {
        TabLayout.Tab t = tabs == null ? null : tabs.getTabAt(tab);
        String name = t == null || t.getText() == null ? "" : t.getText().toString();
        String activity;
        if (tab == TAB_CUSTOMIZE) activity = "Customizing cursors, sounds and the interface";
        else if (tab == TAB_PACKS) activity = "Browsing community mod packs";
        else if (tab == TAB_LIBRARY) activity = "Managing installed mods";
        else activity = "Cursors, sounds, overlays, skyboxes";
        AppPresence.set(R.id.nav_mods, name.isEmpty() ? "Mods" : "Mods, " + name, activity);
    }

    void selectTab(int index) {
        TabLayout.Tab t = tabs.getTabAt(index);
        if (t != null) t.select();
        else showTab(index);
    }

    private void refreshTab() {
        switch (tab) {
            case TAB_CUSTOMIZE:
                customize.reload();
                break;
            case TAB_PACKS:
                packs.refresh();
                break;
            case TAB_LIBRARY:
                library.reload();
                break;
            default:
                files.reload();
                break;
        }
    }

    void openManaged(ManagedMods.Record r) {
        files.showFolder(ManagedMods.folder(requireContext(), r.id), r.name);
        selectTab(TAB_FILES);
    }

    void pick(String[] types, Consumer<Uri> cb) {
        onPick = cb;
        pickOne.launch(types);
    }

    void pickMany(String[] types, Consumer<List<Uri>> cb) {
        onPickMany = cb;
        pickMany.launch(types);
    }

    void save(String name, String mime, Consumer<Uri> cb) {
        onSave = cb;
        saveMime = mime;
        saveOne.launch(name);
    }

    void runTask(int message, Job job) {
        Context app = requireContext().getApplicationContext();
        AtomicBoolean cancel = new AtomicBoolean();
        View content = LayoutInflater.from(requireContext()).inflate(R.layout.view_progress, null, false);
        ((TextView) content.findViewById(R.id.progress_text)).setText(message);
        AlertDialog progress = new MaterialAlertDialogBuilder(requireContext())
                .setView(content)
                .setCancelable(false)
                .setNegativeButton(R.string.common_cancel, (dlg, w) -> cancel.set(true))
                .show();
        store.work.execute(() -> {
            String result;
            try {
                result = job.run(app, cancel);
            } catch (IOException | RuntimeException e) {
                result = cancel.get() ? app.getString(R.string.mods_error_cancelled_full) : describe(app, e);
            }
            String text = result;
            store.main.post(() -> {
                if (progress.isShowing()) progress.dismiss();
                if (!isAdded()) return;
                if (text != null && !text.isEmpty()) Ui.say(host(), text);
                changed();
            });
        });
    }

    static String describe(Context c, Exception e) {
        String m = e.getMessage();
        if (e instanceof ModArchives.Rejected && m != null) return m;
        if (m != null && m.length() > 8 && Character.isUpperCase(m.charAt(0)) && m.indexOf(' ') > 0) return m;
        if (e instanceof java.net.UnknownHostException || e instanceof java.net.SocketTimeoutException || e instanceof java.net.ConnectException)
            return c.getString(R.string.mods_error_network);
        return c.getString(R.string.mods_error_generic);
    }

    void importPack(Uri uri) {
        runTask(R.string.mods_importing, (app, cancel) -> {
            String display = Mods.displayName(app.getContentResolver(), uri);
            String name = display == null ? "Mod" : display.replaceAll("(?i)\\.(zip|rar|7z)$", "");
            File tmp = new File(app.getCacheDir(), "import_" + System.nanoTime());
            try {
                try (InputStream in = app.getContentResolver().openInputStream(uri); OutputStream out = new FileOutputStream(tmp)) {
                    if (in == null) throw new IOException("stream");
                    byte[] buf = new byte[65536];
                    long total = 0;
                    int n;
                    while ((n = in.read(buf)) > 0) {
                        total += n;
                        if (total > ModArchives.MAX_PACKAGE) throw new ModArchives.Rejected(app.getString(R.string.mods_error_package_size));
                        if (cancel.get()) throw new IOException("cancelled");
                        out.write(buf, 0, n);
                    }
                }
                ManagedMods.Record r = ModArchives.installManaged(app, tmp, name, null, cancel);
                try {
                    ModVariants.capture(app, r.id, tmp, cancel);
                } catch (IOException ignored) {
                }
                store.main.post(() -> {
                    if (isAdded()) selectTab(TAB_LIBRARY);
                });
                return app.getString(R.string.mods_library_added, r.name);
            } finally {
                tmp.delete();
            }
        });
    }

    private void refreshStatus() {
        if (statusText == null) return;
        int gen = ++statusGeneration;
        Context app = requireContext().getApplicationContext();
        String pkg = Targets.selected(app);
        store.work.execute(() -> {
            ModEngine.State s = ModEngine.state(app, pkg);
            int count = ModEngine.collect(app).size();
            long at = ModEngine.appliedAt(app, pkg);
            boolean rootAvailable = FlagWriter.rootAvailable();
            boolean mounted = false;
            File base = ModEngine.baseApk(app, pkg);
            if (base != null) mounted = ModEngine.mountedFor(pkg, base.getAbsolutePath());
            boolean isMounted = mounted;
            store.main.post(() -> {
                if (gen != statusGeneration || !isAdded()) return;
                showStatus(s, count, at, rootAvailable, isMounted);
            });
        });
    }

    private void showStatus(ModEngine.State s, int count, long at, boolean rootAvailable, boolean mounted) {
        Context c = requireContext();
        enabled.setOnCheckedChangeListener(null);
        enabled.setChecked(ModEngine.enabled(c));
        enabled.setOnCheckedChangeListener((b, on) -> {
            ModEngine.setEnabled(requireContext(), on);
            refreshStatus();
        });
        int tint = R.color.vs_caution;
        int icon = R.drawable.ic_info;
        CharSequence text;
        boolean canApply = false;
        switch (s) {
            case NOT_INSTALLED:
                text = c.getString(R.string.mods_state_not_installed, c.getString(Targets.nameRes(Targets.selected(c))));
                break;
            case NO_ROOT:
                text = c.getString(rootAvailable ? R.string.mods_state_root_off : R.string.mods_state_no_root);
                break;
            case OFF:
                text = c.getString(R.string.mods_state_off);
                tint = 0;
                break;
            case EMPTY:
                text = c.getString(R.string.mods_state_empty);
                tint = 0;
                break;
            case APPLIED:
                text = c.getResources().getQuantityString(R.plurals.mods_state_applied, count, count, Ui.ago(c, at));
                tint = R.color.vs_success;
                icon = R.drawable.ic_checkmark_circle;
                break;
            default:
                canApply = true;
                text = !ModEngine.enabled(c) || count == 0
                        ? c.getString(R.string.mods_state_pending_remove)
                        : c.getResources().getQuantityString(R.plurals.mods_state_pending, count, count);
                icon = R.drawable.ic_warning;
                break;
        }
        statusText.setText(text);
        statusIcon.setImageResource(icon);
        statusIcon.setImageTintList(android.content.res.ColorStateList.valueOf(tint == 0 ? Ui.attr(c, com.google.android.material.R.attr.colorSecondary) : c.getColor(tint)));
        boolean hasRoot = FlagWriter.rootMode(c) != FlagWriter.Mode.NONE;
        apply.setVisibility(hasRoot && s != ModEngine.State.NOT_INSTALLED ? View.VISIBLE : View.GONE);
        apply.setEnabled(canApply && !applying);
        root.setVisibility(s == ModEngine.State.NO_ROOT && rootAvailable ? View.VISIBLE : View.GONE);
        remove.setVisibility(hasRoot && mounted ? View.VISIBLE : View.GONE);
        remove.setEnabled(!applying);
    }

    private void applyNow() {
        Context app = requireContext().getApplicationContext();
        String pkg = Targets.selected(app);
        AtomicBoolean cancel = new AtomicBoolean();
        View content = LayoutInflater.from(requireContext()).inflate(R.layout.view_progress, null, false);
        TextView label = content.findViewById(R.id.progress_text);
        label.setText(R.string.mods_apply_preparing);
        AlertDialog progress = new MaterialAlertDialogBuilder(requireContext())
                .setView(content)
                .setCancelable(false)
                .setNegativeButton(R.string.common_cancel, (dlg, w) -> cancel.set(true))
                .show();
        applying = true;
        apply.setEnabled(false);
        store.work.execute(() -> {
            ModEngine.Result r = ModEngine.apply(app, pkg, false, cancel, stage -> store.main.post(() -> {
                if (progress.isShowing()) label.setText(stage == 0 ? R.string.mods_apply_preparing : stage == 1 ? R.string.mods_apply_building : R.string.mods_apply_mounting);
            }));
            store.main.post(() -> {
                applying = false;
                if (progress.isShowing()) progress.dismiss();
                if (!isAdded()) return;
                Ui.say(host(), resultMessage(r));
                changed();
            });
        });
    }

    private int resultMessage(ModEngine.Result r) {
        switch (r) {
            case APPLIED:
                return R.string.mods_result_applied;
            case UNCHANGED:
                return R.string.mods_result_unchanged;
            case REMOVED:
                return R.string.mods_result_removed;
            case NO_ROOT:
                return R.string.mods_result_no_root;
            case NOT_INSTALLED:
                return R.string.launch_not_installed;
            case NO_SPACE:
                return R.string.mods_error_space_full;
            case CANCELLED:
                return R.string.mods_error_cancelled_full;
            default:
                return R.string.mods_result_failed;
        }
    }

    private void restore() {
        Context app = requireContext().getApplicationContext();
        String pkg = Targets.selected(app);
        applying = true;
        remove.setEnabled(false);
        store.work.execute(() -> {
            ModEngine.Result r = ModEngine.remove(app, pkg, false);
            store.main.post(() -> {
                applying = false;
                if (!isAdded()) return;
                Ui.say(host(), resultMessage(r));
                changed();
            });
        });
    }

    private void useRoot() {
        Context app = requireContext().getApplicationContext();
        root.setEnabled(false);
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
                root.setEnabled(true);
                Ui.say(host(), r == FlagWriter.OK ? R.string.settings_root_ready : R.string.settings_root_denied);
            });
        });
    }
}
