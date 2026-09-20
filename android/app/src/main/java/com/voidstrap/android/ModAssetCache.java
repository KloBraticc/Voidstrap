package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;

final class ModAssetCache {
    static final String BACKUP = "AssetCacheBackup";
    private static final int HEADER = 37;
    private static final int LENGTH_OFFSET = 25;
    private static final long MAX_ENTRY = 64L * 1024 * 1024;

    private ModAssetCache() {
    }

    static String hashOf(String fileName) {
        String name = fileName.substring(fileName.lastIndexOf('/') + 1);
        int dot = name.lastIndexOf('.');
        if (dot > 0) name = name.substring(0, dot);
        for (int start = name.length() - 32; start >= 0; start--) {
            String cand = name.substring(start, start + 32).toLowerCase(Locale.ROOT);
            if (cand.matches("[0-9a-f]{32}")) return cand;
        }
        return null;
    }

    static boolean entry(byte[] header, long total) {
        if (header.length < HEADER || total <= HEADER || total > MAX_ENTRY) return false;
        if (header[0] != 'R' || header[1] != 'B' || header[2] != 'X' || header[3] != 'H') return false;
        long declared = (header[LENGTH_OFFSET] & 0xFFL) | (header[LENGTH_OFFSET + 1] & 0xFFL) << 8 | (header[LENGTH_OFFSET + 2] & 0xFFL) << 16 | (header[LENGTH_OFFSET + 3] & 0xFFL) << 24;
        return declared == total - HEADER;
    }

    static boolean hasEntries(Context c, File archive, AtomicBoolean cancel) throws IOException {
        boolean[] found = {false};
        ModArchives.read(archive, (name, size, data) -> {
            if (found[0] || name.toUpperCase(Locale.ROOT).contains("RESTORE") || hashOf(name) == null) return;
            byte[] all = readBounded(data);
            if (all != null && all.length > HEADER && entry(all, all.length)) found[0] = true;
        }, cancel);
        return found[0];
    }

    private static byte[] readBounded(InputStream in) throws IOException {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        byte[] buf = new byte[65536];
        int n;
        while ((n = in.read(buf)) > 0) {
            if (out.size() + n > MAX_ENTRY) return null;
            out.write(buf, 0, n);
        }
        return out.toByteArray();
    }

    static ManagedMods.Record installPack(Context c, File archive, String name, ManagedMods.Pack pack, AtomicBoolean cancel) throws IOException {
        File staging = new File(ManagedMods.root(c), "staging_" + System.nanoTime());
        File backup = new File(staging, BACKUP);
        if (!backup.mkdirs()) throw new IOException("mkdir");
        int[] count = {0};
        try {
            Set<String> seen = new LinkedHashSet<>();
            ModArchives.read(archive, (entry, size, data) -> {
                if (entry.toUpperCase(Locale.ROOT).contains("RESTORE")) return;
                String hash = hashOf(entry);
                if (hash == null || seen.contains(hash)) return;
                byte[] all = readBounded(data);
                if (all == null || !entry(all, all.length)) return;
                seen.add(hash);
                try (OutputStream out = new FileOutputStream(new File(backup, hash + ".mod.lock"))) {
                    out.write(all);
                }
                count[0]++;
            }, cancel);
            if (count[0] == 0) throw new ModArchives.Rejected("This package does not contain any usable Roblox asset cache entries.");
            if (pack != null) ManagedMods.writePack(staging, pack);
            return ManagedMods.adopt(c, staging, name);
        } finally {
            Mods.delete(staging);
        }
    }

    static boolean isCacheMod(File folder) {
        String[] kids = new File(folder, BACKUP).list();
        return kids != null && kids.length > 0;
    }

    static Map<String, File> wanted(Context c) {
        Map<String, File> out = new LinkedHashMap<>();
        for (ManagedMods.Record r : ManagedMods.load(c)) {
            if (!r.enabled) continue;
            File[] files = new File(ManagedMods.folder(c, r.id), BACKUP).listFiles();
            if (files == null) continue;
            for (File f : files) {
                String n = f.getName();
                if (!n.endsWith(".mod.lock")) continue;
                String hash = n.substring(0, n.length() - 9);
                if (hash.matches("[0-9a-f]{32}") && !out.containsKey(hash)) out.put(hash, f);
            }
        }
        return out;
    }

    private static File stateFile(Context c, String pkg) {
        return new File(ModEngine.stateDir(c), "cache_" + pkg + ".json");
    }

