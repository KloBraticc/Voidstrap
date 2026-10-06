package com.voidstrap.android;

import android.app.Activity;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Matrix;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.drawable.Drawable;
import android.media.ExifInterface;
import android.net.Uri;
import android.view.View;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

public final class AppBackground {
    public static final String SETTING = "background";
    public static final String DIM = "backgroundDim";
    public static final String CUSTOM = "custom";
    public static final int DEFAULT_DIM = 55;
    private static final String FILE = "app_background.jpg";
    private static final long MAX_BYTES = 40L * 1024 * 1024;
    private static final int MAX_EDGE = 2560;
    private static Bitmap cached;
    private static long cachedStamp;

    private AppBackground() {
    }

    public static boolean enabled(Context c) {
        return CUSTOM.equals(Store.get(c).setting(SETTING, "")) && file(c).isFile();
    }

    public static int dim(Context c) {
        try {
            return Math.max(0, Math.min(90, Integer.parseInt(Store.get(c).setting(DIM, String.valueOf(DEFAULT_DIM)))));
        } catch (NumberFormatException e) {
            return DEFAULT_DIM;
        }
    }

    private static File file(Context c) {
        return new File(c.getFilesDir(), FILE);
    }

    public static boolean install(Context c, Uri uri) {
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        if (!decode(c, uri, bounds) || bounds.outWidth <= 0 || bounds.outHeight <= 0) return false;
        int sample = 1;
        while (Math.max(bounds.outWidth, bounds.outHeight) / (sample * 2) >= MAX_EDGE) sample *= 2;
        BitmapFactory.Options options = new BitmapFactory.Options();
        options.inSampleSize = sample;
        options.inPreferredConfig = Bitmap.Config.ARGB_8888;
        Bitmap image = decodeBitmap(c, uri, options);
        if (image == null) return false;
        image = rotate(image, orientation(c, uri));
        android.util.DisplayMetrics metrics = c.getResources().getDisplayMetrics();
        int target = Math.max(1080, Math.min(MAX_EDGE, Math.max(metrics.widthPixels, metrics.heightPixels)));
        int longest = Math.max(image.getWidth(), image.getHeight());
        if (longest > target) {
            float scale = target / (float) longest;
            try {
                Bitmap scaled = Bitmap.createScaledBitmap(image, Math.max(1, Math.round(image.getWidth() * scale)), Math.max(1, Math.round(image.getHeight() * scale)), true);
                if (scaled != image) image.recycle();
                image = scaled;
            } catch (OutOfMemoryError e) {
                image.recycle();
                return false;
            }
        }
        File tmp = new File(c.getFilesDir(), FILE + ".tmp");
        boolean ok = false;
        try (OutputStream out = new FileOutputStream(tmp)) {
            ok = image.compress(Bitmap.CompressFormat.JPEG, 92, out);
        } catch (IOException e) {
            ok = false;
        } finally {
            image.recycle();
        }
        if (!ok || !tmp.renameTo(file(c))) {
            tmp.delete();
            return false;
        }
        synchronized (AppBackground.class) {
            cached = null;
        }
        return true;
    }

    public static void remove(Context c) {
        file(c).delete();
        synchronized (AppBackground.class) {
            cached = null;
        }
    }

    private static boolean decode(Context c, Uri uri, BitmapFactory.Options options) {
        try (InputStream in = c.getContentResolver().openInputStream(uri)) {
            if (in == null) return false;
            BitmapFactory.decodeStream(new Limited(in), null, options);
            return true;
        } catch (IOException | SecurityException | IllegalArgumentException e) {
            return false;
        }
    }

    @Nullable
    private static Bitmap decodeBitmap(Context c, Uri uri, BitmapFactory.Options options) {
        try (InputStream in = c.getContentResolver().openInputStream(uri)) {
            return in == null ? null : BitmapFactory.decodeStream(new Limited(in), null, options);
        } catch (IOException | SecurityException | IllegalArgumentException | OutOfMemoryError e) {
            return null;
        }
    }

