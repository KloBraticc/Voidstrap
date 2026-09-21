package com.voidstrap.android;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;

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
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;
import java.util.zip.ZipOutputStream;

public final class ModPresets {
    public static final String DEATH = "content/sounds/oof.ogg";
    public static final String AVATAR = "ExtraContent/places/Mobile.rbxl";
    public static final String EMOJI = "content/fonts/TwemojiMozilla.ttf";
    public static final String FONT = "content/fonts/CustomFont.ttf";
    public static final String FAMILIES = "content/fonts/families";
    public static final String SKY = "PlatformContent/pc/textures/sky";
    public static final String CURSOR_DIR = "content/textures/Cursors/KeyboardMouse";
    public static final String SHIFTLOCK = "content/textures/MouseLockedCursor.png";
    public static final String FONT_ASSET = "rbxasset://fonts/CustomFont.ttf";

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

    static byte[] resource(Context c, String name) throws IOException {
        try (InputStream in = c.getAssets().open("mods/" + name)) {
            return ModEngine.readAll(in);
        }
    }

    static boolean same(File f, byte[] data) {
        if (!f.isFile() || f.length() != data.length) return false;
        try (InputStream in = new FileInputStream(f)) {
            return Arrays.equals(ModEngine.readAll(in), data);
        } catch (IOException e) {
            return false;
        }
    }

