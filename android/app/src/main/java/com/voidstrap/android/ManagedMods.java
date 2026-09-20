package com.voidstrap.android;

import android.content.Context;
import android.util.AtomicFile;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.TimeZone;
import java.util.UUID;

public final class ManagedMods {
    public static final String PACK_FILE = "ModPack.lock";
    private static final int MAX_FILES = 100000;
    private static final Object SYNC = new Object();

    public static final class Record {
        public String id;
        public String name;
        public boolean enabled = true;
        public long created;

        Record copy() {
            Record r = new Record();
            r.id = id;
            r.name = name;
            r.enabled = enabled;
            r.created = created;
            return r;
        }
    }

    public static final class Pack {
        public String source = "";
        public long id;
        public String name = "";
        public String author = "";
        public String iconUrl = "";
        public String profileUrl = "";
        public String category = "";
        public String installKind = "";

        JSONObject toJson() throws JSONException {
            return new JSONObject().put("Source", source).put("Id", id).put("Name", name).put("Author", author)
                    .put("IconUrl", iconUrl).put("ProfileUrl", profileUrl).put("Category", category)
                    .put("InstallKind", installKind).put("InstalledUtc", iso(System.currentTimeMillis()));
        }

        static Pack from(JSONObject o) {
            Pack p = new Pack();
            p.source = o.optString("Source", o.optString("source"));
            p.id = o.optLong("Id", o.optLong("id"));
            p.name = o.optString("Name", o.optString("name"));
            p.author = o.optString("Author", o.optString("author"));
            p.iconUrl = o.optString("IconUrl", o.optString("iconUrl"));
            p.profileUrl = o.optString("ProfileUrl", o.optString("profileUrl"));
            p.category = o.optString("Category", o.optString("category"));
            p.installKind = o.optString("InstallKind", o.optString("installKind"));
            return p;
        }
    }

    public static final class Entry {
        public final Record record;
        public int files;
        public long bytes;
        public Pack pack;
        public final Set<String> paths = new HashSet<>();
        public int conflicts;
        public int overridden;

        Entry(Record record) {
            this.record = record;
        }
    }

    public static final class ModFile {
        public final Record mod;
        public final File source;
        public final String relative;

        ModFile(Record mod, File source, String relative) {
            this.mod = mod;
            this.source = source;
            this.relative = relative;
        }
    }

    private ManagedMods() {
    }

    public static File root(Context c) {
        return new File(c.getFilesDir(), "managed");
    }

    private static File packages(Context c) {
        return new File(root(c), "Packages");
    }

    private static File index(Context c) {
        return new File(root(c), "Index.json");
    }

    public static boolean validId(String id) {
        return id != null && id.matches("[0-9a-fA-F]{32}");
    }

    public static File folder(Context c, String id) {
        if (!validId(id)) throw new IllegalArgumentException("id");
        return new File(packages(c), id.toLowerCase(Locale.ROOT));
    }

    public static List<Record> load(Context c) {
        synchronized (SYNC) {
            List<Record> out = new ArrayList<>();
            for (Record r : loadCore(c)) out.add(r.copy());
            return out;
        }
    }

    public static Record create(Context c, String name) throws IOException {
        synchronized (SYNC) {
            List<Record> records = loadCore(c);
            Record r = new Record();
            r.id = UUID.randomUUID().toString().replace("-", "");
            r.name = normalizeName(name, "Mod " + r.id.substring(0, 8));
            r.enabled = true;
            r.created = System.currentTimeMillis();
            File f = folder(c, r.id);
            if (!f.isDirectory() && !f.mkdirs()) throw new IOException("mkdir");
            records.add(0, r);
            save(c, records);
            return r.copy();
        }
    }

    public static Record adopt(Context c, File staged, String name) throws IOException {
        synchronized (SYNC) {
            List<Record> records = loadCore(c);
            Record r = new Record();
            r.id = UUID.randomUUID().toString().replace("-", "");
            r.name = normalizeName(name, "Mod " + r.id.substring(0, 8));
            r.enabled = true;
            r.created = System.currentTimeMillis();
            File f = folder(c, r.id);
            File parent = f.getParentFile();
            if (parent != null && !parent.isDirectory()) parent.mkdirs();
            if (!staged.renameTo(f)) throw new IOException("move");
            records.add(0, r);
            save(c, records);
            return r.copy();
        }
    }

    public static void rename(Context c, String id, String name) throws IOException {
        String n = normalizeName(name, null);
        if (n == null) throw new IOException("name");
        mutate(c, id, r -> r.name = n);
    }

    public static void setEnabled(Context c, String id, boolean enabled) throws IOException {
        mutate(c, id, r -> r.enabled = enabled);
    }

