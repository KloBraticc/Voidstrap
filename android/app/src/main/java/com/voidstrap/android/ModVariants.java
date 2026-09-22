package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ModVariants {
    static final Set<String> PREVIEW_EXTENSIONS = new HashSet<>(Arrays.asList("png", "jpg", "jpeg", "bmp", "gif", "webp"));

    public static final class Option {
        public final String label;
        public final File source;

        Option(String label, File source) {
            this.label = label;
            this.source = source;
        }
    }

    public static final class Slot {
        public final String target;
        public final List<Option> options = new ArrayList<>();
        public int selected;

        Slot(String target) {
            this.target = target;
        }
    }

    private ModVariants() {
    }

    public static File sources(Context c, String id) {
        return new File(new File(ManagedMods.root(c), "Sources"), id.toLowerCase(Locale.ROOT));
    }

    public static boolean hasSlots(Context c, String id) {
        return Core.flag("mods.hasSlots", Core.args("managed", ManagedMods.root(c), "id", id));
    }

    public static int capture(Context c, String id, File archive, AtomicBoolean cancel) throws IOException {
        JSONObject args = Core.indexed(c, "managed", ManagedMods.root(c), "id", id, "archive", archive);
        return Core.run("mods.capture", args, null, -1, cancel).optInt("v");
    }

    public static List<Slot> build(Context c, String id) {
        List<Slot> out = new ArrayList<>();
        try {
            Object v = Core.value("mods.buildSlots", Core.indexed(c, "managed", ManagedMods.root(c), "id", id));
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            for (int i = 0; i < a.length(); i++) {
                JSONObject o = a.optJSONObject(i);
                if (o == null) continue;
                Slot slot = new Slot(o.optString("target"));
                JSONArray opts = o.optJSONArray("options");
                if (opts != null) for (int k = 0; k < opts.length(); k++) {
                    JSONObject p = opts.optJSONObject(k);
                    if (p == null) continue;
                    String label = p.isNull("label") ? null : p.optString("label");
                    String source = p.isNull("source") ? null : p.optString("source");
                    slot.options.add(new Option(label, source == null ? null : new File(source)));
                }
                slot.selected = o.optInt("selected");
                out.add(slot);
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    public static int apply(Context c, String id, List<Slot> slots) {
        JSONArray a = new JSONArray();
        for (Slot s : slots) {
            File source = s.options.get(s.selected).source;
            a.put(Core.args("target", s.target, "source", source));
        }
        try {
            return Core.run("mods.applySlots", Core.args("managed", ManagedMods.root(c), "id", id, "slots", a)).optInt("v");
        } catch (IOException e) {
            return 0;
        }
    }

    public static void delete(Context c, String id) {
        Mods.delete(sources(c, id));
    }
}
