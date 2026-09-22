package com.voidstrap.android;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Build;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ModEngine {
    public static final String REMOTE_DIR = "/data/local/tmp/voidstrap_mods";
    public static final String SKY_PREFIX = "assets/android/textures/sky/";

    public enum State { NO_ROOT, NOT_INSTALLED, OFF, EMPTY, PENDING, APPLIED }

    public enum Result { APPLIED, UNCHANGED, REMOVED, NO_ROOT, NOT_INSTALLED, NO_SPACE, CANCELLED, FAILED }

    private static final class Plan {
        final String target;
        final File base;
        final boolean pristine;
        final Map<String, ApkPatcher.Source> changes = new TreeMap<>();
        final String fingerprint;

        Plan(JSONObject o) {
            target = o.optString("target");
            String b = o.isNull("base") ? null : o.optString("base");
            base = b == null ? null : new File(b);
            pristine = o.optBoolean("pristine");
            fingerprint = o.optString("fingerprint");
            JSONArray c = o.optJSONArray("changes");
            if (c != null) for (int i = 0; i < c.length(); i++) {
                JSONArray p = c.optJSONArray(i);
                if (p == null || p.length() < 2) continue;
                changes.put(p.optString(0), new ApkPatcher.FileSource(new File(p.optString(1))));
            }
            JSONArray log = o.optJSONArray("log");
            if (log != null) for (int i = 0; i < log.length(); i++) ModLog.add(log.optString(i));
        }
    }

    private static final Object APPLY = new Object();

    private ModEngine() {
    }

    public static boolean enabled(Context c) {
        return !"0".equals(Store.get(c).setting("modsEnabled", "1"));
    }

    public static void setEnabled(Context c, boolean on) {
        Store.get(c).putSetting("modsEnabled", on ? null : "0");
    }

    public static boolean ignored(String relative) {
        return Core.flag("mods.ignored", Core.args("rel", relative));
    }

    public static String assetPath(String relative) {
        try {
            return Core.text("mods.assetPath", Core.args("rel", relative));
        } catch (IOException e) {
            return null;
        }
    }

    public static Map<String, File> collect(Context c) {
        Map<String, File> out = new LinkedHashMap<>();
        try {
            Object v = Core.value("mods.collect", Core.roots(c));
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

    public static File originalApk(Context c, String pkg) {
        File target = baseApk(c, pkg);
        if (target == null) return null;
        try {
            String o = Core.text("mods.originalApk", Core.roots(c, "pkg", pkg, "target", target));
            return o == null ? null : new File(o);
        } catch (IOException e) {
            return null;
        }
    }

    private static Plan plan(Context c, String pkg) throws IOException {
        File target = baseApk(c, pkg);
        JSONObject o = Core.run("mods.plan", Core.roots(c, "pkg", pkg, "identity", identity(c, pkg), "target", target));
        return o.optBoolean("none") ? null : new Plan(o);
    }

    public static boolean hasMods(Context c) {
        return !collect(c).isEmpty() || !ModAssetCache.wanted(c).isEmpty();
    }

    private static String fingerprint(Context c, String pkg) {
        try {
            Object s = Core.value("mods.stamp", Core.roots(c, "pkg", pkg));
            return s instanceof JSONObject ? ((JSONObject) s).optString("fingerprint") : null;
        } catch (IOException e) {
            return null;
        }
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
            String stamp = fingerprint(c, pkg);
            boolean same = p != null && stamp != null && p.fingerprint.equals(stamp);
            return same && mounted ? State.APPLIED : State.PENDING;
        } catch (IOException e) {
            return State.PENDING;
        }
    }

    public static boolean mountedFor(String pkg, String target) {
        return Core.flag("mods.mountedFor", Core.args("pkg", pkg, "target", target));
    }

    public interface Progress {
        void on(int stage);
    }

    public static Result apply(Context c, String pkg, boolean restart, AtomicBoolean cancel, Progress progress) {
        synchronized (APPLY) {
            if (!safePkg(pkg)) return Result.FAILED;
            FlagWriter.Mode mode = FlagWriter.rootMode(c);
            if (mode == FlagWriter.Mode.NONE) {
                ModLog.add("apply skipped for " + pkg + ", no root");
                return Result.NO_ROOT;
            }
            ModLog.add("apply started for " + pkg + " via " + mode + ", workspace files " + collect(c).size());
            Result r = applyFiles(c, pkg, restart, cancel, progress, mode);
            ModLog.add("apply files result " + r);
            if (r != Result.APPLIED && r != Result.UNCHANGED && r != Result.REMOVED) return r;
            boolean pending = ModAssetCache.pending(c, pkg);
            int code = ModAssetCache.sync(c, pkg, mode, false);
            if (code != 0 && code != 4) return code < 0 ? Result.NO_ROOT : Result.FAILED;
            return pending && r == Result.UNCHANGED ? Result.APPLIED : r;
        }
    }

    private static String script(String pkg, String target, String build, boolean restart) throws IOException {
        return Core.text("mods.script", Core.args("pkg", pkg, "target", target, "build", build, "restart", restart));
    }

    private static String removeScript(String pkg, String target, boolean restart) throws IOException {
        return Core.text("mods.removeScript", Core.args("pkg", pkg, "target", target, "restart", restart));
    }

    private static Result applyFiles(Context c, String pkg, boolean restart, AtomicBoolean cancel, Progress progress, FlagWriter.Mode mode) {
        try {
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
            String stamp = fingerprint(c, pkg);
            boolean same = stamp != null && plan.fingerprint.equals(stamp);
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
            Core.run("mods.saveStamp", Core.roots(c, "pkg", pkg, "fingerprint", plan.fingerprint, "target", plan.target,
                    "original", originalSize, "files", plan.changes.size()));
            return Result.APPLIED;
        } catch (IOException e) {
            return Result.FAILED;
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

    private static boolean stamped(Context c, String pkg) {
        return Core.flag("mods.stampExists", Core.roots(c, "pkg", pkg));
    }

    private static Result remove(Context c, String pkg, boolean restart, FlagWriter.Mode mode) {
        if (!safePkg(pkg)) return Result.FAILED;
        File base = baseApk(c, pkg);
        String target = base == null ? "" : base.getAbsolutePath();
        if (!stamped(c, pkg) && (base == null || !mountedFor(pkg, target))) return Result.UNCHANGED;
        try {
            int code = FlagWriter.raw(mode, removeScript(pkg, target, restart), 60);
            if (code != 0) return code < 0 ? Result.NO_ROOT : Result.FAILED;
            Core.run("mods.deleteStamp", Core.roots(c, "pkg", pkg));
        } catch (IOException e) {
            return Result.FAILED;
        }
        return Result.REMOVED;
    }

    public static boolean syncForLaunch(Context c, String pkg) {
        boolean want = enabled(c) && hasMods(c);
        FlagWriter.Mode mode = FlagWriter.rootMode(c);
        if (mode == FlagWriter.Mode.NONE) return !want;
        Result r = want ? apply(c, pkg, true, null, null) : remove(c, pkg, true);
        return ok(r);
    }

    public static boolean ok(Result r) {
        return r == Result.APPLIED || r == Result.UNCHANGED || r == Result.REMOVED;
    }

    public static void onBoot(Context c) {
        if (FlagWriter.rootMode(c) == FlagWriter.Mode.NONE || !enabled(c)) return;
        for (String pkg : Targets.ALL) {
            if (stamped(c, pkg) || ModAssetCache.pending(c, pkg)) apply(c, pkg, false, null, null);
        }
    }

    static String quote(String s) {
        return "'" + s.replace("'", "'\\''") + "'";
    }

    private static boolean safePkg(String pkg) {
        return pkg != null && pkg.matches("[A-Za-z0-9_.]+");
    }

    private static void convertSky(Plan plan) throws IOException {
        int faces = 0;
        int kept = 0;
        int converted = 0;
        int rejected = 0;
        for (Map.Entry<String, ApkPatcher.Source> e : new ArrayList<>(plan.changes.entrySet())) {
            String name = e.getKey();
            if (!name.startsWith(SKY_PREFIX) || !name.toLowerCase(Locale.ROOT).endsWith(".tex")) continue;
            faces++;
            if (!(e.getValue() instanceof ApkPatcher.FileSource)) continue;
            File f = ((ApkPatcher.FileSource) e.getValue()).file;
            String kind = Core.text("mods.skyKind", Core.args("path", f));
            if ("reject".equals(kind)) {
                rejected++;
                ModLog.add("skybox face " + f.getName() + " rejected, size " + f.length());
                continue;
            }
            if ("skip".equals(kind)) continue;
            if ("keep".equals(kind)) {
                kept++;
                continue;
            }
            byte[] encoded = toPng(f);
            if (encoded != null) {
                plan.changes.put(name, new ApkPatcher.BytesSource(encoded));
                converted++;
            } else {
                rejected++;
                ModLog.add("skybox face " + f.getName() + " could not be decoded as an image");
            }
        }
        if (faces > 0) ModLog.add("skybox faces in plan " + faces + ", kept " + kept + ", converted " + converted + ", rejected " + rejected);
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

    public static long appliedAt(Context c, String pkg) {
        try {
            Object s = Core.value("mods.stamp", Core.roots(c, "pkg", pkg));
            return s instanceof JSONObject ? ((JSONObject) s).optLong("time") : 0;
        } catch (IOException e) {
            return 0;
        }
    }

    static byte[] readAll(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] b = new byte[65536];
        int n;
        while ((n = in.read(b)) > 0) out.write(b, 0, n);
        return out.toByteArray();
    }
}
