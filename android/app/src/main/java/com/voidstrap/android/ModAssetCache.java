package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;

final class ModAssetCache {
    static final String BACKUP = "AssetCacheBackup";

    private ModAssetCache() {
    }

    static boolean hasEntries(Context c, File archive, AtomicBoolean cancel) throws IOException {
        return Boolean.TRUE.equals(Core.run("mods.cacheHasEntries", Core.args("archive", archive), null, -1, cancel).opt("v"));
    }

    static ManagedMods.Record installPack(Context c, File archive, String name, ManagedMods.Pack pack, AtomicBoolean cancel) throws IOException {
        JSONObject args = Core.args("managed", ManagedMods.root(c), "archive", archive, "name", name, "pack", pack == null ? null : pack.toJson());
        return ManagedMods.Record.from(Core.run("mods.cacheInstall", args, null, -1, cancel));
    }

    static boolean isCacheMod(File folder) {
        return Core.flag("mods.isCacheMod", Core.args("folder", folder));
    }

    static Map<String, File> wanted(Context c) {
        Map<String, File> out = new LinkedHashMap<>();
        try {
            Object v = Core.value("mods.cacheWanted", Core.roots(c));
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            for (int i = 0; i < a.length(); i++) {
                JSONArray p = a.optJSONArray(i);
                if (p == null || p.length() < 2) continue;
                out.put(p.optString(0), new File(p.optString(1)));
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    static boolean pending(Context c, String pkg) {
        return Core.flag("mods.cachePending", Core.roots(c, "pkg", pkg, "enabled", ModEngine.enabled(c)));
    }

    static int sync(Context c, String pkg, FlagWriter.Mode mode, boolean restoreAll) {
        if (!pkg.matches("[A-Za-z0-9_.]+")) return FlagWriter.FAILED;
        try {
            JSONObject plan = Core.run("mods.cacheSync", Core.roots(c, "pkg", pkg, "enabled", ModEngine.enabled(c), "restoreAll", restoreAll));
            if (plan.optBoolean("skip")) return FlagWriter.OK;
            int code = FlagWriter.raw(mode, plan.optString("script"), 120);
            if (code == 0) Core.run("mods.cacheSynced", Core.roots(c, "pkg", pkg, "hashes", plan.optJSONArray("hashes")));
            return code;
        } catch (IOException e) {
            return FlagWriter.FAILED;
        }
    }
}
