package com.voidstrap.android;

import android.content.Context;

import java.io.IOException;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

public final class FlagWriter {
    public static final int OK = 0;
    public static final int DENIED = 1;
    public static final int FAILED = 2;

    public static final String PATH = "/data/local/tmp/ClientAppSettings.json";
    private static final String REFRESH_BACKUP = "/data/local/tmp/voidstrap_refresh_backup";
    static final String GAME_FPS_CAP = "debug.graphics.game_default_frame_rate.disabled";

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
    private static volatile boolean rootConfirmed;

    private FlagWriter() {
    }

    private static boolean hasSu() {
        long now = android.os.SystemClock.elapsedRealtime();
        if (su != null && now - suCheckedAt < SU_CACHE_MS) return su;
        su = Core.flag("shell.hasSu", Core.args());
        suCheckedAt = now;
        return su;
    }

    private static void forgetSu() {
        su = null;
        suCheckedAt = 0;
    }

    public static boolean rootAvailable() {
        return rootConfirmed || hasSu();
    }

    public static boolean rootEnabled(Context c) {
        return "1".equals(Store.get(c).setting("useRoot", "0"));
    }

    public static void setRootEnabled(Context c, boolean on) {
        Store.get(c).putSetting("useRoot", on ? "1" : null);
        forgetSu();
    }

    public static int testRoot() {
        if (openRootShell()) return OK;
        forgetSu();
        return openRootShell() ? OK : DENIED;
    }

    private static boolean openRootShell() {
        if (!Core.flag("shell.rootProbe", Core.args())) return false;
        rootConfirmed = true;
        forgetSu();
        return true;
    }

    public static Mode mode(Context c) {
        if (Helper.running()) return Mode.HELPER;
        if (rootEnabled(c)) return Mode.ROOT;
        return Mode.NONE;
    }

    public static boolean readable(String json) {
        return Core.flag("shell.readable", Core.args("json", json));
    }

    public static boolean tooLarge(String json) {
        return json.length() > Flags.MAX_BYTES;
    }

    static boolean safeForHeredoc(String json) {
        return Core.flag("shell.safeForHeredoc", Core.args("json", json));
    }

    private static String script(String op, Object... kv) {
        try {
            String s = Core.text("shell." + op, Core.args(kv));
            return s == null ? "exit 2\n" : s;
        } catch (IOException e) {
            return "exit 2\n";
        }
    }

    public static int shell(Context c, String script, int timeoutSeconds) {
        return run(mode(c), script, timeoutSeconds);
    }

    public static int write(Mode mode, String json) {
        if (!safeForHeredoc(json)) return FAILED;
        return run(mode, script("writeScript", "json", json), 60);
    }

    public static int remove(Mode mode) {
        return run(mode, script("removeScript"), 60);
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
            + "setprop " + GAME_FPS_CAP + " false\n"
            + "rm -f " + REFRESH_BACKUP + "\n"
            + "fi\n";

    public static float maxRefresh(Context c) {
        float max = 0;
        android.hardware.display.DisplayManager dm = c.getSystemService(android.hardware.display.DisplayManager.class);
        android.view.Display d = dm == null ? null : dm.getDisplay(android.view.Display.DEFAULT_DISPLAY);
        if (d != null) for (android.view.Display.Mode m : d.getSupportedModes()) max = Math.max(max, m.getRefreshRate());
        return max;
    }

    public static int syncForLaunch(Context c, String pkg, String json, boolean unlocked) {
        if (tooLarge(json)) return R.string.flags_too_large;
        if (!safeForHeredoc(json)) return R.string.flags_launch_not_applied;
        Mode mode = mode(c);
        if (mode != Mode.HELPER && mode != Mode.ROOT) return readable(json) ? 0 : R.string.flags_launch_not_applied;
        if (!pkg.matches("[A-Za-z0-9_.]+")) return 0;
        String s = script("launchScript", "json", json, "unlocked", unlocked, "pkg", pkg, "restore", RESTORE_REFRESH);
        return run(mode, s, 15) == OK ? 0 : R.string.flags_launch_not_applied;
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
        try {
            Object v = Core.value("shell.su", Core.args("script", script, "timeout", timeoutSeconds));
            int code = v instanceof Number ? ((Number) v).intValue() : -1;
            if (code == -2) {
                forgetSu();
                return -1;
            }
            return code;
        } catch (IOException e) {
            return FAILED;
        }
    }
}
