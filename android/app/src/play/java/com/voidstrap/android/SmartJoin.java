package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
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

    private static JSONObject env(Context c, Object... kv) {
        return Core.merge(Core.args("files", c.getFilesDir(), "cache", c.getCacheDir(), "agent", "Voidstrap Android/" + BuildConfig.VERSION_NAME), Core.args(kv));
    }

    static Deeplink rewrite(Context app, Deeplink destination) {
        Store store = Store.get(app);
        if (destination == null || !destination.isPlace() || destination.instanceId != null || destination.linkCode != null) return destination;
        if (!enabled(store)) return destination;
        Regions.Region me = region(store);
        String where = regionName(app, me);
        boolean near = nearMode(store);
        int seen;
        try {
            Object v = Core.value("smart.seen", env(app, "place", destination.placeId));
            seen = v instanceof Number ? ((Number) v).intValue() : 0;
        } catch (IOException e) {
            seen = 0;
        }
        if (near && seen == 0) {
            toast(app, app.getString(R.string.smart_roblox_near, where));
            return destination;
        }
        toast(app, app.getString(R.string.smart_finding));
        JSONObject choice;
        try {
            Object v = Core.value("smart.pick", env(app, "place", destination.placeId, "lat", me.lat, "lon", me.lon, "near", near,
                    "preferEmpty", preferEmpty(store), "minFps", minFps(store)));
            choice = v instanceof JSONObject ? (JSONObject) v : null;
        } catch (IOException e) {
            choice = null;
        }
        Deeplink picked = choice == null ? null : Deeplink.place(destination.placeId, choice.optString("job"), null);
        if (picked == null) {
            toast(app, app.getString(near ? R.string.smart_roblox_near : R.string.smart_fallback, where));
            return destination;
        }
        String city = choice.optString("city");
        int ping = choice.optInt("ping");
        double fps = choice.optDouble("fps", 0);
        if (!city.isEmpty()) toast(app, app.getString(R.string.smart_joining_city, city));
        else if (ping > 0 && fps > 0) toast(app, app.getString(R.string.smart_joining, Math.round(fps), ping));
        else toast(app, app.getString(R.string.smart_joining_plain));
        return picked;
    }

    static void onJoined(Context c, ActivityWatcher.Data data, Alert alert) {
        boolean saved = Core.flag("smart.onJoined", env(c, "job", data.jobId, "place", data.placeId, "universe", data.universeId, "address", data.serverAddress));
        if (saved) Store.get(c).changed();
    }

    static List<Known> known(Context c) {
        List<Known> out = new ArrayList<>();
        try {
            Object v = Core.value("smart.known", env(c));
            JSONArray list = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
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
                out.add(k);
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    static void forget(Context c) {
        try {
            Core.run("smart.forget", env(c));
        } catch (IOException ignored) {
        }
    }

    private static void toast(Context app, String text) {
        Store.get(app).main.post(() -> Notify.toast(app, Notify.SMART, text));
    }
}
