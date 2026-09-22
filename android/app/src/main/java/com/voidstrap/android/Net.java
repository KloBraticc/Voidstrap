package com.voidstrap.android;

import android.app.ActivityManager;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.util.LruCache;
import android.widget.ImageView;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Locale;
import java.util.concurrent.Future;
import java.util.function.Consumer;

public final class Net {
    private static final int MAX_JSON = 1024 * 1024;
    private static final int MAX_IMAGE = 4 * 1024 * 1024;
    private static final int TIMEOUT_MS = 8000;

    private static LruCache<String, Bitmap> memory;

    private Net() {
    }

    public static final class Meta {
        public long placeId;
        public long universeId;
        public String name;
        public String iconUrl;
    }

    public static Future<?> meta(Context c, long placeId, Consumer<Meta> done) {
        Store store = Store.get(c);
        return store.work.submit(() -> {
            Meta m = null;
            try {
                Object v = Core.value("feed.meta", Core.feed(c, "place", placeId));
                if (v instanceof JSONObject) {
                    JSONObject o = (JSONObject) v;
                    m = new Meta();
                    m.placeId = placeId;
                    m.universeId = o.optLong("universeId");
                    m.name = Core.opt(o, "name");
                    m.iconUrl = Core.opt(o, "iconUrl");
                }
            } catch (IOException ignored) {
            }
            Meta result = m;
            if (!Thread.currentThread().isInterrupted()) store.main.post(() -> done.accept(result));
        });
    }

    static void ensureDir(File dir, String label) throws IOException {
        if (dir.isDirectory()) return;
        dir.mkdirs();
        if (!dir.isDirectory()) throw new IOException(label);
    }

    private static void requireHttps(String url) throws IOException {
        if (url == null || !url.startsWith("https://")) throw new IOException("not https");
    }

    private static byte[] get(String url, int limit) throws IOException {
        requireHttps(url);
        HttpURLConnection con = (HttpURLConnection) new URL(url).openConnection();
        con.setConnectTimeout(TIMEOUT_MS);
        con.setReadTimeout(TIMEOUT_MS);
        con.setRequestProperty("Accept", "application/json, image/png");
        con.setRequestProperty("User-Agent", "Voidstrap Android/" + BuildConfig.VERSION_NAME);
        try {
            int code = con.getResponseCode();
            if (code != 200) throw new IOException("HTTP " + code);
            try (InputStream in = con.getInputStream()) {
                ByteArrayOutputStream out = new ByteArrayOutputStream();
                byte[] buf = new byte[16384];
                int n;
                while ((n = in.read(buf)) > 0) {
                    if (out.size() + n > limit) throw new IOException("too large");
                    if (Thread.currentThread().isInterrupted()) throw new IOException("cancelled");
                    out.write(buf, 0, n);
                }
                return out.toByteArray();
            }
        } finally {
            con.disconnect();
        }
    }

    public static byte[] fetch(String url, int limit) throws IOException {
        return get(url, limit);
    }

    public static String text(String url, int limit) throws IOException {
        return new String(get(url, limit), StandardCharsets.UTF_8);
    }

    public interface Progress {
        void at(int percent);
    }

    public static void download(String url, File dest, long limit, java.util.concurrent.atomic.AtomicBoolean cancel) throws IOException {
        downloadTracked(url, dest, limit, cancel, null);
    }

    public static void downloadTracked(String url, File dest, long limit, java.util.concurrent.atomic.AtomicBoolean cancel, Progress progress) throws IOException {
        requireHttps(url);
        HttpURLConnection con = (HttpURLConnection) new URL(url).openConnection();
        con.setConnectTimeout(15000);
        con.setReadTimeout(30000);
        con.setInstanceFollowRedirects(true);
        con.setRequestProperty("User-Agent", "Voidstrap Android/" + BuildConfig.VERSION_NAME);
        File tmp = new File(dest.getPath() + ".part");
        try {
            int code = con.getResponseCode();
            if (code != 200) throw new IOException("HTTP " + code);
            long declared = con.getContentLengthLong();
            if (declared > limit) throw new IOException("too large");
            File parent = dest.getParentFile();
            if (parent != null) ensureDir(parent, "mkdir");
            try (InputStream in = con.getInputStream(); FileOutputStream out = new FileOutputStream(tmp)) {
                byte[] buf = new byte[65536];
                long total = 0;
                int n;
                int last = -1;
                while ((n = in.read(buf)) > 0) {
                    total += n;
                    if (total > limit) throw new IOException("too large");
                    if ((cancel != null && cancel.get()) || Thread.currentThread().isInterrupted()) throw new IOException("cancelled");
                    out.write(buf, 0, n);
                    if (progress != null && declared > 0) {
                        int percent = (int) Math.min(100, total * 100 / declared);
                        if (percent != last) {
                            last = percent;
                            progress.at(percent);
                        }
                    }
                }
                out.getFD().sync();
            }
            if (!tmp.renameTo(dest)) throw new IOException("move");
        } finally {
            tmp.delete();
            con.disconnect();
        }
    }

