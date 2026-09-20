package com.voidstrap.android;

import android.util.JsonReader;
import android.util.JsonToken;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.StringReader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.regex.Pattern;

public final class Flags {
    public static final int MAX_BYTES = 4 * 1024 * 1024;
    public static final int MAX_ENTRIES = 10000;
    public static final int MAX_KEY = 256;
    public static final int MAX_VALUE = 4096;
    public static final int MAX_NAME = 64;
    public static final String FORMAT = "voidstrap.flags";

    private static final Pattern INTEGER = Pattern.compile("-?\\d{1,18}");
    private static final Pattern NAME = Pattern.compile("[A-Za-z0-9_]{1," + MAX_KEY + "}");

    public final List<Profile> profiles = new ArrayList<>();
    public String current;

    public static Flags from(JSONObject o) {
        Flags f = new Flags();
        JSONArray a = o.optJSONArray("profiles");
        if (a != null) for (int i = 0; i < a.length(); i++) {
            JSONObject p = a.optJSONObject(i);
            if (p == null) continue;
            Profile profile = new Profile(p.optString("id", UUID.randomUUID().toString()), Store.clip(p.optString("name", "Default"), MAX_NAME));
            profile.created = p.optLong("created", System.currentTimeMillis());
            profile.updated = p.optLong("updated", profile.created);
            profile.exported = p.optLong("exported", 0);
            JSONObject values = p.optJSONObject("flags");
            if (values != null) {
                Iterator<String> keys = values.keys();
                while (keys.hasNext() && profile.values.size() < MAX_ENTRIES) {
                    String k = keys.next();
                    Object v = fromJson(values.opt(k));
                    if (v != null && storedKey(k)) profile.values.put(k, v);
                }
            }
            f.profiles.add(profile);
        }
        if (f.profiles.isEmpty()) f.profiles.add(new Profile(UUID.randomUUID().toString(), "Default"));
        f.current = o.optString("current", f.profiles.get(0).id);
        if (f.byId(f.current) == null) f.current = f.profiles.get(0).id;
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
        for (Profile p : profiles) if (p.id.equals(id)) return p;
        return null;
    }

    public Profile active() {
        Profile p = byId(current);
        return p != null ? p : profiles.get(0);
    }

    public static boolean validKey(String key) {
        return key != null && NAME.matcher(key).matches();
    }

    private static boolean storedKey(String key) {
        if (key == null || key.isEmpty() || key.length() > MAX_KEY) return false;
        for (int i = 0; i < key.length(); i++) {
            char c = key.charAt(i);
            if (Character.isWhitespace(c) || Character.isISOControl(c)) return false;
        }
        return true;
    }

    public static boolean intFlag(String key) {
        return key.startsWith("FInt") || key.startsWith("DFInt") || key.startsWith("SFInt");
    }

    public static boolean fitsInt(Object v) {
        return v instanceof Long && (Long) v == ((Long) v).intValue();
    }

    public static String cleanKey(CharSequence text) {
        return text == null ? "" : text.toString().trim().replaceAll("^[\"']+|[\"'\\s:=,]+$", "");
    }

    public static boolean validValue(Object v) {
        if (v instanceof Boolean || v instanceof Long) return true;
        if (v instanceof Double) return !((Double) v).isNaN() && !((Double) v).isInfinite();
        if (v instanceof String) {
            String s = (String) v;
            if (s.length() > MAX_VALUE) return false;
            for (int i = 0; i < s.length(); i++) if (Character.isISOControl(s.charAt(i)) && s.charAt(i) != '\t') return false;
            return true;
        }
        return false;
    }

    static Object fromJson(Object v) {
        if (v instanceof Boolean || v instanceof String) return validValue(v) ? v : null;
        if (v instanceof Integer || v instanceof Long) return ((Number) v).longValue();
        if (v instanceof Number) {
            double d = ((Number) v).doubleValue();
            return validValue(d) ? d : null;
        }
        return null;
    }