    private static Set<String> installed(Context c, String pkg) {
        Set<String> out = new LinkedHashSet<>();
        File f = stateFile(c, pkg);
        if (!f.isFile()) return out;
        try {
            JSONArray a = new JSONObject(ModPresets.readText(f)).optJSONArray("installed");
            if (a != null) for (int i = 0; i < a.length(); i++) {
                String h = a.getString(i);
                if (h.matches("[0-9a-f]{32}")) out.add(h);
            }
        } catch (IOException | JSONException ignored) {
        }
        return out;
    }

    private static void saveInstalled(Context c, String pkg, Set<String> hashes) {
        try (OutputStream out = new FileOutputStream(stateFile(c, pkg))) {
            out.write(new JSONObject().put("installed", new JSONArray(hashes)).toString().getBytes(StandardCharsets.UTF_8));
        } catch (IOException | JSONException ignored) {
        }
    }

    static boolean pending(Context c, String pkg) {
        Map<String, File> want = ModEngine.enabled(c) ? wanted(c) : new LinkedHashMap<>();
        Set<String> have = installed(c, pkg);
        return !want.keySet().equals(have);
    }

    static int sync(Context c, String pkg, FlagWriter.Mode mode, boolean restoreAll) {
        if (!pkg.matches("[A-Za-z0-9_.]+")) return FlagWriter.FAILED;
        Map<String, File> want = ModEngine.enabled(c) && !restoreAll ? wanted(c) : new LinkedHashMap<>();
        Set<String> have = installed(c, pkg);
        if (want.isEmpty() && have.isEmpty()) return FlagWriter.OK;

        File originals = new File(new File(ModEngine.stateDir(c), "cache_originals"), pkg);
        if (!originals.isDirectory()) originals.mkdirs();
        StringBuilder sb = new StringBuilder();
        sb.append("S=/data/data/").append(pkg).append("/cache/rbx-storage\n");
        sb.append("[ -d /data/data/").append(pkg).append(" ] || exit 4\n");
        sb.append("mkdir -p \"$S\"\n");
        sb.append("O=$(stat -c %u:%g \"$S\")\n");
        sb.append("X=$(stat -c %C \"$S\")\n");
        sb.append("B=").append(ModEngine.quote(originals.getAbsolutePath())).append('\n');
        sb.append("BO=$(stat -c %u:%g \"$B\")\n");
        sb.append("BX=$(stat -c %C \"$B\")\n");
        sb.append("put() { d=\"$S/${2%${2#??}}\"; mkdir -p \"$d\"; chown \"$O\" \"$d\"; chmod 700 \"$d\"; chcon \"$X\" \"$d\" 2>/dev/null; t=\"$d/$2\"; if [ -f \"$t\" ] && [ ! -f \"$B/$2\" ]; then cp -f \"$t\" \"$B/$2\" && chown \"$BO\" \"$B/$2\" && chcon \"$BX\" \"$B/$2\" 2>/dev/null; fi; cp -f \"$1\" \"$t\" || return 1; chown \"$O\" \"$t\"; chmod 600 \"$t\"; chcon \"$X\" \"$t\" 2>/dev/null; }\n");
        sb.append("back() { t=\"$S/${1%${1#??}}/$1\"; if [ -f \"$B/$1\" ]; then cp -f \"$B/$1\" \"$t\" && chown \"$O\" \"$t\" && chmod 600 \"$t\" && chcon \"$X\" \"$t\" 2>/dev/null; rm -f \"$B/$1\"; else rm -f \"$t\"; fi; }\n");
        sb.append("R=0\n");
        for (Map.Entry<String, File> e : want.entrySet()) {
            sb.append("put ").append(ModEngine.quote(e.getValue().getAbsolutePath())).append(' ').append(e.getKey()).append(" || R=2\n");
        }
        for (String h : have) if (!want.containsKey(h)) sb.append("back ").append(h).append('\n');
        String list = HelperServer.CACHE_LIST + pkg;
        if (want.isEmpty()) sb.append("rm -f ").append(list).append('\n');
        else sb.append("printf '%s\\n' ").append(String.join(" ", want.keySet())).append(" > ").append(list).append('\n');
        sb.append("exit $R\n");
        int code = FlagWriter.raw(mode, sb.toString(), 120);
        if (code == 0) saveInstalled(c, pkg, want.keySet());
        return code;
    }
}