    public static void move(Context c, String id, int delta) throws IOException {
        synchronized (SYNC) {
            List<Record> records = loadCore(c);
            int from = indexOf(records, id);
            if (from < 0) throw new IOException("missing");
            int to = Math.max(0, Math.min(records.size() - 1, from + delta));
            if (to == from) return;
            records.add(to, records.remove(from));
            save(c, records);
        }
    }

    public static void moveTo(Context c, String id, int to) throws IOException {
        synchronized (SYNC) {
            List<Record> records = loadCore(c);
            int from = indexOf(records, id);
            if (from < 0) throw new IOException("missing");
            to = Math.max(0, Math.min(records.size() - 1, to));
            if (to == from) return;
            records.add(to, records.remove(from));
            save(c, records);
        }
    }

    public static void delete(Context c, String id) throws IOException {
        synchronized (SYNC) {
            List<Record> records = loadCore(c);
            int at = indexOf(records, id);
            if (at < 0) return;
            records.remove(at);
            File f = folder(c, id);
            File trash = new File(root(c), "Trash");
            trash.mkdirs();
            File moved = new File(trash, UUID.randomUUID().toString().replace("-", ""));
            if (f.isDirectory() && !f.renameTo(moved)) Mods.delete(f);
            save(c, records);
            Mods.delete(moved);
            File[] left = trash.listFiles();
            if (left != null) for (File x : left) Mods.delete(x);
        }
    }

    public static Pack readPack(File folder) {
        File f = new File(folder, PACK_FILE);
        if (!f.isFile() || f.length() > 256 * 1024) return null;
        try {
            return Pack.from(new JSONObject(new String(java.nio.file.Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8)));
        } catch (IOException | JSONException | RuntimeException e) {
            return null;
        }
    }

    public static void writePack(File folder, Pack pack) {
        try (FileOutputStream out = new FileOutputStream(new File(folder, PACK_FILE))) {
            out.write(pack.toJson().toString(2).getBytes(StandardCharsets.UTF_8));
        } catch (IOException | JSONException ignored) {
        }
    }

    public static List<Entry> scan(Context c) {
        synchronized (SYNC) {
            List<Entry> out = new ArrayList<>();
            for (Record r : loadCore(c)) {
                Entry e = new Entry(r.copy());
                File f = folder(c, r.id);
                e.pack = readPack(f);
                for (String rel : files(f)) {
                    File file = new File(f, rel);
                    e.files++;
                    e.bytes += file.length();
                    if (!ModEngine.ignored(rel)) e.paths.add(rel.toLowerCase(Locale.ROOT));
                }
                out.add(e);
            }
            Set<String> claimed = new HashSet<>();
            for (Entry e : out) {
                if (!e.record.enabled) continue;
                for (String p : e.paths) if (!claimed.add(p)) e.overridden++;
            }
            for (Entry e : out) {
                for (Entry other : out) {
                    if (other == e || !other.record.enabled) continue;
                    for (String p : e.paths) if (other.paths.contains(p)) {
                        e.conflicts++;
                        break;
                    }
                }
            }
            return out;
        }
    }

    public static List<ModFile> enabledFiles(Context c) {
        synchronized (SYNC) {
            List<ModFile> out = new ArrayList<>();
            Set<String> claimed = new HashSet<>();
            for (Record r : loadCore(c)) {
                if (!r.enabled) continue;
                File f = folder(c, r.id);
                for (String rel : files(f)) {
                    if (ModEngine.ignored(rel)) continue;
                    if (claimed.add(rel.toLowerCase(Locale.ROOT))) out.add(new ModFile(r.copy(), new File(f, rel), rel));
                }
            }
            return out;
        }
    }

    static List<String> files(File root) {
        List<String> out = new ArrayList<>();
        if (!root.isDirectory()) return out;
        String base = root.getAbsolutePath();
        ArrayDeque<File> pending = new ArrayDeque<>();
        pending.push(root);
        while (!pending.isEmpty() && out.size() < MAX_FILES) {
            File d = pending.pop();
            File[] kids = d.listFiles();
            if (kids == null) continue;
            for (File k : kids) {
                if (java.nio.file.Files.isSymbolicLink(k.toPath())) continue;
                if (k.isDirectory()) pending.push(k);
                else if (k.isFile()) {
                    String rel = k.getAbsolutePath().substring(base.length() + 1).replace(File.separatorChar, '/');
                    if (rel.endsWith(".lock") || rel.startsWith(".") || rel.contains("/.")) continue;
                    out.add(rel);
                }
            }
        }
        return out;
    }

    private interface Mutation {
        void apply(Record r);
    }

    private static void mutate(Context c, String id, Mutation m) throws IOException {
        synchronized (SYNC) {
            List<Record> records = loadCore(c);
            int at = indexOf(records, id);
            if (at < 0) throw new IOException("missing");
            m.apply(records.get(at));
            save(c, records);
        }
    }

