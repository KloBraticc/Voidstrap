package com.voidstrap.android;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public final class Flags {
    public static final int MAX_BYTES = 4 * 1024 * 1024;
    public static final int MAX_ENTRIES = 10000;
    public static final int MAX_NAME = 64;
    public static final String FORMAT = "voidstrap.flags";

    public final List<Profile> profiles = new ArrayList<>();
    public String current;

    static Object value(Object v) {
        if (v instanceof Integer || v instanceof Long) return ((Number) v).longValue();
        return v == JSONObject.NULL ? null : v;
    }

    private static void fill(LinkedHashMap<String, Object> into, JSONArray pairs) {
        if (pairs == null) return;
        for (int i = 0; i < pairs.length(); i++) {
            JSONArray p = pairs.optJSONArray(i);
            if (p != null) into.put(p.optString(0), value(p.opt(1)));
        }
    }

    public static Flags empty() {
        Flags f = new Flags();
        f.profiles.add(new Profile(java.util.UUID.randomUUID().toString(), "Default"));
        f.current = f.profiles.get(0).id;
        return f;
    }

    public static Flags from(JSONObject o) {
        JSONObject loaded;
        try {
            loaded = Core.run("flags.load", Core.args("text", o == null ? "{}" : o.toString()));
        } catch (IOException e) {
            loaded = new JSONObject();
        }
        Flags f = new Flags();
        JSONArray a = loaded.optJSONArray("profiles");
        if (a != null) for (int i = 0; i < a.length(); i++) {
            JSONObject p = a.optJSONObject(i);
            if (p == null) continue;
            Profile profile = new Profile(p.optString("id"), p.optString("name"));
            profile.created = p.optLong("created");
            profile.updated = p.optLong("updated");
            profile.exported = p.optLong("exported");
            fill(profile.values, p.optJSONArray("flags"));
            f.profiles.add(profile);
        }
        if (f.profiles.isEmpty()) f.profiles.add(new Profile(java.util.UUID.randomUUID().toString(), "Default"));
        f.current = loaded.optString("current", f.profiles.get(0).id);
        return f;
    }

    public JSONObject toJson() {
        JSONObject o = new JSONObject();
        try {
            o.put("version", 1).put("current", current);
            JSONArray a = new JSONArray();
            for (Profile p : profiles) {
                a.put(new JSONObject().put("id", p.id).put("name", p.name).put("created", p.created).put("updated", p.updated).put("exported", p.exported).put("flags", p.valuesJson()));
            }
            o.put("profiles", a);
        } catch (JSONException ignored) {
        }
        return o;
    }

    public Profile byId(String id) {
        for (Profile p : profiles) if (p.id != null && p.id.equals(id)) return p;
        return null;
    }

    public Profile active() {
        Profile p = byId(current);
        if (p != null) return p;
        if (profiles.isEmpty()) profiles.add(new Profile(java.util.UUID.randomUUID().toString(), "Default"));
        return profiles.get(0);
    }

    public static boolean validKey(String key) {
        return key != null && Core.flag("flags.validKey", Core.args("key", key));
    }

    public static boolean intFlag(String key) {
        return Core.flag("flags.intFlag", Core.args("key", key));
    }

    public static boolean fitsInt(Object v) {
        return v instanceof Long && (Long) v == ((Long) v).intValue();
    }

    public static String cleanKey(CharSequence text) {
        if (text == null) return "";
        try {
            String k = Core.text("flags.cleanKey", Core.args("text", text.toString()));
            return k == null ? "" : k;
        } catch (IOException e) {
            return text.toString().trim();
        }
    }

    public static Object parseTyped(String type, String text) {
        try {
            return value(Core.value("flags.parseTyped", Core.args("type", type, "text", text)));
        } catch (IOException e) {
            return null;
        }
    }

    public static String typeOf(Object v) {
        if (v instanceof Boolean) return "bool";
        if (v instanceof Number) return "number";
        return "string";
    }

    public static String display(Object v) {
        return String.valueOf(v);
    }

    public static final class Profile {
        public final String id;
        public String name;
        public long created;
        public long updated;
        public long exported;
        public final LinkedHashMap<String, Object> values = new LinkedHashMap<>();

        public Profile(String id, String name) {
            this.id = id;
            this.name = name;
            created = System.currentTimeMillis();
            updated = created;
        }

        public JSONObject valuesJson() {
            JSONObject o = new JSONObject();
            for (Map.Entry<String, Object> e : values.entrySet()) {
                try {
                    o.put(e.getKey(), e.getValue());
                } catch (JSONException ignored) {
                }
            }
            return o;
        }

        public JSONObject envelope(String targetPackage) throws JSONException {
            return new JSONObject()
                    .put("format", FORMAT)
                    .put("version", 1)
                    .put("name", name)
                    .put("targetPlatform", "android")
                    .put("targetPackage", targetPackage)
                    .put("created", created)
                    .put("updated", updated)
                    .put("notes", "Saved by Voidstrap for Android.")
                    .put("flags", valuesJson());
        }
    }

    public static final class ImportResult {
        public String name;
        public final LinkedHashMap<String, Object> values = new LinkedHashMap<>();
        public final List<String> duplicates = new ArrayList<>();
        public final List<String> rejected = new ArrayList<>();
    }

    public static final class FormatException extends Exception {
        public final int reason;

        FormatException(int reason) {
            super(String.valueOf(reason));
            this.reason = reason;
        }
    }

    public static String readBounded(InputStream in) throws IOException, FormatException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) {
            if (out.size() + n > MAX_BYTES) throw new FormatException(R.string.flags_error_too_large);
            out.write(buf, 0, n);
        }
        return new String(out.toByteArray(), StandardCharsets.UTF_8);
    }

    public static ImportResult parse(String json) throws FormatException {
        JSONObject r;
        try {
            r = Core.run("flags.parse", Core.args("text", json));
        } catch (IOException e) {
            throw new FormatException(R.string.flags_error_invalid);
        }
        String error = r.optString("error", "");
        if (error.equals("not_object")) throw new FormatException(R.string.flags_error_not_object);
        if (error.equals("too_many")) throw new FormatException(R.string.flags_error_too_many);
        if (!error.isEmpty()) throw new FormatException(R.string.flags_error_invalid);
        ImportResult out = new ImportResult();
        out.name = r.isNull("name") ? null : r.optString("name");
        fill(out.values, r.optJSONArray("values"));
        out.duplicates.addAll(Core.strings(r.optJSONArray("duplicates")));
        out.rejected.addAll(Core.strings(r.optJSONArray("rejected")));
        return out;
    }
}
