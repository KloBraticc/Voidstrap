package com.voidstrap.android;

import android.content.Context;

import java.io.File;
import java.io.IOException;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

public final class ContentPlacer {
    private static final Set<String> ROOTS = new HashSet<>(Arrays.asList("content", "extracontent", "platformcontent", "shaders"));
    private static final Map<String, String> ANCHORS = new HashMap<>();
    private static final Map<String, String> KNOWN = new HashMap<>();
    private static final String AMBIGUOUS = "";

    private static String indexKey;
    private static Set<String> clientFiles = new HashSet<>();
    private static Map<String, String> uniqueNames = new HashMap<>();

    static {
        ANCHORS.put("keyboardmouse", "content/textures/Cursors/KeyboardMouse");
        ANCHORS.put("dragdetector", "content/textures/Cursors/DragDetector");
        ANCHORS.put("cursors", "content/textures/Cursors");
        ANCHORS.put("families", "content/fonts/families");
        ANCHORS.put("particles", "content/textures/particles");
        ANCHORS.put("sky", "PlatformContent/pc/textures/sky");
        ANCHORS.put("textures", "content/textures");
        ANCHORS.put("sounds", "content/sounds");
        ANCHORS.put("fonts", "content/fonts");
        ANCHORS.put("music", "content/music");
        ANCHORS.put("models", "content/models");
        ANCHORS.put("avatar", "content/avatar");
        for (String n : new String[]{"ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png", "ArrowCursorDecalDrag.png"}) KNOWN.put(n.toLowerCase(Locale.ROOT), "content/textures/Cursors/KeyboardMouse");
        KNOWN.put("activatedcursor.png", "content/textures/Cursors/DragDetector");
        KNOWN.put("hovercursor.png", "content/textures/Cursors/DragDetector");
        KNOWN.put("mouselockedcursor.png", "content/textures");
        for (String f : ModPresets.SKY_FACES) KNOWN.put(f, "PlatformContent/pc/textures/sky");
        KNOWN.put("oof.ogg", "content/sounds");
        KNOWN.put("ouch.ogg", "content/sounds");
    }

    private ContentPlacer() {
    }

    public static String resolve(Context c, String relative) {
        String n = ModArchives.normalize(relative);
        String[] parts = n.split("/");
        int count = 0;
        for (String p : parts) if (!p.isEmpty()) count++;
        if (count == 0) return null;
        String[] seg = new String[count];
        int k = 0;
        for (String p : parts) if (!p.isEmpty()) seg[k++] = p;
        for (int i = 0; i < seg.length - 1; i++) {
            if (ROOTS.contains(seg[i].toLowerCase(Locale.ROOT))) return canonicalRoot(join(seg, i));
        }
        String candidate = null;
        for (int i = seg.length - 2; i >= 0 && candidate == null; i--) {
            String anchor = ANCHORS.get(seg[i].toLowerCase(Locale.ROOT));
            if (anchor != null) candidate = anchor + "/" + join(seg, i + 1);
        }
        String name = seg[seg.length - 1];
        if (candidate == null) {
            String folder = KNOWN.get(name.toLowerCase(Locale.ROOT));
            if (folder != null) candidate = folder + "/" + name;
        }
        if (candidate == null && name.toLowerCase(Locale.ROOT).startsWith("img_set_")) {
            candidate = "ExtraContent/LuaPackages/Packages/_Index/FoundationImages/FoundationImages/SpriteSheets/" + name;
        }
        ensureIndex(c);
        if (candidate != null && existsInClient(candidate)) return candidate;
        String unique = uniqueNames.get(name.toLowerCase(Locale.ROOT));
        if (unique != null && !unique.equals(AMBIGUOUS)) return unique;
        return candidate;
    }

    private static String canonicalRoot(String path) {
        int slash = path.indexOf('/');
        if (slash <= 0) return path;
        String head = path.substring(0, slash).toLowerCase(Locale.ROOT);
        String rest = path.substring(slash + 1);
        switch (head) {
            case "content":
                return "content/" + rest;
            case "extracontent":
                return "ExtraContent/" + rest;
            case "platformcontent":
                return "PlatformContent/" + rest;
            default:
                return path;
        }
    }

    public static boolean clientCopy(Context c, String relative) {
        String resolved = resolve(c, relative);
        return resolved != null && Mods.extension(resolved).equals(Mods.extension(relative)) && existsInClient(resolved);
    }

    static boolean existsInClient(String desktopRelative) {
        return clientFiles.contains(desktopRelative.toLowerCase(Locale.ROOT));
    }

    public static synchronized Set<String> clientFiles(Context c) {
        ensureIndex(c);
        return clientFiles;
    }

    private static synchronized void ensureIndex(Context c) {
        String pkg = Targets.selected(c);
        File base = ModEngine.originalApk(c, pkg);
        String key = base == null ? "" : ModEngine.identity(c, pkg);
        if (key.equals(indexKey)) return;
        Set<String> files = new HashSet<>();
        Map<String, String> names = new HashMap<>();
        if (base != null) {
            try {
                for (String entry : ApkPatcher.read(base).entries.keySet()) {
                    String desktop = desktopPath(entry);
                    if (desktop == null) continue;
                    files.add(desktop.toLowerCase(Locale.ROOT));
                    String file = desktop.substring(desktop.lastIndexOf('/') + 1).toLowerCase(Locale.ROOT);
                    names.put(file, names.containsKey(file) ? AMBIGUOUS : desktop);
                }
            } catch (IOException ignored) {
            }
        }
        clientFiles = files;
        uniqueNames = names;
        indexKey = key;
    }

    public static String desktopPath(String apkEntry) {
        if (apkEntry.startsWith("assets/content/")) return "content/" + apkEntry.substring(15);
        if (apkEntry.startsWith("assets/ExtraContent/")) return "ExtraContent/" + apkEntry.substring(20);
        if (apkEntry.startsWith("assets/android/")) return "PlatformContent/pc/" + apkEntry.substring(15);
        return null;
    }

    private static String join(String[] seg, int from) {
        StringBuilder sb = new StringBuilder();
        for (int i = from; i < seg.length; i++) {
            if (sb.length() > 0) sb.append('/');
            sb.append(seg[i]);
        }
        return sb.toString();
    }
}
