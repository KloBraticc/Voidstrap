package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

public final class ManagedMods {
    public static final String PACK_FILE = "ModPack.lock";

    public static final class Record {
        public String id;
        public String name;
        public boolean enabled = true;
        public long created;

        static Record from(JSONObject o) {
            Record r = new Record();
            r.id = o.optString("id");
            r.name = o.optString("name");
            r.enabled = o.optBoolean("enabled", true);
            r.created = o.optLong("created");
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

        JSONObject toJson() {
            return Core.args("source", source, "id", id, "name", name, "author", author, "iconUrl", iconUrl,
                    "profileUrl", profileUrl, "category", category, "installKind", installKind);
        }

        static Pack from(JSONObject o) {
            if (o == null) return null;
            Pack p = new Pack();
            p.source = o.optString("source");
            p.id = o.optLong("id");
            p.name = o.optString("name");
            p.author = o.optString("author");
            p.iconUrl = o.optString("iconUrl");
            p.profileUrl = o.optString("profileUrl");
            p.category = o.optString("category");
            p.installKind = o.optString("installKind");
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

    public static boolean validId(String id) {
        return id != null && id.matches("[0-9a-fA-F]{32}");
    }

    public static File folder(Context c, String id) {
        if (!validId(id)) throw new IllegalArgumentException("id");
        return new File(new File(root(c), "Packages"), id.toLowerCase(Locale.ROOT));
    }

    private static JSONObject at(Context c, Object... kv) {
        return Core.merge(Core.args("managed", root(c)), Core.args(kv));
    }

    private static List<Record> records(Object v) {
        List<Record> out = new ArrayList<>();
        if (v instanceof JSONArray) {
            JSONArray a = (JSONArray) v;
            for (int i = 0; i < a.length(); i++) out.add(Record.from(a.optJSONObject(i)));
        }
        return out;
    }

    public static List<Record> load(Context c) {
        try {
            return records(Core.value("mods.load", at(c)));
        } catch (IOException e) {
            return new ArrayList<>();
        }
    }

    public static Record create(Context c, String name) throws IOException {
        return Record.from(Core.run("mods.create", at(c, "name", name)));
    }

    public static Record adopt(Context c, File staged, String name) throws IOException {
        return Record.from(Core.run("mods.adopt", at(c, "staged", staged, "name", name)));
    }

    public static void rename(Context c, String id, String name) throws IOException {
        Core.run("mods.rename", at(c, "id", id, "name", name));
    }

    public static void setEnabled(Context c, String id, boolean enabled) throws IOException {
        Core.run("mods.setEnabled", at(c, "id", id, "enabled", enabled));
    }

    public static void move(Context c, String id, int delta) throws IOException {
        Core.run("mods.move", at(c, "id", id, "delta", delta));
    }

    public static void moveTo(Context c, String id, int to) throws IOException {
        Core.run("mods.move", at(c, "id", id, "to", to));
    }

    public static void delete(Context c, String id) throws IOException {
        Core.run("mods.delete", at(c, "id", id));
    }

    public static Pack readPack(File folder) {
        try {
            Object v = Core.value("mods.readPack", Core.args("folder", folder));
            return v instanceof JSONObject ? Pack.from((JSONObject) v) : null;
        } catch (IOException e) {
            return null;
        }
    }

    public static void writePack(File folder, Pack pack) {
        try {
            Core.run("mods.writePack", Core.args("folder", folder, "pack", pack.toJson()));
        } catch (IOException ignored) {
        }
    }

    public static List<Entry> scan(Context c) {
        List<Entry> out = new ArrayList<>();
        try {
            Object v = Core.value("mods.scan", at(c));
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            for (int i = 0; i < a.length(); i++) {
                JSONObject o = a.optJSONObject(i);
                if (o == null) continue;
                Entry e = new Entry(Record.from(o.optJSONObject("record")));
                e.files = o.optInt("files");
                e.bytes = o.optLong("bytes");
                e.pack = Pack.from(o.optJSONObject("pack"));
                e.paths.addAll(Core.strings(o.optJSONArray("paths")));
                e.conflicts = o.optInt("conflicts");
                e.overridden = o.optInt("overridden");
                out.add(e);
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    public static List<ModFile> enabledFiles(Context c) {
        List<ModFile> out = new ArrayList<>();
        try {
            Object v = Core.value("mods.enabledFiles", at(c));
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            for (int i = 0; i < a.length(); i++) {
                JSONObject o = a.optJSONObject(i);
                if (o == null) continue;
                out.add(new ModFile(Record.from(o.optJSONObject("record")), new File(o.optString("source")), o.optString("relative")));
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    static List<String> files(File root) {
        try {
            return Core.strings(Core.value("mods.files", Core.args("root", root)));
        } catch (IOException e) {
            return new ArrayList<>();
        }
    }

    static String normalizeName(String name, String fallback) {
        try {
            return Core.text("mods.normalizeName", Core.args("name", name, "fallback", fallback));
        } catch (IOException e) {
            return fallback;
        }
    }
}
