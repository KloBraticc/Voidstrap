package com.voidstrap.android;

import android.net.Uri;

import org.json.JSONObject;

import java.io.IOException;

public final class Deeplink {
    public static final int MAX_INPUT = 2048;

    public final long placeId;
    public final String instanceId;
    public final String linkCode;
    private final boolean place;
    private final String uri;
    private final String id;

    private Deeplink(JSONObject o) {
        placeId = o.optLong("placeId");
        instanceId = Core.opt(o, "instanceId");
        linkCode = Core.opt(o, "linkCode");
        place = o.optBoolean("isPlace");
        uri = o.optString("uri");
        id = o.optString("id");
    }

    private static Deeplink call(String op, JSONObject args) {
        try {
            Object v = Core.value(op, args);
            return v instanceof JSONObject ? new Deeplink((JSONObject) v) : null;
        } catch (IOException e) {
            return null;
        }
    }

    public static Deeplink place(long placeId, String instanceId, String linkCode) {
        return call("link.place", Core.args("placeId", placeId, "instanceId", instanceId, "linkCode", linkCode));
    }

    public static Deeplink findIn(String text) {
        return text == null ? null : call("link.findIn", Core.args("text", text));
    }

    public static Deeplink parse(String input) {
        return input == null ? null : call("link.parse", Core.args("text", input));
    }

    public boolean isPlace() {
        return place;
    }

    public Uri toUri() {
        return Uri.parse(uri == null ? "" : uri);
    }

    public String stableId() {
        return id == null || id.isEmpty() ? "game_0" : id;
    }
}
