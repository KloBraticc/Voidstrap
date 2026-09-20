package com.voidstrap.android;

import android.content.Context;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipInputStream;

public final class ModArchives {
    public static final long MAX_PACKAGE = 256L * 1024 * 1024;
    public static final long MAX_EXTRACTED = 1024L * 1024 * 1024;
    public static final int MAX_ENTRIES = 6000;
    public static final int MAX_RATIO = 120;
    public static final long BOMB_FLOOR = 64L * 1024 * 1024;

    public enum Kind { ZIP, RAR4, RAR5, SEVEN_ZIP, UNKNOWN }

    public interface Handler {
        void entry(String name, long size, InputStream data) throws IOException;
    }

    static final Set<String> ASSET_EXTENSIONS = new HashSet<>(Arrays.asList(
            "png", "jpg", "jpeg", "bmp", "tga", "dds", "ktx", "webp", "tex",
            "ogg", "mp3", "wav", "flac",
            "mesh", "rbxm", "rbxmx", "rbxl", "rbxlx",
            "ttf", "otf", "woff", "woff2", "font", "fontfamily",
            "json", "xml", "txt", "csv", "md", "dat", "bin", "ini", "cfg",
            "hlsl", "glsl", "fx", "pack", "idx", "mp4", "webm", "gif"));

    static final Set<String> DANGEROUS = new HashSet<>(Arrays.asList(
            "exe", "dll", "sys", "drv", "com", "scr", "cpl", "msi", "msix", "msp",
            "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js", "jse", "wsf",
            "wsh", "hta", "reg", "lnk", "url", "scf", "inf", "jar", "py", "pyc",
            "sh", "apk", "app", "so", "dylib", "ocx", "ax", "efi", "iso", "img",
            "vhd", "vhdx", "lua", "luau", "rbxs", "dmp", "pif", "gadget", "application", "dex"));

    private static final Set<String> BLOCKED_ROOTS = new HashSet<>(Arrays.asList("ssl", "clientsettings", "webview2", "webview2runtimeinstaller"));
    private static final Set<String> RESERVED = new HashSet<>(Arrays.asList("con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"));

    public static final class Rejected extends IOException {
        public Rejected(String reason) {
            super(reason);
        }
    }

    private ModArchives() {
    }

    public static Kind kind(File f) {
        byte[] h = new byte[8];
        try (InputStream in = new FileInputStream(f)) {
            if (in.read(h) < 6) return Kind.UNKNOWN;
        } catch (IOException e) {
            return Kind.UNKNOWN;
        }
        if (h[0] == 'P' && h[1] == 'K' && (h[2] == 3 || h[2] == 5 || h[2] == 7)) return Kind.ZIP;
        if (h[0] == 'R' && h[1] == 'a' && h[2] == 'r' && h[3] == '!' && h[4] == 0x1A && h[5] == 0x07) return h[6] == 1 ? Kind.RAR5 : Kind.RAR4;
        if ((h[0] & 0xFF) == '7' && (h[1] & 0xFF) == 'z' && (h[2] & 0xFF) == 0xBC && (h[3] & 0xFF) == 0xAF && (h[4] & 0xFF) == 0x27 && (h[5] & 0xFF) == 0x1C) return Kind.SEVEN_ZIP;
        return Kind.UNKNOWN;
    }

    public static void read(File archive, Handler handler, AtomicBoolean cancel) throws IOException {
        switch (kind(archive)) {
            case ZIP:
                readZip(archive, handler, cancel);
                return;
            case RAR5:
                Rar5.read(archive, handler, cancel);
                return;
            case RAR4:
                RarLegacy.read(archive, handler, cancel);
                return;
            case SEVEN_ZIP:
                SevenZip.read(archive, handler, cancel);
                return;
            default:
                throw new Rejected("The package is not a zip, rar or 7z archive.");
        }
    }

