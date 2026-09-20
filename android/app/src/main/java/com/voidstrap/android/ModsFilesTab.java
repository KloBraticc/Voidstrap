package com.voidstrap.android;

import android.app.Dialog;
import android.content.Context;
import android.media.MediaPlayer;
import android.net.Uri;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AlertDialog;
import androidx.recyclerview.widget.DiffUtil;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.ListAdapter;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;

import java.io.File;
import java.io.IOException;
import java.text.DateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

final class ModsFilesTab {
    private static final String STATE_DIR = "filesDir";
    private static final String STATE_ROOT = "filesRoot";
    private static final String STATE_ROOT_NAME = "filesRootName";
    private static final String STATE_SELECTED = "filesSelected";

    private final ModsFragment host;
    private final Context c;
    private File workspace;
    private File root;
    private String rootName;
    private File dir;
    private String selectedPath;
    private String query = "";
    private final TextView breadcrumb;
    private final View up;
    private final TextView empty;
    private final TextView info;
    private MaterialCardView pane;
    private final FileAdapter adapter = new FileAdapter();
    private MediaPlayer player;
    private Dialog previewDialog;

    ModsFilesTab(ModsFragment host, View v, Bundle saved) {
        this.host = host;
        this.c = v.getContext();
        workspace = Mods.root(c);
        root = workspace;
        dir = workspace;
        if (saved != null) {
            String r = saved.getString(STATE_ROOT);
            if (r != null && new File(r).isDirectory() && (Mods.inside(workspace, new File(r)) || Mods.inside(ManagedMods.root(c), new File(r)))) {
                root = new File(r);
                rootName = saved.getString(STATE_ROOT_NAME);
            }
            String d = saved.getString(STATE_DIR);
            if (d != null && Mods.inside(root, new File(d)) && new File(d).isDirectory()) dir = new File(d);
            else dir = root;
            selectedPath = saved.getString(STATE_SELECTED);
        }
        pane = v.findViewById(R.id.preview_pane);
        if (pane != null && pane.getVisibility() != View.VISIBLE) pane = null;
        RecyclerView list = v.findViewById(R.id.files);
        list.setLayoutManager(new LinearLayoutManager(c));
        View header = LayoutInflater.from(c).inflate(R.layout.view_mods_files_header, list, false);
        breadcrumb = header.findViewById(R.id.breadcrumb);
        up = header.findViewById(R.id.up);
        empty = header.findViewById(R.id.files_empty);
        info = header.findViewById(R.id.files_info);
        list.setAdapter(new androidx.recyclerview.widget.ConcatAdapter(new HeaderAdapter(header), adapter));
        v = header;
        v.findViewById(R.id.import_files).setOnClickListener(x -> host.pickMany(new String[]{"*/*"}, this::onFiles));
        v.findViewById(R.id.import_pack).setOnClickListener(x -> host.pick(ModsFragment.ARCHIVE_TYPES, host::importPack));
        v.findViewById(R.id.new_folder).setOnClickListener(x -> newFolder());
        up.setOnClickListener(x -> {
            if (!dir.equals(root) && dir.getParentFile() != null) open(dir.getParentFile());
            else if (!root.equals(workspace)) showWorkspace();
        });
        TextInputEditText search = v.findViewById(R.id.search);
        search.addTextChangedListener(new LibraryFragment.Watcher(() -> {
            query = search.getText() == null ? "" : search.getText().toString().trim().toLowerCase(Locale.ROOT);
            reload();
        }));
        Mods.sweepStaging(c);
    }

    void save(Bundle out) {
        if (dir != null) out.putString(STATE_DIR, dir.getPath());
        if (root != null && !root.equals(workspace)) {
            out.putString(STATE_ROOT, root.getPath());
            out.putString(STATE_ROOT_NAME, rootName);
        }
        if (selectedPath != null) out.putString(STATE_SELECTED, selectedPath);
    }

    void stop() {
        stopAudio();
    }

    void destroy() {
        if (previewDialog != null) previewDialog.dismiss();
        stopAudio();
    }

    void showWorkspace() {
        root = workspace;
        rootName = null;
        open(workspace);
    }

    void showFolder(File folder, String name) {
        root = folder;
        rootName = name;
        open(folder);
    }

    private void open(File d) {
        dir = d;
        selectedPath = null;
        reload();
    }

