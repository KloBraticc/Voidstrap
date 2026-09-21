package com.voidstrap.android;

import android.app.Dialog;
import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.imageview.ShapeableImageView;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;

import java.io.File;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.function.Consumer;

final class ModsCustomizeTab {
    private static final String HELP_URL = "https://github.com/Bloxstraplabs/Bloxstrap/wiki/Adding-custom-mods";
    private static final String[] IMAGE_TYPES = {"image/*", "application/octet-stream"};

    private final ModsFragment host;
    private final Context c;
    private final LinearLayout rows;
    private int generation;
    private Snapshot snap;
    private List<String> skyPacks;
    private boolean skyLoading;
    private boolean customSkyOpen;
    private String selectedSet;
    private List<String> fontCatalog;
    private String pendingFontFamily;
    private File pendingFont;
    private LinearLayout sheetBody;
    private Consumer<LinearLayout> sheetFill;
    private boolean dirty = true;

    private static final class Snapshot {
        int cursor;
        boolean customCursor;
        boolean shiftLock;
        List<String> sets;
        boolean oldSounds;
        boolean deathSound;
        int deathVolume;
        boolean oldAvatar;
        int emoji;
        boolean skyEnabled;
        String skyName;
        boolean customSky;
        boolean[] customFaces = new boolean[6];
        boolean font;
        String fontName;
        int fontScale;
        boolean ps4;
        boolean noGui;

        String sig() {
            return cursor + "|" + customCursor + "|" + shiftLock + "|" + sets + "|" + oldSounds + "|" + deathSound + "|" + deathVolume + "|" + oldAvatar + "|" + emoji
                    + "|" + skyEnabled + "|" + skyName + "|" + customSky + "|" + java.util.Arrays.toString(customFaces) + "|" + font + "|" + fontName + "|" + fontScale
                    + "|" + ps4 + "|" + noGui;
        }
    }

    ModsCustomizeTab(ModsFragment host, View v) {
        this.host = host;
        this.c = v.getContext();
        rows = v.findViewById(R.id.customize_rows);
    }

    void reload() {
        int gen = ++generation;
        Context app = c.getApplicationContext();
        host.store().work.execute(() -> {
            Snapshot s = new Snapshot();
            s.cursor = ModPresets.cursorStyle(app);
            s.customCursor = ModPresets.hasCustomCursor(app);
            s.shiftLock = ModPresets.ws(app, ModPresets.SHIFTLOCK).isFile();
            s.sets = ModPresets.cursorSets(app);
            s.oldSounds = ModPresets.presetOn(app, ModPresets.OLD_SOUNDS);
            s.deathSound = ModPresets.hasDeathSound(app);
            s.deathVolume = ModPresets.deathVolume(app);
            s.oldAvatar = ModPresets.presetOn(app, ModPresets.OLD_AVATAR);
            s.emoji = ModPresets.emoji(app);
            s.skyEnabled = ModPresets.skyEnabled(app);
            s.skyName = ModPresets.skyName(app);
            s.customSky = ModPresets.hasCustomSky(app);
            for (int i = 0; i < 6; i++) s.customFaces[i] = ModPresets.customFace(app, i).isFile();
            s.font = ModPresets.hasFont(app);
            s.fontName = ModPresets.fontName(app);
            s.fontScale = ModPresets.fontScale(app);
            s.ps4 = ModInterface.ps4(app);
            s.noGui = ModInterface.hideCoreGui(app);
            host.store().main.post(() -> {
                if (gen != generation || !host.isAdded()) return;
                if (!dirty && snap != null && snap.sig().equals(s.sig())) return;
                dirty = false;
                snap = s;
                build();
            });
        });
        if (skyPacks == null && !skyLoading) loadSky();
    }