    private static int orientation(Context c, Uri uri) {
        try (InputStream in = c.getContentResolver().openInputStream(uri)) {
            if (in == null) return ExifInterface.ORIENTATION_NORMAL;
            return new ExifInterface(in).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL);
        } catch (IOException | SecurityException | IllegalArgumentException e) {
            return ExifInterface.ORIENTATION_NORMAL;
        }
    }

    private static Bitmap rotate(Bitmap image, int orientation) {
        float degrees = orientation == ExifInterface.ORIENTATION_ROTATE_90 ? 90f
                : orientation == ExifInterface.ORIENTATION_ROTATE_180 ? 180f
                : orientation == ExifInterface.ORIENTATION_ROTATE_270 ? 270f : 0f;
        if (degrees == 0f) return image;
        Matrix m = new Matrix();
        m.postRotate(degrees);
        try {
            Bitmap rotated = Bitmap.createBitmap(image, 0, 0, image.getWidth(), image.getHeight(), m, true);
            if (rotated != image) image.recycle();
            return rotated;
        } catch (OutOfMemoryError e) {
            return image;
        }
    }

    @Nullable
    private static Bitmap load(Context c) {
        File f = file(c);
        long stamp = f.lastModified();
        synchronized (AppBackground.class) {
            if (cached != null && cachedStamp == stamp) return cached;
        }
        Bitmap image;
        try {
            image = BitmapFactory.decodeFile(f.getAbsolutePath());
        } catch (OutOfMemoryError e) {
            image = null;
        }
        synchronized (AppBackground.class) {
            cached = image;
            cachedStamp = stamp;
        }
        return image;
    }

    private static View target(Activity a) {
        return a.findViewById(a.findViewById(R.id.sidebar_items) != null ? R.id.snackbar_anchor : R.id.column);
    }

    public static void apply(Activity a) {
        View target = target(a);
        if (target == null) return;
        Object tag = target.getTag(R.id.app_background);
        if (tag == null) {
            tag = new Object[]{target.getBackground()};
            target.setTag(R.id.app_background, tag);
        }
        Drawable original = (Drawable) ((Object[]) tag)[0];
        if (!enabled(a)) {
            if (target.getBackground() instanceof Cover) target.setBackground(original);
            return;
        }
        int overlay = Ui.attr(a, R.attr.vsWindow);
        int dim = dim(a);
        Bitmap image;
        synchronized (AppBackground.class) {
            image = cached != null && cachedStamp == file(a).lastModified() ? cached : null;
        }
        if (image != null) {
            target.setBackground(new Cover(image, overlay, dim));
            return;
        }
        Context app = a.getApplicationContext();
        Store store = Store.get(a);
        store.work.execute(() -> {
            Bitmap loaded = load(app);
            store.main.post(() -> {
                if (a.isFinishing() || a.isDestroyed() || !enabled(a)) return;
                target.setBackground(loaded == null ? original : new Cover(loaded, overlay, dim));
            });
        });
    }

    public static void setDim(Activity a, int dim) {
        View target = target(a);
        if (target != null && target.getBackground() instanceof Cover) ((Cover) target.getBackground()).dim(dim);
    }

    private static final class Cover extends Drawable {
        private final Bitmap image;
        private final Paint paint = new Paint(Paint.FILTER_BITMAP_FLAG | Paint.ANTI_ALIAS_FLAG);
        private final Paint shade = new Paint();
        private final Matrix matrix = new Matrix();
        private final int overlay;

        Cover(Bitmap image, int overlay, int dim) {
            this.image = image;
            this.overlay = overlay;
            dim(dim);
        }

        void dim(int dim) {
            int alpha = Math.round(Math.max(0, Math.min(90, dim)) * 2.55f);
            shade.setColor((overlay & 0x00FFFFFF) | (alpha << 24));
            invalidateSelf();
        }

        @Override
        protected void onBoundsChange(@NonNull Rect bounds) {
            float scale = Math.max(bounds.width() / (float) image.getWidth(), bounds.height() / (float) image.getHeight());
            matrix.setScale(scale, scale);
            matrix.postTranslate(bounds.left + (bounds.width() - image.getWidth() * scale) / 2f, bounds.top + (bounds.height() - image.getHeight() * scale) / 2f);
        }

        @Override
        public void draw(@NonNull Canvas canvas) {
            Rect b = getBounds();
            canvas.save();
            canvas.clipRect(b);
            canvas.drawBitmap(image, matrix, paint);
            canvas.restore();
            canvas.drawRect(b, shade);
        }

        @Override
        public void setAlpha(int alpha) {
            paint.setAlpha(alpha);
        }

        @Override
        public void setColorFilter(@Nullable ColorFilter filter) {
            paint.setColorFilter(filter);
        }

        @Override
        public int getOpacity() {
            return PixelFormat.OPAQUE;
        }
    }

    private static final class Limited extends java.io.FilterInputStream {
        private long total;

        Limited(InputStream in) {
            super(in);
        }

        @Override
        public int read() throws IOException {
            int b = super.read();
            if (b >= 0 && ++total > MAX_BYTES) throw new IOException("Image too large");
            return b;
        }

        @Override
        public int read(byte[] buffer, int offset, int length) throws IOException {
            int n = super.read(buffer, offset, length);
            if (n > 0 && (total += n) > MAX_BYTES) throw new IOException("Image too large");
            return n;
        }
    }
}
