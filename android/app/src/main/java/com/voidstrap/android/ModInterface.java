package com.voidstrap.android;

import android.content.Context;

import java.io.File;
import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

final class ModInterface {
    static final long PS4_MOD = 596647;
    static final long PS4_FILE = 1445838;
    static final String PS4_NAME = "PS4 Buttons Overlay";

    private static final String PS4_STATE = "Ps4ModId";
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
            Core.run("mods.trim", Core.indexed(c, "managed", ManagedMods.root(c), "id", added.id));
            writeState(c, PS4_STATE, added.id);
        }
    }

    static boolean hideCoreGui(Context c) {
        return Core.flag("mods.hideCoreGui", Core.roots(c));
    }

    static void setHideCoreGui(Context c, boolean on, AtomicBoolean cancel) throws IOException {
        synchronized (LOCK) {
            Core.run("mods.setHideCoreGui", Core.roots(c, "on", on, "base", source(c)), null, -1, cancel);
        }
    }

    private static ManagedMods.Record ps4Record(Context c) {
        String id = readState(c, PS4_STATE);
        if (id.isEmpty()) return null;
        for (ManagedMods.Record r : ManagedMods.load(c)) if (id.equals(r.id)) return r;
        return null;
    }

    private static File source(Context c) {
        String pkg = Targets.selected(c);
        File base = ModEngine.originalApk(c, pkg);
        if (base == null) base = ModEngine.baseApk(c, pkg);
        return base != null && base.canRead() ? base : null;
    }

    private static String readState(Context c, String name) {
        try {
            String v = Core.text("mods.readState", Core.roots(c, "name", name));
            return v == null ? "" : v;
        } catch (IOException e) {
            return "";
        }
    }

    private static void writeState(Context c, String name, String value) throws IOException {
        Core.run("mods.writeState", Core.roots(c, "name", name, "value", value));
    }
}
