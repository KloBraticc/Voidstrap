package com.voidstrap.android;

import android.app.Dialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.net.Uri;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AlertDialog;
import androidx.recyclerview.widget.DiffUtil;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.ListAdapter;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.button.MaterialButtonToggleGroup;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.UUID;

public final class FlagsFragment extends Page {
    private MaterialButton profileButton;
    private MaterialButton deleteSelected;
    private boolean presetsShown;
    private TextView empty;
    private TextInputEditText search;
    private RecyclerView list;
    private com.google.android.material.appbar.AppBarLayout appbar;
    private final Runnable keepHeaderReachable = this::keepHeaderReachable;
    private String query = "";
    private final FlagAdapter adapter = new FlagAdapter();
    private final java.util.Set<String> selected = new java.util.HashSet<>();
    private final androidx.activity.OnBackPressedCallback clearSelection = new androidx.activity.OnBackPressedCallback(false) {
        @Override
        public void handleOnBackPressed() {
            selected.clear();
            bindList();
        }
    };
    private final Runnable applySearch = this::bindList;
    private boolean exportEnvelope;

    private ActivityResultLauncher<String[]> importLauncher;
    private ActivityResultLauncher<String> exportLauncher;

    public FlagsFragment() {
        super(R.layout.fragment_flags);
    }

    @Override
    public void onCreate(@Nullable Bundle saved) {
        super.onCreate(saved);
        importLauncher = registerForActivityResult(new ActivityResultContracts.OpenDocument(), this::onImport);
        exportLauncher = registerForActivityResult(new ActivityResultContracts.CreateDocument("application/json"), this::onExport);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        profileButton = v.findViewById(R.id.profile_button);
        deleteSelected = v.findViewById(R.id.delete_selected);
        empty = v.findViewById(R.id.flags_empty);
        search = v.findViewById(R.id.flag_search);
        appbar = v.findViewById(R.id.flags_appbar);
        list = v.findViewById(R.id.flag_list);
        list.setLayoutManager(new LinearLayoutManager(requireContext()));
        list.setAdapter(adapter);
        profileButton.setOnClickListener(x -> profileSheet());
        v.findViewById(R.id.add).setOnClickListener(x -> edit(null));
        deleteSelected.setOnClickListener(x -> deleteSelected());
        presetsShown = !"0".equals(store.setting("showPresetFlags", "1"));
        v.findViewById(R.id.flags_more).setOnClickListener(this::more);
        search.addTextChangedListener(new LibraryFragment.Watcher(() -> {
            query = search.getText() == null ? "" : search.getText().toString().trim().toLowerCase(Locale.ROOT);
            store.main.removeCallbacks(applySearch);
            store.main.postDelayed(applySearch, 150);
        }));
        requireActivity().getOnBackPressedDispatcher().addCallback(getViewLifecycleOwner(), clearSelection);
    }

    @Override
    public void onDestroyView() {
        store.main.removeCallbacks(applySearch);
        if (list != null) list.removeCallbacks(keepHeaderReachable);
        appbar = null;
        list = null;
        super.onDestroyView();
    }

    @Override
    public void onHiddenChanged(boolean hidden) {
        if (hidden && !selected.isEmpty()) {
            selected.clear();
            bindList();
        }
        if (!hidden && appbar != null) appbar.setExpanded(true, false);
        super.onHiddenChanged(hidden);
    }

    private void keepHeaderReachable() {
        if (appbar == null || list == null) return;
        if (!list.canScrollVertically(-1) && !list.canScrollVertically(1)) appbar.setExpanded(true, false);
    }

    @Override
    protected void refresh() {
        FlagSync.ask(this);
        Flags.Profile p = store.flags.active();
        String detail = getResources().getQuantityString(R.plurals.flags_count, p.values.size(), p.values.size());
        profileButton.setText(p.name);
        profileButton.setContentDescription(getString(R.string.flags_profile_description, p.name, detail));
        bindList();
    }

