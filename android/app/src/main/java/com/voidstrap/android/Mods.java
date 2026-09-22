package com.voidstrap.android;

import android.content.ContentResolver;
import android.content.Context;
import android.database.Cursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
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

public final class Mods {
    public static final long MAX_FILE = 256L * 1024 * 1024;
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
        if (storage != null && android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
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
            copy(in, out, cancel, staging, c.getSystemService(android.os.storage.StorageManager.class));
            out.flush();
        } catch (IOException | ImportException e) {
            staging.delete();
            throw e;
        }
        File dest = unique(dir, name);
        if (!staging.renameTo(dest)) {
            staging.delete();
            throw new IOException("move");
        }
        return dest;
    }

    private static void copy(InputStream in, OutputStream out, AtomicBoolean cancel, File dest, android.os.storage.StorageManager storage) throws IOException, ImportException {
        byte[] buf = new byte[65536];
        long total = 0;
        int n;
        while ((n = in.read(buf)) > 0) {
            total += n;
            if (total > MAX_FILE) throw new ImportException(R.string.mods_error_too_large);
            if (cancel.get()) throw new ImportException(R.string.mods_error_cancelled);
            if ((total & 0xFFFFF) < n && dest.getParentFile() != null && lowSpace(storage, dest.getParentFile())) throw new ImportException(R.string.mods_error_space);
            out.write(buf, 0, n);
        }
    }

    public static void export(Context c, File f, Uri target) throws IOException {
        if (f.isDirectory()) {
            try (ParcelFileDescriptor pfd = c.getContentResolver().openFileDescriptor(target, "wt")) {
                if (pfd == null) throw new IOException("stream");
                Core.run("mods.exportDir", Core.args("path", f), null, pfd.detachFd(), null);
            }
            return;
        }
        try (OutputStream out = c.getContentResolver().openOutputStream(target, "wt"); InputStream in = new FileInputStream(f)) {
            if (out == null) throw new IOException("stream");
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
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
}
