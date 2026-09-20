package com.voidstrap.android;

import android.content.Context;

import java.io.File;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.TimeUnit;

public final class FlagWriter {
    public static final int OK = 0;
    public static final int DENIED = 1;
    public static final int FAILED = 2;

    public static final String PATH = "/data/local/tmp/ClientAppSettings.json";
    private static final String REFRESH_BACKUP = "/data/local/tmp/voidstrap_refresh_backup";

    public enum Mode { HELPER, ROOT, NONE }

    public static final Map<String, String> ALLOWLIST = new LinkedHashMap<>();
    public static final Set<String> ALLOWED = ALLOWLIST.keySet();

    static {
        ALLOWLIST.put("DFIntCSGLevelOfDetailSwitchingDistance", "Geometry");
        ALLOWLIST.put("DFIntCSGLevelOfDetailSwitchingDistanceL12", "Geometry");
        ALLOWLIST.put("DFIntCSGLevelOfDetailSwitchingDistanceL23", "Geometry");
        ALLOWLIST.put("DFIntCSGLevelOfDetailSwitchingDistanceL34", "Geometry");
        ALLOWLIST.put("FFlagHandleAltEnterFullscreenManually", "Rendering");
        ALLOWLIST.put("DFFlagTextureQualityOverrideEnabled", "Rendering");
        ALLOWLIST.put("DFIntTextureQualityOverride", "Rendering");
        ALLOWLIST.put("FIntDebugForceMSAASamples", "Rendering");
        ALLOWLIST.put("DFFlagDisableDPIScale", "Rendering");
        ALLOWLIST.put("FFlagDebugGraphicsPreferD3D11", "Rendering");
        ALLOWLIST.put("FFlagDebugSkyGray", "Rendering");
        ALLOWLIST.put("DFFlagDebugPauseVoxelizer", "Rendering");
        ALLOWLIST.put("DFIntDebugFRMQualityLevelOverride", "Rendering");
        ALLOWLIST.put("FIntFRMMaxGrassDistance", "Rendering");
        ALLOWLIST.put("FIntFRMMinGrassDistance", "Rendering");
        ALLOWLIST.put("FFlagDebugGraphicsPreferVulkan", "Rendering");
        ALLOWLIST.put("FFlagDebugGraphicsPreferOpenGL", "Rendering");
        ALLOWLIST.put("FIntGrassMovementReducedMotionFactor", "User Interface");
    }

    private static final long SU_CACHE_MS = 10000;

    private static Boolean su;
    private static long suCheckedAt;

    private FlagWriter() {
    }

    private static boolean hasSu() {
        long now = android.os.SystemClock.elapsedRealtime();
        if (su != null && now - suCheckedAt < SU_CACHE_MS) return su;
        boolean found = false;
        String path = System.getenv("PATH");
        String dirs = (path == null ? "" : path) + ":/system/bin:/system/xbin";
        for (String d : dirs.split(":")) if (!d.isEmpty() && new File(d, "su").exists()) found = true;
        su = found;
        suCheckedAt = now;
        return found;
    }

    private static void forgetSu() {
        su = null;
        suCheckedAt = 0;
    }

    public static boolean rootAvailable() {
        return hasSu();
    }

    public static boolean rootEnabled(Context c) {
        return "1".equals(Store.get(c).setting("useRoot", "0"));
    }

    public static void setRootEnabled(Context c, boolean on) {
        Store.get(c).putSetting("useRoot", on ? "1" : null);
        forgetSu();
    }

    public static int testRoot() {
        return run(Mode.ROOT, "id > /dev/null || exit 1\nexit 0\n", 60);
    }

    public static Mode mode(Context c) {
        if (Helper.running()) return Mode.HELPER;
        if (rootEnabled(c)) return Mode.ROOT;
        return Mode.NONE;
    }

    public static boolean readable(String json) {
        java.io.File f = new java.io.File(PATH);
        if ("{}".equals(json)) return !f.exists();
        try {
            byte[] b = java.nio.file.Files.readAllBytes(f.toPath());
            String on = new String(b, StandardCharsets.UTF_8);
            return on.equals(json) || on.equals(json + "\n");
        } catch (IOException | SecurityException e) {
            return false;
        }
    }

    public static boolean tooLarge(String json) {
        return json.length() > Flags.MAX_BYTES;
    }

    private static final String FLAGS_DELIMITER = "VOIDSTRAP_FLAGS_END";

    static boolean safeForHeredoc(String json) {
        for (String line : json.split("\n", -1)) if (line.trim().equals(FLAGS_DELIMITER)) return false;
        return true;
    }

    private static String writeScript(String json) {
        String tmp = PATH + ".new";
        return "rm -f " + tmp + "\n"
                + "cat > " + tmp + " <<'VOIDSTRAP_FLAGS_END' || exit 2\n" + json + "\nVOIDSTRAP_FLAGS_END\n"
                + "if cmp -s " + tmp + " " + PATH + "; then rm -f " + tmp + "; else mv -f " + tmp + " " + PATH + " || exit 2; fi\n"
                + "chmod 0644 " + PATH + " || exit 2\n";
    }

