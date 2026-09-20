package com.voidstrap.android;

import android.content.ContentResolver;
import android.content.Context;
import android.database.Cursor;
import android.net.Uri;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipException;
import java.util.zip.ZipInputStream;
import java.util.zip.ZipOutputStream;

public final class Mods {
    public static final long MAX_FILE = 256L * 1024 * 1024;
    public static final int MAX_ENTRIES = 5000;
    public static final long MAX_ENTRY = 64L * 1024 * 1024;
    public static final long MAX_TOTAL = 512L * 1024 * 1024;
    public static final int MAX_RATIO = 100;
    public static final long SPACE_MARGIN = 64L * 1024 * 1024;

    private static final Set<String> BLOCKED = new HashSet<>(Arrays.asList(
            "dex", "so", "jar", "apk", "apks", "xapk", "aab", "odex", "vdex", "oat",
            "lua", "luau", "rbxm", "rbxmx", "js", "sh", "bat", "cmd", "exe", "dll", "ps1", "py"));

    public static final class ImportException extends Exception {
        public final int reason;

        ImportException(int reason) {
            super(String.valueOf(reason));
            this.reason = reason;
        }
    }

    private Mods() {
    }

    public static File root(Context c) {
        File r = new File(c.getFilesDir(), "mods");
        if (!r.isDirectory()) r.mkdirs();
        return r;
    }

    static boolean lowSpace(android.os.storage.StorageManager storage, File dir) {
        return lowSpace(storage, dir, 0);
    }

    static boolean lowSpace(android.os.storage.StorageManager storage, File dir, long need) {
        if (storage != null) {
            try {
                return storage.getAllocatableBytes(storage.getUuidForPath(dir)) < need + SPACE_MARGIN;
            } catch (IOException | RuntimeException ignored) {
            }
        }
        return dir.getUsableSpace() < need + SPACE_MARGIN;
    }

    public static boolean inside(File root, File f) {
        try {
            String r = root.getCanonicalPath();
            String p = f.getCanonicalPath();
            return p.equals(r) || p.startsWith(r + File.separator);
        } catch (IOException e) {
            return false;
        }
    }

    public static List<File> list(File dir) {
        File[] files = dir.listFiles();
        List<File> out = new ArrayList<>();
        if (files == null) return out;
        Arrays.sort(files, (a, b) -> {
            if (a.isDirectory() != b.isDirectory()) return a.isDirectory() ? -1 : 1;
            return a.getName().compareToIgnoreCase(b.getName());
        });
        for (File f : files) if (!f.getName().startsWith(".")) out.add(f);
        return out;
    }

