package com.voidstrap.android;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.os.ParcelFileDescriptor;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ModPresets {
    public static final String DEATH = "content/sounds/oof.ogg";
    public static final String AVATAR = "ExtraContent/places/Mobile.rbxl";
    public static final String EMOJI = "content/fonts/TwemojiMozilla.ttf";
    public static final String FONT = "content/fonts/CustomFont.ttf";
    public static final String SKY = "PlatformContent/pc/textures/sky";
    public static final String SHIFTLOCK = "content/textures/MouseLockedCursor.png";

    public static final String[][] OLD_SOUNDS = {
            {"content/sounds/action_footsteps_plastic.mp3", "Sounds/OldWalk.mp3"},
            {"content/sounds/action_jump.mp3", "Sounds/OldJump.mp3"},
            {"content/sounds/action_get_up.mp3", "Sounds/OldGetUp.mp3"},
            {"content/sounds/action_falling.mp3", "Sounds/Empty.mp3"},
            {"content/sounds/action_jump_land.mp3", "Sounds/Empty.mp3"},
            {"content/sounds/action_swim.mp3", "Sounds/Empty.mp3"},
            {"content/sounds/impact_water.mp3", "Sounds/Empty.mp3"}
    };
    public static final String[][] OLD_AVATAR = {{AVATAR, "OldAvatarBackground.rbxl"}};

    public static final int[] DEATH_VOLUMES = {25, 50, 75, 90, 100, 125, 150, 200, 250, 300, 400, 500};
    public static final int[] FONT_SCALES = {0, 25, 50, 75, 90, 100, 125, 150, 200, 250, 300, 400, 500};

    public static final String[] EMOJI_FILES = {"", "Catmoji.ttf", "Win1122H2SegoeUIEmoji.ttf", "Win10April2018SegoeUIEmoji.ttf", "Win8.1SegoeUIEmoji.ttf"};
    public static final String[] EMOJI_HASHES = {"",
            "58D781FF4800AB10144A6FC0BB30479881F5048ECD57196148C9AB791FDAB622",
            "4E3CEC7D1995B6D74102C0B4669E4507AC35CBF9A9830A93AC14C6E40DFE36A9",
            "7C0244DD8EEB7C6BDECDFC3F9E59833527FC18A66D0295CE47339069692A2B4F",
            "86BE288EED6561684BE645F671409210C914815E3833A0FC3B587CBF64C03928"};
    public static final long[] EMOJI_SIZES = {0, 1335828, 2838884, 2072388, 676304};
    private static final String EMOJI_URL = "https://github.com/BloxstrapLabs/rbxcustom-fontemojis/releases/download/my-phone-is-78-percent/";

    public static final int CURSOR_DEFAULT = 0;
    public static final int CURSOR_CUSTOM = 10;
    public static final int CURSOR_VOIDSTRAP = 9;
    public static final int[] CURSOR_ORDER = {0, 9, 4, 3, 2, 1, 5, 6, 7, 8, 10};
    private static final Map<Integer, String> CURSOR_FOLDERS = new LinkedHashMap<>();

    static {
        CURSOR_FOLDERS.put(9, "BibataModernIce");
        CURSOR_FOLDERS.put(1, "FPSCursor");
        CURSOR_FOLDERS.put(2, "CleanCursor");
        CURSOR_FOLDERS.put(3, "DotCursor");
        CURSOR_FOLDERS.put(4, "StoofsCursor");
        CURSOR_FOLDERS.put(5, "From2006");
        CURSOR_FOLDERS.put(6, "From2013");
        CURSOR_FOLDERS.put(7, "WhiteDotCursor");
        CURSOR_FOLDERS.put(8, "VerySmallWhiteDot");
    }

    public static final String[] SLOT_FILES = {"ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png", "ArrowCursorDecalDrag.png"};
    public static final int SLOT_ARROW = 0;
    public static final int SLOT_FAR = 1;
    public static final int SLOT_TEXT = 2;
    public static final int SLOT_DRAG = 3;
    public static final int SLOT_SHIFTLOCK = 4;

    public static final String[] SKY_FACES = {"sky512_bk.tex", "sky512_dn.tex", "sky512_ft.tex", "sky512_lf.tex", "sky512_rt.tex", "sky512_up.tex"};
    public static final String CUSTOM_SKY = "Voidstrap Custom";
    private static final String SKY_API = "https://api.github.com/repos/KloBraticc/SkyboxPackV2/contents";
    private static final String SKY_COMMIT = "https://api.github.com/repos/KloBraticc/SkyboxPackV2/commits/main";
    private static final String SKY_RAW = "https://raw.githubusercontent.com/KloBraticc/SkyboxPackV2/";

    private static final String FONT_CATALOG = "https://fonts.google.com/metadata/fonts";
    public static final String[] STARTER_FONTS = {"Bebas Neue", "Dancing Script", "Fira Sans", "Inter", "JetBrains Mono", "Lato", "Merriweather", "Montserrat", "Noto Sans", "Nunito", "Open Sans", "Oswald", "Pacifico", "Playfair Display", "Poppins", "Raleway", "Roboto", "Rubik", "Source Sans 3", "Ubuntu"};
    private static final long MAX_FONT = 32L * 1024 * 1024;
    private static final long MAX_AUDIO = 256L * 1024 * 1024;
    private static final int MAX_IMAGE = 4096;

    private static final Object LOCK = new Object();

    private ModPresets() {
    }

    public static File ws(Context c, String rel) {
        return new File(Mods.root(c), rel);
    }

    static File state(Context c, String name) {
        return new File(ModEngine.stateDir(c), name);
    }

    private static JSONObject pairs(String[][] map) {
        JSONArray a = new JSONArray();
        for (String[] e : map) a.put(new JSONArray(Arrays.asList(e)));
        return Core.args("pairs", a);
    }

    static void write(File f, byte[] data) throws IOException {
        Core.run("mods.writeFile", Core.args("path", f), data, -1, null);
    }

    static void copy(File from, File to) throws IOException {
        Core.run("mods.copyFile", Core.args("from", from, "to", to));
    }

    public static boolean presetOn(Context c, String[][] map) {
        return Core.flag("mods.presetOn", Core.merge(Core.roots(c), pairs(map)));
    }

    public static void setPreset(Context c, String[][] map, boolean on) throws IOException {
        Core.run("mods.setPreset", Core.merge(Core.roots(c, "on", on), pairs(map)));
    }

    public static File deathSource(Context c) {
        return state(c, "CustomDeathSoundSource");
    }

    public static boolean hasDeathSound(Context c) {
        return deathSource(c).isFile() || ws(c, DEATH).isFile();
    }

    public static int deathVolume(Context c) {
        try {
            return Math.max(1, Math.min(800, Integer.parseInt(Store.get(c).setting("deathVolume", "100"))));
        } catch (NumberFormatException e) {
            return 100;
        }
    }

    public static void importDeathSound(Context c, Uri uri) throws IOException {
        synchronized (LOCK) {
            File tmp = state(c, "death_import_" + System.nanoTime());
            try (InputStream in = c.getContentResolver().openInputStream(uri); OutputStream out = new FileOutputStream(tmp)) {
                if (in == null) throw new IOException("stream");
                byte[] buf = new byte[65536];
                long total = 0;
                int n;
                while ((n = in.read(buf)) > 0) {
                    total += n;
                    if (total > MAX_AUDIO) throw new IOException("The selected audio file is larger than 256 MB");
                    out.write(buf, 0, n);
                }
            } catch (IOException e) {
                tmp.delete();
                throw e;
            }
            if (tmp.length() == 0) {
                tmp.delete();
                throw new IOException("The selected audio file is empty");
            }
            try {
                byte[] rendered = AudioGain.render(tmp, 1.0);
                if (rendered == null) throw new IOException("That audio file could not be read");
            } catch (IOException e) {
                tmp.delete();
                throw e;
            }
            File source = deathSource(c);
            if (!tmp.renameTo(source)) {
                tmp.delete();
                throw new IOException("move");
            }
            renderDeath(c);
        }
    }

    public static void setDeathVolume(Context c, int percent) throws IOException {
        Store.get(c).putSetting("deathVolume", percent == 100 ? null : String.valueOf(percent));
        synchronized (LOCK) {
            renderDeath(c);
        }
    }

    private static void renderDeath(Context c) throws IOException {
        File source = deathSource(c);
        if (!source.isFile()) return;
        byte[] out = AudioGain.render(source, deathVolume(c) / 100.0);
        if (out == null) throw new IOException("The death sound could not be converted");
        write(ws(c, DEATH), out);
    }

    public static void removeDeathSound(Context c) {
        synchronized (LOCK) {
            try {
                Core.run("mods.removeDeath", Core.roots(c));
            } catch (IOException ignored) {
            }
        }
    }

    public static int emoji(Context c) {
        File f = ws(c, EMOJI);
        if (!f.isFile()) return 0;
        String h = sha256(f);
        for (int i = 1; i < EMOJI_HASHES.length; i++) if (EMOJI_HASHES[i].equalsIgnoreCase(h)) return i;
        return 0;
    }

    public static void setEmoji(Context c, int type, AtomicBoolean cancel) throws IOException {
        synchronized (LOCK) {
            File f = ws(c, EMOJI);
            int current = emoji(c);
            if (type <= 0) {
                if (current > 0) Core.run("mods.deleteAndPrune", Core.roots(c, "path", f));
                return;
            }
            if (current == type) return;
            File tmp = state(c, "emoji_" + System.nanoTime() + ".download");
            try {
                Net.download(EMOJI_URL + EMOJI_FILES[type], tmp, EMOJI_SIZES[type] + 1, cancel);
                if (tmp.length() != EMOJI_SIZES[type] || !EMOJI_HASHES[type].equalsIgnoreCase(sha256(tmp))) throw new IOException("The emoji font did not match its checksum");
                File parent = f.getParentFile();
                if (parent != null) parent.mkdirs();
                if (!tmp.renameTo(f)) throw new IOException("move");
            } finally {
                tmp.delete();
            }
        }
    }

    public static int cursorStyle(Context c) {
        try {
            int v = Integer.parseInt(Store.get(c).setting("cursorType", "0"));
            return v == CURSOR_CUSTOM || v == CURSOR_DEFAULT || CURSOR_FOLDERS.containsKey(v) ? v : CURSOR_DEFAULT;
        } catch (NumberFormatException e) {
            return CURSOR_DEFAULT;
        }
    }

    static File working(Context c, int slot) {
        return new File(state(c, "CursorCustom"), slotPath(slot));
    }

    static String slotPath(int slot) {
        return slot == SLOT_SHIFTLOCK ? "MouseLockedCursor.png" : "Cursors/KeyboardMouse/" + SLOT_FILES[slot];
    }

    public static File cursorPreview(Context c, int slot) {
        return slot == SLOT_SHIFTLOCK ? ws(c, SHIFTLOCK) : working(c, slot);
    }

    private static void cursorSetting(Context c, int style) {
        Store.get(c).putSetting("cursorType", style == CURSOR_DEFAULT ? null : String.valueOf(style));
    }

    public static void applyCursorStyle(Context c, int style) throws IOException {
        synchronized (LOCK) {
            Core.run("mods.applyCursor", Core.roots(c, "style", style));
            cursorSetting(c, style);
        }
    }

    public static byte[] encodePng(Context c, Uri uri) throws IOException {
        byte[] raw;
        try (InputStream in = c.getContentResolver().openInputStream(uri)) {
            if (in == null) throw new IOException("stream");
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) {
                if (out.size() + n > 32 * 1024 * 1024) throw new IOException("That image is too large");
                out.write(buf, 0, n);
            }
            raw = out.toByteArray();
        }
        return encodePng(raw);
    }

    static byte[] encodePng(byte[] raw) throws IOException {
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(raw, 0, raw.length, o);
        if (o.outWidth <= 0 || o.outHeight <= 0) throw new IOException("That image could not be read");
        if (o.outWidth > MAX_IMAGE || o.outHeight > MAX_IMAGE) throw new IOException("Use an image that is 4096 pixels or smaller on each side");
        if (raw.length > 8 && (raw[0] & 0xFF) == 0x89 && raw[1] == 'P' && raw[2] == 'N' && raw[3] == 'G') return raw;
        Bitmap b = BitmapFactory.decodeByteArray(raw, 0, raw.length);
        if (b == null) throw new IOException("That image could not be read");
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            b.compress(Bitmap.CompressFormat.PNG, 100, out);
            return out.toByteArray();
        } finally {
            b.recycle();
        }
    }

    public static boolean hasCustomCursor(Context c) {
        return Core.flag("mods.hasCustomCursor", Core.roots(c));
    }

    public static void setCustomCursor(Context c, Uri uri) throws IOException {
        byte[] png = encodePng(c, uri);
        synchronized (LOCK) {
            Core.run("mods.setCustomCursor", Core.roots(c), png, -1, null);
            cursorSetting(c, CURSOR_CUSTOM);
        }
    }

    public static void removeCustomCursor(Context c) throws IOException {
        synchronized (LOCK) {
            Object style = Core.value("mods.removeCustomCursor", Core.roots(c, "current", cursorStyle(c)));
            if (style instanceof Number) cursorSetting(c, ((Number) style).intValue());
        }
    }

    public static void setShiftLock(Context c, Uri uri) throws IOException {
        byte[] png = encodePng(c, uri);
        Core.run("mods.setShiftLock", Core.roots(c), png, -1, null);
    }

    public static void removeShiftLock(Context c) {
        try {
            Core.run("mods.removeShiftLock", Core.roots(c));
        } catch (IOException ignored) {
        }
    }

    public static List<String> cursorSets(Context c) {
        try {
            return Core.strings(Core.value("mods.cursorSets", Core.roots(c)));
        } catch (IOException e) {
            return new ArrayList<>();
        }
    }

    public static String validSetName(String requested) {
        try {
            return Core.text("mods.validSetName", Core.args("name", requested));
        } catch (IOException e) {
            return null;
        }
    }

    public static String uniqueSetName(Context c, String base) {
        try {
            return Core.text("mods.uniqueSetName", Core.roots(c, "base", base));
        } catch (IOException e) {
            return base;
        }
    }

    public static String createSet(Context c, String requested) throws IOException {
        return Core.text("mods.createSet", Core.roots(c, "name", requested));
    }

    public static File setSlot(Context c, String set, int slot) throws IOException {
        return new File(Core.text("mods.setSlot", Core.roots(c, "set", set, "slot", slot)));
    }

    public static void setSetImage(Context c, String set, int slot, Uri uri) throws IOException {
        byte[] png = encodePng(c, uri);
        write(setSlot(c, set, slot), png);
    }

    public static void clearSetImage(Context c, String set, int slot) throws IOException {
        setSlot(c, set, slot).delete();
    }

    public static void copyCurrentToSet(Context c, String set) throws IOException {
        Core.run("mods.copyCurrentToSet", Core.roots(c, "set", set));
    }

    public static void useSet(Context c, String set) throws IOException {
        synchronized (LOCK) {
            Core.run("mods.useSet", Core.roots(c, "set", set));
            cursorSetting(c, CURSOR_CUSTOM);
        }
    }

    public static String renameSet(Context c, String old, String requested) throws IOException {
        return Core.text("mods.renameSet", Core.roots(c, "old", old, "name", requested));
    }

    public static void deleteSet(Context c, String set) throws IOException {
        Core.run("mods.deleteSet", Core.roots(c, "set", set));
    }

    public static void exportSet(Context c, String set, Uri target) throws IOException {
        try (ParcelFileDescriptor pfd = c.getContentResolver().openFileDescriptor(target, "wt")) {
            if (pfd == null) throw new IOException("stream");
            Core.run("mods.exportSet", Core.roots(c, "set", set), null, pfd.detachFd(), null);
        }
    }

    public static String importSet(Context c, Uri uri) throws IOException {
        String display = Mods.displayName(c.getContentResolver(), uri);
        JSONObject r;
        try (ParcelFileDescriptor pfd = c.getContentResolver().openFileDescriptor(uri, "r")) {
            if (pfd == null) throw new IOException("stream");
            r = Core.run("mods.importSet", Core.roots(c, "display", display), null, pfd.detachFd(), null);
        }
        String name = r.optString("name");
        JSONArray convert = r.optJSONArray("convert");
        try {
            if (convert != null) for (int i = 0; i < convert.length(); i++) {
                File f = new File(convert.getString(i));
                byte[] raw;
                try (InputStream in = new FileInputStream(f)) {
                    raw = ModEngine.readAll(in);
                }
                write(f, encodePng(raw));
            }
            return name;
        } catch (IOException | JSONException | RuntimeException e) {
            Core.run("mods.deleteSet", Core.roots(c, "set", name));
            throw e instanceof IOException ? (IOException) e : new IOException(e);
        }
    }

    public static boolean skyEnabled(Context c) {
        return "1".equals(Store.get(c).setting("skyboxEnabled", "0"));
    }

    public static String skyName(Context c) {
        return Store.get(c).setting("skyboxName", "Default");
    }

    static File skyPacks(Context c) {
        return state(c, "SkyboxPacks");
    }

    public static File customSky(Context c) {
        return state(c, "CustomSkybox");
    }

    public static boolean hasCustomSky(Context c) {
        return Core.flag("mods.hasCustomSky", Core.roots(c));
    }

    static boolean safeSkyName(String n) {
        return n != null && !n.trim().isEmpty() && n.length() <= 128 && !n.equals(".") && !n.equals("..") && n.indexOf('/') < 0 && n.indexOf('\\') < 0;
    }

    public static List<String> loadSkyPacks(Context c) {
        String online;
        try {
            online = Net.text(SKY_API, 2 * 1024 * 1024);
        } catch (IOException e) {
            online = null;
        }
        try {
            return Core.strings(Core.value("mods.skyNames", Core.roots(c, "online", online, "saved", skyName(c))));
        } catch (IOException e) {
            List<String> names = new ArrayList<>();
            names.add("Default");
            return names;
        }
    }

    public static void setSky(Context c, String name, boolean enabled, AtomicBoolean cancel) throws IOException {
        if (!safeSkyName(name)) throw new IOException("The selected skybox name is invalid.");
        synchronized (LOCK) {
            File source = null;
            if (enabled && !name.equalsIgnoreCase("Default") && !name.equals(CUSTOM_SKY)) source = ensurePack(c, name, cancel);
            JSONObject r = Core.run("mods.applySky", Core.roots(c, "name", name, "enabled", enabled, "source", source));
            JSONArray log = r.optJSONArray("log");
            if (log != null) for (int i = 0; i < log.length(); i++) ModLog.add(log.optString(i));
            if (r.has("error")) throw new IOException(r.optString("error"));
            Store s = Store.get(c);
            s.putSetting("skyboxName", name.equals("Default") ? null : name);
            s.putSetting("skyboxEnabled", enabled ? "1" : null);
        }
    }

    private static File ensurePack(Context c, String name, AtomicBoolean cancel) throws IOException {
        File folder = new File(skyPacks(c), name);
        if (!Mods.inside(skyPacks(c), folder)) throw new IOException("invalid");
        File commit = new File(folder, ".commit");
        boolean present = true;
        for (String f : SKY_FACES) if (!new File(folder, f).isFile()) present = false;
        if (present && commit.isFile() && System.currentTimeMillis() - commit.lastModified() < 6L * 3600 * 1000) return folder;
        String latest;
        try {
            latest = new JSONObject(Net.text(SKY_COMMIT, 1024 * 1024)).optString("sha");
            if (!latest.matches("[0-9a-fA-F]{40}")) throw new IOException("The skybox version is invalid");
        } catch (IOException | JSONException e) {
            if (present) return folder;
            throw e instanceof IOException ? (IOException) e : new IOException(e);
        }
        if (present && commit.isFile() && latest.equals(readText(commit).trim())) {
            commit.setLastModified(System.currentTimeMillis());
            return folder;
        }
        File staging = new File(skyPacks(c), name + ".new." + System.nanoTime());
        try {
            for (String f : SKY_FACES) {
                String url = SKY_RAW + latest + "/" + Uri.encode(name) + "/" + Uri.encode(f);
                Net.download(url, new File(staging, f), 64L * 1024 * 1024, cancel);
            }
            write(new File(staging, ".commit"), latest.getBytes(StandardCharsets.UTF_8));
            Mods.delete(folder);
            if (!staging.renameTo(folder)) throw new IOException("move");
            return folder;
        } finally {
            Mods.delete(staging);
        }
    }

    public static File customFace(Context c, int face) {
        return new File(state(c, "CustomSkyboxPick"), SKY_FACES[face]);
    }

    public static void pickCustomFace(Context c, int face, Uri uri) throws IOException {
        write(customFace(c, face), readSkyImage(c, uri));
    }

    public static void pickCustomAll(Context c, Uri uri) throws IOException {
        byte[] data = readSkyImage(c, uri);
        for (int i = 0; i < SKY_FACES.length; i++) write(customFace(c, i), data);
    }

    private static byte[] readSkyImage(Context c, Uri uri) throws IOException {
        byte[] raw;
        try (InputStream in = c.getContentResolver().openInputStream(uri)) {
            if (in == null) throw new IOException("stream");
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) {
                if (out.size() + n > 64 * 1024 * 1024) throw new IOException("The selected image is empty or too large");
                out.write(buf, 0, n);
            }
            raw = out.toByteArray();
        }
        if (raw.length > 128 && raw[0] == 'D' && raw[1] == 'D' && raw[2] == 'S' && raw[3] == ' ') {
            if (raw.length > 16 * 1024 * 1024) throw new IOException("The selected DDS image is too large");
            return raw;
        }
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(raw, 0, raw.length, o);
        if (o.outWidth <= 0 || o.outHeight <= 0) throw new IOException("The selected file is not a supported image");
        if (o.outWidth > 16384 || o.outHeight > 16384 || (long) o.outWidth * o.outHeight > 16777216L) throw new IOException("The selected image dimensions are too large");
        BitmapFactory.Options d = new BitmapFactory.Options();
        d.inSampleSize = 1;
        while (Math.min(o.outWidth, o.outHeight) / (d.inSampleSize * 2) >= 512) d.inSampleSize *= 2;
        Bitmap b = BitmapFactory.decodeByteArray(raw, 0, raw.length, d);
        if (b == null) throw new IOException("The selected file is not a supported image");
        try {
            int side = Math.min(b.getWidth(), b.getHeight());
            Bitmap square = Bitmap.createBitmap(b, (b.getWidth() - side) / 2, (b.getHeight() - side) / 2, side, side);
            Bitmap scaled = Bitmap.createScaledBitmap(square, 512, 512, true);
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            scaled.compress(Bitmap.CompressFormat.PNG, 100, out);
            if (scaled != square) scaled.recycle();
            if (square != b) square.recycle();
            return out.toByteArray();
        } finally {
            b.recycle();
        }
    }

    public static void saveCustomSky(Context c) throws IOException {
        Core.run("mods.saveCustomSky", Core.roots(c));
        setSky(c, CUSTOM_SKY, true, null);
    }

    public static void removeCustomSky(Context c) throws IOException {
        Core.run("mods.removeCustomSky", Core.roots(c));
        if (CUSTOM_SKY.equals(skyName(c))) setSky(c, "Default", false, null);
    }

    public static File fontSource(Context c) {
        return state(c, "CustomFontSource.ttf");
    }

    public static boolean hasFont(Context c) {
        return ws(c, FONT).isFile();
    }

    public static String fontName(Context c) {
        return Store.get(c).setting("customFontName", "");
    }

    public static int fontScale(Context c) {
        try {
            return Math.max(0, Math.min(500, Integer.parseInt(Store.get(c).setting("fontScale", "100"))));
        } catch (NumberFormatException e) {
            return 100;
        }
    }

    public static List<String> loadFontCatalog(Context c, boolean force) {
        File cache = new File(new File(c.getCacheDir(), "fonts"), "catalog.json");
        if (!force && cache.isFile() && System.currentTimeMillis() - cache.lastModified() < 24L * 3600 * 1000) {
            List<String> cached = readCatalog(cache);
            if (!cached.isEmpty()) return cached;
        }
        try {
            byte[] data = Net.fetch(FONT_CATALOG, 8 * 1024 * 1024);
            List<String> names = Core.strings(Core.value("mods.fontCatalog", Core.args("text", new String(data, StandardCharsets.UTF_8))));
            if (names.isEmpty()) throw new IOException("empty");
            write(cache, new JSONArray(names).toString().getBytes(StandardCharsets.UTF_8));
            return names;
        } catch (IOException e) {
            List<String> cached = readCatalog(cache);
            return cached.isEmpty() ? new ArrayList<>(Arrays.asList(STARTER_FONTS)) : cached;
        }
    }

    private static List<String> readCatalog(File f) {
        List<String> out = new ArrayList<>();
        try {
            JSONArray a = new JSONArray(readText(f));
            for (int i = 0; i < a.length(); i++) out.add(a.getString(i));
        } catch (IOException | JSONException ignored) {
        }
        return out;
    }

    public static File downloadGoogleFont(Context c, String family, AtomicBoolean cancel) throws IOException {
        String css = Net.text("https://fonts.googleapis.com/css2?family=" + Uri.encode(family).replace("%20", "+") + "&display=swap", 256 * 1024);
        String url = Core.text("mods.gstaticUrl", Core.args("css", css));
        if (url == null) throw new IOException("No compatible font file was found");
        File dir = new File(new File(c.getCacheDir(), "fonts"), "files");
        File dest = new File(dir, Core.text("mods.fontFileName", Core.args("family", family, "url", url)) + ".ttf");
        if (validFont(dest)) return dest;
        Net.download(url, dest, MAX_FONT, cancel);
        if (!validFont(dest)) {
            dest.delete();
            throw new IOException("The downloaded file is not a supported font");
        }
        return dest;
    }

    public static File importLocalFont(Context c, Uri uri) throws IOException {
        String name = Mods.displayName(c.getContentResolver(), uri);
        String ext = name == null ? "" : Mods.extension(name);
        if (!ext.equals("ttf") && !ext.equals("otf")) throw new IOException("The font file type is not supported");
        File dir = new File(new File(c.getCacheDir(), "fonts"), "local");
        if (!dir.isDirectory()) dir.mkdirs();
        File tmp = new File(dir, "import_" + System.nanoTime());
        try (InputStream in = c.getContentResolver().openInputStream(uri); OutputStream out = new FileOutputStream(tmp)) {
            if (in == null) throw new IOException("stream");
            byte[] buf = new byte[65536];
            long total = 0;
            int n;
            while ((n = in.read(buf)) > 0) {
                total += n;
                if (total > MAX_FONT) throw new IOException("The font file is too large");
                out.write(buf, 0, n);
            }
        }
        if (!validFont(tmp)) {
            tmp.delete();
            throw new IOException("The font file is not supported");
        }
        File dest = new File(dir, sha256(tmp) + "." + ext);
        if (!tmp.renameTo(dest)) {
            tmp.delete();
            throw new IOException("move");
        }
        return dest;
    }

    static boolean validFont(File f) {
        return Core.flag("mods.validFont", Core.args("path", f));
    }

    public static void useFont(Context c, File source, String name) throws IOException {
        synchronized (LOCK) {
            Core.run("mods.useFont", Core.roots(c, "source", source, "scale", fontScale(c)));
            Store.get(c).putSetting("customFontName", name == null || name.isEmpty() ? null : name);
        }
    }

    public static void setFontScale(Context c, int percent) throws IOException {
        Store.get(c).putSetting("fontScale", percent == 100 ? null : String.valueOf(percent));
        synchronized (LOCK) {
            Core.run("mods.setFontScale", Core.roots(c, "scale", fontScale(c)));
        }
    }

    public static void removeFont(Context c) {
        synchronized (LOCK) {
            try {
                Core.run("mods.removeFont", Core.roots(c));
            } catch (IOException ignored) {
            }
            Store.get(c).putSetting("customFontName", null);
        }
    }

    public static void prepareForApply(Context c, String pkg) {
        File base = ModEngine.originalApk(c, pkg);
        if (base == null) base = ModEngine.baseApk(c, pkg);
        try {
            JSONArray log = Core.run("mods.prepareForApply", Core.roots(c, "pkg", pkg, "base", base)).optJSONArray("log");
            if (log != null) for (int i = 0; i < log.length(); i++) ModLog.add(log.optString(i));
        } catch (IOException | RuntimeException e) {
            ModLog.add("font families failed, " + e);
        }
    }

    static String readText(File f) throws IOException {
        try (InputStream in = new FileInputStream(f)) {
            return new String(ModEngine.readAll(in), StandardCharsets.UTF_8);
        }
    }

    static String sha256(File f) {
        try {
            String h = Core.text("mods.sha256File", Core.args("path", f));
            return h == null ? "" : h;
        } catch (IOException e) {
            return "";
        }
    }
}
