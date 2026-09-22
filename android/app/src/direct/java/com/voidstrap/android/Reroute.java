package com.voidstrap.android;

import android.content.Context;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;

final class Reroute {
    private Reroute() {
    }

    static void onJoined(Context c, ActivityWatcher.Data d, SmartJoin.Alert alert) {
        Store s = Store.get(c);
        JSONObject r;
        try {
            r = Core.run("mm.reroute", Core.merge(Matchmaker.env(c, "place", d.placeId, "job", d.jobId, "address", d.serverAddress,
                    "public", d.serverType == ActivityWatcher.ServerType.PUBLIC, "excluded", Matchmaker.excluded(s).contains(d.placeId),
                    "enabled", Matchmaker.enabled(s), "maxRetries", Matchmaker.maxRetries(s)), Matchmaker.settings(c, null)));
        } catch (IOException e) {
            return;
        }
        Matchmaker.settle(c, r);
        String text = message(c, r.optJSONArray("alert"));
        if (text != null) alert.show(text);
        String job = r.isNull("rejoin") ? null : r.optString("rejoin", null);
        if (job != null) rejoin(c, d.placeId, job);
    }

    private static String message(Context c, JSONArray parts) {
        if (parts == null || parts.length() == 0) return null;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < parts.length(); i++) {
            JSONArray p = parts.optJSONArray(i);
            if (p == null) continue;
            String kind = p.optString(0);
            switch (kind) {
                case "preferred":
                    sb.append(c.getString(R.string.matchmaker_alert_preferred, p.optString(1), p.optInt(2)));
                    break;
                case "connected":
                    sb.append(c.getString(R.string.matchmaker_alert_connected, p.optString(1), p.optInt(2)));
                    break;
                case "blocked_none":
                    sb.append(c.getString(R.string.matchmaker_alert_blocked_none, p.optString(1)));
                    break;
                case "blocked_same":
                    sb.append(c.getString(R.string.matchmaker_alert_blocked_same, p.optString(1)));
                    break;
                case "preferred_empty":
                    sb.append(c.getString(R.string.matchmaker_alert_preferred_empty, p.optString(1), p.optString(2)));
                    break;
                case "good":
                    sb.append(c.getString(R.string.matchmaker_alert_good, p.optInt(1)));
                    break;
                case "limit":
                    sb.append(c.getResources().getQuantityString(R.plurals.matchmaker_alert_limit, p.optInt(1), p.optInt(1), p.optString(2)));
                    break;
                case "moving":
                    sb.append(c.getString(R.string.matchmaker_alert_moving, p.optString(1), p.optInt(2)));
                    break;
                case "attempt":
                    sb.append(' ').append(c.getString(R.string.matchmaker_alert_attempt, p.optInt(1), p.optInt(2)));
                    break;
                case "closer":
                    sb.append('\n').append(c.getString(R.string.matchmaker_alert_blocked_closer, p.optString(1)));
                    break;
                default:
                    break;
            }
        }
        return sb.toString();
    }

    static void expect(long placeId, String city) {
        try {
            Core.run("mm.expect", Core.args("place", placeId, "city", city));
        } catch (IOException ignored) {
        }
    }

    static boolean rejoining() {
        return Core.flag("mm.rejoining", Core.args());
    }

    private static void rejoin(Context c, long placeId, String jobId) {
        Deeplink link = Deeplink.place(placeId, jobId, null);
        if (link == null) return;
        String pkg = Targets.selected(c);
        String script = "am force-stop " + ModEngine.quote(pkg) + "\n"
                + "sleep 1\n"
                + "am start -a android.intent.action.VIEW -c android.intent.category.BROWSABLE -d " + ModEngine.quote(link.toUri().toString()) + " " + ModEngine.quote(pkg) + " >/dev/null 2>&1 || exit 1\n"
                + "exit 0\n";
        int r = FlagWriter.shell(c, script, 30);
        if (r != 0) Log.w(Matchmaker.TAG, "Rejoin failed with " + r);
    }
}
