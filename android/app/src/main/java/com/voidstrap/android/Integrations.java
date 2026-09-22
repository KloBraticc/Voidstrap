package com.voidstrap.android;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

public final class Integrations {
    public static final String TRACKING = "activityTracking";
    public static final String LOCATION = "activityLocation";
    public static final String NOTIFY = "activityNotify";
    public static final String RPC = "discordRpc";
    public static final String JOINING = "discordJoin";
    public static final String NAME = "rpcGameName";
    public static final String ICON = "rpcGameIcon";
    public static final String CREATOR = "rpcCreator";
    public static final String SERVER = "rpcServerType";
    public static final String RPC_LOCATION = "rpcLocation";
    public static final String BETA = "rpcBetaTag";
    public static final String FLAG_COUNT = "rpcFlagCount";
    public static final String CUSTOM_NAME = "rpcCustomName";
    public static final String CUSTOM_ICON = "rpcCustomIcon";
    public static final String IDLE_ICON = "rpcIdleIcon";
    public static final String ACCOUNT = "discordAccount";
    static final String USER_ID = "robloxUserId";

    public static final String[] IDLE_KEYS = {"blue", "black", "old"};
    static final String[] PLAYER_LOG_FLAGS = {"DFLogSocialCounterpartyManager", "FStringDebugLuaLogLevel", "FStringDebugLuaLogPattern"};
    static final Object[] PLAYER_LOG_VALUES = {7L, "trace", "ExpChat/mountClientApp"};

    private static final Set<String> ON_BY_DEFAULT = new HashSet<>(Arrays.asList(TRACKING, LOCATION, NOTIFY, RPC, ACCOUNT, NAME, ICON, CREATOR, SERVER));

    private Integrations() {
    }

    public static boolean on(Store s, String key) {
        return "1".equals(s.setting(key, ON_BY_DEFAULT.contains(key) ? "1" : "0"));
    }

    public static void set(Store s, String key, boolean value) {
        s.putSetting(key, value ? "1" : "0");
    }

    public static boolean playerLogs(Store s) {
        Flags.Profile p = s.flags.active();
        Object event = p.values.get(PLAYER_LOG_FLAGS[0]);
        return (event != null && "7".equals(String.valueOf(event))) || ("trace".equals(p.values.get(PLAYER_LOG_FLAGS[1])) && "ExpChat/mountClientApp".equals(p.values.get(PLAYER_LOG_FLAGS[2])));
    }

    public static void setPlayerLogs(Store s, boolean on) {
        Flags.Profile p = s.flags.active();
        for (int i = 0; i < PLAYER_LOG_FLAGS.length; i++) {
            if (on) p.values.put(PLAYER_LOG_FLAGS[i], PLAYER_LOG_VALUES[i]);
            else p.values.remove(PLAYER_LOG_FLAGS[i]);
        }
        p.updated = System.currentTimeMillis();
        s.saveFlags();
        s.changed();
    }

    public static final class Game {
        public String name = "";
        public String description = "";
        public String creator = "";
        public boolean verified;
        public String icon = "";
        public String location = "";
    }

    public static final class Presence {
        public String details = "";
        public String state = "";
        public String largeImage = "";
        public String largeText = "";
        public String smallImage = "";
        public String smallText = "";
        public long start;
        public boolean detailsStatus;
        public final List<String[]> buttons = new ArrayList<>();

        Presence copy() {
            Presence p = new Presence();
            p.details = details;
            p.state = state;
            p.largeImage = largeImage;
            p.largeText = largeText;
            p.smallImage = smallImage;
            p.smallText = smallText;
            p.start = start;
            p.detailsStatus = detailsStatus;
            p.buttons.addAll(buttons);
            return p;
        }

        static Presence from(JSONObject o) {
            Presence p = new Presence();
            p.details = o.optString("details", "");
            p.state = o.optString("state", "");
            p.largeImage = o.optString("largeImage", "");
            p.largeText = o.optString("largeText", "");
            p.smallImage = o.optString("smallImage", "");
            p.smallText = o.optString("smallText", "");
            p.start = o.optLong("start");
            p.detailsStatus = o.optBoolean("detailsStatus");
            JSONArray list = o.optJSONArray("buttons");
            for (int i = 0; list != null && i < list.length(); i++) {
                JSONArray button = list.optJSONArray(i);
                if (button != null) p.buttons.add(new String[]{button.optString(0), button.optString(1)});
            }
            return p;
        }