    public static int shell(Context c, String script, int timeoutSeconds) {
        return run(mode(c), script, timeoutSeconds);
    }

    public static int write(Mode mode, String json) {
        if (!safeForHeredoc(json)) return FAILED;
        return run(mode, writeScript(json) + "exit 0\n", 60);
    }

    public static int remove(Mode mode) {
        return run(mode, "rm -f " + PATH + " || exit 2\nexit 0\n", 60);
    }

    static final String RESTORE_REFRESH = "if [ -e " + REFRESH_BACKUP + " ]; then\n"
            + "while read ns k v; do\n"
            + "if [ -z \"$v\" ]; then v=$k; k=$ns; ns=old; fi\n"
            + "case $ns in\n"
            + "system|secure|global) if [ \"$v\" = null ]; then settings delete $ns $k >/dev/null 2>&1; else settings put $ns $k \"$v\"; fi;;\n"
            + "old) if [ \"$k\" = null ]; then settings delete system peak_refresh_rate >/dev/null 2>&1; else settings put system peak_refresh_rate \"$k\"; fi\n"
            + "if [ \"$v\" = null ]; then settings delete system min_refresh_rate >/dev/null 2>&1; else settings put system min_refresh_rate \"$v\"; fi;;\n"
            + "esac\n"
            + "done < " + REFRESH_BACKUP + "\n"
            + "setprop debug.graphics.game_default_frame_rate.disabled false\n"
            + "rm -f " + REFRESH_BACKUP + "\n"
            + "fi\n";

    public static float maxRefresh(Context c) {
        float max = 0;
        android.hardware.display.DisplayManager dm = c.getSystemService(android.hardware.display.DisplayManager.class);
        android.view.Display d = dm == null ? null : dm.getDisplay(android.view.Display.DEFAULT_DISPLAY);
        if (d != null) for (android.view.Display.Mode m : d.getSupportedModes()) max = Math.max(max, m.getRefreshRate());
        return max;
    }

    public static int syncForLaunch(Context c, String pkg, String json) {
        if (tooLarge(json)) return R.string.flags_too_large;
        if (!safeForHeredoc(json)) return R.string.flags_launch_not_applied;
        Mode mode = mode(c);
        if (mode != Mode.HELPER && mode != Mode.ROOT) return readable(json) ? 0 : R.string.flags_launch_not_applied;
        if (!pkg.matches("[A-Za-z0-9_.]+")) return 0;
        String running = RESTORE_REFRESH + "p=$(pidof " + pkg + ")\np=${p%% *}\n";
        String script;
        if ("{}".equals(json)) {
            script = "c=0\nif [ -e " + PATH + " ]; then rm -f " + PATH + "; c=1; fi\n"
                    + running
                    + "if [ -n \"$p\" ] && [ $c = 1 ]; then am force-stop " + pkg + "; fi\nexit 0\n";
        } else {
            script = writeScript(json)
                    + running
                    + "if [ -n \"$p\" ] && [ " + PATH + " -nt /proc/$p ]; then am force-stop " + pkg + "; fi\nexit 0\n";
        }
        return run(mode, script, 15) == OK ? 0 : R.string.flags_launch_not_applied;
    }

    private static int run(Mode mode, String script, int timeoutSeconds) {
        int code = raw(mode, script, timeoutSeconds);
        if (code == OK || code == FAILED) return code;
        return DENIED;
    }

    public static Mode rootMode(Context c) {
        if (Helper.uid() == 0) return Mode.HELPER;
        if (rootEnabled(c)) return Mode.ROOT;
        return Mode.NONE;
    }

    public static int raw(Mode mode, String script, int timeoutSeconds) {
        if (mode == Mode.HELPER) {
            Integer r = Helper.run(script, timeoutSeconds);
            return r == null ? -1 : r;
        }
        if (mode != Mode.ROOT) return -1;
        Process p;
        try {
            p = new ProcessBuilder("su").redirectErrorStream(true).start();
        } catch (IOException | RuntimeException e) {
            forgetSu();
            return -1;
        }
        Thread drain = new Thread(() -> {
            try (java.io.InputStream out = p.getInputStream()) {
                byte[] b = new byte[4096];
                while (out.read(b) >= 0) {
                }
            } catch (IOException ignored) {
            }
        });
        drain.setDaemon(true);
        drain.start();
        try (OutputStream in = p.getOutputStream()) {
            in.write(script.getBytes(StandardCharsets.UTF_8));
        } catch (IOException | RuntimeException e) {
            p.destroy();
            return -1;
        }
        try {
            if (!p.waitFor(timeoutSeconds, TimeUnit.SECONDS)) {
                p.destroy();
                return -1;
            }
            return p.exitValue();
        } catch (InterruptedException e) {
            p.destroy();
            Thread.currentThread().interrupt();
            return FAILED;
        } catch (RuntimeException e) {
            p.destroy();
            return FAILED;
        }
    }
}
