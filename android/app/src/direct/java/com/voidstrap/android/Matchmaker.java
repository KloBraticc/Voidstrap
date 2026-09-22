package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

final class Matchmaker {
    static final String TAG = "VoidstrapMatchmaker";
    static final String ENABLED = "mmEnabled";
    static final String PREFER_EMPTY = "mmPreferEmpty";
    static final String PREFERRED = "mmPreferred";
    static final String BLOCKED = "mmBlocked";
    static final String EXCLUDED = "mmExcluded";
    static final String AUTO = "mmAutoCandidates";
    static final String MAX = "mmMaxCandidates";
    static final String RETRIES = "mmMaxRetries";
    static final String API = "mmApi";
    private static final String GEO = "mmGeo";

    static final int MIN_CANDIDATES = 8;
    static final int MAX_CANDIDATES = 64;

    private static String builtin;

    static final class Datacenter {
        final String city;
        final String region;
        final String country;
        final double lat;
        final double lon;

        Datacenter(JSONObject o) {
            city = o.optString("city");
            region = o.optString("region");
            country = o.optString("country");
            lat = o.optDouble("lat");
            lon = o.optDouble("lon");
        }

        String key() {
            return city + "|" + country;
        }

        String display() {
            String c = displayCountry(country);
            return region.isEmpty() || region.equalsIgnoreCase(city) || city.equalsIgnoreCase(c) ? city + ", " + c : city + ", " + region + ", " + c;
        }

        JSONObject json() {
            return Core.args("city", city, "region", region, "country", country);
        }
    }

    static final class Geo {
        double lat;
        double lon;
        String city = "";
        String region = "";
        String country = "";

        static Geo from(JSONObject o) {
            if (o == null) return null;
            Geo g = new Geo();
            g.lat = o.optDouble("lat");
            g.lon = o.optDouble("lon");
            g.city = o.optString("city");
            g.region = o.optString("region");
            g.country = o.optString("country");
            return g;
        }
    }

    static final class Candidate {
        String jobId;
        String ip;
        int port;
        Datacenter dc;
        double km;
        int playing;
        int maxPlayers;
        int estimatedPing;
        double score;
        String blockedClosestCity;

        static Candidate from(JSONObject o) {
            if (o == null) return null;
            Candidate c = new Candidate();
            c.jobId = o.optString("jobId");
            c.ip = o.optString("ip");
            c.port = o.optInt("port");
            c.dc = new Datacenter(o.optJSONObject("dc"));
            c.km = o.optDouble("km");
            c.playing = o.optInt("playing");
            c.maxPlayers = o.optInt("maxPlayers");
            c.estimatedPing = o.optInt("estimatedPing");
            c.score = o.optDouble("score");
            c.blockedClosestCity = o.isNull("blockedClosestCity") ? null : o.optString("blockedClosestCity");
            return c;
        }
    }

    private Matchmaker() {
    }

    static String displayCountry(String country) {
        if (country.length() == 2) {
            try {
                String d = new Locale.Builder().setRegion(country).build().getDisplayCountry(Locale.ENGLISH);
                if (!d.isEmpty()) return d;
            } catch (java.util.IllformedLocaleException ignored) {
            }
        }
        return country;
    }

    static String lastReport() {
        try {
            String r = Core.text("mm.lastReport", Core.args());
            return r == null ? "" : r;
        } catch (IOException e) {
            return "";
        }
    }

    static int estimatePingMs(double km) {
        try {
            Object v = Core.value("mm.ping", Core.args("km", km));
            return v instanceof Number ? ((Number) v).intValue() : -1;
        } catch (IOException e) {
            return -1;
        }
    }

    static double haversineKm(double lat1, double lon1, double lat2, double lon2) {
        try {
            Object v = Core.value("mm.km", Core.args("lat1", lat1, "lon1", lon1, "lat2", lat2, "lon2", lon2));
            return v instanceof Number ? ((Number) v).doubleValue() : Double.NaN;
        } catch (IOException e) {
            return Double.NaN;
        }
    }

    static boolean matchesPreferred(Datacenter dc, String preferred) {
        if (dc == null || preferred == null) return false;
        return Core.flag("mm.matchesPreferred", Core.args("dc", dc.json(), "preferred", preferred));
    }

    static boolean enabled(Store s) {
        return "1".equals(s.setting(ENABLED, "0"));
    }

    static boolean preferEmpty(Store s) {
        return "1".equals(s.setting(PREFER_EMPTY, "0"));
    }

    static String preferred(Store s) {
        return s.setting(PREFERRED, "").trim();
    }

    static Set<String> blocked(Store s) {
        return stringSet(s.setting(BLOCKED, "[]"));
    }