        JSONObject json() {
            JSONArray list = new JSONArray();
            for (String[] button : buttons) list.put(new JSONArray(Arrays.asList(button)));
            return Core.args("details", details, "state", state, "largeImage", largeImage, "largeText", largeText, "smallImage", smallImage, "smallText", smallText, "start", start, "detailsStatus", detailsStatus, "buttons", list);
        }

        String signature() {
            StringBuilder b = new StringBuilder();
            b.append(details).append('\n').append(state).append('\n').append(largeImage).append('\n').append(largeText).append('\n').append(smallImage).append('\n').append(smallText).append('\n').append(start).append('\n').append(detailsStatus);
            for (String[] button : buttons) b.append('\n').append(button[0]).append(' ').append(button[1]);
            return b.toString();
        }
    }

    static Presence presence(String op, JSONObject args) {
        try {
            return Presence.from(Core.run(op, args));
        } catch (IOException e) {
            return new Presence();
        }
    }

    public static String shownName(Store s, Game g) {
        try {
            return Core.text("presence.shown", Core.args("custom", s.setting(CUSTOM_NAME, ""), "name", g.name));
        } catch (IOException e) {
            return g.name;
        }
    }

    static JSONObject account(android.content.Context c, long user, boolean circular) {
        try {
            Object v = Core.value("presence.account", Core.feed(c, "user", user, "circular", circular));
            return v instanceof JSONObject ? (JSONObject) v : null;
        } catch (IOException e) {
            return null;
        }
    }

    public static Presence game(Store s, ActivityWatcher.Data d, Game g, JSONObject account, int flagCount) {
        JSONObject settings = Core.args("account", on(s, ACCOUNT), "customName", s.setting(CUSTOM_NAME, ""), "customIcon", s.setting(CUSTOM_ICON, ""), "beta", on(s, BETA), "name", on(s, NAME), "server", on(s, SERVER), "location", on(s, RPC_LOCATION), "creator", on(s, CREATOR), "flagCount", on(s, FLAG_COUNT), "icon", on(s, ICON), "joining", on(s, JOINING));
        JSONObject data = Core.args("placeId", d.placeId, "jobId", d.jobId, "serverType", d.serverType.name(), "joined", d.joined, "launchData", d.launchData);
        JSONObject game = Core.args("name", g.name, "creator", g.creator, "verified", g.verified, "icon", g.icon, "location", g.location);
        return presence("presence.game", Core.args("settings", settings, "data", data, "game", game, "account", account, "flags", flagCount));
    }

    public static Presence idle(Store s, long start, JSONObject account) {
        return presence("presence.idle", Core.args("icon", s.setting(IDLE_ICON, "blue"), "start", start, "settings", Core.args("account", on(s, ACCOUNT)), "account", account));
    }

    public static Presence applyRpc(Presence current, Presence original, String json) {
        try {
            Object v = Core.value("presence.rpc", Core.args("current", current.json(), "original", original.json(), "json", json));
            return v instanceof JSONObject ? Presence.from((JSONObject) v) : null;
        } catch (IOException e) {
            return null;
        }
    }

    public static String location(JSONObject info) {
        String city = info.optString("city", "").trim();
        String region = info.optString("region", "").trim();
        String country = info.optString("country", "").trim();
        if (country.isEmpty()) return "";
        try {
            String display = new Locale.Builder().setRegion(country).build().getDisplayCountry(Locale.ENGLISH);
            if (!display.isEmpty()) country = display;
        } catch (java.util.IllformedLocaleException ignored) {
        }
        if (city.isEmpty()) return country;
        if (region.isEmpty() || region.equalsIgnoreCase(city) || city.equalsIgnoreCase(country)) return city + ", " + country;
        return city + ", " + region + ", " + country;
    }
}
