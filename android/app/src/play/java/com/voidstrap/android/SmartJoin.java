package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.TimeZone;

final class SmartJoin {
    interface Alert {
        void show(String text);
    }

    static final String ENABLED = "sjEnabled";
    static final String PREFER_EMPTY = "sjPreferEmpty";
    static final String MODE = "sjMode";
    static final String REGION = "sjRegion";
    static final String MIN_FPS = "sjMinFps";
    static final String MODE_NEAR = "near";
    static final String MODE_FAST = "fast";
    static final int[] FPS_LIMITS = {0, 30, 45, 55};

    private static final int PAGE_LIMIT = 100;
    private static final int MAX_PAGES = 3;
    private static final double UNKNOWN_PING = 250;
    private static final double MAX_PING = 300;
    private static final double FPS_FLOOR = 55;
    private static final double LOW_FPS_PENALTY = 40;
    private static final double FILL_WEIGHT = 30;
    private static final double NEAR_KM = 1500;
    private static final double FAR_KM = 4000;
    private static final long FRESH_MS = 12 * 60 * 60 * 1000L;
    private static final long LOCATION_CACHE_MS = 7 * 24 * 60 * 60 * 1000L;
    private static final int KNOWN_LIMIT = 200;
    private static final String FILE = "smart_servers.json";

    static final class Known {
        String job = "";
        long place;
        String game = "";
        String city = "";
        String country = "";
        double lat;
        double lon;
        long time;
    }

    private static final class Server {
        String id;
        int playing;
        int max;
        int ping;
        double fps;
    }

    private SmartJoin() {
    }

    static boolean enabled(Store s) {
        return "1".equals(s.setting(ENABLED, "0"));
    }

    static boolean preferEmpty(Store s) {
        return "1".equals(s.setting(PREFER_EMPTY, "1"));
    }

    static boolean nearMode(Store s) {
        return !MODE_FAST.equals(s.setting(MODE, MODE_NEAR));
    }

    static int minFps(Store s) {
        try {
            int v = Integer.parseInt(s.setting(MIN_FPS, "45"));
            for (int limit : FPS_LIMITS) if (limit == v) return v;
        } catch (NumberFormatException ignored) {
        }
        return 45;
    }

    static boolean autoRegion(Store s) {
        return Regions.byKey(s.setting(REGION, "")) == null;
    }

    static Regions.Region region(Store s) {
        Regions.Region chosen = Regions.byKey(s.setting(REGION, ""));
        return chosen != null ? chosen : Regions.fromTimeZone(TimeZone.getDefault());
    }

    static String regionName(Context c, Regions.Region r) {
        return c.getResources().getStringArray(R.array.smart_regions)[Regions.index(r)];
    }

    static boolean available() {
        return true;
    }

    static String title(Context c) {
        return c.getString(R.string.smart_title);
    }

    static String alertsTitle(Context c) {
        return null;
    }

    static String alertsBody(Context c) {
        return null;
    }

    static String openBody(Context c) {
        return c.getString(R.string.smart_open_body);
    }

    static Page page() {
        return new SmartJoinFragment();
    }

    static String[] presence() {
        return new String[]{"Voidstrap Smart Join", "Picking a server"};
    }

    static boolean rejoining() {
        return false;
    }

    static Deeplink rewrite(Context app, Deeplink destination) {
        Store store = Store.get(app);
        if (destination == null || !destination.isPlace() || destination.instanceId != null || destination.linkCode != null) return destination;
        if (!enabled(store)) return destination;
        Regions.Region me = region(store);
        String where = regionName(app, me);
        Map<String, Known> seen = new HashMap<>();
        long now = System.currentTimeMillis();
        for (Known k : known(app)) if (k.place == destination.placeId && now - k.time < FRESH_MS) seen.put(k.job, k);
        boolean near = nearMode(store);
        if (near && seen.isEmpty()) {
            toast(app, app.getString(R.string.smart_roblox_near, where));
            return destination;
        }
        toast(app, app.getString(R.string.smart_finding));
        List<Server> servers = servers(destination.placeId, preferEmpty(store));
        int floor = minFps(store);
        boolean quiet = preferEmpty(store);
        Server best = null;
        Known bestKnown = null;
        double bestCost = Double.MAX_VALUE;
        for (Server s : servers) {
            if (!healthy(s, floor)) continue;
            Known k = seen.get(s.id);
            if (near && k == null) continue;
            double km = k == null ? NEAR_KM : Regions.km(me.lat, me.lon, k.lat, k.lon);
            if (km > (near ? NEAR_KM : FAR_KM)) continue;
            double cost = quality(s, quiet) + km / 10;
            if (cost < bestCost) {
                bestCost = cost;
                best = s;
                bestKnown = k;
            }
        }
        Deeplink picked = best == null ? null : Deeplink.place(destination.placeId, best.id, null);
        if (picked == null) {
            toast(app, app.getString(near ? R.string.smart_roblox_near : R.string.smart_fallback, where));
            return destination;
        }
        if (bestKnown != null && !bestKnown.city.isEmpty()) toast(app, app.getString(R.string.smart_joining_city, bestKnown.city));
        else if (best.ping > 0 && best.fps > 0) toast(app, app.getString(R.string.smart_joining, Math.round(best.fps), best.ping));
        else toast(app, app.getString(R.string.smart_joining_plain));
        return picked;
    }

