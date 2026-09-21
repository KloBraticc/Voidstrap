package com.voidstrap.android;

import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Rect;

import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.pm.ShortcutInfoCompat;
import androidx.core.content.pm.ShortcutManagerCompat;
import androidx.core.graphics.drawable.IconCompat;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class Shortcuts {
    public static final String EXTRA_LINK = "link";
    public static final String EXTRA_NAME = "name";
    private static final int DYNAMIC_MAX = 4;

    private static volatile String published;

    private Shortcuts() {
    }

    public static boolean pinSupported(Context c) {
        return ShortcutManagerCompat.isRequestPinShortcutSupported(c);
    }

    public static void request(AppCompatActivity a, Deeplink d, String name, String iconUrl) {
        if (!pinSupported(a)) {
            Ui.say(a, R.string.shortcut_unsupported);
            return;
        }
        Store store = Store.get(a);
        store.work.execute(() -> {
            Bitmap b = art(a.getApplicationContext(), iconUrl);
            store.main.post(() -> {
                if (!a.isFinishing() && !a.isDestroyed()) pin(a, d, name, b);
            });
        });
    }

    public static void watch(Context c) {
        Context app = c.getApplicationContext();
        Store store = Store.get(app);
        Runnable check = () -> {
            List<Store.Game> top = new ArrayList<>();
            StringBuilder sig = new StringBuilder();
            for (Store.Game g : store.library) {
                if (top.size() == DYNAMIC_MAX) break;
                top.add(new Store.Game(g.placeId, g.universeId, g.name, g.iconUrl, g.added));
                sig.append(g.placeId).append('\n').append(g.name).append('\n').append(g.iconUrl).append('\n');
            }
            String s = sig.toString();
            if (s.equals(published)) return;
            published = s;
            store.work.execute(() -> publish(app, top));
        };
        store.observe(check);
        check.run();
    }

    private static void publish(Context app, List<Store.Game> games) {
        List<ShortcutInfoCompat> list = new ArrayList<>();
        for (Store.Game g : games) {
            list.add(builder(app, Deeplink.place(g.placeId, null, null), g.title(), art(app, g.iconUrl)).setRank(list.size()).build());
        }
        try {
            ShortcutManagerCompat.setDynamicShortcuts(app, list);
        } catch (RuntimeException e) {
            published = null;
        }
    }

    private static Bitmap art(Context app, String iconUrl) {
        return iconUrl == null || iconUrl.isEmpty() ? null : Net.loadBitmap(app, iconUrl, 256);
    }

    private static String label(Context c, String name) {
        return name == null || name.isEmpty() ? c.getString(R.string.shortcut_default_label) : Store.clip(name, 200);
    }

    private static ShortcutInfoCompat.Builder builder(Context c, Deeplink d, String name, Bitmap art) {
        String label = label(c, name);
        Intent intent = new Intent(c, ShortcutActivity.class)
                .setAction(Intent.ACTION_VIEW)
                .putExtra(EXTRA_LINK, d.toUri().toString())
                .putExtra(EXTRA_NAME, label);
        IconCompat icon = art != null ? IconCompat.createWithAdaptiveBitmap(adaptive(art)) : IconCompat.createWithResource(c, R.mipmap.ic_launcher);
        return new ShortcutInfoCompat.Builder(c, d.stableId())
                .setShortLabel(Store.clip(label, 25))
                .setLongLabel(Store.clip(label, 50))
                .setIcon(icon)
                .setIntent(intent);
    }

    private static void pin(AppCompatActivity a, Deeplink d, String name, Bitmap art) {
        ShortcutInfoCompat info = builder(a, d, name, art).build();
        String label = label(a, name);
        String id = info.getId();
        List<ShortcutInfoCompat> pinned = ShortcutManagerCompat.getShortcuts(a, ShortcutManagerCompat.FLAG_MATCH_PINNED);
        for (ShortcutInfoCompat s : pinned) {
            if (s.getId().equals(id)) {
                ShortcutManagerCompat.updateShortcuts(a, Collections.singletonList(info));
                Notify.say(a, Notify.SHORTCUTS, a.getString(R.string.shortcut_already, label));
                return;
            }
        }
        Intent callback = new Intent(a, PinResultReceiver.class).putExtra(EXTRA_NAME, label);
        PendingIntent pi = PendingIntent.getBroadcast(a, id.hashCode(), callback, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        boolean asked = ShortcutManagerCompat.requestPinShortcut(a, info, pi.getIntentSender());
        Notify.say(a, asked ? Notify.SHORTCUTS : null, asked ? R.string.shortcut_requested : R.string.shortcut_failed);
    }

    static Bitmap adaptive(Bitmap art) {
        int size = 216;
        int inner = 144;
        Bitmap out = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(out);
        Bitmap one = Bitmap.createScaledBitmap(art, 1, 1, true);
        c.drawColor(one.getPixel(0, 0) | 0xFF000000);
        int off = (size - inner) / 2;
        Paint p = new Paint(Paint.FILTER_BITMAP_FLAG | Paint.ANTI_ALIAS_FLAG);
        c.drawBitmap(art, null, new Rect(off, off, off + inner, off + inner), p);
        return out;
    }
}
