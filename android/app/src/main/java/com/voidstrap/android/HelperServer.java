package com.voidstrap.android;

import android.os.Process;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.util.concurrent.TimeUnit;

public final class HelperServer {
    static final String PORT_FILE = "/data/local/tmp/voidstrap_helper_port";
    static final int NONCE_BYTES = 16;
    static final int PROOF_BYTES = 32;
    static final int VERSION = 6;
    static final int PING = -1;
    static final int STOP = -2;
    static final int STALE = -3;
    static final int STREAM = -4;
    static final String STREAM_START = "\u0001START";
    static final String STREAM_EXIT = "\u0001EXIT";
    static final String STREAM_IDLE = "\u0001IDLE";
    static final String[] STREAM_MARKERS = {
            "! Joining game",
            "game_join_loadtime",
            "UDMUX Address",
            "[FLog::Network] serverId:",
            "Time to disconnect replication data",
            "leaveUGCGameInternal",
            "doTeleport: joinScriptUrl",
            "[VoidstrapRPC]",
            "[BloxstrapRPC]",
            "ExpChat/mountClientApp",
            "SocialCounterpartyManager",
            "setStage: (stage:"
    };
    static final int MAX_SCRIPT = 8 * 1024 * 1024;
    static final int SHELL_UID = 2000;
    static final String CACHE_LIST = "/data/local/tmp/voidstrap_cache_";
    static final String CLEANUP = "K=0\n"
            + "for f in " + FlagWriter.PATH + " " + FlagWriter.PATH + ".new " + LibraryData.JOINS + "; do [ -e \"$f\" ] && K=1; rm -f \"$f\"; done\n"
            + FlagWriter.RESTORE_REFRESH
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
            + "rm -f " + PORT_FILE + "\n"
            + "rm -rf " + ModEngine.REMOTE_DIR + "\n"
            + "if [ $K = 1 ]; then for p in " + Targets.GLOBAL + " " + Targets.VN + "; do am force-stop $p 2>/dev/null; done; fi\n"
            + "exit 0\n";

    private static String pkg;
    private static int allowedUid;
    private static String token;
    private static ServerSocket server;

    private HelperServer() {
    }

    static byte[] proof(String token, byte[] nonce) {
        try {
            MessageDigest d = MessageDigest.getInstance("SHA-256");
            d.update(token.getBytes(StandardCharsets.UTF_8));
            d.update(nonce);
            return d.digest();
        } catch (NoSuchAlgorithmException e) {
            return new byte[PROOF_BYTES];
        }
    }

    static String launchLine(String pkg, int uid, String token) {
        return "p=$(pm path " + pkg + " | sed -n 's/^package://p' | grep base.apk | head -n 1)\n"
                + "[ -z \"$p\" ] && p=$(pm path " + pkg + " | sed -n '1s/^package://p')\n"
                + "[ -z \"$p\" ] && { echo 'Voidstrap is not installed'; exit 1; }\n"
                + "CLASSPATH=\"$p\" nohup app_process /system/bin --nice-name=voidstrap_helper "
                + HelperServer.class.getName() + " " + pkg + " " + uid + " " + token + " </dev/null >/dev/null 2>&1 &\n";
    }

    public static void main(String[] args) {
        if (args.length < 3) return;
        pkg = args[0];
        try {
            allowedUid = Integer.parseInt(args[1]);
        } catch (NumberFormatException e) {
            return;
        }
        token = args[2];
        if (token.isEmpty()) return;
        server = bind();
        if (server == null) {
            System.out.println("The Voidstrap helper could not listen");
            return;
        }
        System.out.println("The Voidstrap helper started");
        Thread watch = new Thread(HelperServer::watch, "watch");
        watch.setDaemon(true);
        watch.start();
        while (true) {
            Socket client;
            try {
                client = server.accept();
            } catch (IOException e) {
                System.exit(0);
                return;
            }
            new Thread(() -> serve(client), "request").start();
        }
    }