    private static int indexOf(List<Record> records, String id) {
        for (int i = 0; i < records.size(); i++) if (records.get(i).id.equalsIgnoreCase(id)) return i;
        return -1;
    }

    private static List<Record> loadCore(Context c) {
        File pk = packages(c);
        if (!pk.isDirectory()) pk.mkdirs();
        List<Record> records = readIndex(c);
        boolean changed = false;
        Set<String> indexed = new HashSet<>();
        for (Record r : records) indexed.add(r.id.toLowerCase(Locale.ROOT));
        File[] dirs = pk.listFiles();
        if (dirs != null) for (File d : dirs) {
            if (!d.isDirectory()) continue;
            String n = d.getName();
            if (validId(n)) {
                if (indexed.add(n.toLowerCase(Locale.ROOT))) {
                    Record r = new Record();
                    r.id = n.toLowerCase(Locale.ROOT);
                    r.name = "Mod " + n.substring(0, 8);
                    r.created = d.lastModified();
                    records.add(r);
                    changed = true;
                }
                continue;
            }
            Record r = new Record();
            r.id = UUID.randomUUID().toString().replace("-", "");
            r.name = normalizeName(n, "Mod " + r.id.substring(0, 8));
            r.created = System.currentTimeMillis();
            if (d.renameTo(new File(pk, r.id))) {
                records.add(r);
                indexed.add(r.id);
                changed = true;
            }
        }
        for (int i = records.size() - 1; i >= 0; i--) {
            if (!folder(c, records.get(i).id).isDirectory()) {
                records.remove(i);
                changed = true;
            }
        }
        if (changed || !index(c).isFile()) save(c, records);
        return records;
    }

    private static List<Record> readIndex(Context c) {
        List<Record> out = new ArrayList<>();
        File f = index(c);
        if (!f.isFile() || f.length() <= 0 || f.length() > 2 * 1024 * 1024) return out;
        try {
            JSONObject o = new JSONObject(new String(new AtomicFile(f).readFully(), StandardCharsets.UTF_8));
            JSONArray mods = o.optJSONArray("Mods");
            Set<String> ids = new HashSet<>();
            if (mods != null) for (int i = 0; i < mods.length(); i++) {
                JSONObject m = mods.optJSONObject(i);
                if (m == null) continue;
                String id = m.optString("Id");
                if (!validId(id) || !ids.add(id.toLowerCase(Locale.ROOT))) continue;
                Record r = new Record();
                r.id = id.toLowerCase(Locale.ROOT);
                r.name = normalizeName(m.optString("Name"), "Mod " + r.id.substring(0, 8));
                r.enabled = m.optBoolean("Enabled", true);
                r.created = parseIso(m.optString("CreatedUtc"));
                out.add(r);
            }
        } catch (IOException | JSONException | RuntimeException ignored) {
        }
        return out;
    }

    private static void save(Context c, List<Record> records) {
        File dir = root(c);
        if (!dir.isDirectory()) dir.mkdirs();
        AtomicFile af = new AtomicFile(index(c));
        FileOutputStream out = null;
        try {
            JSONArray mods = new JSONArray();
            for (Record r : records) mods.put(new JSONObject().put("Id", r.id).put("Name", r.name).put("Enabled", r.enabled).put("CreatedUtc", iso(r.created)));
            byte[] body = new JSONObject().put("Version", 1).put("Mods", mods).toString(2).getBytes(StandardCharsets.UTF_8);
            out = af.startWrite();
            out.write(body);
            af.finishWrite(out);
        } catch (IOException | JSONException e) {
            if (out != null) af.failWrite(out);
        }
    }

    static String normalizeName(String name, String fallback) {
        StringBuilder sb = new StringBuilder();
        String n = name == null ? "" : name.trim().replaceAll("\\s+", " ");
        for (int i = 0; i < n.length() && sb.length() < 80; i++) {
            char ch = n.charAt(i);
            if (!Character.isISOControl(ch)) sb.append(ch);
        }
        String out = sb.toString().trim();
        return out.isEmpty() ? fallback : out;
    }

    private static String iso(long t) {
        SimpleDateFormat f = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.ROOT);
        f.setTimeZone(TimeZone.getTimeZone("UTC"));
        return f.format(new Date(t <= 0 ? System.currentTimeMillis() : t));
    }

    private static long parseIso(String s) {
        if (s == null || s.length() < 19) return System.currentTimeMillis();
        try {
            SimpleDateFormat f = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.ROOT);
            f.setTimeZone(TimeZone.getTimeZone("UTC"));
            Date d = f.parse(s.substring(0, 19));
            return d == null ? System.currentTimeMillis() : d.getTime();
        } catch (java.text.ParseException e) {
            return System.currentTimeMillis();
        }
    }
}
