package com.voidstrap.android;

import android.content.Context;

import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ModArchives {
    public static final long MAX_PACKAGE = 256L * 1024 * 1024;

    public static final class Rejected extends IOException {
        public Rejected(String reason) {
            super(reason);
        }
    }

    public static final class Inspection {
        public boolean robloxContent;
        public int files;
        public long bytes;
    }

    private ModArchives() {
    }

    public static Inspection inspect(Context c, File archive, AtomicBoolean cancel) throws IOException {
        JSONObject o = Core.run("mods.inspect", Core.indexed(c, "archive", archive), null, -1, cancel);
        Inspection r = new Inspection();
        r.robloxContent = o.optBoolean("robloxContent");
        r.files = o.optInt("files");
        r.bytes = o.optLong("bytes");
        return r;
    }

    public static ManagedMods.Record installManaged(Context c, File archive, String name, ManagedMods.Pack pack, AtomicBoolean cancel) throws IOException {
        JSONObject args = Core.full(c, "archive", archive, "name", name, "pack", pack == null ? null : pack.toJson());
        return ManagedMods.Record.from(Core.run("mods.installManaged", args, null, -1, cancel));
    }
}