    public static String cleanName(String name) {
        if (name == null) return null;
        String n = name.trim();
        int slash = Math.max(n.lastIndexOf('/'), n.lastIndexOf('\\'));
        if (slash >= 0) n = n.substring(slash + 1);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n.length(); i++) {
            char ch = n.charAt(i);
            if (Character.isISOControl(ch) || "<>:\"|?*".indexOf(ch) >= 0) continue;
            sb.append(ch);
        }
        n = sb.toString().trim();
        if (n.isEmpty() || n.equals(".") || n.equals("..") || n.startsWith(".")) return null;
        if (n.length() > 128) n = n.substring(0, 128);
        return n;
    }

    public static String extension(String name) {
        int dot = name.lastIndexOf('.');
        return dot < 0 ? "" : name.substring(dot + 1).toLowerCase(Locale.ROOT);
    }

    public static boolean blocked(String name) {
        return BLOCKED.contains(extension(name));
    }

    public static boolean isImage(File f) {
        String e = extension(f.getName());
        return e.equals("png") || e.equals("jpg") || e.equals("jpeg") || e.equals("webp") || e.equals("gif") || e.equals("bmp");
    }

    public static boolean isAudio(File f) {
        String e = extension(f.getName());
        return e.equals("ogg") || e.equals("mp3") || e.equals("wav") || e.equals("m4a") || e.equals("flac");
    }

    public static File unique(File dir, String name) {
        File f = new File(dir, name);
        if (!f.exists()) return f;
        String base = name;
        String ext = "";
        int dot = name.lastIndexOf('.');
        if (dot > 0) {
            base = name.substring(0, dot);
            ext = name.substring(dot);
        }
        for (int i = 2; i < 10000; i++) {
            f = new File(dir, base + " (" + i + ")" + ext);
            if (!f.exists()) return f;
        }
        return new File(dir, base + " " + System.currentTimeMillis() + ext);
    }

    public static String displayName(ContentResolver cr, Uri uri) {
        try (Cursor c = cr.query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (c != null && c.moveToFirst()) return c.getString(0);
        } catch (RuntimeException ignored) {
        }
        return uri.getLastPathSegment();
    }

    public static File importFile(Context c, Uri uri, File dir, AtomicBoolean cancel) throws IOException, ImportException {
        ContentResolver cr = c.getContentResolver();
        String name = cleanName(displayName(cr, uri));
        if (name == null) throw new ImportException(R.string.mods_error_name);
        if (blocked(name)) throw new ImportException(R.string.mods_error_blocked);
        File staging = new File(stagingDir(c), "import_" + System.nanoTime());
        try (InputStream in = cr.openInputStream(uri); OutputStream out = new FileOutputStream(staging)) {
            if (in == null) throw new IOException("stream");
            copy(in, out, MAX_FILE, cancel, staging, c.getSystemService(android.os.storage.StorageManager.class));
            out.flush();
        } catch (IOException | ImportException e) {
            staging.delete();
            throw e;
        }
        File dest = unique(dir, name);
        if (!move(staging, dest)) {
            staging.delete();
            throw new IOException("move");
        }
        return dest;
    }

    public static File importPack(Context c, Uri uri, File dir, AtomicBoolean cancel) throws IOException, ImportException {
        ContentResolver cr = c.getContentResolver();
        String packName = cleanName(displayName(cr, uri));
        if (packName == null) packName = "Pack";
        if (packName.toLowerCase(Locale.ROOT).endsWith(".zip")) packName = packName.substring(0, packName.length() - 4);
        packName = cleanName(packName);
        if (packName == null) packName = "Pack";
        File staging = new File(stagingDir(c), "pack_" + System.nanoTime());
        if (!staging.mkdirs()) throw new IOException("staging");
        try (InputStream raw = cr.openInputStream(uri)) {
            if (raw == null) throw new IOException("stream");
            extract(raw, staging, cancel, c.getSystemService(android.os.storage.StorageManager.class));
            File dest = unique(dir, packName);
            if (!move(staging, dest)) throw new IOException("move");
            return dest;
        } catch (IOException | ImportException e) {
            delete(staging);
            throw e;
        }
    }

    static void extract(InputStream raw, File staging, AtomicBoolean cancel) throws IOException, ImportException {
        extract(raw, staging, cancel, null);
    }

    static void extract(InputStream raw, File staging, AtomicBoolean cancel, android.os.storage.StorageManager storage) throws IOException, ImportException {
        Counting counted = new Counting(raw);
        Set<String> seen = new HashSet<>();
        long total = 0;
        int entries = 0;
        try (ZipInputStream zip = new ZipInputStream(counted)) {
            ZipEntry e;
            byte[] buf = new byte[65536];
            while ((e = next(zip)) != null) {
                if (cancel.get()) throw new ImportException(R.string.mods_error_cancelled);
                if (++entries > MAX_ENTRIES) throw new ImportException(R.string.mods_error_too_many);
                String path = normalize(e.getName());
                if (path == null) throw new ImportException(R.string.mods_error_unsafe);
                if (!seen.add(path.toLowerCase(Locale.ROOT))) throw new ImportException(R.string.mods_error_collision);
                File out = new File(staging, path);
                if (!inside(staging, out)) throw new ImportException(R.string.mods_error_unsafe);
                if (e.isDirectory()) {
                    if (!out.isDirectory() && !out.mkdirs()) throw new IOException("mkdir");
                    continue;
                }
                if (blocked(out.getName())) throw new ImportException(R.string.mods_error_blocked);
                File parent = out.getParentFile();
                if (parent != null && !parent.isDirectory() && !parent.mkdirs()) throw new IOException("mkdir");
                long size = 0;
                try (OutputStream os = new FileOutputStream(out)) {
                    int n;
                    while ((n = zip.read(buf)) > 0) {
                        size += n;
                        total += n;
                        if (size > MAX_ENTRY) throw new ImportException(R.string.mods_error_too_large);
                        if (total > MAX_TOTAL) throw new ImportException(R.string.mods_error_too_large);
                        if (total > 1024 * 1024 && total > counted.count * MAX_RATIO) throw new ImportException(R.string.mods_error_ratio);
                        if (cancel.get()) throw new ImportException(R.string.mods_error_cancelled);
                        if ((total & 0xFFFFF) < n && lowSpace(storage, staging)) throw new ImportException(R.string.mods_error_space);
                        os.write(buf, 0, n);
                    }
                }
            }
        }
        if (entries == 0) throw new ImportException(R.string.mods_error_empty);
    }

    private static ZipEntry next(ZipInputStream zip) throws IOException, ImportException {
        try {
            return zip.getNextEntry();
        } catch (ZipException e) {
            String m = e.getMessage();
            throw new ImportException(m != null && m.toLowerCase(Locale.ROOT).contains("path") ? R.string.mods_error_unsafe : R.string.mods_error_not_zip);
        }
    }

    static String normalize(String name) {
        if (name == null) return null;
        String n = name.replace('\\', '/');
        if (n.startsWith("/") || n.contains(":") || n.isEmpty()) return null;
        List<String> parts = new ArrayList<>();
        for (String p : n.split("/")) {
            if (p.isEmpty() || p.equals(".")) continue;
            if (p.equals("..")) return null;
            for (int i = 0; i < p.length(); i++) if (Character.isISOControl(p.charAt(i))) return null;
            if (p.length() > 128) return null;
            parts.add(p);
        }
        if (parts.isEmpty() || parts.size() > 16) return null;
        return String.join("/", parts);
    }

    private static void copy(InputStream in, OutputStream out, long max, AtomicBoolean cancel, File dest, android.os.storage.StorageManager storage) throws IOException, ImportException {
        byte[] buf = new byte[65536];
        long total = 0;
        int n;
        while ((n = in.read(buf)) > 0) {
            total += n;
            if (total > max) throw new ImportException(R.string.mods_error_too_large);
            if (cancel.get()) throw new ImportException(R.string.mods_error_cancelled);
            if ((total & 0xFFFFF) < n && dest.getParentFile() != null && lowSpace(storage, dest.getParentFile())) throw new ImportException(R.string.mods_error_space);
            out.write(buf, 0, n);
        }
    }

    private static boolean move(File from, File to) {
        return from.renameTo(to);
    }

    public static void export(Context c, File f, Uri target) throws IOException {
        try (OutputStream out = c.getContentResolver().openOutputStream(target, "wt")) {
            if (out == null) throw new IOException("stream");
            if (f.isDirectory()) {
                try (ZipOutputStream zip = new ZipOutputStream(out)) {
                    zipDir(zip, f, "");
                }
            } else {
                try (InputStream in = new FileInputStream(f)) {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                }
            }
        }
    }

    private static void zipDir(ZipOutputStream zip, File dir, String prefix) throws IOException {
        for (File f : list(dir)) {
            String name = prefix + f.getName();
            if (f.isDirectory()) {
                zip.putNextEntry(new ZipEntry(name + "/"));
                zip.closeEntry();
                zipDir(zip, f, name + "/");
            } else {
                zip.putNextEntry(new ZipEntry(name));
                try (InputStream in = new FileInputStream(f)) {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = in.read(buf)) > 0) zip.write(buf, 0, n);
                }
                zip.closeEntry();
            }
        }
    }

    public static int count(File f) {
        if (!f.isDirectory()) return 1;
        int n = 0;
        File[] kids = f.listFiles();
        if (kids != null) for (File k : kids) n += count(k);
        return n;
    }

    public static long size(File f) {
        if (!f.isDirectory()) return f.length();
        long n = 0;
        File[] kids = f.listFiles();
        if (kids != null) for (File k : kids) n += size(k);
        return n;
    }

    public static boolean delete(File f) {
        if (f.isDirectory()) {
            File[] kids = f.listFiles();
            if (kids != null) for (File k : kids) delete(k);
        }
        return f.delete();
    }

    private static File stagingDir(Context c) {
        File d = new File(c.getFilesDir(), ".staging");
        if (!d.isDirectory()) d.mkdirs();
        return d;
    }

    public static void sweepStaging(Context c) {
        for (File dir : new File[]{c.getCacheDir(), stagingDir(c)}) {
            File[] files = dir.listFiles();
            if (files == null) continue;
            for (File f : files) {
                if (f.getName().startsWith("pack_") || f.getName().startsWith("import_")) delete(f);
            }
        }
    }

    private static final class Counting extends FilterInputStream {
        long count;

        Counting(InputStream in) {
            super(in);
        }

        @Override
        public int read() throws IOException {
            int b = super.read();
            if (b >= 0) count++;
            return b;
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            int n = super.read(b, off, len);
            if (n > 0) count += n;
            return n;
        }

        @Override
        public long skip(long n) throws IOException {
            long s = super.skip(n);
            count += s;
            return s;
        }
    }
}