    public static Object parseTyped(String type, String text) {
        switch (type) {
            case "bool":
                String b = text.trim().replaceAll("^\"(.*)\"$", "$1");
                if ("true".equalsIgnoreCase(b)) return Boolean.TRUE;
                if ("false".equalsIgnoreCase(b)) return Boolean.FALSE;
                return null;
            case "number":
                String t = text.trim().replaceAll("^\"(.*)\"$", "$1");
                if (INTEGER.matcher(t).matches()) return Long.parseLong(t);
                try {
                    double d = Double.parseDouble(t);
                    return validValue(d) ? d : null;
                } catch (NumberFormatException e) {
                    return null;
                }
            default:
                return validValue(text) ? text : null;
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

    static String tolerant(String json) {
        String text = json.trim();
        if (!text.isEmpty() && text.charAt(0) == 0xFEFF) text = text.substring(1).trim();
        if (text.startsWith("\"")) text = "{" + text + "\n}";
        StringBuilder out = new StringBuilder(text.length());
        int n = text.length();
        for (int i = 0; i < n; i++) {
            char ch = text.charAt(i);
            if (ch == '"') {
                int j = i + 1;
                while (j < n && text.charAt(j) != '"') j += text.charAt(j) == '\\' ? 2 : 1;
                out.append(text, i, Math.min(j + 1, n));
                i = j;
            } else if (ch == '/' && i + 1 < n && text.charAt(i + 1) == '/') {
                while (i + 1 < n && text.charAt(i + 1) != '\n') i++;
            } else if (ch == '/' && i + 1 < n && text.charAt(i + 1) == '*') {
                int end = text.indexOf("*/", i + 2);
                i = end < 0 ? n : end + 1;
            } else {
                if (ch == '}' || ch == ']') {
                    int k = out.length() - 1;
                    while (k >= 0 && Character.isWhitespace(out.charAt(k))) k--;
                    if (k >= 0 && out.charAt(k) == ',') out.deleteCharAt(k);
                }
                out.append(ch);
            }
        }
        return out.toString();
    }

    public static ImportResult parse(String json) throws FormatException {
        ImportResult raw = new ImportResult();
        ImportResult envelope = null;
        String format = null;
        String name = null;
        try (JsonReader r = new JsonReader(new StringReader(tolerant(json)))) {
            if (r.peek() != JsonToken.BEGIN_OBJECT) throw new FormatException(R.string.flags_error_not_object);
            r.beginObject();
            int count = 0;
            while (r.hasNext()) {
                String key = r.nextName();
                if (++count > MAX_ENTRIES) throw new FormatException(R.string.flags_error_too_many);
                JsonToken t = r.peek();
                if (t == JsonToken.BEGIN_OBJECT && "flags".equals(key)) {
                    envelope = new ImportResult();
                    readValues(r, envelope);
                    continue;
                }
                if (t == JsonToken.BEGIN_OBJECT && "applicationSettings".equals(key)) {
                    readValues(r, raw);
                    continue;
                }
                if ("format".equals(key) && t == JsonToken.STRING) {
                    String v = r.nextString();
                    format = v;
                    add(raw, key, v);
                    continue;
                }
                if ("name".equals(key) && t == JsonToken.STRING) {
                    String v = r.nextString();
                    name = v;
                    add(raw, key, v);
                    continue;
                }
                readOne(r, key, t, raw);
            }
            r.endObject();
            if (r.peek() != JsonToken.END_DOCUMENT) throw new FormatException(R.string.flags_error_invalid);
        } catch (IOException | IllegalStateException | NumberFormatException e) {
            throw new FormatException(R.string.flags_error_invalid);
        }
        if (FORMAT.equals(format) && envelope != null) {
            envelope.name = name == null ? null : Store.clip(name, MAX_NAME);
            return envelope;
        }
        return raw;
    }

    private static void readValues(JsonReader r, ImportResult into) throws IOException, FormatException {
        r.beginObject();
        int count = 0;
        while (r.hasNext()) {
            String key = r.nextName();
            if (++count > MAX_ENTRIES) throw new FormatException(R.string.flags_error_too_many);
            readOne(r, key, r.peek(), into);
        }
        r.endObject();
    }

    private static void readOne(JsonReader r, String key, JsonToken t, ImportResult into) throws IOException {
        Object v;
        switch (t) {
            case BOOLEAN:
                v = r.nextBoolean();
                break;
            case NUMBER:
                String lit = r.nextString();
                if (INTEGER.matcher(lit).matches()) v = Long.parseLong(lit);
                else {
                    double d = Double.parseDouble(lit);
                    v = validValue(d) ? d : null;
                }
                break;
            case STRING:
                v = r.nextString();
                break;
            default:
                r.skipValue();
                v = null;
        }
        if (v == null || !validKey(key) || !validValue(v)) {
            into.rejected.add(Store.clip(key, 80));
            return;
        }
        add(into, key, v);
    }

    private static void add(ImportResult into, String key, Object v) {
        if (into.values.containsKey(key)) into.duplicates.add(Store.clip(key, 80));
        else into.values.put(key, v);
    }
}