    static void write(File f, byte[] data) throws IOException {
        if (same(f, data)) return;
        File parent = f.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) throw new IOException("mkdir");
        File tmp = new File(f.getPath() + ".vs" + System.nanoTime());
        try (FileOutputStream out = new FileOutputStream(tmp)) {
            out.write(data);
            out.getFD().sync();
        }
        if (!tmp.renameTo(f)) {
            tmp.delete();
            throw new IOException("move");
        }
    }

    static void copy(File from, File to) throws IOException {
        try (InputStream in = new FileInputStream(from)) {
            write(to, ModEngine.readAll(in));
        }
    }

    static void deleteAndPrune(Context c, File f) {
        f.delete();
        File root = Mods.root(c);
        File d = f.getParentFile();
        while (d != null && !d.equals(root) && Mods.inside(root, d)) {
            String[] kids = d.list();
            if (kids == null || kids.length > 0 || !d.delete()) break;
            d = d.getParentFile();
        }
    }

    public static boolean presetOn(Context c, String[][] map) {
        try {
            for (String[] e : map) if (same(ws(c, e[0]), resource(c, e[1]))) return true;
        } catch (IOException ignored) {
        }
        return false;
    }

    public static void setPreset(Context c, String[][] map, boolean on) throws IOException {
        synchronized (LOCK) {
            for (String[] e : map) {
                byte[] data = resource(c, e[1]);
                File f = ws(c, e[0]);
                if (on) write(f, data);
                else if (same(f, data)) deleteAndPrune(c, f);
            }
        }
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
            deathSource(c).delete();
            deleteAndPrune(c, ws(c, DEATH));
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
                if (current > 0) deleteAndPrune(c, f);
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

    public static String cursorFolder(int style) {
        return CURSOR_FOLDERS.get(style);
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

    public static void applyCursorStyle(Context c, int style) throws IOException {
        synchronized (LOCK) {
            for (int s = 0; s < SLOT_FILES.length; s++) deleteAndPrune(c, ws(c, CURSOR_DIR + "/" + SLOT_FILES[s]));
            if (style == CURSOR_CUSTOM) {
                for (int s = 0; s < SLOT_FILES.length; s++) copyWorking(c, s);
            } else if (CURSOR_FOLDERS.containsKey(style)) {
                String folder = CURSOR_FOLDERS.get(style);
                for (String name : SLOT_FILES) {
                    byte[] data;
                    try {
                        data = resource(c, "Cursor/" + folder + "/" + name);
                    } catch (IOException e) {
                        continue;
                    }
                    write(ws(c, CURSOR_DIR + "/" + name), data);
                }
            }
            Store.get(c).putSetting("cursorType", style == CURSOR_DEFAULT ? null : String.valueOf(style));
        }
    }

    private static void copyWorking(Context c, int slot) throws IOException {
        File w = working(c, slot);
        File dest = ws(c, CURSOR_DIR + "/" + SLOT_FILES[slot]);
        if (w.isFile()) copy(w, dest);
        else deleteAndPrune(c, dest);
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
        for (int s = SLOT_ARROW; s <= SLOT_TEXT; s++) if (working(c, s).isFile()) return true;
        return false;
    }

    public static void setCustomCursor(Context c, Uri uri) throws IOException {
        byte[] png = encodePng(c, uri);
        synchronized (LOCK) {
            for (int s = SLOT_ARROW; s <= SLOT_TEXT; s++) write(working(c, s), png);
        }
        applyCursorStyle(c, CURSOR_CUSTOM);
    }

    public static void removeCustomCursor(Context c) throws IOException {
        synchronized (LOCK) {
            for (int s = SLOT_ARROW; s <= SLOT_TEXT; s++) working(c, s).delete();
        }
        if (cursorStyle(c) == CURSOR_CUSTOM) applyCursorStyle(c, working(c, SLOT_DRAG).isFile() ? CURSOR_CUSTOM : CURSOR_DEFAULT);
    }

    public static void setShiftLock(Context c, Uri uri) throws IOException {
        byte[] png = encodePng(c, uri);
        synchronized (LOCK) {
            write(ws(c, SHIFTLOCK), png);
        }
    }

    public static void removeShiftLock(Context c) {
        synchronized (LOCK) {
            deleteAndPrune(c, ws(c, SHIFTLOCK));
        }
    }

    public static File setsFolder(Context c) {
        File d = state(c, "CursorSets");
        if (!d.isDirectory()) d.mkdirs();
        return d;
    }

    public static List<String> cursorSets(Context c) {
        List<String> out = new ArrayList<>();
        File[] dirs = setsFolder(c).listFiles();
        if (dirs != null) for (File d : dirs) if (d.isDirectory()) out.add(d.getName());
        Collections.sort(out, String.CASE_INSENSITIVE_ORDER);
        return out;
    }

    public static String validSetName(String requested) {
        String n = requested == null ? "" : requested.trim();
        while (n.endsWith(".")) n = n.substring(0, n.length() - 1).trim();
        if (n.isEmpty() || n.length() > 64) return null;
        for (int i = 0; i < n.length(); i++) {
            char ch = n.charAt(i);
            if (Character.isISOControl(ch) || "/\\<>:\"|?*".indexOf(ch) >= 0) return null;
        }
        return n;
    }

    public static String uniqueSetName(Context c, String base) {
        String name = base;
        for (int i = 2; new File(setsFolder(c), name).exists(); i++) name = base + " " + i;
        return name;
    }

    public static File setFolder(Context c, String name) throws IOException {
        String n = validSetName(name);
        if (n == null) throw new IOException("That cursor set name is not valid");
        return new File(setsFolder(c), n);
    }

    public static String createSet(Context c, String requested) throws IOException {
        String n = validSetName(requested);
        if (n == null) throw new IOException("That name cannot be used for a folder. Try a different name.");
        File f = new File(setsFolder(c), n);
        if (f.exists()) throw new IOException("A cursor set named " + n + " already exists.");
        if (!f.mkdirs()) throw new IOException("mkdir");
        return n;
    }

    public static File setSlot(Context c, String set, int slot) throws IOException {
        return new File(setFolder(c, set), slotPath(slot));
    }

    public static void setSetImage(Context c, String set, int slot, Uri uri) throws IOException {
        byte[] png = encodePng(c, uri);
        write(setSlot(c, set, slot), png);
    }

    public static void clearSetImage(Context c, String set, int slot) throws IOException {
        setSlot(c, set, slot).delete();
    }

    public static void copyCurrentToSet(Context c, String set) throws IOException {
        synchronized (LOCK) {
            for (int s = 0; s < SLOT_FILES.length; s++) copyOrDelete(working(c, s), setSlot(c, set, s));
            copyOrDelete(ws(c, SHIFTLOCK), setSlot(c, set, SLOT_SHIFTLOCK));
        }
    }

    public static void useSet(Context c, String set) throws IOException {
        synchronized (LOCK) {
            for (int s = 0; s < SLOT_FILES.length; s++) copyOrDelete(setSlot(c, set, s), working(c, s));
            File shift = setSlot(c, set, SLOT_SHIFTLOCK);
            if (shift.isFile()) copy(shift, ws(c, SHIFTLOCK));
        }
        applyCursorStyle(c, CURSOR_CUSTOM);
    }

    private static void copyOrDelete(File from, File to) throws IOException {
        if (from.isFile()) copy(from, to);
        else to.delete();
    }

    public static String renameSet(Context c, String old, String requested) throws IOException {
        File folder = setFolder(c, old);
        String n = validSetName(requested);
        if (n == null) throw new IOException("That name cannot be used for a folder. Try a different name.");
        if (n.equals(old)) return n;
        File dest = new File(setsFolder(c), n);
        if (!n.equalsIgnoreCase(old) && dest.exists()) throw new IOException("A cursor set named " + n + " already exists.");
        File tmp = new File(setsFolder(c), n + "." + System.nanoTime());
        if (!folder.renameTo(tmp) || !tmp.renameTo(dest)) throw new IOException("rename");
        return n;
    }

    public static void deleteSet(Context c, String set) throws IOException {
        Mods.delete(setFolder(c, set));
    }

    public static void exportSet(Context c, String set, Uri target) throws IOException {
        File folder = setFolder(c, set);
        try (OutputStream out = c.getContentResolver().openOutputStream(target, "wt")) {
            if (out == null) throw new IOException("stream");
            try (ZipOutputStream zip = new ZipOutputStream(out)) {
                for (String rel : ManagedMods.files(folder)) {
                    zip.putNextEntry(new ZipEntry(rel));
                    try (InputStream in = new FileInputStream(new File(folder, rel))) {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = in.read(buf)) > 0) zip.write(buf, 0, n);
                    }
                    zip.closeEntry();
                }
            }
        }
    }

    public static String importSet(Context c, Uri uri) throws IOException {
        String display = Mods.displayName(c.getContentResolver(), uri);
        String base = display == null ? "Imported cursor set" : display.replaceAll("(?i)\\.zip$", "");
        String clean = validSetName(base.replaceAll("[/\\\\<>:\"|?*]", " ").trim());
        String name = uniqueSetName(c, clean == null ? "Imported cursor set" : clean);
        File folder = new File(setsFolder(c), name);
        int copied = 0;
        try (InputStream raw = c.getContentResolver().openInputStream(uri)) {
            if (raw == null) throw new IOException("stream");
            if (!folder.mkdirs()) throw new IOException("mkdir");
            Set<String> wanted = new HashSet<>();
            for (String s : SLOT_FILES) wanted.add(s.toLowerCase(Locale.ROOT));
            wanted.add("mouselockedcursor.png");
            try (ZipInputStream zip = new ZipInputStream(raw)) {
                ZipEntry e;
                int entries = 0;
                while ((e = zip.getNextEntry()) != null) {
                    if (++entries > 256) break;
                    if (e.isDirectory()) continue;
                    String n = e.getName().replace('\\', '/');
                    String file = n.substring(n.lastIndexOf('/') + 1);
                    if (!wanted.contains(file.toLowerCase(Locale.ROOT))) continue;
                    ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                    byte[] buf = new byte[65536];
                    int r;
                    while ((r = zip.read(buf)) > 0) {
                        if (bytes.size() + r > 64 * 1024 * 1024) throw new IOException("too large");
                        bytes.write(buf, 0, r);
                    }
                    int slot = SLOT_SHIFTLOCK;
                    for (int s = 0; s < SLOT_FILES.length; s++) if (SLOT_FILES[s].equalsIgnoreCase(file)) slot = s;
                    write(new File(folder, slotPath(slot)), encodePng(bytes.toByteArray()));
                    copied++;
                }
            }
            if (copied == 0) throw new IOException("That zip does not contain any cursor images.");
            return name;
        } catch (IOException | RuntimeException e) {
            Mods.delete(folder);
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
        for (String f : SKY_FACES) if (!new File(customSky(c), f).isFile()) return false;
        return true;
    }

    static boolean safeSkyName(String n) {
        return n != null && !n.trim().isEmpty() && n.length() <= 128 && !n.equals(".") && !n.equals("..") && n.indexOf('/') < 0 && n.indexOf('\\') < 0;
    }

    public static List<String> loadSkyPacks(Context c) {
        List<String> names = new ArrayList<>();
        boolean online = false;
        try {
            JSONArray arr = new JSONArray(Net.text(SKY_API, 2 * 1024 * 1024));
            for (int i = 0; i < arr.length(); i++) {
                JSONObject o = arr.optJSONObject(i);
                if (o == null || !"dir".equals(o.optString("type"))) continue;
                String n = o.optString("name");
                if (safeSkyName(n) && !names.contains(n)) names.add(n);
            }
            online = !names.isEmpty();
        } catch (IOException | JSONException ignored) {
        }
        if (!online) {
            File[] dirs = skyPacks(c).listFiles();
            if (dirs != null) for (File d : dirs) if (d.isDirectory() && safeSkyName(d.getName())) names.add(d.getName());
        }
        Collections.sort(names, (a, b) -> {
            if (a.equalsIgnoreCase("Default")) return -1;
            if (b.equalsIgnoreCase("Default")) return 1;
            return a.compareToIgnoreCase(b);
        });
        if (hasCustomSky(c) && !names.contains(CUSTOM_SKY)) {
            int at = names.indexOf("Default");
            names.add(at >= 0 ? at + 1 : 0, CUSTOM_SKY);
        }
        String saved = skyName(c);
        if (safeSkyName(saved) && !names.contains(saved)) names.add(saved);
        if (!names.contains("Default")) names.add(0, "Default");
        return names;
    }

    public static void setSky(Context c, String name, boolean enabled, AtomicBoolean cancel) throws IOException {
        if (!safeSkyName(name)) throw new IOException("The selected skybox name is invalid.");
        synchronized (LOCK) {
            File target = ws(c, SKY);
            if (!enabled || name.equalsIgnoreCase("Default")) {
                Mods.delete(target);
                deleteAndPrune(c, target);
                ModLog.add("skybox turned off, faces removed from the workspace");
            } else {
                File source = name.equals(CUSTOM_SKY) ? customSky(c) : ensurePack(c, name, cancel);
                for (String f : SKY_FACES) {
                    File face = new File(source, f);
                    if (!face.isFile()) {
                        ModLog.add("skybox " + name + " incomplete, missing " + f);
                        throw new IOException("The selected skybox is incomplete.");
                    }
                }
                for (String f : SKY_FACES) copy(new File(source, f), new File(target, f));
                ModLog.add("skybox " + name + " copied " + SKY_FACES.length + " faces into the workspace");
            }
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

    public static boolean customPickComplete(Context c) {
        for (int i = 0; i < SKY_FACES.length; i++) if (!customFace(c, i).isFile()) return false;
        return true;
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
        if (!customPickComplete(c)) throw new IOException("Choose an image for every skybox face");
        synchronized (LOCK) {
            File dest = customSky(c);
            File staging = new File(dest.getPath() + ".new." + System.nanoTime());
            try {
                for (int i = 0; i < SKY_FACES.length; i++) copy(customFace(c, i), new File(staging, SKY_FACES[i]));
                Mods.delete(dest);
                if (!staging.renameTo(dest)) throw new IOException("move");
            } finally {
                Mods.delete(staging);
            }
        }
        setSky(c, CUSTOM_SKY, true, null);
    }

    public static void removeCustomSky(Context c) throws IOException {
        synchronized (LOCK) {
            Mods.delete(customSky(c));
            Mods.delete(state(c, "CustomSkyboxPick"));
        }
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
            String text = new String(data, StandardCharsets.UTF_8);
            int start = text.indexOf('{');
            if (start < 0) throw new IOException("json");
            JSONArray families = new JSONObject(text.substring(start)).optJSONArray("familyMetadataList");
            List<String> names = new ArrayList<>();
            Set<String> seen = new HashSet<>();
            if (families != null) for (int i = 0; i < families.length(); i++) {
                JSONObject f = families.optJSONObject(i);
                String n = f == null ? "" : f.optString("family").trim();
                if (!n.isEmpty() && n.length() <= 128 && seen.add(n.toLowerCase(Locale.ROOT))) names.add(n);
            }
            Collections.sort(names, String.CASE_INSENSITIVE_ORDER);
            if (names.size() > 3000) names = new ArrayList<>(names.subList(0, 3000));
            if (names.isEmpty()) throw new IOException("empty");
            File dir = cache.getParentFile();
            if (dir != null) dir.mkdirs();
            write(cache, new JSONArray(names).toString().getBytes(StandardCharsets.UTF_8));
            return names;
        } catch (IOException | JSONException e) {
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
        java.util.regex.Matcher m = java.util.regex.Pattern.compile("https://fonts\\.gstatic\\.com/[^)'\"\\s]+\\.ttf", java.util.regex.Pattern.CASE_INSENSITIVE).matcher(css);
        if (!m.find()) throw new IOException("No compatible font file was found");
        String url = m.group();
        File dir = new File(new File(c.getCacheDir(), "fonts"), "files");
        File dest = new File(dir, sha256(family + "|" + url).substring(0, 20) + ".ttf");
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
        if (!f.isFile() || f.length() < 12 || f.length() > MAX_FONT) return false;
        byte[] h = new byte[4];
        try (InputStream in = new FileInputStream(f)) {
            if (in.read(h) != 4) return false;
        } catch (IOException e) {
            return false;
        }
        int tag = (h[0] & 0xFF) << 24 | (h[1] & 0xFF) << 16 | (h[2] & 0xFF) << 8 | (h[3] & 0xFF);
        return tag == 0x00010000 || tag == 0x4F54544F || tag == 0x74727565;
    }

    public static void useFont(Context c, File source, String name) throws IOException {
        synchronized (LOCK) {
            copyRaw(source, fontSource(c));
            writeFont(c);
            Store.get(c).putSetting("customFontName", name == null || name.isEmpty() ? null : name);
        }
    }

    public static void setFontScale(Context c, int percent) throws IOException {
        Store.get(c).putSetting("fontScale", percent == 100 ? null : String.valueOf(percent));
        synchronized (LOCK) {
            if (fontSource(c).isFile()) writeFont(c);
        }
    }

    private static void writeFont(Context c) throws IOException {
        byte[] data;
        try (InputStream in = new FileInputStream(fontSource(c))) {
            data = ModEngine.readAll(in);
        }
        write(ws(c, FONT), FontScaler.scale(data, Math.max(0.01, fontScale(c) / 100.0)));
    }

    public static void removeFont(Context c) {
        synchronized (LOCK) {
            deleteAndPrune(c, ws(c, FONT));
            fontSource(c).delete();
            removeGeneratedFamilies(c);
            Store.get(c).putSetting("customFontName", null);
        }
    }

    private static void copyRaw(File from, File to) throws IOException {
        try (InputStream in = new FileInputStream(from)) {
            write(to, ModEngine.readAll(in));
        }
    }

    public static void prepareForApply(Context c, String pkg) {
        synchronized (LOCK) {
            try {
                if (!hasFont(c)) {
                    removeGeneratedFamilies(c);
                    ModLog.add("no custom font set, generated font families removed");
                    return;
                }
                File base = ModEngine.originalApk(c, pkg);
                if (base == null) base = ModEngine.baseApk(c, pkg);
                if (base == null) {
                    ModLog.add("font families skipped, no readable apk for " + pkg);
                    return;
                }
                ApkPatcher.Directory dir = ApkPatcher.read(base);
                String prefix = "assets/content/fonts/families/";
                File families = ws(c, FAMILIES);
                int written = 0;
                int seen = 0;
                for (ApkPatcher.Entry e : dir.entries.values()) {
                    if (!e.name.startsWith(prefix) || !e.name.endsWith(".json") || e.name.indexOf('/', prefix.length()) >= 0) continue;
                    seen++;
                    File target = new File(families, e.name.substring(prefix.length()));
                    if (target.isFile() && !generatedFamily(target)) continue;
                    try {
                        JSONObject family = new JSONObject(new String(ApkPatcher.readEntry(base, e), StandardCharsets.UTF_8));
                        JSONArray faces = family.optJSONArray("faces");
                        if (faces == null || faces.length() == 0) continue;
                        for (int i = 0; i < faces.length(); i++) {
                            JSONObject face = faces.optJSONObject(i);
                            if (face != null) face.put("assetId", FONT_ASSET);
                        }
                        write(target, family.toString(2).getBytes(StandardCharsets.UTF_8));
                        written++;
                    } catch (IOException | JSONException ex) {
                        ModLog.add("font family " + e.name + " failed, " + ex);
                    }
                }
                ModLog.add("font families rewritten " + written + " of " + seen + " from " + base.getName());
            } catch (IOException | RuntimeException e) {
                ModLog.add("font families failed, " + e);
            }
        }
    }

    static void removeGeneratedFamilies(Context c) {
        File families = ws(c, FAMILIES);
        File[] files = families.listFiles();
        if (files == null) return;
        for (File f : files) if (f.getName().endsWith(".json") && generatedFamily(f)) f.delete();
        deleteAndPrune(c, new File(families, ".none"));
    }

    static boolean generatedFamily(File f) {
        try {
            JSONArray faces = new JSONObject(readText(f)).optJSONArray("faces");
            if (faces == null || faces.length() == 0) return false;
            for (int i = 0; i < faces.length(); i++) {
                JSONObject face = faces.optJSONObject(i);
                if (face == null || !FONT_ASSET.equals(face.optString("assetId"))) return false;
            }
            return true;
        } catch (IOException | JSONException e) {
            return false;
        }
    }

    static String readText(File f) throws IOException {
        try (InputStream in = new FileInputStream(f)) {
            return new String(ModEngine.readAll(in), StandardCharsets.UTF_8);
        }
    }

    static String sha256(File f) {
        try (InputStream in = new FileInputStream(f)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) md.update(buf, 0, n);
            return hex(md.digest());
        } catch (IOException | NoSuchAlgorithmException e) {
            return "";
        }
    }

    static String sha256(String s) {
        try {
            return hex(MessageDigest.getInstance("SHA-256").digest(s.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            return Integer.toHexString(s.hashCode());
        }
    }

    static String hex(byte[] b) {
        StringBuilder sb = new StringBuilder();
        for (byte x : b) sb.append(String.format(Locale.ROOT, "%02X", x));
        return sb.toString();
    }
}
