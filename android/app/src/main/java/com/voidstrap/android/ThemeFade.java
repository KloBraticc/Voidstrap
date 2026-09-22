package com.voidstrap.android;

import android.app.Activity;
import android.graphics.Bitmap;
import android.os.Handler;
import android.os.Looper;
import android.view.PixelCopy;
import android.view.View;
import android.view.ViewGroup;
import android.view.animation.PathInterpolator;
import android.widget.ImageView;

public final class ThemeFade {
    private static final int MAX_SIZE = 1280;
    private static final long DURATION_MS = 260;
    private static Bitmap shot;

    private ThemeFade() {
    }

    public static void restart(Activity a, int tab) {
        if (a == null || a.getWindow() == null) return;
        View decor = a.getWindow().getDecorView();
        int w = decor.getWidth();
        int h = decor.getHeight();
        if (w <= 0 || h <= 0 || android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.O) {
            relaunch(a, tab);
            return;
        }
        float scale = Math.min(1f, MAX_SIZE / (float) Math.max(w, h));
        Bitmap bitmap;
        try {
            bitmap = Bitmap.createBitmap(Math.max(1, Math.round(w * scale)), Math.max(1, Math.round(h * scale)), Bitmap.Config.ARGB_8888);
        } catch (RuntimeException | OutOfMemoryError e) {
            relaunch(a, tab);
            return;
        }
        try {
            PixelCopy.request(a.getWindow(), bitmap, result -> {
                if (result == PixelCopy.SUCCESS) shot = bitmap;
                else Crash.run("theme snapshot", bitmap::recycle);
                relaunch(a, tab);
            }, new Handler(Looper.getMainLooper()));
        } catch (RuntimeException e) {
            Crash.run("theme snapshot", bitmap::recycle);
            relaunch(a, tab);
        }
    }

    private static void relaunch(Activity a, int tab) {
        if (a == null || a.isFinishing() || a.isDestroyed()) return;
        Crash.run("theme restart", () -> {
            if (a.getIntent() != null) a.getIntent().putExtra(MainActivity.EXTRA_TAB, tab);
            a.recreate();
        });
    }

    public static void play(Activity a) {
        Bitmap b = shot;
        shot = null;
        if (b == null || b.isRecycled()) return;
        if (a == null || a.getWindow() == null || !(a.getWindow().getDecorView() instanceof ViewGroup)) {
            Crash.run("theme fade", b::recycle);
            return;
        }
        Crash.run("theme fade", () -> fade(a, b));
    }

    private static void fade(Activity a, Bitmap b) {
        ViewGroup decor = (ViewGroup) a.getWindow().getDecorView();
        ImageView cover = new ImageView(a);
        cover.setImageBitmap(b);
        cover.setScaleType(ImageView.ScaleType.FIT_XY);
        cover.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);
        decor.addView(cover, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        cover.animate()
                .alpha(0f)
                .setDuration(DURATION_MS)
                .setInterpolator(new PathInterpolator(0.61f, 1f, 0.88f, 1f))
                .withEndAction(() -> Crash.run("theme fade end", () -> {
                    decor.removeView(cover);
                    cover.setImageDrawable(null);
                    b.recycle();
                }));
    }
}