    private static void readZip(File archive, Handler handler, AtomicBoolean cancel) throws IOException {
        try (ZipInputStream zip = new ZipInputStream(new FileInputStream(archive))) {
            ZipEntry e;
            while (true) {
                try {
                    e = zip.getNextEntry();
                } catch (java.util.zip.ZipException ex) {
                    throw new Rejected("The package is not a readable zip archive.");
                }
                if (e == null) break;
                if (cancel != null && cancel.get()) throw new IOException("cancelled");
                if (e.isDirectory()) continue;
                handler.entry(e.getName(), e.getSize(), new FilterInputStream(zip) {
                    @Override
                    public void close() {
                    }
                });
            }
        }
    }

    public static List<String> list(File archive, AtomicBoolean cancel) throws IOException {
        if (kind(archive) == Kind.ZIP) {
            List<String> out = new ArrayList<>();
            try (ZipFile z = new ZipFile(archive)) {
                java.util.Enumeration<? extends ZipEntry> en = z.entries();
                while (en.hasMoreElements()) {
                    ZipEntry e = en.nextElement();
                    if (!e.isDirectory()) out.add(e.getName());
                    if (out.size() > MAX_ENTRIES) throw new Rejected("The package contains more than " + MAX_ENTRIES + " files.");
                }
            } catch (java.util.zip.ZipException e) {
                throw new Rejected("The package is not a readable zip archive.");
            }
            return out;
        }
        List<String> out = new ArrayList<>();
        read(archive, (name, size, data) -> {
            out.add(name);
            if (out.size() > MAX_ENTRIES) throw new Rejected("The package contains more than " + MAX_ENTRIES + " files.");
        }, cancel);
        return out;
    }

    static String normalize(String name) {
        String n = name.replace('\\', '/');
        while (n.startsWith("./")) n = n.substring(2);
        return n;
    }

    public static String inspectPath(Context c, String relative) {
        String n = normalize(relative);
        if (n.trim().isEmpty()) return "The package contains an unnamed file.";
        if (n.contains("..")) return "The package tries to escape its folder with a relative path.";
        if (n.startsWith("/") || n.contains(":")) return "The package contains an absolute path.";
        for (String seg : n.split("/")) {
            if (seg.isEmpty()) continue;
            String trimmed = seg.replaceAll("[ .]+$", "");
            if (trimmed.isEmpty() || trimmed.length() != seg.length()) return "The package contains an invalid file name.";
            for (int i = 0; i < seg.length(); i++) {
                char ch = seg.charAt(i);
                if (ch < 32 || "<>\"|?*".indexOf(ch) >= 0) return "The package contains an invalid file name.";
            }
            String stem = seg.contains(".") ? seg.substring(0, seg.indexOf('.')) : seg;
            if (RESERVED.contains(stem.toLowerCase(Locale.ROOT))) return "The package uses the reserved name " + seg + ".";
        }
        int slash = n.indexOf('/');
        String root = slash <= 0 ? "" : n.substring(0, slash).toLowerCase(Locale.ROOT);
        if (BLOCKED_ROOTS.contains(root)) return "The package writes into the protected " + root + " folder.";
        String ext = Mods.extension(n);
        if (DANGEROUS.contains(ext) && !ContentPlacer.clientCopy(c, n)) return "The package contains a ." + ext + " file, which a Roblox mod never needs.";
        return null;
    }

    public static boolean installable(String relative) {
        return ASSET_EXTENSIONS.contains(Mods.extension(relative));
    }

    public static final class Inspection {
        public boolean robloxContent;
        public int files;
        public long bytes;
    }

    public static Inspection inspect(Context c, File archive, AtomicBoolean cancel) throws IOException {
        Inspection r = new Inspection();
        long packed = archive.length();
        read(archive, (name, size, data) -> {
            String n = normalize(name);
            String bad = inspectPath(c, n);
            if (bad != null) throw new Rejected(bad);
            if (++r.files > MAX_ENTRIES) throw new Rejected("The package contains more than " + MAX_ENTRIES + " files.");
            long counted = count(data, MAX_EXTRACTED - r.bytes + 1);
            r.bytes += counted;
            if (r.bytes > MAX_EXTRACTED) throw new Rejected("The package expands to more than " + MAX_EXTRACTED / 1048576 + " MB.");
            if (installable(n) && ContentPlacer.resolve(c, n) != null) r.robloxContent = true;
        }, cancel);
        if (r.files == 0) throw new Rejected("The package is empty.");
        if (packed > 0 && r.bytes > BOMB_FLOOR && r.bytes / packed > MAX_RATIO) throw new Rejected("The package expands far beyond its download size, which is how zip bombs behave.");
        return r;
    }