    void reload() {
        if (!dir.isDirectory() || !Mods.inside(root, dir)) {
            if (!root.isDirectory()) {
                root = workspace;
                rootName = null;
            }
            dir = root;
        }
        List<File> files = new ArrayList<>();
        for (File f : Mods.list(dir)) if (query.isEmpty() || f.getName().toLowerCase(Locale.ROOT).contains(query)) files.add(f);
        adapter.submitList(files);
        String top = rootName == null ? c.getString(R.string.mods_root) : c.getString(R.string.mods_tab_library) + " / " + rootName;
        String rel = root.toURI().relativize(dir.toURI()).getPath();
        breadcrumb.setText(rel.isEmpty() ? top : top + " / " + rel.replaceAll("/$", "").replace("/", " / "));
        up.setVisibility(dir.equals(workspace) ? View.INVISIBLE : View.VISIBLE);
        info.setText(rootName == null ? R.string.mods_info : R.string.mods_info_managed);
        empty.setVisibility(files.isEmpty() ? View.VISIBLE : View.GONE);
        empty.setText(query.isEmpty() ? R.string.mods_empty : R.string.mods_no_match);
        File sel = selectedPath == null ? null : new File(selectedPath);
        if (sel != null && !sel.exists()) {
            selectedPath = null;
            sel = null;
        }
        if (pane != null) showPreview(sel);
    }

    private void onItem(File f) {
        if (f.isDirectory()) {
            open(f);
            return;
        }
        String previous = selectedPath;
        selectedPath = f.getPath();
        refreshRow(previous);
        refreshRow(selectedPath);
        if (pane != null) showPreview(f);
        else {
            View content = buildPreview(f, null);
            previewDialog = Ui.surface(host.host(), R.string.mods_preview_title, 0, content, this::stopAudio);
            previewDialog.show();
        }
    }

    private void refreshRow(String path) {
        if (path == null) return;
        List<File> files = adapter.getCurrentList();
        for (int i = 0; i < files.size(); i++) {
            if (files.get(i).getPath().equals(path)) {
                adapter.notifyItemChanged(i);
                return;
            }
        }
    }

    private void showPreview(File f) {
        pane.removeAllViews();
        boolean has = f != null && f.isFile();
        pane.setVisibility(has ? View.VISIBLE : View.GONE);
        if (has) pane.addView(buildPreview(f, pane));
    }

    private View buildPreview(File f, ViewGroup parent) {
        View v = LayoutInflater.from(c).inflate(R.layout.view_mod_preview, parent, false);
        ImageView image = v.findViewById(R.id.preview_image);
        if (Mods.isImage(f)) {
            image.setImageResource(R.drawable.ic_image);
            Net.image(image, "file:" + f.getPath() + "#" + f.lastModified(), R.drawable.ic_image, Ui.dp(c, 512));
        } else {
            image.setImageResource(Mods.isAudio(f) ? R.drawable.ic_music_note_2 : R.drawable.ic_document);
            image.setScaleType(ImageView.ScaleType.CENTER);
            image.setMinimumHeight(Ui.dp(c, 120));
        }
        ((TextView) v.findViewById(R.id.preview_name)).setText(f.getName());
        String type = Mods.extension(f.getName()).toUpperCase(Locale.ROOT);
        String detail = c.getString(R.string.mods_file_detail, type.isEmpty() ? c.getString(R.string.mods_type_file) : type, Ui.size(c, f.length()), DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(new Date(f.lastModified())));
        ((TextView) v.findViewById(R.id.preview_detail)).setText(detail);
        ((TextView) v.findViewById(R.id.info_text)).setText(status(f));
        MaterialButton play = v.findViewById(R.id.preview_play);
        play.setVisibility(Mods.isAudio(f) ? View.VISIBLE : View.GONE);
        play.setOnClickListener(x -> toggleAudio(f, play));
        v.findViewById(R.id.preview_export).setOnClickListener(x -> export(f));
        v.findViewById(R.id.preview_rename).setOnClickListener(x -> rename(f));
        v.findViewById(R.id.preview_remove).setOnClickListener(x -> remove(f));
        return v;
    }

    private CharSequence status(File f) {
        String rel = root.toURI().relativize(f.toURI()).getPath();
        if (ModEngine.ignored(rel)) return c.getString(R.string.mods_file_ignored);
        String asset = ModEngine.assetPath(rel);
        if (asset == null) return c.getString(R.string.mods_file_unplaced);
        return c.getString(R.string.mods_file_applies, asset.substring(7));
    }