    private void bindList() {
        if (getView() == null) return;
        Flags.Profile p = store.flags.active();
        boolean presets = presetsShown;
        selected.retainAll(p.values.keySet());
        List<Map.Entry<String, Object>> items = new ArrayList<>();
        for (Map.Entry<String, Object> e : p.values.entrySet()) {
            if (!presets && FlagPresets.KEYS.contains(e.getKey())) continue;
            if (query.isEmpty() || e.getKey().toLowerCase(Locale.ROOT).contains(query) || String.valueOf(e.getValue()).toLowerCase(Locale.ROOT).contains(query)) {
                items.add(new java.util.AbstractMap.SimpleImmutableEntry<>(e.getKey(), e.getValue()));
            }
        }
        adapter.submitList(items, () -> {
            adapter.notifyItemRangeChanged(0, adapter.getItemCount(), SELECTION);
            if (list == null) return;
            list.removeCallbacks(keepHeaderReachable);
            list.post(keepHeaderReachable);
        });
        empty.setVisibility(items.isEmpty() ? View.VISIBLE : View.GONE);
        empty.setText(p.values.isEmpty() ? R.string.flags_empty : query.isEmpty() ? R.string.flags_presets_hidden : R.string.flags_no_match);
        deleteSelected.setVisibility(selected.isEmpty() ? View.GONE : View.VISIBLE);
        clearSelection.setEnabled(!selected.isEmpty());
    }

    private void toggle(String key) {
        if (!selected.remove(key)) selected.add(key);
        bindList();
    }

    private void deleteSelected() {
        if (selected.isEmpty()) {
            Ui.say(host(), R.string.flags_select_hint);
            return;
        }
        Flags.Profile p = store.flags.active();
        LinkedHashMap<String, Object> before = new LinkedHashMap<>(p.values);
        int n = 0;
        for (String k : selected) if (p.values.remove(k) != null) n++;
        selected.clear();
        changed(p, before, getResources().getQuantityString(R.plurals.flags_deleted, n, n));
    }

    private void deleteAll() {
        Flags.Profile p = store.flags.active();
        if (p.values.isEmpty()) return;
        new MaterialAlertDialogBuilder(requireContext())
                .setTitle(R.string.flags_clear_title)
                .setMessage(getString(R.string.flags_clear_body, p.name, p.values.size()))
                .setPositiveButton(R.string.flags_delete_all, (d, w) -> {
                    LinkedHashMap<String, Object> before = new LinkedHashMap<>(p.values);
                    p.values.clear();
                    selected.clear();
                    changed(p, before, getResources().getQuantityString(R.plurals.flags_deleted, before.size(), before.size()));
                })
                .setNegativeButton(R.string.common_cancel, null)
                .show();
    }

    private void changed(Flags.Profile p, LinkedHashMap<String, Object> before, CharSequence message) {
        save(p);
        Ui.make(host(), message).setDuration(6000).setAction(R.string.common_undo, x -> {
            p.values.clear();
            p.values.putAll(before);
            save(p);
        }).show();
    }

    private void save(Flags.Profile p) {
        p.updated = System.currentTimeMillis();
        store.saveFlags();
        store.changed();
    }

    private void copyJson() {
        try {
            Ui.copy(requireContext(), getString(R.string.flags_title), store.flags.active().valuesJson().toString(2));
            Ui.say(host(), R.string.flags_json_copied);
        } catch (JSONException e) {
            Ui.say(host(), R.string.export_failed);
        }
    }

    private void commit() {
        save(store.flags.active());
    }

    static int guessType(String key) {
        String rest = key.replaceFirst("^[DS]?F", "");
        if (rest.startsWith("Flag")) return R.id.type_bool;
        if (rest.startsWith("Int") || rest.startsWith("Log")) return R.id.type_number;
        if (rest.startsWith("String")) return R.id.type_string;
        return 0;
    }

