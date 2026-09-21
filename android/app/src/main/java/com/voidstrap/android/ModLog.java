package com.voidstrap.android;

import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Date;
import java.util.Deque;
import java.util.List;
import java.util.Locale;

public final class ModLog {
    private static final String TAG = "VoidstrapMods";
    private static final int MAX = 150;
    private static final Deque<String> LINES = new ArrayDeque<>();
    private static final SimpleDateFormat STAMP = new SimpleDateFormat("HH:mm:ss", Locale.US);
    private static final long MAX_BYTES = 128L * 1024;
    private static File file;

    private ModLog() {
    }

    public static synchronized void init(File dir) {
        if (dir == null) return;
        file = new File(dir, "modlog.txt");
        if (file.length() > MAX_BYTES) file.delete();
    }

    public static synchronized void add(String text) {
        if (text == null || text.isEmpty()) return;
        if (LINES.size() >= MAX) LINES.removeFirst();
        String line = STAMP.format(new Date()) + " " + text;
        LINES.addLast(line);
        Log.i(TAG, text);
        persist(line);
    }

    public static synchronized String dump() {
        List<String> lines = LINES.isEmpty() ? tail() : new ArrayList<>(LINES);
        if (lines.isEmpty()) return "  nothing recorded yet\n";
        StringBuilder sb = new StringBuilder();
        for (String line : lines) sb.append("  ").append(line).append('\n');
        return sb.toString();
    }

    public static synchronized void clear() {
        LINES.clear();
        File f = file;
        if (f != null) f.delete();
    }

    private static void persist(String line) {
        File f = file;
        if (f == null) return;
        try (FileOutputStream out = new FileOutputStream(f, true)) {
            out.write((line + "\n").getBytes(StandardCharsets.UTF_8));
        } catch (IOException | RuntimeException ignored) {
        }
    }

    private static List<String> tail() {
        List<String> out = new ArrayList<>();
        File f = file;
        if (f == null || !f.isFile()) return out;
        try {
            List<String> all = java.nio.file.Files.readAllLines(f.toPath(), StandardCharsets.UTF_8);
            out.addAll(all.subList(Math.max(0, all.size() - MAX), all.size()));
        } catch (IOException | RuntimeException ignored) {
        }
        return out;
    }
}
