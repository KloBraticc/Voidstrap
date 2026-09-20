package com.voidstrap.android;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Build;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ModEngine {
    public static final int FORMAT = 1;
    public static final String REMOTE_DIR = "/data/local/tmp/voidstrap_mods";
    public static final String SKY_PREFIX = "assets/android/textures/sky/";

    public enum State { NO_ROOT, NOT_INSTALLED, OFF, EMPTY, PENDING, APPLIED }

    public enum Result { APPLIED, UNCHANGED, REMOVED, NO_ROOT, NOT_INSTALLED, NO_SPACE, CANCELLED, FAILED }

    public static final class Plan {
        public final String pkg;
        public final File base;
        public final String target;
        public final boolean pristine;
        public final Map<String, ApkPatcher.Source> changes = new TreeMap<>();
        public final List<String> skipped = new ArrayList<>();
        public int added;
        public int replaced;
        public String fingerprint;

        Plan(String pkg, File target, File original) {
            this.pkg = pkg;
            this.base = original;
            this.target = target.getAbsolutePath();
            this.pristine = original != null;
        }
    }

    private static final Object APPLY = new Object();
    private static final String[] VERSION_LOCKED = {"content/configs/", "extracontent/models/", "extracontent/translations/"};
    private static final String[] PACKAGE_ART = {".png", ".jpg", ".jpeg", ".dds", ".ktx", ".webp", ".ttf", ".otf"};

    private ModEngine() {
    }

    public static boolean enabled(Context c) {
        return !"0".equals(Store.get(c).setting("modsEnabled", "1"));
    }

    public static void setEnabled(Context c, boolean on) {
        Store.get(c).putSetting("modsEnabled", on ? null : "0");
    }

    public static boolean ignored(String relative) {
        String n = relative.replace('\\', '/');
        String lower = n.toLowerCase(Locale.ROOT);
        if (lower.endsWith(".lua") || lower.endsWith(".luau") || lower.endsWith(".lock")) return true;
        if (lower.startsWith("extracontent/luapackages/")) {
            for (String e : PACKAGE_ART) if (lower.endsWith(e)) return duplicate(n);
            return true;
        }
        for (String f : VERSION_LOCKED) if (lower.startsWith(f)) return true;
        return duplicate(n);
    }

    static boolean duplicate(String relative) {
        String name = relative.substring(relative.lastIndexOf('/') + 1);
        int dot = name.lastIndexOf('.');
        if (dot > 0) name = name.substring(0, dot);
        int open = name.lastIndexOf(" (");
        if (open <= 0 || !name.endsWith(")") || name.length() - open <= 3) return false;
        for (int i = open + 2; i < name.length() - 1; i++) if (!Character.isDigit(name.charAt(i))) return false;
        return true;
    }

    public static String assetPath(String relative) {
        String n = relative.replace('\\', '/');
        while (n.startsWith("/")) n = n.substring(1);
        int slash = n.indexOf('/');
        if (slash <= 0) return null;
        String head = n.substring(0, slash);
        String rest = n.substring(slash + 1);
        if (rest.isEmpty()) return null;
        if (head.equalsIgnoreCase("content")) return "assets/content/" + rest;
        if (head.equalsIgnoreCase("ExtraContent")) return "assets/ExtraContent/" + rest;
        if (head.equalsIgnoreCase("PlatformContent")) {
            int s2 = rest.indexOf('/');
            if (s2 <= 0) return null;
            String platform = rest.substring(0, s2);
            if (!platform.equalsIgnoreCase("pc") && !platform.equalsIgnoreCase("android")) return null;
            String tail = rest.substring(s2 + 1);
            return tail.isEmpty() ? null : "assets/android/" + tail;
        }
        return null;
    }

    public static Map<String, File> collect(Context c) {
        Map<String, File> out = new LinkedHashMap<>();
        Map<String, String> keys = new HashMap<>();
        File root = Mods.root(c);
        for (String rel : ManagedMods.files(root)) {
            if (ignored(rel) || assetPath(rel) == null) continue;
            put(out, keys, rel, new File(root, rel));
        }
        for (ManagedMods.ModFile f : ManagedMods.enabledFiles(c)) {
            if (assetPath(f.relative) == null) continue;
            put(out, keys, f.relative, f.source);
        }
        return out;
    }

    private static void put(Map<String, File> out, Map<String, String> keys, String rel, File f) {
        String key = rel.toLowerCase(Locale.ROOT);
        String old = keys.put(key, rel);
        if (old != null) out.remove(old);
        out.put(rel, f);
    }

    public static File baseApk(Context c, String pkg) {
        try {
            PackageManager pm = c.getPackageManager();
            ApplicationInfo ai = Build.VERSION.SDK_INT >= 33
                    ? pm.getApplicationInfo(pkg, PackageManager.ApplicationInfoFlags.of(0))
                    : pm.getApplicationInfo(pkg, 0);
            return ai.sourceDir == null ? null : new File(ai.sourceDir);
        } catch (PackageManager.NameNotFoundException e) {
            return null;
        }
    }

    static String identity(Context c, String pkg) {
        try {
            PackageManager pm = c.getPackageManager();
            android.content.pm.PackageInfo info = Build.VERSION.SDK_INT >= 33
                    ? pm.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
                    : pm.getPackageInfo(pkg, 0);
            String dir = info.applicationInfo == null ? "" : info.applicationInfo.sourceDir;
            return dir + "|" + androidx.core.content.pm.PackageInfoCompat.getLongVersionCode(info) + "|" + info.lastUpdateTime;
        } catch (PackageManager.NameNotFoundException e) {
            return "";
        }
    }

    public static File originalFile(String pkg) {
        return new File(REMOTE_DIR, pkg + ".orig.apk");
    }

    public static File originalApk(Context c, String pkg) {
        File target = baseApk(c, pkg);
        if (target == null) return null;
        if (!mountedFor(pkg, target.getAbsolutePath())) return target;
        File o = originalFile(pkg);
        JSONObject stamp = stamp(c, pkg);
        long size = stamp == null ? -1 : stamp.optLong("original", -1);
        return exposes(o.getAbsolutePath()) && o.canRead() && o.length() == size ? o : null;
    }

    private static boolean exposes(String path) {
        for (String[] f : mounts()) if (f.length > 4 && f[4].equals(path)) return true;
        return false;
    }

    private static java.util.List<String[]> mounts() {
        java.util.List<String[]> out = new ArrayList<>();
        try (InputStream in = new FileInputStream("/proc/self/mountinfo")) {
            for (String line : new String(readAll(in), StandardCharsets.UTF_8).split("\n")) out.add(line.split(" "));
        } catch (IOException | RuntimeException ignored) {
        }
        return out;
    }

    public static Plan plan(Context c, String pkg) throws IOException {
        File target = baseApk(c, pkg);
        if (target == null) return null;
        File original = originalApk(c, pkg);
        Plan plan = new Plan(pkg, target, original);
        ApkPatcher.Directory dir = ApkPatcher.read(original != null ? original : target);
        Map<String, String> lower = new HashMap<>();
        for (String name : dir.entries.keySet()) lower.put(name.toLowerCase(Locale.ROOT), name);
        MessageDigest md = sha();
        update(md, "v" + FORMAT + "\n" + pkg + "\n" + identity(c, pkg) + "\n");
        Map<String, File> files = collect(c);
        Map<String, File> resolved = new TreeMap<>();
        for (Map.Entry<String, File> e : files.entrySet()) {
            String asset = assetPath(e.getKey());
            if (asset == null) {
                plan.skipped.add(e.getKey());
                continue;
            }
            String existing = lower.get(asset.toLowerCase(Locale.ROOT));
            String name = existing != null ? existing : asset;
            resolved.put(name, e.getValue());
            if (existing != null) plan.replaced++;
            else plan.added++;
        }
        for (Map.Entry<String, File> e : resolved.entrySet()) {
            File f = e.getValue();
            update(md, e.getKey() + "\t" + f.getAbsolutePath() + "\t" + f.length() + "\t" + f.lastModified() + "\n");
            plan.changes.put(e.getKey(), new ApkPatcher.FileSource(f));
        }
        plan.fingerprint = hex(md.digest());
        return plan;
    }

    public static boolean hasMods(Context c) {
        return !collect(c).isEmpty() || !ModAssetCache.wanted(c).isEmpty();
    }

    public static State state(Context c, String pkg) {
        File base = baseApk(c, pkg);
        if (base == null) return State.NOT_INSTALLED;
        boolean mounted = mountedFor(pkg, base.getAbsolutePath());
        boolean cache = ModAssetCache.pending(c, pkg);
        if (!enabled(c)) return mounted || cache ? State.PENDING : State.OFF;
        if (FlagWriter.rootMode(c) == FlagWriter.Mode.NONE) return State.NO_ROOT;
        boolean files = !collect(c).isEmpty();
        if (!files) {
            if (mounted || cache) return State.PENDING;
            return ModAssetCache.wanted(c).isEmpty() ? State.EMPTY : State.APPLIED;
        }
        if (cache) return State.PENDING;
        try {
            Plan p = plan(c, pkg);
            JSONObject stamp = stamp(c, pkg);
            boolean same = p != null && stamp != null && p.fingerprint.equals(stamp.optString("fingerprint"));
            return same && mounted ? State.APPLIED : State.PENDING;
        } catch (IOException e) {
            return State.PENDING;
        }
    }

    public static boolean mountedFor(String pkg, String target) {
        for (String[] f : mounts()) if (f.length > 4 && f[4].equals(target) && f[3].endsWith("/voidstrap_mods/" + pkg + ".apk")) return true;
        return false;
    }

    public interface Progress {
        void on(int stage);
    }

    public static Result apply(Context c, String pkg, boolean restart, AtomicBoolean cancel, Progress progress) {
        synchronized (APPLY) {
            if (!safePkg(pkg)) return Result.FAILED;
            FlagWriter.Mode mode = FlagWriter.rootMode(c);
            if (mode == FlagWriter.Mode.NONE) return Result.NO_ROOT;
            Result r = applyFiles(c, pkg, restart, cancel, progress, mode);
            if (r != Result.APPLIED && r != Result.UNCHANGED && r != Result.REMOVED) return r;
            boolean pending = ModAssetCache.pending(c, pkg);
            int code = ModAssetCache.sync(c, pkg, mode, false);
            if (code != 0 && code != 4) return code < 0 ? Result.NO_ROOT : Result.FAILED;
            return pending && r == Result.UNCHANGED ? Result.APPLIED : r;
        }
    }

    private static Result applyFiles(Context c, String pkg, boolean restart, AtomicBoolean cancel, Progress progress, FlagWriter.Mode mode) {
        {
            if (!enabled(c) || collect(c).isEmpty()) return remove(c, pkg, restart, mode);
            Plan plan;
            try {
                if (progress != null) progress.on(0);
                ModPresets.prepareForApply(c, pkg);
                plan = plan(c, pkg);
            } catch (IOException | RuntimeException e) {
                return Result.FAILED;
            }
            if (plan == null) return Result.NOT_INSTALLED;
            JSONObject stamp = stamp(c, pkg);
            boolean same = stamp != null && plan.fingerprint.equals(stamp.optString("fingerprint"));
            if (same) {
                if (mountedFor(pkg, plan.target)) return Result.UNCHANGED;
                if (progress != null) progress.on(2);
                int code = FlagWriter.raw(mode, script(pkg, plan.target, null, restart), 60);
                if (code == 0) return Result.APPLIED;
                if (code != 3) return code < 0 ? Result.NO_ROOT : Result.FAILED;
            }
            if (!plan.pristine) {
                int code = FlagWriter.raw(mode, removeScript(pkg, plan.target, false), 60);
                if (code != 0) return code < 0 ? Result.NO_ROOT : Result.FAILED;
                try {
                    plan = plan(c, pkg);
                } catch (IOException | RuntimeException e) {
                    return Result.FAILED;
                }
                if (plan == null) return Result.NOT_INSTALLED;
                if (!plan.pristine) return Result.FAILED;
            }
            long originalSize;
            File build = new File(stateDir(c), "build_" + pkg + ".apk");
            build.delete();
            if (Mods.lowSpace(c.getSystemService(android.os.storage.StorageManager.class), stateDir(c), plan.base.length())) return Result.NO_SPACE;
            try {
                if (progress != null) progress.on(1);
                convertSky(plan);
                originalSize = plan.base.length();
                ApkPatcher.patch(plan.base, build, plan.changes, cancel);
            } catch (IOException | RuntimeException e) {
                build.delete();
                return cancel != null && cancel.get() ? Result.CANCELLED : Result.FAILED;
            }
            if (cancel != null && cancel.get()) {
                build.delete();
                return Result.CANCELLED;
            }
            if (progress != null) progress.on(2);
            int code = FlagWriter.raw(mode, script(pkg, plan.target, build.getAbsolutePath(), restart), 120);
            build.delete();
            if (code != 0) return code < 0 ? Result.NO_ROOT : Result.FAILED;
            saveStamp(c, pkg, plan, originalSize);
            return Result.APPLIED;
        }
    }

    public static Result remove(Context c, String pkg, boolean restart) {
        synchronized (APPLY) {
            FlagWriter.Mode mode = FlagWriter.rootMode(c);
            if (mode == FlagWriter.Mode.NONE) return Result.NO_ROOT;
            Result r = remove(c, pkg, restart, mode);
            if (r != Result.REMOVED && r != Result.UNCHANGED) return r;
            int code = ModAssetCache.sync(c, pkg, mode, true);
            if (code != 0 && code != 4) return code < 0 ? Result.NO_ROOT : Result.FAILED;
            return Result.REMOVED;
        }
    }

    private static Result remove(Context c, String pkg, boolean restart, FlagWriter.Mode mode) {
        if (!safePkg(pkg)) return Result.FAILED;
        File base = baseApk(c, pkg);
        String target = base == null ? "" : base.getAbsolutePath();
        File stampFile = stampFile(c, pkg);
        if (!stampFile.isFile() && (base == null || !mountedFor(pkg, target))) return Result.UNCHANGED;
        int code = FlagWriter.raw(mode, removeScript(pkg, target, restart), 60);
        if (code != 0) return code < 0 ? Result.NO_ROOT : Result.FAILED;
        stampFile.delete();
        return Result.REMOVED;
    }

    public static boolean syncForLaunch(Context c, String pkg) {
        boolean want = enabled(c) && hasMods(c);
        FlagWriter.Mode mode = FlagWriter.rootMode(c);
        if (mode == FlagWriter.Mode.NONE) return !want;
        Result r = want ? apply(c, pkg, true, null, null) : remove(c, pkg, true);
        return r == Result.APPLIED || r == Result.UNCHANGED || r == Result.REMOVED;
    }

    public static void onBoot(Context c) {
        if (FlagWriter.rootMode(c) == FlagWriter.Mode.NONE || !enabled(c)) return;
        for (String pkg : Targets.ALL) {
            if (stampFile(c, pkg).isFile() || ModAssetCache.pending(c, pkg)) apply(c, pkg, false, null, null);
        }
    }

    static String quote(String s) {
        return "'" + s.replace("'", "'\\''") + "'";
    }

    private static boolean safePkg(String pkg) {
        return pkg != null && pkg.matches("[A-Za-z0-9_.]+");
    }

    private static String common(String pkg, String target) {
        return "P=" + quote(pkg) + "\n"
                + "T=" + quote(target) + "\n"
                + "R=" + REMOTE_DIR + "\n"
                + "D=$R/$P.apk\n"
                + "O=$R/$P.orig.apk\n"
                + "NS=''\n"
                + "if nsenter -t 1 -m -- true 2>/dev/null; then NS='nsenter -t 1 -m --'; fi\n"
                + "ours() { grep \" $T \" /proc/1/mountinfo | grep -q \"/voidstrap_mods/$P.apk \"; }\n"
                + "any() { grep -q \" $T \" /proc/1/mountinfo; }\n"
                + "unmount_all() {\n"
                + "  M=$(cat /proc/1/mountinfo)\n"
                + "  echo \"$M\" | while read -r a b c r m rest; do\n"
                + "    case \"$r\" in */voidstrap_mods/\"$P\".apk) $NS umount -l \"$m\" 2>/dev/null;; esac\n"
                + "    if [ \"$m\" = \"$T\" ] || [ \"$m\" = \"$O\" ]; then $NS umount -l \"$m\" 2>/dev/null; fi\n"
                + "  done\n"
                + "}\n"
                + "stop() { if [ \"$K\" = 1 ] && [ -n \"$(pidof $P)\" ]; then am force-stop $P; fi; }\n";
    }

    static String script(String pkg, String target, String build, boolean restart) {
        StringBuilder sb = new StringBuilder(common(pkg, target));
        sb.append("K=").append(restart ? 1 : 0).append('\n');
        sb.append("[ -e \"$T\" ] || exit 4\n");
        if (build != null) {
            sb.append("B=").append(quote(build)).append('\n');
            sb.append("[ -f \"$B\" ] || exit 2\n");
            sb.append("mkdir -p $R && chmod 755 $R || exit 2\n");
            sb.append("unmount_all\n");
            sb.append("rm -f \"$D\"\n");
            sb.append("mv -f \"$B\" \"$D\" 2>/dev/null || { cp -f \"$B\" \"$D\" && rm -f \"$B\"; } || exit 2\n");
        } else {
            sb.append("[ -f \"$D\" ] || exit 3\n");
            sb.append("if ours; then exit 0; fi\n");
            sb.append("unmount_all\n");
        }
        sb.append("[ -f \"$O\" ] || touch \"$O\" || exit 2\n");
        sb.append("chmod 644 \"$O\"\n");
        sb.append("$NS mount -o bind \"$T\" \"$O\" || exit 2\n");
        sb.append("$NS mount -o private none \"$O\" 2>/dev/null\n");
        sb.append("chown 1000:1000 \"$D\"; chmod 644 \"$D\" || exit 2\n");
        sb.append("chcon u:object_r:apk_data_file:s0 \"$D\" || exit 2\n");
        sb.append("$NS mount -o bind \"$D\" \"$T\" || exit 2\n");
        sb.append("ours || exit 5\n");
        sb.append("stop\n");
        sb.append("exit 0\n");
        return sb.toString();
    }

    static String removeScript(String pkg, String target, boolean restart) {
        return common(pkg, target)
                + "K=" + (restart ? 1 : 0) + "\n"
                + "W=0\n"
                + "if [ -n \"$T\" ] && any; then W=1; fi\n"
                + "unmount_all\n"
                + "rm -f \"$D\" \"$O\"\n"
                + "if [ $W = 1 ]; then stop; fi\n"
                + "exit 0\n";
    }

    private static void convertSky(Plan plan) throws IOException {
        for (Map.Entry<String, ApkPatcher.Source> e : new ArrayList<>(plan.changes.entrySet())) {
            String name = e.getKey();
            if (!name.startsWith(SKY_PREFIX) || !name.toLowerCase(Locale.ROOT).endsWith(".tex")) continue;
            if (!(e.getValue() instanceof ApkPatcher.FileSource)) continue;
            File f = ((ApkPatcher.FileSource) e.getValue()).file;
            if (f.length() < 8 || f.length() > 64L * 1024 * 1024) continue;
            byte[] head = new byte[4];
            try (InputStream in = new FileInputStream(f)) {
                if (in.read(head) != 4) continue;
            }
            boolean dds = head[0] == 'D' && head[1] == 'D' && head[2] == 'S' && head[3] == ' ';
            boolean png = (head[0] & 0xFF) == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G';
            boolean ktx = (head[0] & 0xFF) == 0xAB && head[1] == 'K' && head[2] == 'T' && head[3] == 'X';
            if (dds || png || ktx) continue;
            byte[] converted = toPng(f);
            if (converted != null) plan.changes.put(name, new ApkPatcher.BytesSource(converted));
        }
    }

    static byte[] toPng(File f) {
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        BitmapFactory.decodeFile(f.getAbsolutePath(), bounds);
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null;
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inSampleSize = 1;
        while ((long) (bounds.outWidth / o.inSampleSize) * (bounds.outHeight / o.inSampleSize) > 4096L * 4096) o.inSampleSize *= 2;
        Bitmap b = BitmapFactory.decodeFile(f.getAbsolutePath(), o);
        if (b == null) return null;
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            b.compress(Bitmap.CompressFormat.PNG, 100, out);
            return out.toByteArray();
        } finally {
            b.recycle();
        }
    }

    static File stateDir(Context c) {
        File d = new File(c.getFilesDir(), "modstate");
        if (!d.isDirectory()) d.mkdirs();
        return d;
    }

    private static File stampFile(Context c, String pkg) {
        return new File(stateDir(c), "applied_" + pkg + ".json");
    }

    private static JSONObject stamp(Context c, String pkg) {
        File f = stampFile(c, pkg);
        if (!f.isFile()) return null;
        try (InputStream in = new FileInputStream(f)) {
            return new JSONObject(new String(readAll(in), StandardCharsets.UTF_8));
        } catch (IOException | JSONException e) {
            return null;
        }
    }

    private static void saveStamp(Context c, String pkg, Plan plan, long originalSize) {
        try (FileOutputStream out = new FileOutputStream(stampFile(c, pkg))) {
            out.write(new JSONObject().put("fingerprint", plan.fingerprint).put("target", plan.target).put("original", originalSize)
                    .put("files", plan.changes.size()).put("time", System.currentTimeMillis()).toString().getBytes(StandardCharsets.UTF_8));
        } catch (IOException | JSONException ignored) {
        }
    }

    public static long appliedAt(Context c, String pkg) {
        JSONObject s = stamp(c, pkg);
        return s == null ? 0 : s.optLong("time");
    }

    static byte[] readAll(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] b = new byte[65536];
        int n;
        while ((n = in.read(b)) > 0) out.write(b, 0, n);
        return out.toByteArray();
    }

    private static MessageDigest sha() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    private static void update(MessageDigest md, String s) {
        md.update(s.getBytes(StandardCharsets.UTF_8));
    }

    private static String hex(byte[] b) {
        StringBuilder sb = new StringBuilder();
        for (byte x : b) sb.append(String.format(Locale.ROOT, "%02x", x));
        return sb.toString();
    }
}