    private static synchronized LruCache<String, Bitmap> memory(Context c) {
        if (memory == null) {
            ActivityManager am = (ActivityManager) c.getSystemService(Context.ACTIVITY_SERVICE);
            int cap = (am != null && am.isLowRamDevice() ? 6 : 16) * 1024 * 1024;
            int bytes = (int) Math.min(cap, Runtime.getRuntime().maxMemory() / 8);
            memory = new LruCache<String, Bitmap>(bytes) {
                @Override
                protected int sizeOf(String key, Bitmap value) {
                    return value.getByteCount();
                }
            };
        }
        return memory;
    }

    public static void trim() {
        LruCache<String, Bitmap> m = memory;
        if (m != null) m.evictAll();
    }

    public static void image(ImageView view, String url, int fallback, int px) {
        if (view == null) return;
        Context c = view.getContext().getApplicationContext();
        view.setTag(R.id.tag_image, url);
        if (url == null || url.isEmpty()) {
            view.setImageResource(fallback);
            return;
        }
        String key = url + "@" + px;
        Bitmap hit = memory(c).get(key);
        if (hit != null) {
            view.setImageBitmap(hit);
            return;
        }
        view.setImageResource(fallback);
        Store store = Store.get(c);
        store.work.execute(() -> {
            Bitmap b = memory(c).get(key);
            if (b == null) {
                b = Crash.call("image load", () -> loadBitmap(c, url, px), null);
                if (b == null) return;
                memory(c).put(key, b);
            }
            Bitmap ready = b;
            store.main.post(() -> {
                if (!ready.isRecycled() && url.equals(view.getTag(R.id.tag_image))) view.setImageBitmap(ready);
            });
        });
    }

    public static Bitmap loadBitmap(Context c, String url, int px) {
        if (c == null || url == null || url.isEmpty()) return null;
        try {
            if (url.startsWith("file:")) {
                int hash = url.indexOf('#');
                return decode(new File(hash < 0 ? url.substring(5) : url.substring(5, hash)), px);
            }
            File dir = new File(c.getCacheDir(), "icons");
            if (!dir.isDirectory() && !dir.mkdirs()) return null;
            File f = new File(dir, sha1(url) + ".png");
            if (!f.isFile()) {
                if (!url.startsWith("https://")) return null;
                byte[] data = get(url, MAX_IMAGE);
                File tmp = new File(dir, f.getName() + ".tmp");
                try (FileOutputStream out = new FileOutputStream(tmp)) {
                    out.write(data);
                }
                if (!tmp.renameTo(f)) return null;
            }
            return decode(f, px);
        } catch (IOException | RuntimeException | OutOfMemoryError e) {
            return null;
        }
    }

    public static Bitmap decode(File f, int px) {
        if (f == null || !f.isFile()) return null;
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        try {
            BitmapFactory.decodeFile(f.getPath(), o);
        } catch (RuntimeException | OutOfMemoryError e) {
            return null;
        }
        if (o.outWidth <= 0 || o.outHeight <= 0) return null;
        int sample = 1;
        int wanted = Math.max(1, px);
        while (sample < 1 << 12 && o.outWidth / (sample * 2) >= wanted && o.outHeight / (sample * 2) >= wanted) sample *= 2;
        BitmapFactory.Options d = new BitmapFactory.Options();
        d.inSampleSize = sample;
        int shortSide = Math.min(o.outWidth, o.outHeight) / sample;
        if (shortSide > px * 5 / 4) {
            d.inScaled = true;
            d.inDensity = shortSide;
            d.inTargetDensity = px;
        }
        try {
            return BitmapFactory.decodeFile(f.getPath(), d);
        } catch (RuntimeException | OutOfMemoryError e) {
            return null;
        }
    }

    private static String sha1(String s) {
        try {
            byte[] h = MessageDigest.getInstance("SHA-1").digest(s.getBytes(StandardCharsets.UTF_8));
            StringBuilder sb = new StringBuilder();
            for (byte b : h) sb.append(String.format(Locale.ROOT, "%02x", b));
            return sb.toString();
        } catch (NoSuchAlgorithmException e) {
            return Integer.toHexString(s.hashCode());
        }
    }
}
