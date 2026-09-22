package com.voidstrap.android;

public final class ActivityWatcher {
    static {
        System.loadLibrary("voidstrap_core");
    }

    public enum ServerType { PUBLIC, PRIVATE, RESERVED }

    public static final class Data {
        public long placeId;
        public String jobId = "";
        public long universeId;
        public long userId;
        public ServerType serverType = ServerType.PUBLIC;
        public String serverAddress = "";
        public long joined;
        public boolean teleport;
        public String launchData = "";

        Data copy() {
            Data d = new Data();
            d.placeId = placeId;
            d.jobId = jobId;
            d.universeId = universeId;
            d.userId = userId;
            d.serverType = serverType;
            d.serverAddress = serverAddress;
            d.joined = joined;
            d.teleport = teleport;
            d.launchData = launchData;
            return d;
        }
    }

    public interface Listener {
        void onJoined(Data data);

        void onLeft(Data data);

        void onMenu();

        void onLog(String kind, String text);

        void onRpc(String command, String json);
    }

    private final Listener listener;
    private long handle = create();

    public ActivityWatcher(Listener listener) {
        this.listener = listener;
    }

    public synchronized boolean inGame() {
        return handle != 0 && inGame(handle);
    }

    public synchronized Data current() {
        return handle == 0 ? null : data(current(handle), 0);
    }

    public synchronized long userId() {
        return handle == 0 ? 0 : userId(handle);
    }

    public synchronized void reset() {
        Data old = handle == 0 ? null : data(reset(handle), 0);
        if (old != null) listener.onLeft(old);
    }

    public synchronized void release() {
        if (handle != 0) {
            destroy(handle);
            handle = 0;
        }
    }

    public void feed(String line) {
        String[] e;
        synchronized (this) {
            e = handle == 0 ? null : feed(handle, line, System.currentTimeMillis());
        }
        if (e == null || e.length == 0 || e[0] == null) return;
        switch (e[0]) {
            case "joined": {
                Data joined = data(e, 1);
                if (joined != null) listener.onJoined(joined);
                break;
            }
            case "left": {
                Data left = data(e, 1);
                if (left != null) listener.onLeft(left);
                break;
            }
            case "menu":
                listener.onMenu();
                break;
            case "log":
                listener.onLog(text(e, 1), text(e, 2));
                break;
            case "rpc":
                listener.onRpc(text(e, 1), text(e, 2));
                break;
            default:
                break;
        }
    }

    private static long number(String[] f, int at) {
        if (f == null || at < 0 || at >= f.length || f[at] == null) return 0;
        try {
            return Long.parseLong(f[at].trim());
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    private static String text(String[] f, int at) {
        return f == null || at < 0 || at >= f.length || f[at] == null ? "" : f[at];
    }

    private static Data data(String[] f, int o) {
        if (f == null || o < 0 || f.length < o + 9) return null;
        Data d = new Data();
        d.placeId = number(f, o);
        d.jobId = text(f, o + 1);
        d.universeId = number(f, o + 2);
        d.userId = number(f, o + 3);
        long kind = number(f, o + 4);
        ServerType[] kinds = ServerType.values();
        d.serverType = kind >= 0 && kind < kinds.length ? kinds[(int) kind] : kinds[0];
        d.serverAddress = text(f, o + 5);
        d.joined = number(f, o + 6);
        d.teleport = "1".equals(text(f, o + 7));
        d.launchData = text(f, o + 8);
        return d;
    }

    private static native long create();

    private static native void destroy(long handle);

    private static native String[] feed(long handle, String line, long now);

    private static native String[] reset(long handle);

    private static native String[] current(long handle);

    private static native boolean inGame(long handle);

    private static native long userId(long handle);
}
