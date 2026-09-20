package com.voidstrap.android;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Pattern;

public final class Integrations {
    public static final String TRACKING = "activityTracking";
    public static final String LOCATION = "activityLocation";
    public static final String NOTIFY = "activityNotify";
    public static final String RPC = "discordRpc";
    public static final String ACCOUNT = "discordAccount";
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

    public static final String[] IDLE_KEYS = {"blue", "black", "old"};
    public static final String[] IDLE_URLS = {
            "https://upload.wikimedia.org/wikipedia/commons/thumb/f/f2/Roblox_%282025%29_%28App_Icon%29.svg/250px-Roblox_%282025%29_%28App_Icon%29.svg.png",
            "https://devforum-uploads.s3.dualstack.us-east-2.amazonaws.com/uploads/original/5X/c/4/c/2/c4c28132e4907f73ea0430d18d769c06276e39cc.png",
            "https://static.wikia.nocookie.net/logopedia/images/b/b7/ROBLOX_2006-2009.svg/revision/latest/scale-to-width-down/1000?cb=20250403121056"
    };

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

    public static String idleIcon(Store s) {
        String key = s.setting(IDLE_ICON, "blue");
        for (int i = 0; i < IDLE_KEYS.length; i++) if (IDLE_KEYS[i].equals(key)) return IDLE_URLS[i];
        return IDLE_URLS[0];
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
        public String userImage = "";
        public String userText = "";
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

        String signature() {
            StringBuilder b = new StringBuilder();
            b.append(details).append('\n').append(state).append('\n').append(largeImage).append('\n').append(largeText).append('\n').append(smallImage).append('\n').append(smallText).append('\n').append(start).append('\n').append(detailsStatus);
            for (String[] button : buttons) b.append('\n').append(button[0]).append(' ').append(button[1]);
            return b.toString();
        }
    }

    public static String serverName(ActivityWatcher.Data d) {
        if (d.serverType == ActivityWatcher.ServerType.PRIVATE) return "Private Server";
        if (d.serverType == ActivityWatcher.ServerType.RESERVED) {
            String reserved = reservedName(d.launchData);
            return reserved.isEmpty() ? "Reserved Server" : "Reserved Server: " + reserved;
        }
        return "Public Server";
    }

    public static String shownName(Store s, Game g) {
        String custom = s.setting(CUSTOM_NAME, "").trim();
        if (!custom.isEmpty()) return custom;
        return g.name == null || g.name.trim().isEmpty() ? "Private experience" : g.name;
    }

    public static Presence game(Store s, ActivityWatcher.Data d, Game g, int flagCount) {
        String shown = shownName(s, g);
        String[] tagged = on(s, BETA) ? betaTag(shown) : new String[]{shown, null};
        List<String> details = new ArrayList<>();
        if (on(s, NAME)) details.add(tagged[0]);
        if (on(s, SERVER)) details.add(serverName(d));
        if (on(s, RPC_LOCATION) && !g.location.isEmpty()) details.add(g.location);
        String detailText = String.join(" | ", details);
        if (tagged[1] != null) detailText = detailText.isEmpty() ? tagged[1] : detailText + " " + tagged[1];
        String state = on(s, CREATOR) && !g.creator.isEmpty() ? "by " + g.creator + (g.verified ? " \u2611\ufe0f" : "") : "";
        if (on(s, FLAG_COUNT)) state = state.isEmpty() ? "FFlags: " + flagCount : state + " | FFlags: " + flagCount;
        String customIcon = s.setting(CUSTOM_ICON, "").trim();
        Presence p = new Presence();
        p.details = detailText;
        p.state = state;
        p.detailsStatus = !detailText.trim().isEmpty();
        p.start = d.joined;
        p.largeImage = !customIcon.isEmpty() ? customIcon : on(s, ICON) ? g.icon : "";
        p.largeText = customIcon.isEmpty() && on(s, ICON) ? shown : "";
        smallImage(s, g, p);
        if (on(s, JOINING) && (d.serverType == ActivityWatcher.ServerType.PUBLIC || (d.serverType == ActivityWatcher.ServerType.RESERVED && !d.launchData.isEmpty())) && !d.jobId.isEmpty()) {
            p.buttons.add(new String[]{"Join server", d.inviteLink()});
        }
        p.buttons.add(new String[]{"Game Page", "https://www.roblox.com/games/" + d.placeId});
        return p;
    }

    public static Presence idle(Store s, Game user, long start) {
        Presence p = new Presence();
        p.details = "Inside Voidstrap";
        p.state = "Browsing Roblox";
        p.largeImage = idleIcon(s);
        p.largeText = "Roblox";
        p.start = start;
        p.detailsStatus = true;
        smallImage(s, user, p);
        return p;
    }

    private static void smallImage(Store s, Game g, Presence p) {
        boolean account = on(s, ACCOUNT) && g != null && !g.userImage.isEmpty();
        p.smallImage = account ? g.userImage : "voidstrap";
        p.smallText = account ? g.userText : "Voidstrap";
    }

    public static Presence applyRpc(Presence current, Presence original, JSONObject data) {
        Presence p = current.copy();
        p.details = field(p.details, data.optString("details", ""), original.details);
        p.state = field(p.state, data.optString("state", ""), original.state);
        image(p, original, data.optJSONObject("smallImage"), true);
        image(p, original, data.optJSONObject("largeImage"), false);
        return p;
    }

