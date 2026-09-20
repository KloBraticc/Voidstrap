package com.voidstrap.android;

import android.content.Context;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AlertDialog;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.imageview.ShapeableImageView;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

final class ModsLibraryTab {
    private final ModsFragment host;
    private final Context c;
    private final TextView summary;
    private final View header;
    private final TextView empty;
    private final Adapter adapter = new Adapter();
    private List<ManagedMods.Entry> all = new ArrayList<>();
    private final List<ManagedMods.Entry> shown = new ArrayList<>();
    private String query = "";
    private int generation;

    ModsLibraryTab(ModsFragment host, View root) {
        this.host = host;
        this.c = root.getContext();
        RecyclerView list = (RecyclerView) root;
        list.setLayoutManager(new LinearLayoutManager(c));
        View v = LayoutInflater.from(c).inflate(R.layout.view_mods_library_header, list, false);
        header = v;
        summary = v.findViewById(R.id.library_summary);
        empty = v.findViewById(R.id.library_empty);
        list.setAdapter(new androidx.recyclerview.widget.ConcatAdapter(new HeaderAdapter(v), adapter));
        v.findViewById(R.id.library_add).setOnClickListener(x -> add());
        v.findViewById(R.id.library_refresh).setOnClickListener(x -> reload());
        TextInputEditText search = v.findViewById(R.id.library_search);
        search.addTextChangedListener(new LibraryFragment.Watcher(() -> {
            query = search.getText() == null ? "" : search.getText().toString().trim().toLowerCase(Locale.ROOT);
            filter();
        }));
    }

    void reload() {
        int gen = ++generation;
        Context app = c.getApplicationContext();
        host.store().work.execute(() -> {
            List<ManagedMods.Entry> entries = ManagedMods.scan(app);
            host.store().main.post(() -> {
                if (gen != generation || !host.isAdded()) return;
                all = entries;
                filter();
            });
        });
    }

    private void filter() {
        int old = shown.size();
        shown.clear();
        for (ManagedMods.Entry e : all) {
            if (query.isEmpty() || e.record.name.toLowerCase(Locale.ROOT).contains(query) || e.record.id.startsWith(query)
                    || (e.pack != null && e.pack.author.toLowerCase(Locale.ROOT).contains(query))) shown.add(e);
        }
        Ui.replaced(adapter, old);
        int on = 0;
        for (ManagedMods.Entry e : all) if (e.record.enabled) on++;
        summary.setText(all.isEmpty() ? c.getString(R.string.mods_library_none) : c.getResources().getQuantityString(R.plurals.mods_library_summary, all.size(), all.size(), on));
        empty.setVisibility(shown.isEmpty() ? View.VISIBLE : View.GONE);
        empty.setText(all.isEmpty() ? R.string.mods_library_empty : R.string.mods_no_match);
    }

    private void add() {
        new Flyout(c)
                .add(R.drawable.ic_arrow_import, R.string.mods_library_add_archive, () -> host.pick(ModsFragment.ARCHIVE_TYPES, host::importPack))
                .add(R.drawable.ic_folder_add, R.string.mods_library_add_empty, this::addEmpty)
                .show(header.findViewById(R.id.library_add));
    }

    private void addEmpty() {
        ask(R.string.mods_library_add_empty, "", name -> {
            try {
                ManagedMods.Record r = ManagedMods.create(c, name);
                host.changed();
                host.openManaged(r);
            } catch (IOException e) {
                Ui.say(host.host(), R.string.mods_library_failed);
            }
        });
    }

    private interface NameResult {
        void on(String name);
    }