    private void loadSky() {
        skyLoading = true;
        Context app = c.getApplicationContext();
        host.store().work.execute(() -> {
            List<String> packs = ModPresets.loadSkyPacks(app);
            host.store().main.post(() -> {
                skyLoading = false;
                skyPacks = packs;
                if (host.isAdded() && snap != null) build();
            });
        });
    }

    private void build() {
        Snapshot s = snap;
        rows.removeAllViews();
        LinearLayout g = section(rows, R.string.mods_section_cursor);
        String[] styles = c.getResources().getStringArray(R.array.mods_cursor_styles);
        nav(g, c.getString(R.string.mods_section_cursor), styles[cursorIndex(s)], R.string.mods_cursor_style_body, this::fillCursor);
        nav(g, c.getString(R.string.mods_sets_title), s.sets.isEmpty() ? c.getString(R.string.mods_value_none)
                : c.getResources().getQuantityString(R.plurals.mods_sets_count, s.sets.size(), s.sets.size()), R.string.mods_sets_body, this::fillSets);

        g = section(rows, R.string.mods_section_interface);
        toggle(g, R.string.mods_ps4, s.ps4, on -> work(R.string.mods_working, (app, cancel) -> {
            ModInterface.setPs4(app, on, cancel);
            return app.getString(on ? R.string.mods_ps4_on : R.string.mods_ps4_off);
        }));
        toggle(g, R.string.mods_nogui, s.noGui, on -> work(R.string.mods_working, (app, cancel) -> {
            ModInterface.setHideCoreGui(app, on, cancel);
            return app.getString(on ? R.string.mods_nogui_on : R.string.mods_nogui_off);
        }));

        g = section(rows, R.string.mods_section_appearance);
        toggle(g, R.string.mods_old_avatar, s.oldAvatar, on -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.setPreset(app, ModPresets.OLD_AVATAR, on);
            return app.getString(on ? R.string.mods_preset_on : R.string.mods_preset_off, app.getString(R.string.mods_old_avatar));
        }));
        String[] emoji = c.getResources().getStringArray(R.array.mods_emoji_types);
        SettingRows.choice(g, c.getString(R.string.mods_emoji), null, emoji, s.emoji, i -> work(R.string.mods_downloading, (app, cancel) -> {
            ModPresets.setEmoji(app, i, cancel);
            return app.getString(R.string.mods_emoji_set, emoji[i]);
        }));
        nav(g, c.getString(R.string.mods_sky_pack), s.skyEnabled ? s.skyName : c.getString(R.string.mods_value_default), R.string.mods_sky_body, this::fillSky);
        nav(g, c.getString(R.string.mods_font_short), !s.font ? c.getString(R.string.mods_value_default)
                : s.fontName.isEmpty() ? c.getString(R.string.mods_value_local_font) : s.fontName, R.string.mods_font_body, this::fillFont);

        g = section(rows, R.string.mods_section_help);
        SettingRows.row(g, c.getString(R.string.common_help), null, chevron()).setOnClickListener(x -> Ui.openWeb(c, HELP_URL));

        if (sheetBody != null) {
            sheetBody.removeAllViews();
            sheetFill.accept(sheetBody);
        }
    }

    private void open(CharSequence title, int subtitle, Consumer<LinearLayout> fill) {
        LinearLayout body = new LinearLayout(c);
        body.setOrientation(LinearLayout.VERTICAL);
        sheetBody = body;
        sheetFill = fill;
        fill.accept(body);
        Dialog d = Ui.surface(host.host(), title, c.getString(subtitle), body, () -> {
            if (sheetBody == body) {
                sheetBody = null;
                sheetFill = null;
            }
        });
        d.show();
    }

    private LinearLayout section(LinearLayout parent, int title) {
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Section);
        t.setText(title);
        androidx.core.view.ViewCompat.setAccessibilityHeading(t, true);
        t.setPadding(Ui.dp(c, 16), Ui.dp(c, 20), 0, Ui.dp(c, 8));
        parent.addView(t);
        LinearLayout group = new LinearLayout(c);
        group.setOrientation(LinearLayout.VERTICAL);
        group.setBackgroundResource(R.drawable.vs_group);
        group.setClipToOutline(true);
        group.setPadding(0, Ui.dp(c, 4), 0, Ui.dp(c, 4));
        parent.addView(group);
        return group;
    }

    private ImageView chevron() {
        ImageView v = new ImageView(c);
        int size = Ui.dp(c, 20);
        v.setLayoutParams(new LinearLayout.LayoutParams(size, size));
        v.setImageResource(R.drawable.ic_chevron_right);
        v.setImageTintList(ColorStateList.valueOf(Ui.attr(c, R.attr.vsTextSecondary)));
        v.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        return v;
    }

    private void nav(LinearLayout parent, CharSequence title, CharSequence value, int subtitle, Consumer<LinearLayout> fill) {
        SettingRows.row(parent, title, value, chevron()).setOnClickListener(x -> open(title, subtitle, fill));
    }

    private void link(LinearLayout parent, CharSequence title, CharSequence value, Runnable action) {
        SettingRows.row(parent, title, value, chevron()).setOnClickListener(x -> action.run());
    }

    private MaterialSwitch toggle(LinearLayout parent, int title, boolean on, Consumer<Boolean> change) {
        MaterialSwitch sw = new MaterialSwitch(c);
        sw.setChecked(on);
        sw.setContentDescription(c.getString(title));
        LinearLayout row = SettingRows.row(parent, c.getString(title), null, sw);
        row.setOnClickListener(x -> {
            if (sw.isEnabled()) sw.toggle();
        });
        sw.setOnCheckedChangeListener((b, value) -> change.accept(value));
        return sw;
    }

    private MaterialButton button(int text, int icon, boolean subtle, Runnable action) {
        MaterialButton b = subtle
                ? new MaterialButton(c, null, androidx.appcompat.R.attr.borderlessButtonStyle)
                : new MaterialButton(c);
        b.setText(text);
        if (icon != 0) b.setIconResource(icon);
        b.setOnClickListener(x -> action.run());
        return b;
    }

    private MaterialButton iconButton(int icon, int description, View.OnClickListener action) {
        MaterialButton b = new MaterialButton(c, null, com.google.android.material.R.attr.materialIconButtonStyle);
        b.setIconResource(icon);
        b.setContentDescription(c.getString(description));
        b.setOnClickListener(action);
        return b;
    }

    private LinearLayout buttons(View... views) {
        LinearLayout box = new LinearLayout(c);
        box.setOrientation(LinearLayout.HORIZONTAL);
        box.setGravity(Gravity.CENTER_VERTICAL);
        for (View v : views) {
            if (v == null) continue;
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            if (box.getChildCount() > 0) lp.setMarginStart(Ui.dp(c, 8));
            box.addView(v, lp);
        }
        return box;
    }

    private void actions(LinearLayout parent, View... views) {
        LinearLayout box = buttons(views);
        box.setPadding(Ui.dp(c, 16), Ui.dp(c, 8), 0, Ui.dp(c, 4));
        parent.addView(box);
    }

    private ShapeableImageView preview(File f) {
        ShapeableImageView iv = new ShapeableImageView(c);
        int size = Ui.dp(c, 36);
        iv.setLayoutParams(new LinearLayout.LayoutParams(size, size));
        iv.setBackgroundResource(R.drawable.vs_icon_tile);
        iv.setPadding(Ui.dp(c, 4), Ui.dp(c, 4), Ui.dp(c, 4), Ui.dp(c, 4));
        iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
        iv.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        if (f != null && f.isFile()) Net.image(iv, "file:" + f.getPath() + "#" + f.lastModified(), R.drawable.ic_image, size);
        else iv.setImageResource(R.drawable.ic_image);
        return iv;
    }

    private void imageRow(LinearLayout parent, CharSequence title, File f, boolean has, Runnable choose, Runnable remove) {
        LinearLayout row = SettingRows.row(parent, title, has ? null : c.getString(R.string.mods_sky_face_missing),
                buttons(preview(f), has && remove != null ? iconButton(R.drawable.ic_delete, R.string.common_remove, x -> remove.run()) : null));
        row.setOnClickListener(x -> choose.run());
    }

    private void work(int message, ModsFragment.Job job) {
        dirty = true;
        host.runTask(message, job);
    }

    private int cursorIndex(Snapshot s) {
        int[] order = ModPresets.CURSOR_ORDER;
        for (int i = 0; i < order.length; i++) if (order[i] == s.cursor) return i;
        return 0;
    }

    private void fillCursor(LinearLayout box) {
        Snapshot s = snap;
        int[] order = ModPresets.CURSOR_ORDER;
        String[] names = c.getResources().getStringArray(R.array.mods_cursor_styles);
        SettingRows.choice(box, c.getString(R.string.mods_cursor_style), null, names, cursorIndex(s), i -> {
            int style = order[i];
            if (style == ModPresets.CURSOR_CUSTOM && !s.customCursor) {
                pickCursor();
                return;
            }
            work(R.string.mods_working, (app, cancel) -> {
                ModPresets.applyCursorStyle(app, style);
                return app.getString(R.string.mods_cursor_style_set, names[i]);
            });
        });
        imageRow(box, c.getString(R.string.mods_cursor_custom), ModPresets.cursorPreview(c, ModPresets.SLOT_ARROW), s.customCursor, this::pickCursor,
                () -> work(R.string.mods_working, (app, cancel) -> {
                    ModPresets.removeCustomCursor(app);
                    return app.getString(R.string.mods_cursor_removed);
                }));
        imageRow(box, c.getString(R.string.mods_shiftlock), ModPresets.cursorPreview(c, ModPresets.SLOT_SHIFTLOCK), s.shiftLock,
                () -> host.pick(new String[]{"image/*"}, uri -> work(R.string.mods_working, (app, cancel) -> {
                    ModPresets.setShiftLock(app, uri);
                    return app.getString(R.string.mods_shiftlock_set);
                })),
                () -> work(R.string.mods_working, (app, cancel) -> {
                    ModPresets.removeShiftLock(app);
                    return app.getString(R.string.mods_shiftlock_removed);
                }));
    }

    private void pickCursor() {
        host.pick(new String[]{"image/*"}, uri -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.setCustomCursor(app, uri);
            return app.getString(R.string.mods_cursor_set);
        }));
    }

    private void fillSets(LinearLayout box) {
        Snapshot s = snap;
        if (selectedSet == null || !s.sets.contains(selectedSet)) selectedSet = s.sets.isEmpty() ? null : s.sets.get(0);
        if (s.sets.isEmpty()) {
            TextView none = new TextView(c);
            none.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
            none.setText(R.string.mods_sets_none);
            none.setPadding(Ui.dp(c, 16), Ui.dp(c, 8), 0, Ui.dp(c, 8));
            box.addView(none);
            actions(box, button(R.string.mods_sets_new, R.drawable.ic_add, false, this::newSet),
                    button(R.string.common_import, R.drawable.ic_arrow_import, true, this::importSet));
            return;
        }
        String set = selectedSet;
        SettingRows.choice(box, c.getString(R.string.mods_sets_pick), null, s.sets.toArray(new String[0]), s.sets.indexOf(set), i -> {
            selectedSet = s.sets.get(i);
            build();
        });
        int[] slots = {ModPresets.SLOT_ARROW, ModPresets.SLOT_FAR, ModPresets.SLOT_TEXT, ModPresets.SLOT_DRAG, ModPresets.SLOT_SHIFTLOCK};
        String[] slotNames = c.getResources().getStringArray(R.array.mods_cursor_slots);
        for (int k = 0; k < slots.length; k++) {
            int slot = slots[k];
            File f;
            try {
                f = ModPresets.setSlot(c, set, slot);
            } catch (java.io.IOException e) {
                continue;
            }
            imageRow(box, slotNames[k], f, f.isFile(),
                    () -> host.pick(new String[]{"image/*"}, uri -> work(R.string.mods_working, (app, cancel) -> {
                        ModPresets.setSetImage(app, set, slot, uri);
                        return app.getString(R.string.mods_sets_saved, set);
                    })),
                    () -> work(R.string.mods_working, (app, cancel) -> {
                        ModPresets.clearSetImage(app, set, slot);
                        return app.getString(R.string.mods_sets_saved, set);
                    }));
        }
        MaterialButton more = iconButton(R.drawable.ic_more_vertical, R.string.common_more, v -> setMenu(v, set));
        actions(box, button(R.string.mods_sets_use, R.drawable.ic_checkmark_circle, false, () -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.useSet(app, set);
            return app.getString(R.string.mods_sets_used, set);
        })), more);
    }

    private void setMenu(View anchor, String set) {
        PopupMenu menu = new PopupMenu(c, anchor);
        int[] labels = {R.string.mods_sets_new, R.string.common_import, R.string.mods_sets_copy, R.string.common_rename, R.string.common_export, R.string.common_remove};
        for (int i = 0; i < labels.length; i++) menu.getMenu().add(0, i, i, labels[i]);
        menu.setOnMenuItemClickListener(item -> {
            switch (item.getItemId()) {
                case 0:
                    newSet();
                    break;
                case 1:
                    importSet();
                    break;
                case 2:
                    work(R.string.mods_working, (app, cancel) -> {
                        ModPresets.copyCurrentToSet(app, set);
                        return app.getString(R.string.mods_sets_saved, set);
                    });
                    break;
                case 3:
                    askName(R.string.common_rename, set, name -> work(R.string.mods_working, (app, cancel) -> {
                        selectedSet = ModPresets.renameSet(app, set, name);
                        return app.getString(R.string.mods_sets_saved, selectedSet);
                    }));
                    break;
                case 4:
                    host.save(set + ".zip", "application/zip", uri -> work(R.string.mods_working, (app, cancel) -> {
                        ModPresets.exportSet(app, set, uri);
                        return app.getString(R.string.mods_exported, set + ".zip");
                    }));
                    break;
                default:
                    Ui.alert(c)
                            .setTitle(R.string.mods_sets_delete_title)
                            .setMessage(c.getString(R.string.mods_sets_delete_body, set))
                            .setPositiveButton(R.string.common_remove, (d, w) -> work(R.string.mods_working, (app, cancel) -> {
                                ModPresets.deleteSet(app, set);
                                selectedSet = null;
                                return app.getString(R.string.mods_removed, set);
                            }))
                            .setNegativeButton(R.string.common_cancel, null)
                            .show();
                    break;
            }
            return true;
        });
        menu.show();
    }

    private void newSet() {
        askName(R.string.mods_sets_new, ModPresets.uniqueSetName(c, c.getString(R.string.mods_sets_default_name)), name -> work(R.string.mods_working, (app, cancel) -> {
            selectedSet = ModPresets.createSet(app, name);
            return app.getString(R.string.mods_sets_created, selectedSet);
        }));
    }

    private void importSet() {
        host.pick(new String[]{"application/zip", "application/octet-stream"}, uri -> work(R.string.mods_working, (app, cancel) -> {
            selectedSet = ModPresets.importSet(app, uri);
            return app.getString(R.string.mods_sets_imported, selectedSet);
        }));
    }

    private interface Named {
        void on(String name);
    }

    private void askName(int title, String initial, Named done) {
        View content = android.view.LayoutInflater.from(c).inflate(R.layout.dialog_input, null, false);
        TextInputLayout layout = content.findViewById(R.id.input_layout);
        TextInputEditText input = content.findViewById(R.id.input);
        layout.setHint(R.string.mods_name_hint);
        input.setText(initial);
        input.setSelection(initial.length());
        Ui.clearErrorOnEdit(layout, input);
        androidx.appcompat.app.AlertDialog d = Ui.alert(c)
                .setTitle(title)
                .setView(content)
                .setPositiveButton(R.string.common_save, null)
                .setNegativeButton(R.string.common_cancel, null)
                .show();
        Ui.focus(d, input);
        d.getButton(androidx.appcompat.app.AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String name = ModPresets.validSetName(input.getText() == null ? "" : input.getText().toString());
            if (name == null) {
                layout.setError(c.getString(R.string.mods_error_name));
                return;
            }
            d.dismiss();
            done.on(name);
        });
    }

    private void fillDeath(LinearLayout box) {
        Snapshot s = snap;
        LinearLayout row = SettingRows.row(box, c.getString(R.string.mods_death_choose), c.getString(s.deathSound ? R.string.mods_value_custom : R.string.mods_value_default),
                s.deathSound ? iconButton(R.drawable.ic_delete, R.string.mods_death_remove, x -> work(R.string.mods_working, (app, cancel) -> {
                    ModPresets.removeDeathSound(app);
                    return app.getString(R.string.mods_death_removed);
                })) : chevron());
        row.setOnClickListener(x -> host.pick(new String[]{"audio/*", "application/ogg", "video/mp4", "application/octet-stream"}, uri -> work(R.string.mods_death_converting, (app, cancel) -> {
            ModPresets.importDeathSound(app, uri);
            return app.getString(R.string.mods_death_set);
        })));
        if (!s.deathSound || !ModPresets.deathSource(c).isFile()) return;
        int[] volumes = ModPresets.DEATH_VOLUMES;
        String[] labels = new String[volumes.length];
        int sel = 4;
        for (int i = 0; i < volumes.length; i++) {
            labels[i] = volumes[i] + "%";
            if (volumes[i] == s.deathVolume) sel = i;
        }
        SettingRows.choice(box, c.getString(R.string.mods_death_volume), null, labels, sel, i -> work(R.string.mods_death_converting, (app, cancel) -> {
            ModPresets.setDeathVolume(app, volumes[i]);
            return app.getString(R.string.mods_death_volume_set, labels[i]);
        }));
    }

    private void fillSky(LinearLayout box) {
        Snapshot s = snap;
        List<String> packs = skyPacks == null ? new ArrayList<>() : new ArrayList<>(skyPacks);
        packs.remove("Default");
        if (s.customSky && !packs.contains(ModPresets.CUSTOM_SKY)) packs.add(0, ModPresets.CUSTOM_SKY);
        if (!packs.contains(s.skyName) && !s.skyName.equals("Default")) packs.add(s.skyName);
        packs.add(0, "Default");
        String[] labels = packs.toArray(new String[0]);
        labels[0] = c.getString(R.string.mods_value_default);
        int sel = s.skyEnabled ? Math.max(0, packs.indexOf(s.skyName)) : 0;
        SettingRows.choice(box, c.getString(R.string.mods_sky_pack), skyLoading ? c.getString(R.string.mods_sky_loading) : null, labels, sel, i -> work(R.string.mods_downloading, (app, cancel) -> {
            ModPresets.setSky(app, packs.get(i), i != 0, cancel);
            return i == 0 ? app.getString(R.string.mods_sky_off) : app.getString(R.string.mods_sky_set, packs.get(i));
        }));
        LinearLayout custom = SettingRows.row(box, c.getString(R.string.mods_sky_custom), c.getString(R.string.mods_sky_custom_body), chevron());
        custom.getChildAt(custom.getChildCount() - 1).setRotation(customSkyOpen ? 90 : 0);
        custom.setOnClickListener(x -> {
            customSkyOpen = !customSkyOpen;
            build();
        });
        if (!customSkyOpen) return;
        actions(box, button(R.string.mods_sky_one_image, R.drawable.ic_image, true, () -> host.pick(IMAGE_TYPES, uri -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.pickCustomAll(app, uri);
            return app.getString(R.string.mods_sky_faces_set);
        }))));
        String[] faceNames = c.getResources().getStringArray(R.array.mods_sky_faces);
        boolean complete = true;
        for (int k = 0; k < 6; k++) {
            int face = k;
            complete &= s.customFaces[face];
            imageRow(box, faceNames[face], ModPresets.customFace(c, face), s.customFaces[face],
                    () -> host.pick(IMAGE_TYPES, uri -> work(R.string.mods_working, (app, cancel) -> {
                        ModPresets.pickCustomFace(app, face, uri);
                        return app.getString(R.string.mods_sky_faces_set);
                    })), null);
        }
        MaterialButton save = button(R.string.mods_sky_save, R.drawable.ic_checkmark_circle, false, () -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.saveCustomSky(app);
            return app.getString(R.string.mods_sky_set, ModPresets.CUSTOM_SKY);
        }));
        save.setEnabled(complete);
        actions(box, save, s.customSky ? button(R.string.mods_sky_remove_custom, R.drawable.ic_delete, true, () -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.removeCustomSky(app);
            return app.getString(R.string.mods_sky_custom_removed);
        })) : null);
    }

    private void fillFont(LinearLayout box) {
        Snapshot s = snap;
        File previewFile = pendingFont != null ? pendingFont : s.font ? ModPresets.fontSource(c) : null;
        if (previewFile != null && previewFile.isFile()) {
            TextView sample = new TextView(c);
            sample.setTextAppearance(R.style.TextAppearance_Voidstrap_CardTitle);
            sample.setText(R.string.mods_font_sample);
            sample.setTextSize(18);
            sample.setPadding(Ui.dp(c, 12), Ui.dp(c, 10), Ui.dp(c, 12), Ui.dp(c, 10));
            sample.setBackgroundResource(R.drawable.vs_icon_tile);
            try {
                sample.setTypeface(Typeface.createFromFile(previewFile));
                AppFont.keep(sample);
            } catch (RuntimeException ignored) {
            }
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.bottomMargin = Ui.dp(c, 8);
            box.addView(sample, lp);
        }
        String family = pendingFontFamily != null ? pendingFontFamily : s.fontName.isEmpty() ? c.getString(R.string.mods_font_pick) : s.fontName;
        link(box, c.getString(R.string.mods_font_google), family, this::browseFonts);
        link(box, c.getString(R.string.mods_font_local_choose), null, () -> host.pick(new String[]{"font/ttf", "font/otf", "application/x-font-ttf", "application/x-font-otf", "application/octet-stream"}, uri -> work(R.string.mods_working, (app, cancel) -> {
            File f = ModPresets.importLocalFont(app, uri);
            ModPresets.useFont(app, f, "");
            pendingFont = null;
            pendingFontFamily = null;
            return app.getString(R.string.mods_font_set, app.getString(R.string.mods_font_local));
        })));
        if (s.font) {
            int[] scales = ModPresets.FONT_SCALES;
            String[] labels = new String[scales.length];
            int sel = -1;
            int hundred = 0;
            for (int i = 0; i < scales.length; i++) {
                labels[i] = scales[i] + "%";
                if (scales[i] == 100) hundred = i;
                if (scales[i] == s.fontScale) sel = i;
            }
            if (sel < 0) sel = hundred;
            SettingRows.choice(box, c.getString(R.string.mods_font_size), null, labels, sel, i -> work(R.string.mods_working, (app, cancel) -> {
                ModPresets.setFontScale(app, scales[i]);
                return app.getString(R.string.mods_font_size_set, labels[i]);
            }));
        }
        MaterialButton use = null;
        if (pendingFont != null) {
            File f = pendingFont;
            String name = pendingFontFamily;
            use = button(R.string.mods_font_use, R.drawable.ic_checkmark_circle, false, () -> work(R.string.mods_working, (app, cancel) -> {
                ModPresets.useFont(app, f, name);
                pendingFont = null;
                pendingFontFamily = null;
                return app.getString(R.string.mods_font_set, name);
            }));
        }
        MaterialButton remove = s.font ? button(R.string.mods_font_remove, R.drawable.ic_delete, true, () -> work(R.string.mods_working, (app, cancel) -> {
            ModPresets.removeFont(app);
            pendingFont = null;
            pendingFontFamily = null;
            return app.getString(R.string.mods_font_removed);
        })) : null;
        if (use != null || remove != null) actions(box, use, remove);
    }

    private void browseFonts() {
        View content = android.view.LayoutInflater.from(c).inflate(R.layout.view_font_picker, new android.widget.FrameLayout(c), false);
        TextInputEditText search = content.findViewById(R.id.font_search);
        TextView status = content.findViewById(R.id.font_status);
        RecyclerView list = content.findViewById(R.id.font_list);
        list.setLayoutManager(new LinearLayoutManager(c));
        list.getLayoutParams().height = Math.min(Ui.dp(c, 420), c.getResources().getDisplayMetrics().heightPixels / 2);
        List<String> shown = new ArrayList<>();
        Dialog[] dialog = new Dialog[1];
        RecyclerView.Adapter<RecyclerView.ViewHolder> adapter = new RecyclerView.Adapter<RecyclerView.ViewHolder>() {
            @NonNull
            @Override
            public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
                Row r = Row.inflate(parent);
                RecyclerView.ViewHolder h = new RecyclerView.ViewHolder(r.view) {
                };
                r.view.setTag(r);
                return h;
            }

            @Override
            public void onBindViewHolder(@NonNull RecyclerView.ViewHolder h, int position) {
                Row r = (Row) h.itemView.getTag();
                String name = shown.get(position);
                r.set(R.drawable.ic_document, name, null);
                r.view.setOnClickListener(x -> {
                    dialog[0].dismiss();
                    pickFont(name);
                });
            }

            @Override
            public int getItemCount() {
                return shown.size();
            }
        };
        list.setAdapter(adapter);
        Runnable filter = () -> {
            String q = search.getText() == null ? "" : search.getText().toString().trim().toLowerCase(Locale.ROOT);
            int old = shown.size();
            shown.clear();
            if (fontCatalog != null) for (String f : fontCatalog) if (q.isEmpty() || f.toLowerCase(Locale.ROOT).contains(q)) shown.add(f);
            Ui.replaced(adapter, old);
            if (fontCatalog != null) status.setText(c.getResources().getQuantityString(R.plurals.mods_font_count, shown.size(), shown.size()));
        };
        search.addTextChangedListener(new LibraryFragment.Watcher(filter));
        content.findViewById(R.id.font_refresh).setOnClickListener(x -> loadFonts(true, status, filter));
        dialog[0] = Ui.surface(host.host(), R.string.mods_font_google, R.string.mods_font_picker_body, content);
        dialog[0].show();
        if (fontCatalog == null) loadFonts(false, status, filter);
        else filter.run();
    }

    private void loadFonts(boolean force, TextView status, Runnable done) {
        status.setText(R.string.mods_font_loading);
        Context app = c.getApplicationContext();
        host.store().work.execute(() -> {
            List<String> fonts = ModPresets.loadFontCatalog(app, force);
            host.store().main.post(() -> {
                fontCatalog = fonts;
                done.run();
            });
        });
    }

    private void pickFont(String family) {
        work(R.string.mods_downloading, (app, cancel) -> {
            File f = ModPresets.downloadGoogleFont(app, family, cancel);
            pendingFont = f;
            pendingFontFamily = family;
            return app.getString(R.string.mods_font_ready, family);
        });
    }
}
