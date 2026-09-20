package com.voidstrap.android;

import android.content.Context;

import java.io.File;
import java.io.IOException;
import java.util.Locale;

final class ModMigration {
    private static final String KEY = "modsMigrated";

    private ModMigration() {
    }

    static void run(Context c) {
        Store store = Store.get(c);
        if ("1".equals(store.setting(KEY, null))) return;
        File root = Mods.root(c);
        File[] kids = root.listFiles();
        if (kids != null) for (File d : kids) {
            if (!d.isDirectory() || d.getName().startsWith(".")) continue;
            String n = d.getName().toLowerCase(Locale.ROOT);
            if (n.equals("content") || n.equals("extracontent") || n.equals("platformcontent")) continue;
            if (!holdsContent(d)) continue;
            File staging = new File(ManagedMods.root(c), "staging_" + System.nanoTime());
            File parent = staging.getParentFile();
            if (parent != null) parent.mkdirs();
            if (!d.renameTo(staging)) continue;
            try {
                ManagedMods.adopt(c, staging, d.getName());
            } catch (IOException e) {
                staging.renameTo(d);
            }
        }
        store.putSetting(KEY, "1");
    }

    private static boolean holdsContent(File d) {
        String[] names = d.list();
        if (names == null) return false;
        for (String n : names) {
            String l = n.toLowerCase(Locale.ROOT);
            if (l.equals("content") || l.equals("extracontent") || l.equals("platformcontent")) return true;
        }
        return false;
    }
}