    private static String field(String current, String value, String original) {
        if (value.isEmpty()) return current;
        if (value.equals("<reset>")) return original;
        if (value.length() > 128) return current;
        return value;
    }

    private static void image(Presence p, Presence original, JSONObject data, boolean small) {
        if (data == null) return;
        if (data.optBoolean("clear")) {
            if (small) p.smallImage = "";
            else p.largeImage = "";
            return;
        }
        if (data.optBoolean("reset")) {
            if (small) {
                p.smallImage = original.smallImage;
                p.smallText = original.smallText;
            } else {
                p.largeImage = original.largeImage;
                p.largeText = original.largeText;
            }
            return;
        }
        long asset = data.optLong("assetId", 0);
        if (asset > 0) {
            String url = "https://assetdelivery.roblox.com/v1/asset/?id=" + asset;
            if (small) p.smallImage = url;
            else p.largeImage = url;
        }
        String hover = data.optString("hoverText", "");
        if (!hover.isEmpty()) {
            if (small) p.smallText = hover;
            else p.largeText = hover;
        }
    }

    private static final String[][] WIP_MARKERS = {
            {"PRE[\\s-]?ALPHA", "[PRE-ALPHA]"},
            {"EARLY\\s+ACCESS", "[EARLY ACCESS]"},
            {"OPEN\\s+BETA", "[OPEN BETA]"},
            {"CLOSED\\s+BETA", "[CLOSED BETA]"},
            {"IN\\s+WORKS", "[IN WORKS]"},
            {"IN[\\s-]?DEV(?:ELOPMENT)?", "[IN DEV]"},
            {"UNDER\\s+CONSTRUCTION", "[WIP]"},
            {"WORK\\s+IN\\s+PROGRESS", "[WIP]"},
            {"BETA", "[BETA]"},
            {"ALPHA", "[ALPHA]"},
            {"TESTING", "[TESTING]"},
            {"PREVIEW", "[PREVIEW]"},
            {"PROTOTYPE", "[PROTOTYPE]"},
            {"EXPERIMENTAL", "[EXPERIMENTAL]"},
            {"UNRELEASED", "[UNRELEASED]"},
            {"SNAPSHOT", "[SNAPSHOT]"},
            {"DEMO", "[DEMO]"},
            {"WIP", "[WIP]"}
    };
    private static final Pattern[] WIP_PATTERNS = new Pattern[WIP_MARKERS.length];
    private static final Pattern EMPTY_BRACKETS = Pattern.compile("[\\[\\(\\{]\\s*[\\]\\)\\}]");
    private static final Pattern SPACES = Pattern.compile("\\s{2,}");
    private static final Pattern LEFTOVER = Pattern.compile("\\s*[-|:~/,]+\\s*$");

    static {
        for (int i = 0; i < WIP_MARKERS.length; i++) {
            String words = WIP_MARKERS[i][0];
            WIP_PATTERNS[i] = Pattern.compile("(?:[\\[\\(\\{]\\s*" + words + "\\s*[\\]\\)\\}])|(?:\\b" + words + "\\b\\s*$)", Pattern.CASE_INSENSITIVE);
        }
    }

    public static String[] betaTag(String name) {
        if (name == null || name.trim().isEmpty()) return new String[]{name == null ? "" : name, null};
        String working = name;
        String tag = null;
        for (int i = 0; i < WIP_PATTERNS.length; i++) {
            if (!WIP_PATTERNS[i].matcher(working).find()) continue;
            if (tag == null) tag = WIP_MARKERS[i][1];
            working = WIP_PATTERNS[i].matcher(working).replaceAll(" ");
        }
        if (tag == null) return new String[]{name, null};
        working = EMPTY_BRACKETS.matcher(working).replaceAll(" ");
        working = SPACES.matcher(working).replaceAll(" ").trim();
        working = LEFTOVER.matcher(working).replaceAll("").trim();
        return new String[]{working.isEmpty() ? name : working, tag};
    }

    static String reservedName(String launchData) {
        if (launchData == null || launchData.trim().isEmpty()) return "";
        try {
            Object root = new org.json.JSONTokener(launchData).nextValue();
            return findServerName(root);
        } catch (org.json.JSONException e) {
            return "";
        }
    }

    private static String findServerName(Object o) {
        if (o instanceof JSONObject) {
            JSONObject obj = (JSONObject) o;
            JSONArray names = obj.names();
            if (names == null) return "";
            for (int i = 0; i < names.length(); i++) {
                String key = names.optString(i);
                Object value = obj.opt(key);
                String lower = key.toLowerCase(Locale.ROOT);
                if ((lower.equals("servername") || lower.equals("reservedservername") || lower.equals("privateservername")) && value instanceof String) return (String) value;
                String nested = findServerName(value);
                if (!nested.isEmpty()) return nested;
            }
        } else if (o instanceof JSONArray) {
            JSONArray a = (JSONArray) o;
            for (int i = 0; i < a.length(); i++) {
                String nested = findServerName(a.opt(i));
                if (!nested.isEmpty()) return nested;
            }
        }
        return "";
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
