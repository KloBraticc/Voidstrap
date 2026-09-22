package com.voidstrap.android;

import java.io.IOException;
import java.io.InputStream;

public final class HelperServer {
    static final String PORT_FILE = "/data/local/tmp/voidstrap_helper_port";
    static final String BINARY = "/data/local/tmp/voidstrap_helper";
    static final String LIBRARY = "libvoidstrap_helper.so";
    static final int VERSION = BuildConfig.VERSION_CODE;
    static final String STREAM_START = "START";
    static final String STREAM_EXIT = "EXIT";
    static final String STREAM_IDLE = "IDLE";
    static final int SHELL_UID = 2000;
    static final String CACHE_LIST = "/data/local/tmp/voidstrap_cache_";
    private static final String CLEANUP_END = "VOIDSTRAP_CLEANUP_END";
    static final String CLEANUP = "K=0\n"
            + "for f in " + FlagWriter.PATH + " " + FlagWriter.PATH + ".new " + LibraryData.JOINS + "; do [ -e \"$f\" ] && K=1; rm -f \"$f\"; done\n"
            + FlagWriter.RESTORE_REFRESH
            + "setprop " + FlagWriter.GAME_FPS_CAP + " false\n"
            + "NS=''\n"
            + "if nsenter -t 1 -m -- true 2>/dev/null; then NS='nsenter -t 1 -m --'; fi\n"
            + "grep /voidstrap_mods/ /proc/1/mountinfo | while read -r a b c r m rest; do $NS umount -l \"$m\" 2>/dev/null; done\n"
            + "[ -d " + ModEngine.REMOTE_DIR + " ] && K=1\n"
            + "for f in " + CACHE_LIST + "*; do\n"
            + "  [ -f \"$f\" ] || continue\n"
            + "  p=${f#" + CACHE_LIST + "}\n"
            + "  while read -r h; do [ -n \"$h\" ] && rm -f \"/data/data/$p/cache/rbx-storage/${h%${h#??}}/$h\"; done < \"$f\"\n"
            + "  rm -f \"$f\"; K=1\n"
            + "done\n"
            + "rm -f " + PORT_FILE + " " + BINARY + "\n"
            + "rm -rf " + ModEngine.REMOTE_DIR + "\n"
            + "if [ $K = 1 ]; then for p in " + Targets.GLOBAL + " " + Targets.VN + "; do am force-stop $p 2>/dev/null; done; fi\n"
            + "exit 0\n";

    private HelperServer() {
    }

    static String launchLine(String pkg, int uid, String token) {
        return "TMPDIR=/data/local/tmp\nexport TMPDIR\n"
                + "p=$(pm path " + pkg + " | sed -n 's/^package://p' | grep base.apk | head -n 1)\n"
                + "[ -z \"$p\" ] && p=$(pm path " + pkg + " | sed -n '1s/^package://p')\n"
                + "[ -z \"$p\" ] && { echo 'Voidstrap is not installed'; exit 1; }\n"
                + "b=$(ls \"${p%/*}\"/lib/*/" + LIBRARY + " 2>/dev/null | head -n 1)\n"
                + "[ -z \"$b\" ] && { echo 'The Voidstrap helper is missing from this install'; exit 1; }\n"
                + "rm -f " + BINARY + "\n"
                + "cp \"$b\" " + BINARY + " && chmod 0700 " + BINARY + " || { echo 'The Voidstrap helper could not be copied'; exit 1; }\n"
                + "nohup " + BINARY + " " + pkg + " " + uid + " " + token + " \"$p\" >/dev/null 2>&1 <<'" + CLEANUP_END + "' &\n"
                + CLEANUP
                + CLEANUP_END + "\n";
    }

    public static void main(String[] args) {
        if (args.length < 3 || !args[0].matches("[A-Za-z0-9_.]+") || !args[1].matches("[0-9]{1,9}") || !args[2].matches("[0-9a-f]{32}")) return;
        try {
            Process p = new ProcessBuilder("sh", "-c", launchLine(args[0], Integer.parseInt(args[1]), args[2]))
                    .redirectErrorStream(true)
                    .start();
            byte[] buf = new byte[4096];
            try (InputStream out = p.getInputStream()) {
                while (out.read(buf) > 0) {
                }
            }
            p.waitFor();
        } catch (IOException | InterruptedException ignored) {
        }
    }
}
