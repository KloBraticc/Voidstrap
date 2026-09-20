package com.voidstrap.android;

import android.content.Context;
import android.graphics.Bitmap;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

final class ModInterface {
    static final long PS4_MOD = 596647;
    static final long PS4_FILE = 1445838;
    static final String PS4_NAME = "PS4 Buttons Overlay";

    private static final String PS4_STATE = "Ps4ModId";
    private static final String NOGUI_STATE = "NoGuiFiles";
    private static final int MAX_SIDE = 4096;

    private static final String[] BLANK_ROOTS = {
            "content/textures/ui/",
            "ExtraContent/textures/ui/ImageSet/",
            "ExtraContent/textures/ui/LuaChat/",
            "ExtraContent/LuaPackages/Packages/_Index/FoundationImages/FoundationImages/SpriteSheets/"
    };
    private static final String[] BLANK_KEEP = {
            "content/textures/ui/Controls/",
            "content/textures/ui/Input/"
    };
    private static final String BLANK_FONT_DIR = "ExtraContent/LuaPackages/Packages/_Index/BuilderIcons/BuilderIcons/Font/";
    private static final String BLANK_FONT_ASSET = "BlankIcons.ttf";

    private static final Object LOCK = new Object();

    private ModInterface() {
    }

    static boolean ps4(Context c) {
        ManagedMods.Record r = ps4Record(c);
        return r != null && r.enabled;
    }

    static void setPs4(Context c, boolean on, AtomicBoolean cancel) throws IOException {
        synchronized (LOCK) {
            ManagedMods.Record existing = ps4Record(c);
            if (!on) {
                if (existing != null) ManagedMods.delete(c, existing.id);
                writeState(c, PS4_STATE, "");
                return;
            }
            if (existing != null) {
                ManagedMods.setEnabled(c, existing.id, true);
                return;
            }
            ManagedMods.Record added = ModCatalog.installById(c, PS4_MOD, PS4_FILE, PS4_NAME, cancel);
            trim(c, added.id);
            writeState(c, PS4_STATE, added.id);
        }
    }

    static boolean hideCoreGui(Context c) {
        Map<String, Long> blanked = noGuiState(c);
        if (blanked.isEmpty()) return false;
        for (Map.Entry<String, Long> e : blanked.entrySet()) {
            File f = ModPresets.ws(c, e.getKey());
            if (!f.isFile() || f.length() != e.getValue()) return false;
        }
        return true;
    }

    static void setHideCoreGui(Context c, boolean on, AtomicBoolean cancel) throws IOException {
        synchronized (LOCK) {
            clearNoGui(c);
            if (!on) return;
            File base = source(c);
            if (base == null) throw new IOException("Roblox is not installed, so its interface images could not be read.");
            Map<Long, byte[]> blanks = new HashMap<>();
            byte[] font = ModPresets.resource(c, BLANK_FONT_ASSET);
            StringBuilder state = new StringBuilder();
            int written = 0;
            try (ZipFile zip = new ZipFile(base)) {
                Enumeration<? extends ZipEntry> it = zip.entries();
                while (it.hasMoreElements()) {
                    if (cancel != null && cancel.get()) throw new IOException("cancelled");
                    ZipEntry entry = it.nextElement();
                    String rel = target(entry.getName());
                    if (rel == null) continue;
                    byte[] data;
                    if (rel.startsWith(BLANK_FONT_DIR)) {
                        data = font;
                    } else {
                        int[] size = pngSize(zip, entry);
                        data = size == null ? null : blank(size[0], size[1], blanks);
                    }
                    if (data == null) continue;
                    ModPresets.write(ModPresets.ws(c, rel), data);
                    state.append(data.length).append('\t').append(rel).append('\n');
                    written++;
                }
            }
            if (written == 0) throw new IOException("No interface images were found in the Roblox app.");
            writeState(c, NOGUI_STATE, state.toString());
        }
    }

    private static ManagedMods.Record ps4Record(Context c) {
        String id = readState(c, PS4_STATE);
        if (id.isEmpty()) return null;
        for (ManagedMods.Record r : ManagedMods.load(c)) if (id.equals(r.id)) return r;
        return null;
    }

    private static void trim(Context c, String id) {
        Set<String> client = ContentPlacer.clientFiles(c);
        if (client.isEmpty()) return;
        prune(ManagedMods.folder(c, id), client, null);
        prune(ModVariants.sources(c, id), client, c);
    }