    static void setBlocked(Store s, Set<String> keys) {
        s.putSetting(BLOCKED, new JSONArray(keys).toString());
    }

    static Set<Long> excluded(Store s) {
        Set<Long> out = new HashSet<>();
        try {
            JSONArray a = new JSONArray(s.setting(EXCLUDED, "[]"));
            for (int i = 0; i < a.length(); i++) if (a.optLong(i) > 0) out.add(a.optLong(i));
        } catch (JSONException ignored) {
        }
        return out;
    }

    static void setExcluded(Store s, long placeId, boolean on) {
        Set<Long> set = excluded(s);
        if (on) set.add(placeId);
        else set.remove(placeId);
        s.putSetting(EXCLUDED, new JSONArray(set).toString());
    }

    static int candidateCount(Store s) {
        if (!"0".equals(s.setting(AUTO, "1"))) return Math.max(40, Math.min(MAX_CANDIDATES, 40 + blocked(s).size() * 4));
        return manualCandidates(s);
    }

    static int manualCandidates(Store s) {
        try {
            return Math.max(MIN_CANDIDATES, Math.min(MAX_CANDIDATES, Integer.parseInt(s.setting(MAX, "14"))));
        } catch (NumberFormatException e) {
            return 14;
        }
    }

    static int maxRetries(Store s) {
        try {
            return Math.max(1, Math.min(20, Integer.parseInt(s.setting(RETRIES, "3"))));
        } catch (NumberFormatException e) {
            return 3;
        }
    }

    private static Set<String> stringSet(String json) {
        Set<String> out = new HashSet<>();
        try {
            JSONArray a = new JSONArray(json);
            for (int i = 0; i < a.length(); i++) {
                String v = a.optString(i, "").trim();
                if (!v.isEmpty()) out.add(v);
            }
        } catch (JSONException ignored) {
        }
        return out;
    }

    private static synchronized String builtin(Context c) {
        if (builtin != null) return builtin;
        try (InputStream in = c.getResources().openRawResource(R.raw.roblox_datacenters)) {
            builtin = new String(ModEngine.readAll(in), StandardCharsets.UTF_8);
        } catch (IOException e) {
            builtin = "";
        }
        return builtin;
    }

    static JSONObject env(Context c, Object... kv) {
        JSONObject base = Core.args("builtin", builtin(c), "cache", c.getCacheDir(), "files", c.getFilesDir(),
                "agent", "Voidstrap Android/" + BuildConfig.VERSION_NAME, "savedGeo", Store.get(c).setting(GEO, "{}"));
        return Core.merge(base, Core.args(kv));
    }

    static JSONObject settings(Context c, String preferredOverride) {
        Store s = Store.get(c);
        String cookie = RobloxLogin.cookie(c);
        return Core.args("preferred", preferredOverride != null ? preferredOverride.trim() : preferred(s), "blocked", new JSONArray(blocked(s)),
                "budget", candidateCount(s), "preferEmpty", preferEmpty(s), "v2", "2".equals(s.setting(API, "1")), "cookie", cookie == null ? "" : cookie);
    }

    static void settle(Context c, JSONObject result) {
        JSONObject save = result.optJSONObject("save");
        if (save != null) Store.get(c).putSetting(GEO, save.toString());
        if (result.optBoolean("rejected")) RobloxLogin.reject();
    }

    static List<Datacenter> datacenters(Context c) {
        List<Datacenter> out = new ArrayList<>();
        try {
            Object v = Core.value("mm.datacenters", env(c));
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            for (int i = 0; i < a.length(); i++) out.add(new Datacenter(a.optJSONObject(i)));
        } catch (IOException ignored) {
        }
        return out;
    }

    static Geo geo(Context c) {
        try {
            JSONObject r = Core.run("mm.geo", env(c));
            settle(c, r);
            return Geo.from(r.optJSONObject("geo"));
        } catch (IOException e) {
            return null;
        }
    }

    static Datacenter lookup(Context c, String ip) {
        try {
            Object v = Core.value("mm.lookup", env(c, "ip", ip));
            return v instanceof JSONObject ? new Datacenter((JSONObject) v) : null;
        } catch (IOException e) {
            return null;
        }
    }

    static Candidate pick(Context c, long placeId, Set<String> exclude, String preferredOverride) {
        try {
            JSONObject r = Core.run("mm.pick", Core.merge(env(c, "place", placeId, "exclude", exclude == null ? new JSONArray() : new JSONArray(exclude)), settings(c, preferredOverride)));
            settle(c, r);
            return Candidate.from(r.optJSONObject("candidate"));
        } catch (IOException e) {
            return null;
        }
    }
}
