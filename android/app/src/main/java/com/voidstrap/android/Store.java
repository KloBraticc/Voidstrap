package com.voidstrap.android;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.util.AtomicFile;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

public final class Store {
    public static final int HISTORY_LIMIT = 100;
    private static final String[] SECRETS = {"helperToken"};

    private static Store instance;

    public final Handler main = new Handler(Looper.getMainLooper());
    public final ExecutorService work = pool(3);
    private final ExecutorService writer = pool(1);
    private final CopyOnWriteArrayList<Runnable> listeners = new CopyOnWriteArrayList<>();
    private final File dir;
    private final java.util.concurrent.atomic.AtomicBoolean writeFailed = new java.util.concurrent.atomic.AtomicBoolean();
    private final java.util.concurrent.atomic.AtomicBoolean queued = new java.util.concurrent.atomic.AtomicBoolean();

    public final JSONObject settings;
    public final List<Game> library = new ArrayList<>();
    public final List<Launch> history = new ArrayList<>();
    public final Flags flags;

    private static ExecutorService pool(int threads) {
        ThreadPoolExecutor p = new ThreadPoolExecutor(threads, threads, 15, TimeUnit.SECONDS, new LinkedBlockingQueue<>());
        p.allowCoreThreadTimeOut(true);
        return p;
    }

    public static synchronized Store get(Context context) {
        if (instance == null) instance = new Store(context.getApplicationContext());
        return instance;
    }

    private Store(Context context) {
        dir = context.getFilesDir();
        settings = read("settings.json");
        JSONArray games = read("library.json").optJSONArray("games");
        if (games != null) for (int i = 0; i < games.length(); i++) {
            Game g = Game.from(games.optJSONObject(i));
            if (g != null) library.add(g);
        }
        JSONArray launches = read("history.json").optJSONArray("launches");
        if (launches != null) for (int i = 0; i < launches.length() && i < HISTORY_LIMIT; i++) {
            Launch l = Launch.from(launches.optJSONObject(i));
            if (l != null) history.add(l);
        }
        flags = Flags.from(read("flags.json"));
    }

    public void observe(Runnable r) {
        listeners.add(r);
    }

    public void unobserve(Runnable r) {
        listeners.remove(r);
    }