    static void onJoined(Context c, ActivityWatcher.Data data, Alert alert) {
        if (data.jobId.isEmpty() || data.placeId <= 0 || !data.serverAddress.matches("[0-9.]{7,15}")) return;
        Known k = new Known();
        k.job = data.jobId;
        k.place = data.placeId;
        k.time = System.currentTimeMillis();
        try {
            JSONObject info = Net.cachedJson(c, "https://ipinfo.io/" + data.serverAddress + "/json", LOCATION_CACHE_MS);
            String[] loc = info.optString("loc", "").split(",");
            if (loc.length != 2) return;
            k.lat = Double.parseDouble(loc[0]);
            k.lon = Double.parseDouble(loc[1]);
            k.city = Store.clip(info.optString("city", ""), 80);
            k.country = Store.clip(info.optString("country", ""), 8);
        } catch (IOException | JSONException | NumberFormatException e) {
            return;
        }
        k.game = gameName(c, data);
        remember(c, k);
        Store.get(c).changed();
    }

    private static String gameName(Context c, ActivityWatcher.Data data) {
        try {
            long universe = data.universeId;
            if (universe <= 0) universe = Net.cachedJson(c, "https://apis.roblox.com/universes/v1/places/" + data.placeId + "/universe", LOCATION_CACHE_MS).optLong("universeId", 0);
            if (universe <= 0) return "";
            JSONArray games = Net.cachedJson(c, "https://games.roblox.com/v1/games?universeIds=" + universe, 5 * 60 * 1000L).optJSONArray("data");
            JSONObject game = games == null ? null : games.optJSONObject(0);
            return game == null ? "" : Store.clip(game.optString("name", ""), 120);
        } catch (IOException | JSONException e) {
            return "";
        }
    }

    static synchronized List<Known> known(Context c) {
        List<Known> out = new ArrayList<>();
        File f = new File(c.getFilesDir(), FILE);
        if (!f.isFile()) return out;
        try {
            JSONArray list = new JSONArray(new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8));
            for (int i = 0; i < list.length(); i++) {
                JSONObject o = list.optJSONObject(i);
                if (o == null) continue;
                Known k = new Known();
                k.job = o.optString("job");
                k.place = o.optLong("place");
                k.game = o.optString("game");
                k.city = o.optString("city");
                k.country = o.optString("country");
                k.lat = o.optDouble("lat");
                k.lon = o.optDouble("lon");
                k.time = o.optLong("time");
                if (!k.job.isEmpty() && !Double.isNaN(k.lat) && !Double.isNaN(k.lon)) out.add(k);
            }
        } catch (IOException | JSONException ignored) {
        }
        return out;
    }

    private static synchronized void remember(Context c, Known k) {
        List<Known> list = known(c);
        list.removeIf(o -> o.job.equals(k.job));
        list.add(0, k);
        while (list.size() > KNOWN_LIMIT) list.remove(list.size() - 1);
        JSONArray out = new JSONArray();
        try {
            for (Known o : list) {
                out.put(new JSONObject().put("job", o.job).put("place", o.place).put("game", o.game).put("city", o.city)
                        .put("country", o.country).put("lat", o.lat).put("lon", o.lon).put("time", o.time));
            }
            File f = new File(c.getFilesDir(), FILE);
            File tmp = new File(c.getFilesDir(), FILE + ".tmp");
            Files.write(tmp.toPath(), out.toString().getBytes(StandardCharsets.UTF_8));
            if (!tmp.renameTo(f)) tmp.delete();
        } catch (IOException | JSONException ignored) {
        }
    }

    static synchronized void forget(Context c) {
        new File(c.getFilesDir(), FILE).delete();
    }

    private static boolean healthy(Server s, int minFps) {
        if (s.max > 0 && s.playing >= s.max) return false;
        if (minFps > 0 && s.fps > 0 && s.fps < minFps) return false;
        return s.ping <= 0 || s.ping <= MAX_PING;
    }

    private static double quality(Server s, boolean preferEmpty) {
        double cost = s.ping > 0 ? s.ping : UNKNOWN_PING;
        if (s.fps > 0 && s.fps < FPS_FLOOR) cost += LOW_FPS_PENALTY;
        double fill = s.max > 0 ? (double) s.playing / s.max : 0;
        cost += preferEmpty ? fill * FILL_WEIGHT : (1 - fill) * FILL_WEIGHT;
        return cost;
    }

    private static List<Server> servers(long placeId, boolean preferEmpty) {
        List<Server> out = new ArrayList<>();
        String cursor = null;
        for (int page = 0; page < MAX_PAGES; page++) {
            JSONObject body;
            try {
                body = Net.json(url(placeId, preferEmpty, cursor));
            } catch (IOException | JSONException e) {
                break;
            }
            JSONArray data = body.optJSONArray("data");
            if (data == null) break;
            for (int i = 0; i < data.length(); i++) {
                JSONObject o = data.optJSONObject(i);
                if (o == null || o.optString("id", "").isEmpty()) continue;
                Server s = new Server();
                s.id = o.optString("id");
                s.playing = o.optInt("playing", 0);
                s.max = o.optInt("maxPlayers", 0);
                s.ping = o.optInt("ping", 0);
                s.fps = o.optDouble("fps", 0);
                out.add(s);
            }
            cursor = body.optString("nextPageCursor", "");
            if (cursor.isEmpty() || "null".equals(cursor)) break;
        }
        return out;
    }

    private static String url(long placeId, boolean preferEmpty, String cursor) {
        StringBuilder b = new StringBuilder("https://games.roblox.com/v1/games/")
                .append(placeId)
                .append("/servers/Public?excludeFullGames=true&limit=")
                .append(PAGE_LIMIT)
                .append("&sortOrder=")
                .append(preferEmpty ? "Asc" : "Desc");
        if (cursor != null && !cursor.isEmpty()) b.append("&cursor=").append(android.net.Uri.encode(cursor));
        return b.toString();
    }

    private static void toast(Context app, String text) {
        Store.get(app).main.post(() -> Notify.toast(app, Notify.SMART, text));
    }
}