    private static ServerSocket bind() {
        stopOld();
        for (int i = 0; i < 20; i++) {
            try {
                ServerSocket s = new ServerSocket(0, 50, InetAddress.getLoopbackAddress());
                publish(s.getLocalPort());
                return s;
            } catch (IOException e) {
                sleep(250);
            }
        }
        return null;
    }

    private static void publish(int port) throws IOException {
        File f = new File(PORT_FILE);
        f.delete();
        try (OutputStream o = new FileOutputStream(f)) {
            o.write(String.valueOf(port).getBytes(StandardCharsets.UTF_8));
        }
        try {
            android.system.Os.chmod(PORT_FILE, 0644);
        } catch (android.system.ErrnoException e) {
            throw new IOException("port file mode");
        }
    }

    private static int publishedPort() {
        try {
            byte[] b = java.nio.file.Files.readAllBytes(new File(PORT_FILE).toPath());
            return Integer.parseInt(new String(b, StandardCharsets.UTF_8).trim());
        } catch (IOException | RuntimeException e) {
            return 0;
        }
    }

    private static void stopOld() {
        int port = publishedPort();
        if (port <= 0) return;
        try (Socket s = new Socket()) {
            s.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(), port), 2000);
            s.setSoTimeout(2000);
            DataInputStream in = new DataInputStream(s.getInputStream());
            byte[] nonce = new byte[NONCE_BYTES];
            in.readFully(nonce);
            DataOutputStream out = new DataOutputStream(s.getOutputStream());
            out.write(proof(token, nonce));
            out.writeInt(VERSION);
            out.writeInt(STOP);
            out.flush();
            in.readInt();
        } catch (IOException ignored) {
        }
    }

    private static void watch() {
        String apk = System.getenv("CLASSPATH");
        if (apk == null) return;
        byte[] cleanup = CLEANUP.getBytes(StandardCharsets.UTF_8);
        while (true) {
            sleep(TimeUnit.SECONDS.toMillis(5));
            if (new File(apk).exists()) continue;
            if (installed()) {
                relaunch();
                return;
            }
            run(cleanup, 120);
            System.exit(0);
        }
    }

    private static boolean installed() {
        try {
            java.lang.Process p = new ProcessBuilder("pm", "path", pkg).redirectErrorStream(true).start();
            byte[] out = readAll(p.getInputStream());
            p.waitFor(20, TimeUnit.SECONDS);
            return new String(out, StandardCharsets.UTF_8).contains("package:");
        } catch (IOException e) {
            return true;
        } catch (InterruptedException e) {
            return true;
        }
    }

    private static void serve(Socket client) {
        try (Socket s = client) {
            s.setSoTimeout(10_000);
            DataInputStream in = new DataInputStream(s.getInputStream());
            DataOutputStream out = new DataOutputStream(s.getOutputStream());
            byte[] nonce = new byte[NONCE_BYTES];
            new SecureRandom().nextBytes(nonce);
            out.write(nonce);
            out.flush();
            byte[] offered = new byte[PROOF_BYTES];
            in.readFully(offered);
            if (!MessageDigest.isEqual(offered, proof(token, nonce))) return;
            int version = in.readInt();
            int timeout = in.readInt();
            if (timeout == STOP) {
                out.writeInt(0);
                out.flush();
                new File(PORT_FILE).delete();
                System.exit(0);
                return;
            }
            if (timeout == PING) {
                out.writeInt(Process.myUid());
                out.flush();
                if (version != VERSION) relaunch();
                return;
            }
            if (version != VERSION) {
                out.writeInt(STALE);
                out.flush();
                relaunch();
                return;
            }
            if (timeout == STREAM) {
                String target = in.readUTF();
                if (!target.equals(Targets.GLOBAL) && !target.equals(Targets.VN)) return;
                out.writeInt(0);
                out.flush();
                s.setSoTimeout(0);
                stream(target, out);
                return;
            }
            int length = in.readInt();
            if (length < 0 || length > MAX_SCRIPT) return;
            byte[] script = new byte[length];
            in.readFully(script);
            out.writeInt(run(script, Math.max(1, Math.min(timeout, 600))));
            out.flush();
        } catch (IOException ignored) {
        }
    }

    static boolean wanted(String line) {
        for (String m : STREAM_MARKERS) if (line.contains(m)) return true;
        return false;
    }

    private static String pidOf(String target) {
        try {
            java.lang.Process p = new ProcessBuilder("pidof", target).redirectErrorStream(true).start();
            String out = new String(readAll(p.getInputStream()), StandardCharsets.UTF_8).trim();
            p.waitFor(5, TimeUnit.SECONDS);
            if (out.isEmpty()) return null;
            String first = out.split("\\s+")[0];
            return first.matches("[0-9]+") ? first : null;
        } catch (IOException e) {
            return null;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return null;
        }
    }

    private static void send(DataOutputStream out, String line) throws IOException {
        synchronized (out) {
            out.writeUTF(line.length() > 8000 ? line.substring(0, 8000) : line);
            out.flush();
        }
    }

    private static void stream(String target, DataOutputStream out) throws IOException {
        while (true) {
            String pid = pidOf(target);
            if (pid == null) {
                send(out, STREAM_IDLE);
                sleep(2000);
                continue;
            }
            send(out, STREAM_START + " " + pid);
            java.lang.Process log = new ProcessBuilder("logcat", "-v", "raw", "--pid=" + pid, "-s", "Roblox:I")
                    .redirectErrorStream(true)
                    .start();
            java.util.concurrent.atomic.AtomicBoolean failed = new java.util.concurrent.atomic.AtomicBoolean();
            Thread reader = new Thread(() -> {
                try (java.io.BufferedReader r = new java.io.BufferedReader(new java.io.InputStreamReader(log.getInputStream(), StandardCharsets.UTF_8))) {
                    String line;
                    while ((line = r.readLine()) != null) {
                        if (wanted(line)) send(out, line);
                    }
                } catch (IOException e) {
                    failed.set(true);
                }
            }, "logcat");
            reader.setDaemon(true);
            reader.start();
            try {
                while (!failed.get()) {
                    sleep(2000);
                    if (!pid.equals(pidOf(target))) break;
                }
            } finally {
                log.destroyForcibly();
            }
            if (failed.get()) throw new IOException("client closed");
            send(out, STREAM_EXIT);
        }
    }

    private static int run(byte[] script, int timeoutSeconds) {
        java.lang.Process p;
        try {
            p = new ProcessBuilder("sh")
                    .redirectErrorStream(true)
                    .redirectOutput(ProcessBuilder.Redirect.to(new File("/dev/null")))
                    .start();
        } catch (IOException e) {
            return 1;
        }
        try (OutputStream in = p.getOutputStream()) {
            in.write(script);
        } catch (IOException e) {
            p.destroy();
            return 2;
        }
        try {
            if (!p.waitFor(timeoutSeconds, TimeUnit.SECONDS)) {
                p.destroyForcibly();
                return 2;
            }
            return p.exitValue();
        } catch (InterruptedException e) {
            p.destroyForcibly();
            return 2;
        }
    }

    private static synchronized void relaunch() {
        try {
            new ProcessBuilder("sh", "-c", "sleep 1\n" + launchLine(pkg, allowedUid, token))
                    .redirectErrorStream(true)
                    .redirectOutput(ProcessBuilder.Redirect.to(new File("/dev/null")))
                    .start();
        } catch (IOException e) {
            return;
        }
        try {
            server.close();
        } catch (IOException ignored) {
        }
        System.exit(0);
    }

    private static byte[] readAll(java.io.InputStream in) throws IOException {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        byte[] buf = new byte[4096];
        int n;
        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        return out.toByteArray();
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException ignored) {
        }
    }
}