    private static void prune(File root, Set<String> client, Context slots) {
        if (!root.isDirectory()) return;
        for (String rel : ManagedMods.files(root)) {
            String target = slots == null ? rel : ModVariants.resolveSlot(slots, rel);
            if (target != null && client.contains(target.toLowerCase(Locale.ROOT))) continue;
            new File(root, rel).delete();
        }
        dropEmpty(root);
    }

    private static boolean dropEmpty(File dir) {
        File[] kids = dir.listFiles();
        if (kids == null) return false;
        boolean empty = true;
        for (File k : kids) {
            if (k.isDirectory() && dropEmpty(k)) continue;
            empty = false;
        }
        return empty && dir.delete();
    }

    private static void clearNoGui(Context c) throws IOException {
        for (Map.Entry<String, Long> e : noGuiState(c).entrySet()) {
            File f = ModPresets.ws(c, e.getKey());
            if (f.isFile() && f.length() == e.getValue()) ModPresets.deleteAndPrune(c, f);
        }
        writeState(c, NOGUI_STATE, "");
    }

    private static File source(Context c) {
        String pkg = Targets.selected(c);
        File base = ModEngine.originalApk(c, pkg);
        if (base == null) base = ModEngine.baseApk(c, pkg);
        return base != null && base.canRead() ? base : null;
    }

    private static String target(String entry) {
        String lower = entry.toLowerCase(Locale.ROOT);
        String rel = ContentPlacer.desktopPath(entry);
        if (rel == null) return null;
        boolean wanted = false;
        if (rel.startsWith(BLANK_FONT_DIR)) {
            wanted = lower.endsWith(".ttf") || lower.endsWith(".otf");
        } else if (lower.endsWith(".png")) {
            for (String p : BLANK_ROOTS) if (rel.startsWith(p)) wanted = true;
            for (String p : BLANK_KEEP) if (rel.startsWith(p)) wanted = false;
        }
        if (!wanted) return null;
        return ModEngine.ignored(rel) ? null : rel;
    }

    private static int[] pngSize(ZipFile zip, ZipEntry entry) {
        byte[] head = new byte[24];
        try (InputStream in = zip.getInputStream(entry)) {
            int read = 0;
            while (read < head.length) {
                int n = in.read(head, read, head.length - read);
                if (n < 0) return null;
                read += n;
            }
        } catch (IOException e) {
            return null;
        }
        if ((head[0] & 0xFF) != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return null;
        if (head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R') return null;
        int w = int32(head, 16);
        int h = int32(head, 20);
        if (w <= 0 || h <= 0 || w > MAX_SIDE || h > MAX_SIDE) return null;
        return new int[]{w, h};
    }

    private static int int32(byte[] b, int at) {
        return ((b[at] & 0xFF) << 24) | ((b[at + 1] & 0xFF) << 16) | ((b[at + 2] & 0xFF) << 8) | (b[at + 3] & 0xFF);
    }

    private static byte[] blank(int w, int h, Map<Long, byte[]> cache) {
        long key = ((long) w << 32) | h;
        byte[] hit = cache.get(key);
        if (hit != null) return hit;
        Bitmap b;
        try {
            b = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
        } catch (OutOfMemoryError | IllegalArgumentException e) {
            return null;
        }
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            if (!b.compress(Bitmap.CompressFormat.PNG, 100, out)) return null;
            byte[] data = out.toByteArray();
            cache.put(key, data);
            return data;
        } finally {
            b.recycle();
        }
    }

    private static Map<String, Long> noGuiState(Context c) {
        Map<String, Long> out = new LinkedHashMap<>();
        for (String line : readState(c, NOGUI_STATE).split("\n")) {
            int tab = line.indexOf('\t');
            if (tab <= 0 || tab + 1 >= line.length()) continue;
            try {
                out.put(line.substring(tab + 1), Long.parseLong(line.substring(0, tab)));
            } catch (NumberFormatException ignored) {
            }
        }
        return out;
    }

    private static String readState(Context c, String name) {
        File f = ModPresets.state(c, name);
        if (!f.isFile()) return "";
        try (InputStream in = new FileInputStream(f)) {
            return new String(ModEngine.readAll(in), StandardCharsets.UTF_8);
        } catch (IOException e) {
            return "";
        }
    }

    private static void writeState(Context c, String name, String value) throws IOException {
        File f = ModPresets.state(c, name);
        if (value.isEmpty()) {
            f.delete();
            return;
        }
        ModPresets.write(f, value.getBytes(StandardCharsets.UTF_8));
    }
}