    private void toggleAudio(File f, MaterialButton button) {
        if (player != null) {
            stopAudio();
            button.setText(R.string.mods_play);
            button.setIconResource(R.drawable.ic_play);
            return;
        }
        try {
            player = new MediaPlayer();
            player.setAudioAttributes(new android.media.AudioAttributes.Builder()
                    .setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                    .setContentType(android.media.AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build());
            player.setDataSource(f.getPath());
            player.setOnCompletionListener(mp -> {
                stopAudio();
                button.setText(R.string.mods_play);
                button.setIconResource(R.drawable.ic_play);
            });
            player.prepare();
            player.start();
            button.setText(R.string.mods_stop);
            button.setIconResource(R.drawable.ic_pause);
        } catch (IOException | RuntimeException e) {
            stopAudio();
            Ui.say(host.host(), R.string.mods_audio_failed);
        }
    }

    private void stopAudio() {
        if (player == null) return;
        try {
            player.release();
        } catch (RuntimeException ignored) {
        }
        player = null;
    }

    private Flyout menu(File f) {
        Flyout m = new Flyout(c);
        if (f.isDirectory()) m.add(R.drawable.ic_folder, R.string.common_open, () -> open(f));
        return m.add(R.drawable.ic_arrow_download, R.string.common_export, () -> export(f))
                .add(R.drawable.ic_rename, R.string.common_rename, () -> rename(f))
                .separator()
                .add(R.drawable.ic_delete, R.string.common_remove, () -> remove(f));
    }

    private void export(File f) {
        String mime;
        String name;
        if (f.isDirectory()) {
            mime = "application/zip";
            name = f.getName() + ".zip";
        } else {
            String m = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(Mods.extension(f.getName()));
            mime = m != null ? m : "application/octet-stream";
            name = f.getName();
        }
        host.save(name, mime, uri -> {
            Context app = c.getApplicationContext();
            host.store().work.execute(() -> {
                int error = 0;
                try {
                    Mods.export(app, f, uri);
                } catch (IOException | RuntimeException e) {
                    error = Ui.writeFailure(uri, e);
                }
                int result = error;
                host.store().main.post(() -> {
                    if (host.isAdded()) Ui.say(host.host(), result == 0 ? c.getString(R.string.mods_exported, f.getName()) : c.getString(result));
                });
            });
        });
    }

    private void rename(File f) {
        View content = LayoutInflater.from(c).inflate(R.layout.dialog_input, null, false);
        TextInputLayout layout = content.findViewById(R.id.input_layout);
        TextInputEditText input = content.findViewById(R.id.input);
        layout.setHint(R.string.mods_name_hint);
        input.setText(f.getName());
        Ui.clearErrorOnEdit(layout, input);
        AlertDialog d = new MaterialAlertDialogBuilder(c)
                .setTitle(R.string.common_rename)
                .setView(content)
                .setPositiveButton(R.string.common_save, null)
                .setNegativeButton(R.string.common_cancel, null)
                .show();
        Ui.focus(d, input);
        int dot = f.isDirectory() ? -1 : f.getName().lastIndexOf('.');
        input.setSelection(0, dot > 0 ? dot : f.getName().length());
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String name = Mods.cleanName(input.getText() == null ? "" : input.getText().toString());
            if (name == null) {
                layout.setError(c.getString(R.string.mods_error_name));
                return;
            }
            if (!f.isDirectory() && Mods.blocked(name)) {
                layout.setError(c.getString(R.string.mods_error_blocked_name));
                return;
            }
            File target = new File(f.getParentFile(), name);
            if (target.exists() && !target.getName().equalsIgnoreCase(f.getName())) {
                layout.setError(c.getString(R.string.mods_error_exists));
                return;
            }
            if (!f.renameTo(target)) {
                layout.setError(c.getString(R.string.mods_error_rename));
                return;
            }
            if (f.getPath().equals(selectedPath)) selectedPath = target.getPath();
            d.dismiss();
            if (previewDialog != null) previewDialog.dismiss();
            host.changed();
        });
    }

    private void remove(File f) {
        String body = f.isDirectory() && Mods.count(f) > 0
                ? c.getResources().getQuantityString(R.plurals.mods_remove_folder, Mods.count(f), f.getName(), Mods.count(f))
                : c.getString(R.string.mods_remove_file, f.getName());
        new MaterialAlertDialogBuilder(c)
                .setTitle(R.string.mods_remove_title)
                .setMessage(body)
                .setPositiveButton(R.string.common_remove, (dlg, w) -> {
                    stopAudio();
                    boolean ok = Mods.delete(f);
                    if (previewDialog != null) previewDialog.dismiss();
                    if (f.getPath().equals(selectedPath)) selectedPath = null;
                    Ui.say(host.host(), ok ? c.getString(R.string.mods_removed, f.getName()) : c.getString(R.string.mods_remove_failed));
                    host.changed();
                })
                .setNegativeButton(R.string.common_cancel, null)
                .show();
    }

    private void newFolder() {
        View content = LayoutInflater.from(c).inflate(R.layout.dialog_input, null, false);
        TextInputLayout layout = content.findViewById(R.id.input_layout);
        TextInputEditText input = content.findViewById(R.id.input);
        layout.setHint(R.string.mods_name_hint);
        Ui.clearErrorOnEdit(layout, input);
        AlertDialog d = new MaterialAlertDialogBuilder(c)
                .setTitle(R.string.mods_new_folder)
                .setView(content)
                .setPositiveButton(R.string.common_create, null)
                .setNegativeButton(R.string.common_cancel, null)
                .show();
        Ui.focus(d, input);
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String name = Mods.cleanName(input.getText() == null ? "" : input.getText().toString());
            if (name == null) {
                layout.setError(c.getString(R.string.mods_error_name));
                return;
            }
            File target = new File(dir, name);
            if (target.exists()) {
                layout.setError(c.getString(R.string.mods_error_exists));
                return;
            }
            if (!target.mkdirs()) {
                layout.setError(c.getString(R.string.mods_error_rename));
                return;
            }
            d.dismiss();
            reload();
        });
    }

    private void onFiles(List<Uri> uris) {
        if (uris == null || uris.isEmpty()) return;
        File target = dir;
        host.runTask(R.string.mods_importing, (app, cancel) -> {
            int ok = 0;
            int failed = 0;
            int reason = 0;
            for (Uri u : uris) {
                if (cancel.get()) break;
                try {
                    Mods.importFile(app, u, target, cancel);
                    ok++;
                } catch (Mods.ImportException e) {
                    failed++;
                    reason = e.reason;
                } catch (IOException | RuntimeException e) {
                    failed++;
                    reason = R.string.mods_error_read;
                }
            }
            if (failed == 0) return app.getResources().getQuantityString(R.plurals.mods_imported, ok, ok);
            return app.getString(R.string.mods_import_partial, ok, failed, app.getString(reason));
        });
    }

    private final class FileAdapter extends ListAdapter<File, FileAdapter.Holder> {
        FileAdapter() {
            super(new DiffUtil.ItemCallback<File>() {
                @Override
                public boolean areItemsTheSame(@NonNull File a, @NonNull File b) {
                    return a.getPath().equals(b.getPath());
                }

                @Override
                public boolean areContentsTheSame(@NonNull File a, @NonNull File b) {
                    return a.lastModified() == b.lastModified() && a.length() == b.length();
                }
            });
        }

        @NonNull
        @Override
        public Holder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            return new Holder(Row.inflate(parent));
        }

        @Override
        public void onBindViewHolder(@NonNull Holder h, int position) {
            File f = getItem(position);
            Row r = h.row;
            if (f.isDirectory()) {
                int n = Mods.list(f).size();
                r.set(R.drawable.ic_folder, f.getName(), c.getResources().getQuantityString(R.plurals.mods_items, n, n));
            } else {
                int icon = Mods.isImage(f) ? R.drawable.ic_image : Mods.isAudio(f) ? R.drawable.ic_music_note_2 : R.drawable.ic_document;
                r.set(icon, f.getName(), c.getString(R.string.pair, Ui.size(c, f.length()), Ui.ago(c, f.lastModified())));
                if (Mods.isImage(f)) r.showImage("file:" + f.getPath() + "#" + f.lastModified(), R.drawable.ic_image);
            }
            r.chevron.setVisibility(f.isDirectory() ? View.VISIBLE : View.GONE);
            r.more.setVisibility(View.VISIBLE);
            r.more.setContentDescription(c.getString(R.string.mods_actions_for, f.getName()));
            r.more.setOnClickListener(x -> menu(f).show(x));
            r.view.setActivated(f.getPath().equals(selectedPath));
            r.view.setOnClickListener(x -> onItem(f));
            Flyout.bind(r.view, () -> menu(f));
        }

        final class Holder extends RecyclerView.ViewHolder {
            final Row row;

            Holder(Row row) {
                super(row.view);
                this.row = row;
            }
        }
    }
}