    private void edit(String existingKey) {
        Flags.Profile p = store.flags.active();
        View content = LayoutInflater.from(requireContext()).inflate(R.layout.dialog_flag, null, false);
        TextInputLayout keyLayout = content.findViewById(R.id.key_layout);
        TextInputLayout valueLayout = content.findViewById(R.id.value_layout);
        TextInputEditText key = content.findViewById(R.id.key);
        Ui.clearErrorOnEdit(keyLayout, key);
        TextInputEditText value = content.findViewById(R.id.value);
        Ui.clearErrorOnEdit(valueLayout, value);
        MaterialButtonToggleGroup type = content.findViewById(R.id.type);
        if (existingKey != null) {
            Object v = p.values.get(existingKey);
            key.setText(existingKey);
            value.setText(Flags.display(v));
            String t = Flags.typeOf(v);
            type.check(t.equals("bool") ? R.id.type_bool : t.equals("number") ? R.id.type_number : R.id.type_string);
        }
        if (existingKey == null) {
            boolean[] auto = {true};
            boolean[] setting = {false};
            type.addOnButtonCheckedListener((g, id, checked) -> {
                if (checked && !setting[0]) auto[0] = false;
            });
            key.addTextChangedListener(new LibraryFragment.Watcher(() -> {
                if (!auto[0]) return;
                int guess = guessType(Flags.cleanKey(key.getText()));
                if (guess == 0 || type.getCheckedButtonId() == guess) return;
                setting[0] = true;
                type.check(guess);
                setting[0] = false;
            }));
        }
        MaterialAlertDialogBuilder b = new MaterialAlertDialogBuilder(requireContext())
                .setTitle(existingKey == null ? R.string.flags_add_title : R.string.flags_edit_title)
                .setView(content)
                .setPositiveButton(R.string.common_save, null)
                .setNegativeButton(R.string.common_cancel, null);
        if (existingKey != null) b.setNeutralButton(R.string.common_remove, (d, w) -> {
            LinkedHashMap<String, Object> before = new LinkedHashMap<>(p.values);
            p.values.remove(existingKey);
            changed(p, before, getString(R.string.flags_removed, existingKey));
        });
        AlertDialog d = b.show();
        Ui.focus(d, key.length() == 0 ? key : value);
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String k = Flags.cleanKey(key.getText());
            String raw = value.getText() == null ? "" : value.getText().toString();
            keyLayout.setError(null);
            valueLayout.setError(null);
            if (!Flags.validKey(k)) {
                keyLayout.setError(getString(R.string.flags_error_key));
                return;
            }
            if (!k.equals(existingKey) && p.values.containsKey(k)) {
                keyLayout.setError(getString(R.string.flags_error_exists));
                return;
            }
            int checked = type.getCheckedButtonId();
            String t = checked == R.id.type_bool ? "bool" : checked == R.id.type_number ? "number" : "string";
            Object v = Flags.parseTyped(t, t.equals("string") ? raw : raw.trim());
            if (v == null) {
                valueLayout.setError(getString(t.equals("bool") ? R.string.flags_error_bool : t.equals("number") ? R.string.flags_error_number : R.string.flags_error_value));
                return;
            }
            if (t.equals("number") && Flags.intFlag(k) && !Flags.fitsInt(v)) {
                valueLayout.setError(getString(R.string.flags_error_int));
                return;
            }
            if (existingKey == null && p.values.size() >= Flags.MAX_ENTRIES) {
                keyLayout.setError(getString(R.string.flags_error_too_many));
                return;
            }
            if (existingKey != null && !existingKey.equals(k)) {
                java.util.LinkedHashMap<String, Object> copy = new java.util.LinkedHashMap<>();
                for (Map.Entry<String, Object> e : p.values.entrySet()) {
                    if (e.getKey().equals(existingKey)) copy.put(k, v);
                    else copy.put(e.getKey(), e.getValue());
                }
                p.values.clear();
                p.values.putAll(copy);
            } else {
                p.values.put(k, v);
            }
            commit();
            d.dismiss();
        });
    }

    private void more(View anchor) {
        new Flyout(requireContext())
                .add(R.drawable.ic_copy, R.string.flags_copy_json, this::copyJson)
                .add(R.drawable.ic_checkmark_circle, presetsShown ? R.string.flags_hide_presets : R.string.flags_show_presets, () -> {
                    presetsShown = !presetsShown;
                    store.putSetting("showPresetFlags", presetsShown ? "1" : "0");
                    bindList();
                })
                .add(R.drawable.ic_arrow_import, R.string.flags_import_file, () -> importLauncher.launch(new String[]{"application/json", "text/plain", "application/octet-stream"}))
                .add(R.drawable.ic_document, R.string.flags_paste, this::paste)
                .add(R.drawable.ic_arrow_sync, R.string.flags_apply_now, () -> FlagSync.apply(host(), false, null))
                .add(R.drawable.ic_arrow_download, R.string.flags_export_json, () -> {
                    exportEnvelope = false;
                    exportLauncher.launch("ClientAppSettings.json");
                })
                .add(R.drawable.ic_share, R.string.flags_export_profile, () -> {
                    exportEnvelope = true;
                    exportLauncher.launch(safeFileName(store.flags.active().name) + ".voidstrap-flags.json");
                })
                .separator()
                .add(R.drawable.ic_delete, R.string.flags_delete_all, this::deleteAll)
                .add(R.drawable.ic_delete, R.string.flags_remove_root, () -> FlagSync.remove(host()))
                .show(anchor);
    }

    private static String safeFileName(String name) {
        String s = name.replaceAll("[^A-Za-z0-9 _]", "").trim();
        return s.isEmpty() ? "Profile" : s;
    }

    private void onExport(Uri uri) {
        if (uri == null) return;
        Flags.Profile p = store.flags.active();
        String json;
        try {
            json = exportEnvelope ? p.envelope(Targets.selected(requireContext())).toString(2) : p.valuesJson().toString(2);
        } catch (JSONException e) {
            Ui.say(host(), R.string.export_failed);
            return;
        }
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            int error = Ui.writeText(app, uri, json);
            store.main.post(() -> {
                if (error == 0) {
                    p.exported = System.currentTimeMillis();
                    store.saveFlags();
                    store.changed();
                }
                if (isAdded()) Ui.say(host(), error == 0 ? R.string.flags_exported : error);
            });
        });
    }

    private void onImport(Uri uri) {
        if (uri == null) return;
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            Flags.ImportResult result = null;
            int error = 0;
            try (InputStream in = app.getContentResolver().openInputStream(uri)) {
                if (in == null) throw new IOException("stream");
                result = Flags.parse(Flags.readBounded(in));
            } catch (Flags.FormatException e) {
                error = e.reason;
            } catch (IOException | RuntimeException e) {
                error = R.string.flags_error_read;
            }
            imported(result, error);
        });
    }

    private void paste() {
        ClipboardManager cm = requireContext().getSystemService(ClipboardManager.class);
        ClipData clip = cm == null ? null : cm.getPrimaryClip();
        CharSequence text = clip == null || clip.getItemCount() == 0 ? null : clip.getItemAt(0).coerceToText(requireContext());
        if (text == null || text.toString().trim().isEmpty()) {
            Ui.say(host(), R.string.flags_paste_empty);
            return;
        }
        String json = text.toString();
        store.work.execute(() -> {
            Flags.ImportResult result = null;
            int error = 0;
            try {
                if (json.length() > Flags.MAX_BYTES) throw new Flags.FormatException(R.string.flags_error_too_large);
                result = Flags.parse(json);
            } catch (Flags.FormatException e) {
                error = e.reason;
            }
            imported(result, error);
        });
    }

    private void imported(Flags.ImportResult r, int error) {
        store.main.post(() -> {
            if (!isAdded()) return;
            if (r == null) {
                new MaterialAlertDialogBuilder(requireContext()).setTitle(R.string.flags_import_failed).setMessage(error).setPositiveButton(R.string.common_ok, null).show();
                return;
            }
            preview(r);
        });
    }

    private void preview(Flags.ImportResult r) {
        Flags.Profile p = store.flags.active();
        int added = 0;
        int changed = 0;
        int same = 0;
        for (Map.Entry<String, Object> e : r.values.entrySet()) {
            Object cur = p.values.get(e.getKey());
            if (cur == null) added++;
            else if (Objects.equals(cur, e.getValue())) same++;
            else changed++;
        }
        int newEntries = added;
        StringBuilder msg = new StringBuilder(getString(R.string.flags_import_summary, r.values.size(), added, changed, same));
        if (!r.duplicates.isEmpty()) msg.append("\n\n").append(getResources().getQuantityString(R.plurals.flags_import_duplicates, r.duplicates.size(), r.duplicates.size(), join(r.duplicates)));
        if (!r.rejected.isEmpty()) msg.append("\n\n").append(getResources().getQuantityString(R.plurals.flags_import_rejected, r.rejected.size(), r.rejected.size(), join(r.rejected)));
        if (r.values.isEmpty()) {
            new MaterialAlertDialogBuilder(requireContext()).setTitle(R.string.flags_import_nothing).setMessage(msg).setPositiveButton(R.string.common_ok, null).show();
            return;
        }
        String done = getString(R.string.flags_import_done, r.values.size());
        String name = r.name != null && !r.name.trim().isEmpty() ? r.name.trim() : getString(R.string.flags_profile_imported);
        LinearLayout list = new LinearLayout(requireContext());
        list.setOrientation(LinearLayout.VERTICAL);
        Dialog[] sheet = new Dialog[1];
        option(list, sheet, R.drawable.ic_arrow_import, getString(R.string.flags_import_merge, p.name), getString(R.string.flags_import_merge_body), () -> {
            if (p.values.size() + newEntries > Flags.MAX_ENTRIES) {
                Ui.say(host(), R.string.flags_error_too_many);
                return;
            }
            LinkedHashMap<String, Object> before = new LinkedHashMap<>(p.values);
            p.values.putAll(r.values);
            changed(p, before, done);
        });
        option(list, sheet, R.drawable.ic_arrow_sync, getString(R.string.flags_import_replace, p.name), getString(R.string.flags_import_replace_body), () -> {
            LinkedHashMap<String, Object> before = new LinkedHashMap<>(p.values);
            p.values.clear();
            p.values.putAll(r.values);
            changed(p, before, done);
        });
        option(list, sheet, R.drawable.ic_add, getString(R.string.flags_import_new), getString(R.string.flags_import_new_body, name), () -> {
            Flags.Profile created = new Flags.Profile(UUID.randomUUID().toString(), name);
            created.values.putAll(r.values);
            store.flags.profiles.add(created);
            store.flags.current = created.id;
            save(created);
            Ui.say(host(), done);
        });
        sheet[0] = Ui.surface(host(), getString(R.string.flags_import_title), msg, list, null);
        sheet[0].show();
    }

    private void option(LinearLayout list, Dialog[] sheet, int icon, CharSequence title, CharSequence detail, Runnable action) {
        Row row = Row.inflate(list);
        row.set(icon, title, detail);
        row.chevron.setVisibility(View.VISIBLE);
        row.view.setOnClickListener(x -> {
            sheet[0].dismiss();
            action.run();
        });
        list.addView(row.view);
    }

    private static String join(List<String> keys) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < keys.size() && i < 5; i++) sb.append(i == 0 ? "" : ", ").append(keys.get(i));
        if (keys.size() > 5) sb.append(", ...");
        return sb.toString();
    }

    private void profileSheet() {
        LinearLayout list = new LinearLayout(requireContext());
        list.setOrientation(LinearLayout.VERTICAL);
        Dialog[] holder = new Dialog[1];
        for (Flags.Profile p : store.flags.profiles) {
            Row row = Row.inflate(list);
            boolean on = p.id.equals(store.flags.current);
            row.set(R.drawable.ic_flag, p.name, getResources().getQuantityString(R.plurals.flags_count, p.values.size(), p.values.size()));
            row.radio.setVisibility(View.VISIBLE);
            row.radio.setChecked(on);
            row.view.setSelected(on);
            row.view.setOnClickListener(x -> {
                store.flags.current = p.id;
                store.saveFlags();
                store.changed();
                holder[0].dismiss();
            });
            list.addView(row.view);
        }
        LinearLayout actions = new LinearLayout(requireContext());
        actions.setOrientation(LinearLayout.VERTICAL);
        actions.setPadding(0, Ui.dp(requireContext(), 12), 0, 0);
        addAction(actions, R.string.flags_profile_new, R.drawable.ic_add, () -> {
            holder[0].dismiss();
            nameProfile(null, false);
        });
        addAction(actions, R.string.flags_profile_duplicate, R.drawable.ic_copy, () -> {
            holder[0].dismiss();
            nameProfile(store.flags.active(), true);
        });
        addAction(actions, R.string.common_rename, R.drawable.ic_rename, () -> {
            holder[0].dismiss();
            nameProfile(store.flags.active(), false);
        });
        if (store.flags.profiles.size() > 1) addAction(actions, R.string.flags_profile_delete, R.drawable.ic_delete, () -> {
            holder[0].dismiss();
            deleteProfile();
        });
        list.addView(actions);
        holder[0] = Ui.surface(host(), R.string.flags_profiles, R.string.flags_profiles_body, list);
        holder[0].show();
    }

    private void addAction(LinearLayout parent, int text, int icon, Runnable r) {
        MaterialButton b = new MaterialButton(requireContext(), null, androidx.appcompat.R.attr.borderlessButtonStyle);
        b.setText(text);
        b.setIconResource(icon);
        b.setGravity(android.view.Gravity.START | android.view.Gravity.CENTER_VERTICAL);
        b.setOnClickListener(x -> r.run());
        parent.addView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    private void nameProfile(Flags.Profile source, boolean duplicate) {
        View content = LayoutInflater.from(requireContext()).inflate(R.layout.dialog_input, null, false);
        TextInputLayout layout = content.findViewById(R.id.input_layout);
        TextInputEditText input = content.findViewById(R.id.input);
        layout.setHint(R.string.flags_profile_name);
        layout.setCounterEnabled(true);
        layout.setCounterMaxLength(Flags.MAX_NAME);
        if (source != null) input.setText(duplicate ? getString(R.string.flags_profile_copy_name, source.name) : source.name);
        boolean rename = source != null && !duplicate;
        AlertDialog d = new MaterialAlertDialogBuilder(requireContext())
                .setTitle(rename ? R.string.common_rename : duplicate ? R.string.flags_profile_duplicate : R.string.flags_profile_new)
                .setView(content)
                .setPositiveButton(rename ? R.string.common_save : R.string.common_create, null)
                .setNegativeButton(R.string.common_cancel, null)
                .show();
        Ui.focus(d, input);
        input.selectAll();
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String name = input.getText() == null ? "" : input.getText().toString().trim();
            if (name.isEmpty() || name.length() > Flags.MAX_NAME) {
                layout.setError(getString(R.string.flags_error_profile_name));
                return;
            }
            if (rename) {
                source.name = name;
                source.updated = System.currentTimeMillis();
            } else {
                Flags.Profile p = new Flags.Profile(UUID.randomUUID().toString(), name);
                if (duplicate) p.values.putAll(source.values);
                store.flags.profiles.add(p);
                store.flags.current = p.id;
            }
            store.saveFlags();
            store.changed();
            d.dismiss();
        });
    }

    private void deleteProfile() {
        Flags.Profile p = store.flags.active();
        new MaterialAlertDialogBuilder(requireContext())
                .setTitle(R.string.flags_profile_delete)
                .setMessage(getString(R.string.flags_profile_delete_body, p.name, p.values.size()))
                .setPositiveButton(R.string.common_delete, (d, w) -> {
                    store.flags.profiles.remove(p);
                    store.flags.current = store.flags.profiles.get(0).id;
                    store.saveFlags();
                    store.changed();
                })
                .setNegativeButton(R.string.common_cancel, null)
                .show();
    }

    private static final Object SELECTION = new Object();

    private static String shown(Object v) {
        if (v instanceof Boolean) return (Boolean) v ? "True" : "False";
        return Flags.display(v);
    }

    private final class FlagAdapter extends ListAdapter<Map.Entry<String, Object>, FlagAdapter.Holder> {
        FlagAdapter() {
            super(new DiffUtil.ItemCallback<Map.Entry<String, Object>>() {
                @Override
                public boolean areItemsTheSame(@NonNull Map.Entry<String, Object> a, @NonNull Map.Entry<String, Object> b) {
                    return a.getKey().equals(b.getKey());
                }

                @Override
                public boolean areContentsTheSame(@NonNull Map.Entry<String, Object> a, @NonNull Map.Entry<String, Object> b) {
                    return Objects.equals(a.getValue(), b.getValue());
                }
            });
        }

        @NonNull
        @Override
        public Holder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            return new Holder(LayoutInflater.from(parent.getContext()).inflate(R.layout.item_flag_row, parent, false));
        }

        @Override
        public void onBindViewHolder(@NonNull Holder h, int position, @NonNull List<Object> payloads) {
            if (payloads.contains(SELECTION)) h.itemView.setActivated(selected.contains(getItem(position).getKey()));
            else super.onBindViewHolder(h, position, payloads);
        }

        @Override
        public void onBindViewHolder(@NonNull Holder h, int position) {
            Map.Entry<String, Object> e = getItem(position);
            String key = e.getKey();
            h.name.setText(key);
            h.value.setText(shown(e.getValue()));
            h.itemView.setActivated(selected.contains(key));
            h.itemView.setOnClickListener(x -> {
                if (selected.isEmpty()) edit(key);
                else toggle(key);
            });
            h.itemView.setOnLongClickListener(x -> {
                toggle(key);
                return true;
            });
            h.itemView.setContentDescription(getString(R.string.flags_row_description, key, shown(e.getValue())));
        }

        final class Holder extends RecyclerView.ViewHolder {
            final TextView name;
            final TextView value;

            Holder(View v) {
                super(v);
                name = v.findViewById(R.id.flag_name);
                value = v.findViewById(R.id.flag_value);
            }
        }
    }
}
