package com.voidstrap.android;

import android.content.Context;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.util.SparseArray;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

public final class AppFont {
    public static final String SETTING = "appFont";
    public static final String CUSTOM = "custom";
    public static final String[] FAMILIES = {"", "sans-serif-condensed", "serif", "casual", "cursive", "sans-serif-smallcaps", "serif-monospace", "monospace", CUSTOM};
    public static final String[] TYPES = {"font/*", "application/x-font-ttf", "application/x-font-otf", "application/font-sfnt", "application/vnd.ms-opentype", "application/octet-stream"};
    private static final String FILE = "app_font";
    private static final long MAX_BYTES = 20L * 1024 * 1024;
    private static final Object KEEP = new Object();
    private static final SparseArray<Typeface> STYLED = new SparseArray<>();
    private static String loaded;
    private static Typeface base;

    private AppFont() {
    }

    public static int index(Store s) {
        String v = s.setting(SETTING, "");
        for (int i = 0; i < FAMILIES.length; i++) if (FAMILIES[i].equals(v)) return i;
        return 0;
    }

    public static void keep(TextView t) {
        t.setTag(R.id.app_font, KEEP);
    }

    public static void reload() {
        loaded = null;
    }

    private static Typeface base(Context c) {
        String key = Store.get(c).setting(SETTING, "");
        if (!key.equals(loaded)) {
            loaded = key;
            STYLED.clear();
            base = key.isEmpty() ? null : CUSTOM.equals(key) ? load(file(c)) : Typeface.create(key, Typeface.NORMAL);
        }
        return base;
    }

    public static void watch(View root) {
        if (root == null || base(root.getContext()) == null) return;
        if (root.getTag(R.id.app_font_root) == null) {
            root.setTag(R.id.app_font_root, Boolean.TRUE);
            root.getViewTreeObserver().addOnGlobalLayoutListener(() -> Crash.run("font pass", () -> apply(root)));
        }
        apply(root);
    }

    private static void apply(View root) {
        Typeface font = base(root.getContext());
        if (font != null) walk(root, font);
    }

    private static void walk(View v, Typeface font) {
        if (v instanceof TextView) set((TextView) v, font);
        if (!(v instanceof ViewGroup)) return;
        ViewGroup g = (ViewGroup) v;
        for (int i = 0; i < g.getChildCount(); i++) walk(g.getChildAt(i), font);
    }

    private static void set(TextView t, Typeface font) {
        Object tag = t.getTag(R.id.app_font);
        Typeface current = t.getTypeface();
        if (tag == KEEP || current == Typeface.MONOSPACE || (current != null && current == tag)) return;
        int weight = current == null ? 400 : Build.VERSION.SDK_INT >= 28 ? current.getWeight() : current.isBold() ? 700 : 400;
        Typeface next = styled(font, weight, current != null && current.isItalic());
        t.setTag(R.id.app_font, next);
        t.setTypeface(next);
    }

    private static Typeface styled(Typeface font, int weight, boolean italic) {
        int key = weight * 2 + (italic ? 1 : 0);
        Typeface t = STYLED.get(key);
        if (t == null) {
            t = Build.VERSION.SDK_INT >= 28
                    ? Typeface.create(font, weight, italic)
                    : Typeface.create(font, weight >= 600 ? (italic ? Typeface.BOLD_ITALIC : Typeface.BOLD) : (italic ? Typeface.ITALIC : Typeface.NORMAL));
            STYLED.put(key, t);
        }
        return t;
    }

    private static File file(Context c) {
        return new File(c.getFilesDir(), FILE);
    }

    private static Typeface load(File f) {
        if (!f.isFile()) return null;
        try {
            Typeface t = Typeface.createFromFile(f);
            return t == null || t == Typeface.DEFAULT ? null : t;
        } catch (RuntimeException e) {
            return null;
        }
    }

    public static boolean install(Context c, Uri uri) {
        File tmp = new File(c.getFilesDir(), FILE + ".tmp");
        boolean ok = false;
        try (InputStream in = c.getContentResolver().openInputStream(uri); OutputStream out = new FileOutputStream(tmp)) {
            if (in == null) return false;
            byte[] buf = new byte[65536];
            long total = 0;
            int n;
            while ((n = in.read(buf)) > 0) {
                total += n;
                if (total > MAX_BYTES) return false;
                out.write(buf, 0, n);
            }
            ok = true;
        } catch (IOException | SecurityException | IllegalArgumentException e) {
            return false;
        } finally {
            if (!ok) tmp.delete();
        }
        if (load(tmp) == null || !tmp.renameTo(file(c))) {
            tmp.delete();
            return false;
        }
        return true;
    }
}
