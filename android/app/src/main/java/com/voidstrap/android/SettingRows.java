package com.voidstrap.android;

import android.content.Context;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.TextView;

public final class SettingRows {
    public interface IntChoice {
        void on(int index);
    }

    private SettingRows() {
    }

    public static LinearLayout section(LinearLayout root, CharSequence title) {
        Context c = root.getContext();
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Section);
        t.setText(title);
        androidx.core.view.ViewCompat.setAccessibilityHeading(t, true);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tp.topMargin = Ui.dp(c, root.getChildCount() == 0 ? 4 : 20);
        tp.bottomMargin = Ui.dp(c, 8);
        tp.setMarginStart(Ui.dp(c, 4));
        root.addView(t, tp);
        com.google.android.material.card.MaterialCardView card = new com.google.android.material.card.MaterialCardView(c);
        LinearLayout body = new LinearLayout(c);
        body.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(c, 4);
        body.setPadding(0, pad, 0, pad);
        card.addView(body);
        root.addView(card, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return body;
    }

    public static LinearLayout row(LinearLayout parent, CharSequence title, CharSequence summary, View control) {
        Context c = parent.getContext();
        LinearLayout row = new LinearLayout(c);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setMinimumHeight(Ui.dp(c, 56));
        row.setPadding(Ui.dp(c, 16), Ui.dp(c, 8), Ui.dp(c, 12), Ui.dp(c, 8));
        row.setBackgroundResource(R.drawable.vs_row);
        LinearLayout texts = new LinearLayout(c);
        texts.setOrientation(LinearLayout.VERTICAL);
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_RowTitle);
        t.setText(title);
        texts.addView(t);
        if (summary != null && summary.length() > 0) {
            TextView s = new TextView(c);
            s.setTextAppearance(R.style.TextAppearance_Voidstrap_RowSummary);
            s.setText(summary);
            texts.addView(s);
        }
        boolean stack = false;
        if (control != null) {
            control.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
            stack = control.getMeasuredWidth() > c.getResources().getDisplayMetrics().widthPixels * 0.45f;
        }
        if (stack) {
            row.setOrientation(LinearLayout.VERTICAL);
            row.setGravity(Gravity.START);
            row.addView(texts, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            cp.topMargin = Ui.dp(c, 8);
            row.addView(control, cp);
        } else {
            row.addView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
            if (control != null) {
                LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                cp.setMarginStart(Ui.dp(c, 12));
                row.addView(control, cp);
            }
        }
        parent.addView(row);
        return row;
    }

    public static void choice(LinearLayout parent, CharSequence title, CharSequence summary, CharSequence[] labels, int selected, IntChoice change) {
        Context c = parent.getContext();
        int[] current = {selected};
        LinearLayout row = row(parent, title, value(labels[selected], summary), null);
        TextView value = (TextView) ((LinearLayout) row.getChildAt(0)).getChildAt(1);
        row.setContentDescription(c.getString(R.string.pair, title, labels[selected]));
        row.setOnClickListener(v -> {
            androidx.appcompat.app.AlertDialog[] dialog = new androidx.appcompat.app.AlertDialog[1];
            dialog[0] = Ui.alert(c)
                    .setTitle(title)
                    .setSingleChoiceItems(labels, current[0], (d, i) -> {
                        dialog[0].dismiss();
                        if (i == current[0]) return;
                        current[0] = i;
                        value.setText(value(labels[i], summary));
                        row.setContentDescription(c.getString(R.string.pair, title, labels[i]));
                        change.on(i);
                    })
                    .setNegativeButton(R.string.common_cancel, null)
                    .show();
        });
    }

    public static void appearance(LinearLayout parent, android.app.Activity a, int tab) {
        Store store = Store.get(a);
        String currentTheme = store.setting("theme", "system");
        choice(parent, a.getString(R.string.settings_theme), null, a.getResources().getStringArray(R.array.settings_themes), VoidstrapApp.themeIndex(currentTheme), i -> {
            String value = VoidstrapApp.THEMES[i];
            if (value.equals(store.setting("theme", "system"))) return;
            store.putSetting("theme", value);
            ThemeFade.restart(a, tab);
        });
        if (!VoidstrapApp.colorTheme(currentTheme)) {
            boolean dynamic = android.os.Build.VERSION.SDK_INT >= 31;
            String[] accents = dynamic
                    ? new String[]{a.getString(R.string.settings_accent_system), a.getString(R.string.settings_accent_brand)}
                    : new String[]{a.getString(R.string.settings_accent_brand)};
            int accentIndex = dynamic && !"brand".equals(store.setting("accent", "system")) ? 0 : accents.length - 1;
            choice(parent, a.getString(R.string.settings_accent), dynamic ? null : a.getString(R.string.settings_accent_note), accents, accentIndex, i -> {
                String value = dynamic && i == 0 ? "system" : "brand";
                if (value.equals(store.setting("accent", "system"))) return;
                store.putSetting("accent", value);
                ThemeFade.restart(a, tab);
            });
        }
        choice(parent, a.getString(R.string.settings_language), a.getString(R.string.settings_language_body), Translator.NAMES, Translator.indexOf(Translator.language(store)), i -> {
            String code = Translator.CODES[i];
            if (code.equals(Translator.language(store))) return;
            store.putSetting(Translator.SETTING, code);
            LiveTranslator.apply(a);
        });
    }

    private static String value(CharSequence label, CharSequence summary) {
        return summary == null || summary.length() == 0 ? label.toString() : label + " \u00b7 " + summary;
    }
}