    private void ask(int title, String initial, NameResult done) {
        View content = LayoutInflater.from(c).inflate(R.layout.dialog_input, null, false);
        TextInputLayout layout = content.findViewById(R.id.input_layout);
        TextInputEditText input = content.findViewById(R.id.input);
        layout.setHint(R.string.mods_name_hint);
        input.setText(initial);
        input.setSelection(initial.length());
        Ui.clearErrorOnEdit(layout, input);
        AlertDialog d = Ui.alert(c)
                .setTitle(title)
                .setView(content)
                .setPositiveButton(R.string.common_save, null)
                .setNegativeButton(R.string.common_cancel, null)
                .show();
        Ui.focus(d, input);
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String name = ManagedMods.normalizeName(input.getText() == null ? "" : input.getText().toString(), null);
            if (name == null) {
                layout.setError(c.getString(R.string.mods_library_name_required));
                return;
            }
            d.dismiss();
            done.on(name);
        });
    }

    private void toggle(ManagedMods.Entry e, boolean on) {
        try {
            ManagedMods.setEnabled(c, e.record.id, on);
            e.record.enabled = on;
        } catch (IOException ex) {
            Ui.say(host.host(), R.string.mods_library_failed);
        }
        host.changed();
    }

    private Flyout menu(ManagedMods.Entry e, int position) {
        Flyout m = new Flyout(c);
        if (e.pack != null && !e.pack.profileUrl.isEmpty()) m.add(R.drawable.ic_globe, R.string.mods_library_page, () -> Ui.openWeb(c, e.pack.profileUrl));
        m.add(R.drawable.ic_folder, R.string.mods_library_open, () -> host.openManaged(e.record));
        m.add(R.drawable.ic_options, R.string.mods_library_options, () -> options(e));
        m.add(R.drawable.ic_rename, R.string.common_rename, () -> ask(R.string.common_rename, e.record.name, name -> {
            try {
                ManagedMods.rename(c, e.record.id, name);
            } catch (IOException ex) {
                Ui.say(host.host(), R.string.mods_library_failed);
            }
            host.changed();
        }));
        m.separator();
        if (position > 0) {
            m.add(R.drawable.ic_chevron_up, R.string.mods_library_top, () -> move(e, 0, true));
            m.add(R.drawable.ic_chevron_up, R.string.mods_library_up, () -> move(e, -1, false));
        }
        if (position < all.size() - 1) {
            m.add(R.drawable.ic_chevron_down, R.string.mods_library_down, () -> move(e, 1, false));
            m.add(R.drawable.ic_chevron_down, R.string.mods_library_bottom, () -> move(e, all.size(), true));
        }
        m.add(R.drawable.ic_copy, R.string.mods_library_copy_id, () -> {
            Ui.copy(c, c.getString(R.string.mods_library_copy_id), e.record.id);
            Ui.say(host.host(), R.string.mods_library_id_copied);
        });
        m.separator();
        m.add(R.drawable.ic_delete, R.string.common_remove, () -> remove(e));
        return m;
    }

    private void move(ManagedMods.Entry e, int value, boolean absolute) {
        try {
            if (absolute) ManagedMods.moveTo(c, e.record.id, value);
            else ManagedMods.move(c, e.record.id, value);
        } catch (IOException ex) {
            Ui.say(host.host(), R.string.mods_library_failed);
        }
        host.changed();
    }

    private void remove(ManagedMods.Entry e) {
        Ui.alert(c)
                .setTitle(R.string.mods_library_remove_title)
                .setMessage(c.getString(R.string.mods_library_remove_body, e.record.name))
                .setPositiveButton(R.string.common_remove, (d, w) -> {
                    try {
                        ManagedMods.delete(c, e.record.id);
                        ModVariants.delete(c, e.record.id);
                        Ui.say(host.host(), c.getString(R.string.mods_removed, e.record.name));
                    } catch (IOException ex) {
                        Ui.say(host.host(), R.string.mods_remove_failed);
                    }
                    host.changed();
                })
                .setNegativeButton(R.string.common_cancel, null)
                .show();
    }

    private void options(ManagedMods.Entry e) {
        Context app = c.getApplicationContext();
        host.store().work.execute(() -> {
            List<ModVariants.Slot> slots = ModVariants.build(app, e.record.id);
            host.store().main.post(() -> {
                if (!host.isAdded()) return;
                if (slots.isEmpty()) {
                    Ui.say(host.host(), R.string.mods_options_none);
                    return;
                }
                showOptions(e, slots);
            });
        });
    }

    private void showOptions(ManagedMods.Entry e, List<ModVariants.Slot> slots) {
        LinearLayout box = new LinearLayout(c);
        box.setOrientation(LinearLayout.VERTICAL);
        int thumb = Ui.dp(c, 40);
        for (ModVariants.Slot s : slots) {
            CharSequence[] labels = new CharSequence[s.options.size()];
            for (int i = 0; i < labels.length; i++) {
                ModVariants.Option o = s.options.get(i);
                labels[i] = o.label == null ? c.getString(R.string.mods_options_off) : o.label.isEmpty() ? c.getString(R.string.mods_options_installed) : o.label;
            }
            ShapeableImageView preview = new ShapeableImageView(c);
            preview.setScaleType(ImageView.ScaleType.FIT_CENTER);
            preview.setBackgroundResource(R.drawable.vs_icon_tile);
            Runnable showPreview = () -> {
                ModVariants.Option o = s.options.get(s.selected);
                if (o.source != null && ModVariants.PREVIEW_EXTENSIONS.contains(Mods.extension(o.source.getName())))
                    Net.image(preview, "file:" + o.source.getPath() + "#" + o.source.lastModified(), R.drawable.ic_image, thumb);
                else preview.setImageResource(o.source == null ? R.drawable.ic_dismiss_circle : R.drawable.ic_document);
            };
            showPreview.run();
            String name = s.target.substring(s.target.lastIndexOf('/') + 1);
            LinearLayout row = SettingRows.row(box, name, s.target, new Dropdown(c, labels, s.selected, i -> {
                s.selected = i;
                showPreview.run();
            }).view);
            row.addView(preview, 0, new LinearLayout.LayoutParams(thumb, thumb));
            ((LinearLayout.LayoutParams) row.getChildAt(1).getLayoutParams()).setMarginStart(Ui.dp(c, 12));
        }
        com.google.android.material.button.MaterialButton save = new com.google.android.material.button.MaterialButton(c);
        save.setText(R.string.mods_options_save);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Ui.dp(c, 12);
        box.addView(save, lp);
        android.app.Dialog d = Ui.surface(host.host(), R.string.mods_library_options, R.string.mods_options_intro, box);
        save.setOnClickListener(x -> {
            Context app = c.getApplicationContext();
            host.store().work.execute(() -> {
                int changed = ModVariants.apply(app, e.record.id, slots);
                host.store().main.post(() -> {
                    if (!host.isAdded()) return;
                    d.dismiss();
                    Ui.say(host.host(), c.getResources().getQuantityString(R.plurals.mods_options_saved, changed, changed));
                    host.changed();
                });
            });
        });
        d.show();
    }

    private String detail(ManagedMods.Entry e) {
        StringBuilder sb = new StringBuilder();
        sb.append(c.getResources().getQuantityString(R.plurals.mods_items, e.files, e.files));
        sb.append(" · ").append(Ui.size(c, e.bytes));
        if (e.pack != null && !e.pack.author.isEmpty()) sb.append(" · ").append(c.getString(R.string.mods_library_by, e.pack.author));
        if (e.pack != null && !e.pack.source.isEmpty()) sb.append(" · ").append(e.pack.source);
        if (e.record.enabled && e.overridden > 0) sb.append('\n').append(c.getResources().getQuantityString(R.plurals.mods_library_overridden, e.overridden, e.overridden));
        else if (e.conflicts > 0) sb.append('\n').append(c.getString(R.string.mods_library_conflicts));
        return sb.toString();
    }

    private final class Adapter extends RecyclerView.Adapter<Adapter.Holder> {
        @NonNull
        @Override
        public Holder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            return new Holder(LayoutInflater.from(parent.getContext()).inflate(R.layout.item_managed_mod, parent, false));
        }

        @Override
        public void onBindViewHolder(@NonNull Holder h, int position) {
            ManagedMods.Entry e = shown.get(position);
            int index = all.indexOf(e);
            h.name.setText(e.record.name);
            h.detail.setText(detail(e));
            if (e.pack != null && !e.pack.iconUrl.isEmpty() && e.pack.iconUrl.startsWith("https://")) {
                h.image.setVisibility(View.VISIBLE);
                h.icon.setVisibility(View.GONE);
                Net.image(h.image, e.pack.iconUrl, R.drawable.ic_apps, Ui.dp(c, 44));
            } else {
                h.image.setVisibility(View.GONE);
                h.icon.setVisibility(View.VISIBLE);
            }
            h.enabled.setOnCheckedChangeListener(null);
            h.enabled.setChecked(e.record.enabled);
            h.enabled.setContentDescription(c.getString(R.string.mods_library_toggle, e.record.name));
            h.enabled.setOnCheckedChangeListener((b, on) -> toggle(e, on));
            h.more.setContentDescription(c.getString(R.string.mods_actions_for, e.record.name));
            h.more.setOnClickListener(x -> menu(e, index).show(x));
            h.itemView.setOnClickListener(x -> h.enabled.toggle());
            Flyout.bind(h.itemView, () -> menu(e, index));
        }

        @Override
        public int getItemCount() {
            return shown.size();
        }

        final class Holder extends RecyclerView.ViewHolder {
            final ShapeableImageView image;
            final ImageView icon;
            final TextView name;
            final TextView detail;
            final MaterialSwitch enabled;
            final View more;

            Holder(View v) {
                super(v);
                image = v.findViewById(R.id.mod_image);
                icon = v.findViewById(R.id.mod_icon);
                name = v.findViewById(R.id.mod_name);
                detail = v.findViewById(R.id.mod_detail);
                enabled = v.findViewById(R.id.mod_enabled);
                more = v.findViewById(R.id.mod_more);
            }
        }
    }
}