    public void changed() {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            if (queued.compareAndSet(false, true)) main.post(this::changed);
            return;
        }
        queued.set(false);
        for (Runnable r : listeners) r.run();
    }

    public String setting(String key, String fallback) {
        return settings.optString(key, fallback);
    }

    public void putSetting(String key, String value) {
        try {
            if (value == null) settings.remove(key);
            else settings.put(key, value);
        } catch (JSONException ignored) {
            return;
        }
        saveSettings();
        changed();
    }

    public void saveSettings() {
        write("settings.json", settings.toString());
    }

    public Game game(long placeId) {
        for (Game g : library) if (g.placeId == placeId) return g;
        return null;
    }

    public void pin(Game game) {
        Game existing = game(game.placeId);
        if (existing != null) library.remove(existing);
        library.add(0, game);
        saveLibrary();
        changed();
    }

    public void unpin(long placeId) {
        Game g = game(placeId);
        if (g == null) return;
        library.remove(g);
        saveLibrary();
        changed();
    }

    public void saveLibrary() {
        JSONArray a = new JSONArray();
        for (Game g : library) a.put(g.toJson());
        write("library.json", wrap("games", a));
    }

    public void record(Launch launch) {
        history.add(0, launch);
        while (history.size() > HISTORY_LIMIT) history.remove(history.size() - 1);
        saveHistory();
        changed();
    }

    public void forget(long placeId) {
        Game g = game(placeId);
        if (g != null) library.remove(g);
        history.removeIf(l -> l.placeId == placeId);
        saveLibrary();
        saveHistory();
        changed();
    }

    public void importLaunches(List<Launch> found) {
        if (found.isEmpty()) return;
        history.addAll(found);
        history.sort((x, y) -> Long.compare(y.time, x.time));
        while (history.size() > HISTORY_LIMIT) history.remove(history.size() - 1);
        saveHistory();
        changed();
    }

    public void clearHistory() {
        history.clear();
        saveHistory();
        changed();
    }

    private void saveHistory() {
        JSONArray a = new JSONArray();
        for (Launch l : history) a.put(l.toJson());
        write("history.json", wrap("launches", a));
    }

    public void saveFlags() {
        write("flags.json", flags.toJson().toString());
    }

    public void saveAll(java.util.function.Consumer<Boolean> done) {
        writer.execute(() -> writeFailed.set(false));
        saveSettings();
        saveLibrary();
        saveHistory();
        saveFlags();
        writer.execute(() -> {
            boolean ok = !writeFailed.get();
            main.post(() -> done.accept(ok));
        });
    }

    public JSONObject backup() throws JSONException {
        JSONObject o = new JSONObject();
        o.put("format", "voidstrap.android.backup");
        o.put("version", 1);
        o.put("created", System.currentTimeMillis());
        JSONObject safe = new JSONObject(settings.toString());
        for (String key : SECRETS) safe.remove(key);
        o.put("settings", safe);
        JSONArray a = new JSONArray();
        for (Game g : library) a.put(g.toJson());
        o.put("library", a);
        o.put("flags", flags.toJson());
        return o;
    }

    public void restore(JSONObject backup) throws JSONException {
        if (!"voidstrap.android.backup".equals(backup.optString("format"))) throw new JSONException("format");
        JSONObject s = backup.optJSONObject("settings");
        JSONArray games = backup.optJSONArray("library");
        JSONObject f = backup.optJSONObject("flags");
        if (s != null) {
            for (String key : new String[]{"theme", "accent", "target"}) {
                String v = s.optString(key, null);
                if (v != null && v.length() < 128) settings.put(key, v);
            }
            saveSettings();
        }
        if (games != null) {
            for (int i = 0; i < games.length(); i++) {
                Game g = Game.from(games.optJSONObject(i));
                if (g != null && game(g.placeId) == null) library.add(g);
            }
            saveLibrary();
        }
        if (f != null) {
            Flags incoming = Flags.from(f);
            for (Flags.Profile p : incoming.profiles) {
                if (flags.byId(p.id) == null) flags.profiles.add(p);
            }
            saveFlags();
        }
        changed();
    }

    private static String wrap(String key, JSONArray a) {
        try {
            return new JSONObject().put("version", 1).put(key, a).toString();
        } catch (JSONException e) {
            return "{}";
        }
    }

    private JSONObject read(String name) {
        AtomicFile f = new AtomicFile(new File(dir, name));
        try {
            return new JSONObject(new String(f.readFully(), StandardCharsets.UTF_8));
        } catch (IOException | JSONException e) {
            return new JSONObject();
        }
    }

    private void write(String name, String json) {
        writer.execute(() -> {
            AtomicFile f = new AtomicFile(new File(dir, name));
            FileOutputStream out = null;
            try {
                out = f.startWrite();
                out.write(json.getBytes(StandardCharsets.UTF_8));
                f.finishWrite(out);
            } catch (IOException e) {
                writeFailed.set(true);
                if (out != null) f.failWrite(out);
            }
        });
    }

    public static final class Game {
        public final long placeId;
        public long universeId;
        public String name;
        public String iconUrl;
        public final long added;

        public Game(long placeId, long universeId, String name, String iconUrl, long added) {
            this.placeId = placeId;
            this.universeId = universeId;
            this.name = name;
            this.iconUrl = iconUrl;
            this.added = added;
        }

        static Game from(JSONObject o) {
            if (o == null) return null;
            long id = o.optLong("placeId", 0);
            if (id <= 0) return null;
            return new Game(id, o.optLong("universeId", 0), clip(o.optString("name", ""), 200), clip(o.optString("iconUrl", ""), 1024), o.optLong("added", 0));
        }

        JSONObject toJson() {
            JSONObject o = new JSONObject();
            try {
                o.put("placeId", placeId).put("universeId", universeId).put("name", name).put("iconUrl", iconUrl).put("added", added);
            } catch (JSONException ignored) {
            }
            return o;
        }

        public String title() {
            return name == null || name.isEmpty() ? String.valueOf(placeId) : name;
        }
    }

    public static final class Launch {
        public final long time;
        public final long placeId;
        public final String name;
        public final String target;
        public final String result;

        public Launch(long time, long placeId, String name, String target, String result) {
            this.time = time;
            this.placeId = placeId;
            this.name = name;
            this.target = target;
            this.result = result;
        }

        static Launch from(JSONObject o) {
            if (o == null) return null;
            return new Launch(o.optLong("time"), o.optLong("placeId"), clip(o.optString("name", ""), 200), o.optString("target", ""), o.optString("result", ""));
        }

        JSONObject toJson() {
            JSONObject o = new JSONObject();
            try {
                o.put("time", time).put("placeId", placeId).put("name", name).put("target", target).put("result", result);
            } catch (JSONException ignored) {
            }
            return o;
        }
    }

    static String clip(String s, int max) {
        if (s == null) return "";
        return s.length() > max ? s.substring(0, max) : s;
    }
}
