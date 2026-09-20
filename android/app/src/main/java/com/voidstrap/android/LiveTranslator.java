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
        translateTree(activity.getWindow().getDecorView());
    }

    static void translateTree(View root) {
        if (root == null) return;
        Store store = Store.get(root.getContext());
        String lang = Translator.language(store);
        if (Translator.DEFAULT.equals(lang)) {
            restore(root);
            return;
        }
        Translator.load(root.getContext());
        List<String> missing = new ArrayList<>();
        walk(root, lang, missing);
        if (missing.isEmpty()) return;
        Translator.fetch(root.getContext(), store, lang, missing, () -> {
            if (root.isAttachedToWindow()) walk(root, lang, null);
        });
    }

    private static boolean same(CharSequence a, CharSequence b) {
        return a == null ? b == null : b != null && a.toString().equals(b.toString());
    }

    private static CharSequence swap(Map<?, Slot> table, Slot slot, CharSequence current, String lang, List<String> missing) {
        if (slot.applied != null && !same(current, slot.applied)) {
            slot.original = current;
            slot.applied = null;
        }
        if (slot.original == null) slot.original = current;
        if (slot.original == null || Translator.skip(slot.original)) return null;
        String hit = Translator.cached(lang, slot.original.toString());
        if (hit == null) {
            if (missing != null) missing.add(slot.original.toString());
            return null;
        }
        if (same(current, hit)) {
            slot.applied = hit;
            return null;
        }
        slot.applied = hit;
        return hit;
    }

    private static CharSequence revert(Slot slot, CharSequence current) {
        if (slot == null) return null;
        if (slot.applied == null || !same(current, slot.applied)) return null;
        CharSequence original = slot.original;
        slot.applied = null;
        return original;
    }

    private static void walk(View v, String lang, List<String> missing) {
        if (v == null || v.getTag(R.id.vs_no_translate) != null) return;
        if (v instanceof TextInputLayout) {
            TextInputLayout layout = (TextInputLayout) v;
            Slot slot = slot(layoutHints, layout);
            CharSequence next = swap(layoutHints, slot, layout.getHint(), lang, missing);
            if (next != null) layout.setHint(next);
        }
        if (v instanceof TabLayout) {
            TabLayout group = (TabLayout) v;
            for (int i = 0; i < group.getTabCount(); i++) {
                TabLayout.Tab tab = group.getTabAt(i);
                if (tab == null) continue;
                Slot slot = slot(tabs, tab);
                CharSequence next = swap(tabs, slot, tab.getText(), lang, missing);
                if (next != null) tab.setText(next);
            }
        }
        if (v instanceof TextView) {
            TextView t = (TextView) v;
            Slot textSlot = slot(texts, t);
            CharSequence next = swap(texts, textSlot, t.getText(), lang, missing);
            if (next != null) t.setText(next);
            Slot hintSlot = slot(hints, t);
            CharSequence nextHint = swap(hints, hintSlot, t.getHint(), lang, missing);
            if (nextHint != null) t.setHint(nextHint);
        }
        if (v instanceof ViewGroup) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) walk(g.getChildAt(i), lang, missing);
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
