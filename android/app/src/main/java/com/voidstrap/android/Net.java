package com.voidstrap.android;

import android.app.ActivityManager;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.util.LruCache;
import android.widget.ImageView;

import org.json.JSONArray;
import org.json.JSONException;
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
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Future;
import java.util.function.Consumer;

public final class Net {
    private static final int MAX_JSON = 1024 * 1024;
    private static final int MAX_IMAGE = 4 * 1024 * 1024;
    private static final int TIMEOUT_MS = 8000;

    private static LruCache<String, Bitmap> memory;
    private static final ConcurrentHashMap<Long, Store.Game> metaCache = new ConcurrentHashMap<>();

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
                m = fetchMeta(placeId);
            } catch (IOException | JSONException ignored) {
            }
            Meta result = m;
            if (!Thread.currentThread().isInterrupted()) store.main.post(() -> done.accept(result));
        });
    }

    public static Meta fetchMeta(long placeId) throws IOException, JSONException {
        Store.Game cached = metaCache.get(placeId);
        Meta m = new Meta();
        m.placeId = placeId;
        if (cached != null) {
            m.universeId = cached.universeId;
            m.name = cached.name;
            m.iconUrl = cached.iconUrl;
            return m;
        }
        JSONObject u = json("https://apis.roblox.com/universes/v1/places/" + placeId + "/universe");
        m.universeId = u.optLong("universeId", 0);
        if (m.universeId <= 0) return null;
        JSONArray games = json("https://games.roblox.com/v1/games?universeIds=" + m.universeId).optJSONArray("data");
        if (games != null && games.length() > 0) m.name = Store.clip(games.getJSONObject(0).optString("name", ""), 200);
        try {
            JSONArray icons = json("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + m.universeId + "&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false").optJSONArray("data");
            if (icons != null && icons.length() > 0) {
                String url = icons.getJSONObject(0).optString("imageUrl", "");
                if (url.startsWith("https://")) m.iconUrl = url;
            }
        } catch (IOException | JSONException ignored) {
        }
        metaCache.put(placeId, new Store.Game(placeId, m.universeId, m.name, m.iconUrl, 0));
        return m;
    }

    static void ensureDir(File dir, String label) throws IOException {
        if (dir.isDirectory()) return;
        dir.mkdirs();
        if (!dir.isDirectory()) throw new IOException(label);
    }

    public static JSONObject cachedJson(Context c, String url, long maxAgeMs) throws IOException, JSONException {
        File dir = new File(c.getCacheDir(), "feeds");
        ensureDir(dir, "cache");
        File f = new File(dir, sha1(url) + ".json");
        if (f.isFile() && System.currentTimeMillis() - f.lastModified() < maxAgeMs) {
            try {
                return new JSONObject(new String(java.nio.file.Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8));
            } catch (IOException | JSONException ignored) {
            }
        }
        try {
            byte[] data = get(url, MAX_JSON);
            JSONObject parsed = new JSONObject(new String(data, StandardCharsets.UTF_8));
            File tmp = new File(dir, f.getName() + ".tmp");
            try (FileOutputStream out = new FileOutputStream(tmp)) {
                out.write(data);
            }
            if (!tmp.renameTo(f)) tmp.delete();
            return parsed;
        } catch (IOException | JSONException e) {
            if (f.isFile()) return new JSONObject(new String(java.nio.file.Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8));
            throw e;
        }
    }

    static JSONObject json(String url) throws IOException, JSONException {
        return new JSONObject(new String(get(url, MAX_JSON), StandardCharsets.UTF_8));
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

    public static void download(String url, File dest, long limit, java.util.concurrent.atomic.AtomicBoolean cancel) throws IOException {
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
                while ((n = in.read(buf)) > 0) {
                    total += n;
                    if (total > limit) throw new IOException("too large");
                    if ((cancel != null && cancel.get()) || Thread.currentThread().isInterrupted()) throw new IOException("cancelled");
                    out.write(buf, 0, n);
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
                b = loadBitmap(c, url, px);
                if (b == null) return;
                memory(c).put(key, b);
            }
            Bitmap ready = b;
            store.main.post(() -> {
                if (url.equals(view.getTag(R.id.tag_image))) view.setImageBitmap(ready);
            });
        });
    }

    public static Bitmap loadBitmap(Context c, String url, int px) {
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
        } catch (IOException | OutOfMemoryError e) {
            return null;
        }
    }

    public static Bitmap decode(File f, int px) {
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeFile(f.getPath(), o);
        if (o.outWidth <= 0 || o.outHeight <= 0) return null;
        int sample = 1;
        while (o.outWidth / (sample * 2) >= px && o.outHeight / (sample * 2) >= px) sample *= 2;
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
        } catch (OutOfMemoryError e) {
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
