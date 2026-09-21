package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;

final class SmartJoin {
    interface Alert {
        void show(String text);
    }

    static final String ENABLED = "sjEnabled";
    static final String PREFER_EMPTY = "sjPreferEmpty";

    private static final int PAGE_LIMIT = 100;
    private static final int MAX_PAGES = 2;
    private static final double UNKNOWN_PING = 250;
    private static final double FPS_FLOOR = 55;
    private static final double LOW_FPS_PENALTY = 40;
    private static final double FILL_WEIGHT = 30;

    private SmartJoin() {
    }

    static boolean enabled(Store s) {
        return "1".equals(s.setting(ENABLED, "0"));
    }

    static boolean preferEmpty(Store s) {
        return "1".equals(s.setting(PREFER_EMPTY, "1"));
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

    static Deeplink rewrite(Context app, Deeplink destination) {
        Store store = Store.get(app);
        if (destination == null || !destination.isPlace() || destination.instanceId != null || destination.linkCode != null) return destination;
        if (!enabled(store)) return destination;
        toast(app, app.getString(R.string.smart_finding));
        Pick best = pick(destination.placeId, preferEmpty(store));
        if (best == null) {
            toast(app, app.getString(R.string.smart_fallback));
            return destination;
        }
        Deeplink picked = Deeplink.place(destination.placeId, best.jobId, null);
        if (picked == null) return destination;
        toast(app, best.ping > 0
                ? app.getString(R.string.smart_joining, best.ping)
                : app.getString(R.string.smart_joining_plain));
        return picked;
    }

    static boolean rejoining() {
        return false;
    }

    static void onJoined(Context c, ActivityWatcher.Data data, Alert alert) {
    }

    static final class Pick {
        final String jobId;
        final int ping;

        Pick(String jobId, int ping) {
            this.jobId = jobId;
            this.ping = ping;
        }
    }

    static Pick pick(long placeId, boolean preferEmpty) {
        if (placeId <= 0) return null;
        Pick best = null;
        double bestCost = Double.MAX_VALUE;
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
                JSONObject server = data.optJSONObject(i);
                if (server == null) continue;
                String id = server.optString("id", "");
                if (id.isEmpty()) continue;
                int playing = server.optInt("playing", 0);
                int max = server.optInt("maxPlayers", 0);
                if (max > 0 && playing >= max) continue;
                int ping = server.optInt("ping", 0);
                double cost = ping > 0 ? ping : UNKNOWN_PING;
                double fps = server.optDouble("fps", 0);
                if (fps > 0 && fps < FPS_FLOOR) cost += LOW_FPS_PENALTY;
                double fill = max > 0 ? (double) playing / max : 0;
                cost += preferEmpty ? fill * FILL_WEIGHT : (1 - fill) * FILL_WEIGHT;
                if (cost < bestCost) {
                    bestCost = cost;
                    best = new Pick(id, ping);
                }
            }
            cursor = body.optString("nextPageCursor", "");
            if (cursor.isEmpty()) break;
        }
        return best;
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
