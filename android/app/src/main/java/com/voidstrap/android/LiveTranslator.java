package com.voidstrap.android;

import android.app.Activity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import com.google.android.material.tabs.TabLayout;
import com.google.android.material.textfield.TextInputLayout;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.WeakHashMap;

final class LiveTranslator {
    private static final class Slot {
        CharSequence original;
        CharSequence applied;
    }

    private static final Map<TextView, Slot> texts = Collections.synchronizedMap(new WeakHashMap<>());
    private static final Map<TextView, Slot> hints = Collections.synchronizedMap(new WeakHashMap<>());
    private static final Map<TextInputLayout, Slot> layoutHints = Collections.synchronizedMap(new WeakHashMap<>());
    private static final Map<TabLayout.Tab, Slot> tabs = Collections.synchronizedMap(new WeakHashMap<>());

    private LiveTranslator() {
    }

    static void apply(Activity activity) {
        if (activity == null || activity.isFinishing() || activity.getWindow() == null) return;
        Crash.run("translation", () -> translateTree(activity.getWindow().getDecorView()));
    }

    static void translateTree(View root) {
        if (root == null) return;
        Store store = Store.get(root.getContext());
        String lang = Translator.language(store);
        if (Translator.DEFAULT.equals(lang)) {
            if (!texts.isEmpty() || !hints.isEmpty() || !layoutHints.isEmpty() || !tabs.isEmpty()) restore(root);
            return;
        }
        Translator.load(root.getContext());
        List<String> missing = new ArrayList<>();
        walk(root, lang, missing);
        if (missing.isEmpty()) return;
        Translator.fetch(root.getContext(), store, lang, missing, () -> Crash.run("translation pass", () -> {
            if (root.isAttachedToWindow()) walk(root, lang, null);
        }));
    }

    private static boolean same(CharSequence a, CharSequence b) {
        return a == null ? b == null : b != null && a.toString().equals(b.toString());
    }

    private static CharSequence revert(Slot slot, CharSequence current) {
        if (slot == null) return null;
        if (slot.applied == null || !same(current, slot.applied)) return null;
        CharSequence original = slot.original;
        slot.applied = null;
        return original;
    }

    private interface Setter {
        void set(CharSequence value);
    }

    private static final class Item {
        final Slot slot;
        final CharSequence current;
        final Setter setter;

        Item(Slot slot, CharSequence current, Setter setter) {
            this.slot = slot;
            this.current = current;
            this.setter = setter;
        }
    }

    private static void walk(View root, String lang, List<String> missing) {
        List<Item> items = new ArrayList<>();
        collect(root, items);
        List<String> originals = new ArrayList<>();
        for (Item it : items) {
            Slot slot = it.slot;
            if (slot.applied != null && !same(it.current, slot.applied)) {
                slot.original = it.current;
                slot.applied = null;
            }
            if (slot.original == null) slot.original = it.current;
            originals.add(slot.original == null ? null : slot.original.toString());
        }
        Object[] hits = Translator.lookup(lang, originals);
        for (int i = 0; i < items.size(); i++) {
            Object h = hits[i];
            if (Boolean.FALSE.equals(h)) continue;
            Item it = items.get(i);
            if (!(h instanceof String)) {
                if (missing != null) missing.add(originals.get(i));
                continue;
            }
            String hit = (String) h;
            it.slot.applied = hit;
            if (!same(it.current, hit)) it.setter.set(hit);
        }
    }

    private static void collect(View v, List<Item> items) {
        if (v == null || v.getTag(R.id.vs_no_translate) != null) return;
        if (v instanceof TextInputLayout) {
            TextInputLayout layout = (TextInputLayout) v;
            items.add(new Item(slot(layoutHints, layout), layout.getHint(), layout::setHint));
        }
        if (v instanceof TabLayout) {
            TabLayout group = (TabLayout) v;
            for (int i = 0; i < group.getTabCount(); i++) {
                TabLayout.Tab tab = group.getTabAt(i);
                if (tab != null) items.add(new Item(slot(tabs, tab), tab.getText(), tab::setText));
            }
        }
        if (v instanceof TextView) {
            TextView t = (TextView) v;
            items.add(new Item(slot(texts, t), t.getText(), t::setText));
            items.add(new Item(slot(hints, t), t.getHint(), t::setHint));
        }
        if (v instanceof ViewGroup) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) collect(g.getChildAt(i), items);
        }
    }

    private static <K> Slot slot(Map<K, Slot> table, K key) {
        Slot slot = table.get(key);
        if (slot == null) {
            slot = new Slot();
            table.put(key, slot);
        }
        return slot;
    }

    static void restore(View v) {
        if (v == null) return;
        if (v instanceof TextInputLayout) {
            TextInputLayout layout = (TextInputLayout) v;
            CharSequence original = revert(layoutHints.get(layout), layout.getHint());
            if (original != null) layout.setHint(original);
        }
        if (v instanceof TabLayout) {
            TabLayout group = (TabLayout) v;
            for (int i = 0; i < group.getTabCount(); i++) {
                TabLayout.Tab tab = group.getTabAt(i);
                if (tab == null) continue;
                CharSequence original = revert(tabs.get(tab), tab.getText());
                if (original != null) tab.setText(original);
            }
        }
        if (v instanceof TextView) {
            TextView t = (TextView) v;
            CharSequence original = revert(texts.get(t), t.getText());
            if (original != null) t.setText(original);
            CharSequence hint = revert(hints.get(t), t.getHint());
            if (hint != null) t.setHint(hint);
        }
        if (v instanceof ViewGroup) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) restore(g.getChildAt(i));
        }
    }
}