    private static long count(InputStream in, long limit) throws IOException {
        byte[] buf = new byte[65536];
        long total = 0;
        int n;
        while ((n = in.read(buf)) > 0) {
            total += n;
            if (total > limit) return total;
        }
        return total;
    }

    public static int extractVerified(Context c, File archive, File dest, AtomicBoolean cancel) throws IOException {
        Map<String, String> placed = new HashMap<>();
        long[] total = {0};
        int[] written = {0};
        String root = dest.getCanonicalPath();
        read(archive, (name, size, data) -> {
            if (cancel != null && cancel.get()) throw new IOException("cancelled");
            String n = normalize(name);
            String bad = inspectPath(c, n);
            if (bad != null) throw new Rejected(bad);
            if (!installable(n)) return;
            String target = ContentPlacer.resolve(c, n);
            if (target == null || ModEngine.ignored(target)) return;
            File out = new File(dest, target);
            if (!out.getCanonicalPath().startsWith(root + File.separator)) throw new Rejected("The package tried to write outside its own folder.");
            File parent = out.getParentFile();
            if (parent != null && !parent.isDirectory() && !parent.mkdirs()) throw new IOException("mkdir");
            try (OutputStream os = new FileOutputStream(out)) {
                byte[] buf = new byte[65536];
                int r;
                while ((r = data.read(buf)) > 0) {
                    total[0] += r;
                    if (total[0] > MAX_EXTRACTED) throw new Rejected("The package expands to more than " + MAX_EXTRACTED / 1048576 + " MB.");
                    os.write(buf, 0, r);
                }
            }
            if (placed.put(target.toLowerCase(Locale.ROOT), target) == null) written[0]++;
        }, cancel);
        if (written[0] == 0) throw new Rejected("Nothing in the package could be matched to a Roblox client folder, so nothing was installed.");
        return written[0];
    }

    public static int extractFlat(Context c, File archive, File dest, Set<String> extensions, AtomicBoolean cancel) throws IOException {
        int[] written = {0};
        long[] total = {0};
        String root = dest.getCanonicalPath();
        read(archive, (name, size, data) -> {
            String n = normalize(name);
            if (!extensions.contains(Mods.extension(n))) return;
            String bad = inspectPath(c, n);
            if (bad != null) throw new Rejected(bad);
            File out = new File(dest, n);
            if (!out.getCanonicalPath().startsWith(root + File.separator)) throw new Rejected("The package tried to write outside its own folder.");
            File parent = out.getParentFile();
            if (parent != null && !parent.isDirectory() && !parent.mkdirs()) throw new IOException("mkdir");
            try (OutputStream os = new FileOutputStream(out)) {
                byte[] buf = new byte[65536];
                int r;
                while ((r = data.read(buf)) > 0) {
                    total[0] += r;
                    if (total[0] > MAX_EXTRACTED) throw new Rejected("The package expands to more than " + MAX_EXTRACTED / 1048576 + " MB.");
                    os.write(buf, 0, r);
                }
            }
            written[0]++;
        }, cancel);
        if (written[0] == 0) throw new Rejected("The package did not contain the expected files.");
        return written[0];
    }

    public static ManagedMods.Record installManaged(Context c, File archive, String name, ManagedMods.Pack pack, AtomicBoolean cancel) throws IOException {
        Inspection i = inspect(c, archive, cancel);
        if (!i.robloxContent) throw new Rejected("Nothing in the package could be matched to a Roblox client folder, so it is not a Roblox mod.");
        File staging = new File(ManagedMods.root(c), "staging_" + System.nanoTime());
        try {
            if (!staging.mkdirs()) throw new IOException("mkdir");
            extractVerified(c, archive, staging, cancel);
            if (pack != null) ManagedMods.writePack(staging, pack);
            return ManagedMods.adopt(c, staging, name);
        } finally {
            Mods.delete(staging);
        }
    }
}
