package com.voidstrap.android;

import android.content.Context;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

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
    private static final String REMOTE_URL = "https://raw.githubusercontent.com/KloBraticc/Voidstrap-Resources/main/ServerLocations.json";

    static final int MIN_CANDIDATES = 8;
    static final int MAX_CANDIDATES = 64;
    private static final int FILTERED_CEILING = 80;
    private static final int PROBE_CONCURRENCY = 8;
    private static final int JOIN_TIMEOUT_MS = 2500;
    private static final int MAX_JOIN_BYTES = 1024 * 1024;
    private static final int MAX_PAGES = 5;
    private static final double EMPTY_PREFERENCE_MS = 60.0;
    private static final double FULLNESS_TIEBREAK_MS = 8.0;
    private static final double CLOSEST_BAND_MS = 12.0;
    private static final double HANDOFF_PING_MS = 120.0;
    private static final double HANDOFF_FLOOR_MULTIPLIER = 4.0;
    private static final int EARLY_EXIT_MIN_RESULTS = 12;
    private static final int EARLY_EXIT_CLOSEST = 6;
    private static final long DEADLINE_MS = 25_000;
    private static final long GEO_TTL_MS = 6 * 3600_000L;
    private static final int IP_LOOKUP_TIMEOUT_MS = 4000;
    private static final int IP_CACHE_LIMIT = 1024;
    private static final long FAIL_COOLDOWN_MS = 10 * 60_000L;
    private static final long RESOLVED_TTL_MS = 2 * 60_000L;

    static final class Datacenter {
        final String city;
        final String region;
        final String country;
        final double lat;
        final double lon;

        Datacenter(String city, String region, String country, double lat, double lon) {
            this.city = city;
            this.region = region;
            this.country = normalizeCountry(country);
            this.lat = lat;
            this.lon = lon;
        }

        String key() {
            return city + "|" + country;
        }

        String display() {
            String c = displayCountry(country);
            return region.isEmpty() || region.equalsIgnoreCase(city) || city.equalsIgnoreCase(c) ? city + ", " + c : city + ", " + region + ", " + c;
        }
    }

    static final class Geo {
        double lat;
        double lon;
        String city = "";
        String region = "";
        String country = "";
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
    }

    private static final class Server {
        String jobId;
        int playing;
        int maxPlayers;
    }

    private static final class Cidr {
        final long network;
        final long mask;
        final String cidr;
        final Datacenter dc;

        Cidr(long network, long mask, String cidr, Datacenter dc) {
            this.network = network;
            this.mask = mask;
            this.cidr = cidr;
            this.dc = dc;
        }
    }

    private static final Map<String, String> COUNTRIES = new HashMap<>();

    static {
        String[][] pairs = {
                {"US", "USA"}, {"USA", "USA"}, {"United States", "USA"}, {"United States of America", "USA"},
                {"GB", "UK"}, {"UK", "UK"}, {"United Kingdom", "UK"}, {"Great Britain", "UK"}, {"England", "UK"},
                {"NL", "Netherlands"}, {"Netherlands", "Netherlands"}, {"Holland", "Netherlands"},
                {"FR", "France"}, {"France", "France"}, {"DE", "Germany"}, {"Germany", "Germany"},
                {"PL", "Poland"}, {"Poland", "Poland"}, {"IN", "India"}, {"India", "India"},
                {"JP", "Japan"}, {"Japan", "Japan"}, {"SG", "Singapore"}, {"Singapore", "Singapore"},
                {"AU", "Australia"}, {"Australia", "Australia"}, {"CN", "China"}, {"China", "China"}, {"HK", "China"}, {"Hong Kong", "China"},
                {"CA", "Canada"}, {"Canada", "Canada"}, {"BR", "Brazil"}, {"Brazil", "Brazil"},
                {"KR", "South Korea"}, {"South Korea", "South Korea"}, {"Korea", "South Korea"},
                {"TW", "Taiwan"}, {"Taiwan", "Taiwan"}, {"ZA", "South Africa"}, {"South Africa", "South Africa"},
                {"AE", "UAE"}, {"United Arab Emirates", "UAE"}, {"RU", "Russia"}, {"Russia", "Russia"},
                {"MX", "Mexico"}, {"Mexico", "Mexico"}, {"CL", "Chile"}, {"Chile", "Chile"}, {"AR", "Argentina"}, {"Argentina", "Argentina"}
        };
        for (String[] p : pairs) COUNTRIES.put(p[0].toLowerCase(Locale.ROOT), p[1]);
    }

    private static final List<Cidr> CIDRS = new ArrayList<>();
    private static boolean loaded;
    private static final ConcurrentHashMap<String, Datacenter> IP_CACHE = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, Long> IP_FAILED = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, Boolean> IP_INFLIGHT = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, Object[]> RESOLVED = new ConcurrentHashMap<>();
    private static volatile String csrf;
    private static volatile long backoffUntil;
    private static Geo cachedGeo;
    private static long cachedGeoAt;
    private static volatile String lastReport = "";

    private Matchmaker() {
    }

    static String normalizeCountry(String country) {
        if (country == null) return "";
        String mapped = COUNTRIES.get(country.trim().toLowerCase(Locale.ROOT));
        return mapped == null ? country.trim() : mapped;
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
        return lastReport;
    }

    private static void report(String s) {
        lastReport = s;
        Log.i(TAG, s);
    }

    static int estimatePingMs(double km) {
        if (Double.isNaN(km) || km < 0) return -1;
        return (int) Math.max(1, Math.min(999, Math.round(5.0 + km / 75.0)));
    }

    private static double estimateRtt(double km) {
        if (Double.isNaN(km) || km < 0) return 999.0;
        return 5.0 + km / 75.0;
    }

    static double haversineKm(double lat1, double lon1, double lat2, double lon2) {
        double dLat = Math.toRadians(lat2 - lat1);
        double dLon = Math.toRadians(lon2 - lon1);
        double a = Math.sin(dLat / 2) * Math.sin(dLat / 2) + Math.cos(Math.toRadians(lat1)) * Math.cos(Math.toRadians(lat2)) * Math.sin(dLon / 2) * Math.sin(dLon / 2);
        return 6371.0 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    static boolean matchesPreferred(Datacenter dc, String preferred) {
        if (dc == null || preferred == null || preferred.trim().isEmpty()) return false;
        int sep = preferred.indexOf('|');
        String city = sep < 0 ? preferred : preferred.substring(0, sep);
        String country = sep < 0 ? "" : preferred.substring(sep + 1);
        if (!dc.city.equalsIgnoreCase(city)) return false;
        if (country.isEmpty() || dc.country.isEmpty()) return true;
        return normalizeCountry(country).equalsIgnoreCase(dc.country);
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

    private static boolean useV2(Store s) {
        return "2".equals(s.setting(API, "1"));
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

    private static synchronized void ensureLoaded(Context c) {
        if (loaded) return;
        loaded = true;
        try (InputStream in = c.getResources().openRawResource(R.raw.roblox_datacenters)) {
            addEntries(new JSONArray(new String(readAll(in, 1024 * 1024), StandardCharsets.UTF_8)), false);
        } catch (IOException | JSONException e) {
            Log.w(TAG, "Built in datacenter list could not be read");
        }
        try {
            JSONObject remote = Net.cachedJson(c, REMOTE_URL, 24 * 3600_000L);
            JSONObject servers = remote.optJSONObject("Servers");
            if (servers != null) {
                JSONArray list = new JSONArray();
                JSONArray names = servers.names();
                if (names != null) for (int i = 0; i < names.length(); i++) {
                    JSONObject e = servers.optJSONObject(names.optString(i));
                    if (e == null) continue;
                    list.put(new JSONObject()
                            .put("cidr", e.optString("Cidr", names.optString(i)))
                            .put("city", e.optString("City", ""))
                            .put("region", e.optString("Region", ""))
                            .put("country", e.optString("Country", ""))
                            .put("lat", e.optDouble("Lat", 0))
                            .put("lon", e.optDouble("Lon", 0)));
                }
                addEntries(list, false);
            }
        } catch (IOException | JSONException e) {
            Log.w(TAG, "Server locations list could not be downloaded, using the built in list");
        }
        try {
            File f = learnedFile(c);
            if (f.isFile()) addEntries(new JSONArray(new String(java.nio.file.Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8)), false);
        } catch (IOException | JSONException ignored) {
        }
    }

    private static File learnedFile(Context c) {
        return new File(c.getFilesDir(), "matchmaker_learned.json");
    }

    private static synchronized boolean addEntries(JSONArray a, boolean replace) {
        boolean added = false;
        for (int i = 0; i < a.length(); i++) {
            JSONObject e = a.optJSONObject(i);
            if (e == null) continue;
            String cidr = e.optString("cidr", "").trim();
            String city = e.optString("city", "").trim();
            double lat = e.optDouble("lat", 0);
            double lon = e.optDouble("lon", 0);
            if (cidr.isEmpty() || city.isEmpty() || (lat == 0 && lon == 0) || Math.abs(lat) > 90 || Math.abs(lon) > 180) continue;
            String[] parts = cidr.split("/");
            if (parts.length != 2) continue;
            long ip = ipToLong(parts[0]);
            int prefix;
            try {
                prefix = Integer.parseInt(parts[1]);
            } catch (NumberFormatException ex) {
                continue;
            }
            if (ip < 0 || prefix < 0 || prefix > 32) continue;
            boolean exists = false;
            for (Cidr x : CIDRS) if (x.cidr.equalsIgnoreCase(cidr)) exists = true;
            if (exists && !replace) continue;
            long mask = prefix == 0 ? 0 : (0xFFFFFFFFL << (32 - prefix)) & 0xFFFFFFFFL;
            CIDRS.add(new Cidr(ip & mask, mask, cidr, new Datacenter(city, e.optString("region", ""), e.optString("country", ""), lat, lon)));
            added = true;
        }
        return added;
    }

    static long ipToLong(String ip) {
        String[] p = ip == null ? new String[0] : ip.trim().split("\\.");
        if (p.length != 4) return -1;
        long v = 0;
        for (String s : p) {
            int b;
            try {
                b = Integer.parseInt(s);
            } catch (NumberFormatException e) {
                return -1;
            }
            if (b < 0 || b > 255) return -1;
            v = (v << 8) | b;
        }
        return v;
    }

    static boolean isPrivate(String ip) {
        long v = ipToLong(ip);
        if (v < 0) return true;
        int a = (int) (v >> 24);
        int b = (int) ((v >> 16) & 0xFF);
        return a == 0 || a == 10 || a == 127 || (a == 100 && b >= 64 && b <= 127) || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168) || (a == 169 && b == 254) || a >= 224;
    }

    static Datacenter map(Context c, String ip) {
        ensureLoaded(c);
        long v = ipToLong(ip);
        if (v < 0) return null;
        synchronized (Matchmaker.class) {
            for (Cidr x : CIDRS) if ((v & x.mask) == x.network) return x.dc;
        }
        return null;
    }

    static List<Datacenter> datacenters(Context c) {
        ensureLoaded(c);
        LinkedHashMap<String, Datacenter> out = new LinkedHashMap<>();
        synchronized (Matchmaker.class) {
            for (Cidr x : CIDRS) if (!out.containsKey(x.dc.key())) out.put(x.dc.key(), x.dc);
        }
        return new ArrayList<>(out.values());
    }

    static double nearestKm(Context c, Geo geo) {
        double best = Double.POSITIVE_INFINITY;
        for (Datacenter dc : datacenters(c)) best = Math.min(best, haversineKm(geo.lat, geo.lon, dc.lat, dc.lon));
        return Double.isInfinite(best) ? 0 : best;
    }

    static Geo cachedGeo(Context c) {
        synchronized (Matchmaker.class) {
            if (cachedGeo != null) return cachedGeo;
        }
        return loadSavedGeo(Store.get(c));
    }

    static Geo geo(Context c) {
        synchronized (Matchmaker.class) {
            if (cachedGeo != null && System.currentTimeMillis() - cachedGeoAt < GEO_TTL_MS) return cachedGeo;
        }
        Geo g = firstGeo();
        Store s = Store.get(c);
        if (g == null) {
            g = loadSavedGeo(s);
            if (g == null) report("All location providers failed and no location was saved yet, cannot match by location");
            return g;
        }
        try {
            s.putSetting(GEO, new JSONObject().put("lat", g.lat).put("lon", g.lon).put("city", g.city).put("region", g.region).put("country", g.country).toString());
        } catch (JSONException ignored) {
        }
        synchronized (Matchmaker.class) {
            cachedGeo = g;
            cachedGeoAt = System.currentTimeMillis();
        }
        return g;
    }

    private static Geo loadSavedGeo(Store s) {
        try {
            JSONObject o = new JSONObject(s.setting(GEO, "{}"));
            if (!o.has("lat")) return null;
            Geo g = new Geo();
            g.lat = o.optDouble("lat");
            g.lon = o.optDouble("lon");
            g.city = o.optString("city", "");
            g.region = o.optString("region", "");
            g.country = o.optString("country", "");
            return valid(g.lat, g.lon) ? g : null;
        } catch (JSONException e) {
            return null;
        }
    }

    private static boolean valid(double lat, double lon) {
        return !Double.isNaN(lat) && !Double.isNaN(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180 && !(lat == 0 && lon == 0);
    }

    private static Geo firstGeo() {
        ExecutorService pool = Executors.newFixedThreadPool(3);
        try {
            java.util.concurrent.ExecutorCompletionService<Geo> race = new java.util.concurrent.ExecutorCompletionService<>(pool);
            race.submit(() -> geoFrom("https://ipinfo.io/json"));
            race.submit(() -> geoFrom("https://ipwho.is/"));
            race.submit(() -> geoFrom("https://ipapi.co/json/"));
            long end = System.currentTimeMillis() + 9000;
            for (int i = 0; i < 3; i++) {
                long left = end - System.currentTimeMillis();
                if (left <= 0) break;
                java.util.concurrent.Future<Geo> f = race.poll(left, TimeUnit.MILLISECONDS);
                if (f == null) break;
                try {
                    Geo g = f.get();
                    if (g != null && valid(g.lat, g.lon)) return g;
                } catch (java.util.concurrent.ExecutionException ignored) {
                }
            }
            return null;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return null;
        } finally {
            pool.shutdownNow();
        }
    }

    private static Geo geoFrom(String url) {
        return geoFrom(url, 8000);
    }

    private static Geo geoFrom(String url, int timeout) {
        try {
            JSONObject o = new JSONObject(get(url, null, timeout));
            Geo g = new Geo();
            if (o.has("loc")) {
                String[] p = o.optString("loc").split(",");
                if (p.length != 2) return null;
                g.lat = Double.parseDouble(p[0]);
                g.lon = Double.parseDouble(p[1]);
                g.country = o.optString("country", "");
            } else {
                if (o.has("success") && !o.optBoolean("success")) return null;
                if (o.optBoolean("error")) return null;
                if (!o.has("latitude") || !o.has("longitude")) return null;
                g.lat = o.optDouble("latitude");
                g.lon = o.optDouble("longitude");
                g.country = o.has("country_code") ? o.optString("country_code", "") : o.optString("country", "");
            }
            g.city = o.optString("city", "");
            g.region = o.optString("region", "");
            return g;
        } catch (IOException | JSONException | NumberFormatException e) {
            Log.w(TAG, "Location provider failed: " + url);
            return null;
        }
    }

    static Datacenter lookup(Context c, String ip) {
        return lookup(c, ip, System.currentTimeMillis() + 3L * IP_LOOKUP_TIMEOUT_MS);
    }

    static String subnet(String ip) {
        long v = ipToLong(ip);
        if (v < 0) return null;
        return ((v >> 24) & 0xFF) + "." + ((v >> 16) & 0xFF) + "." + ((v >> 8) & 0xFF) + ".0/24";
    }

    private static <V> void trim(Map<String, V> map) {
        if (map.size() > IP_CACHE_LIMIT) map.clear();
    }

    static Datacenter lookup(Context c, String ip, long deadline) {
        if (ip == null || ip.isEmpty() || isPrivate(ip)) return null;
        Datacenter mapped = map(c, ip);
        if (mapped != null) return mapped;
        String net = subnet(ip);
        if (net == null) return null;
        Datacenter cached = IP_CACHE.get(net);
        if (cached != null) return cached;
        Long failed = IP_FAILED.get(net);
        if (failed != null && System.currentTimeMillis() - failed < FAIL_COOLDOWN_MS) return null;
        if (IP_INFLIGHT.putIfAbsent(net, Boolean.TRUE) != null) return null;
        try {
            Geo g = null;
            for (String url : new String[]{"https://ipinfo.io/" + ip + "/json", "https://ipwho.is/" + ip, "https://ipapi.co/" + ip + "/json/"}) {
                int timeout = (int) Math.min(IP_LOOKUP_TIMEOUT_MS, deadline - System.currentTimeMillis());
                if (timeout < 500) break;
                Geo found = geoFrom(url, timeout);
                if (found != null && valid(found.lat, found.lon)) {
                    g = found;
                    break;
                }
            }
            if (g == null) {
                trim(IP_FAILED);
                IP_FAILED.put(net, System.currentTimeMillis());
                return null;
            }
            Datacenter dc = new Datacenter(g.city, g.region, g.country, g.lat, g.lon);
            trim(IP_CACHE);
            IP_CACHE.put(net, dc);
            learn(c, ip, dc);
            report("Learned a new datacenter at " + net + ": " + dc.city + ", " + dc.country);
            return dc;
        } finally {
            IP_INFLIGHT.remove(net);
        }
    }

    private static synchronized void learn(Context c, String ip, Datacenter dc) {
        if (dc.city.isEmpty()) return;
        String cidr = subnet(ip);
        if (cidr == null) return;
        try {
            JSONObject e = new JSONObject().put("cidr", cidr).put("city", dc.city).put("region", dc.region).put("country", dc.country).put("lat", dc.lat).put("lon", dc.lon);
            JSONArray one = new JSONArray().put(e);
            if (!addEntries(one, false)) return;
            File f = learnedFile(c);
            JSONArray all = f.isFile() ? new JSONArray(new String(java.nio.file.Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8)) : new JSONArray();
            all.put(e);
            while (all.length() > 512) all.remove(0);
            java.nio.file.Files.write(f.toPath(), all.toString().getBytes(StandardCharsets.UTF_8));
        } catch (IOException | JSONException ignored) {
        }
    }

    static Candidate pick(Context c, long placeId, Set<String> exclude, String preferredOverride) {
        long deadline = System.currentTimeMillis() + DEADLINE_MS;
        long started = System.currentTimeMillis();
        Store s = Store.get(c);
        String cookie = RobloxLogin.cookie(c);
        if (cookie == null) {
            report("No Roblox login found, sign in to use the matchmaker");
            return null;
        }
        String preferred = preferredOverride != null ? preferredOverride.trim() : preferred(s);
        Set<String> blocked = blocked(s);
        boolean filtering = !preferred.isEmpty() || !blocked.isEmpty();
        int budget = Math.max(MIN_CANDIDATES, Math.min(MAX_CANDIDATES, candidateCount(s)));
        if (filtering) budget = Math.min(FILTERED_CEILING, budget * 2);
        boolean preferEmpty = preferEmpty(s);
        boolean v2 = useV2(s);

        ExecutorService pre = Executors.newFixedThreadPool(3);
        java.util.concurrent.Future<Geo> geoTask = pre.submit(() -> geo(c));
        java.util.concurrent.Future<List<Server>> poolTask = pre.submit(() -> listServers(placeId, cookie, preferEmpty, deadline));
        pre.submit(() -> primeCsrf(placeId, cookie, v2));
        Geo geo;
        List<Server> pool;
        try {
            geo = geoTask.get(Math.max(1, deadline - System.currentTimeMillis()), TimeUnit.MILLISECONDS);
            pool = poolTask.get(Math.max(1, deadline - System.currentTimeMillis()), TimeUnit.MILLISECONDS);
        } catch (Exception e) {
            report("Initial server search failed or timed out");
            return null;
        } finally {
            pre.shutdown();
        }
        if (geo == null) {
            report("Cannot match without your location");
            return null;
        }
        ensureLoaded(c);
        List<Server> filtered = new ArrayList<>();
        for (Server sv : pool) if (exclude == null || !exclude.contains(sv.jobId)) filtered.add(sv);
        if (filtered.isEmpty()) {
            report("No untried public servers available for this place");
            return null;
        }
        List<Server> probeList = filtered.size() > budget ? stratify(filtered, budget) : filtered;
        long listMs = System.currentTimeMillis() - started;
        Log.i(TAG, "Server list ready in " + listMs + "ms, probing " + probeList.size() + " of " + filtered.size() + " servers for place " + placeId);
        double floorMs = estimateRtt(nearestKm(c, geo));
        List<Candidate> probed = probe(c, placeId, probeList, cookie, geo, preferred, blocked, preferEmpty, floorMs, v2, deadline);
        if (probed.isEmpty()) {
            report("No probed server could be resolved to a datacenter");
            return null;
        }
        Log.i(TAG, "Probed " + probed.size() + " of " + probeList.size() + " servers, " + (System.currentTimeMillis() - started) + "ms total");
        Candidate closestOverall = Collections.min(probed, Comparator.comparingDouble(x -> x.km));
        List<Candidate> allowed = new ArrayList<>();
        for (Candidate x : probed) if (!blocked.contains(x.dc.key())) allowed.add(x);
        if (allowed.isEmpty()) {
            report("Every probed server was in a blocked datacenter, nothing to pick");
            return null;
        }
        String blockedClosest = null;
        if (blocked.contains(closestOverall.dc.key())) blockedClosest = closestOverall.dc.city;
        if (!preferred.isEmpty()) {
            List<Candidate> inPreferred = new ArrayList<>();
            for (Candidate x : allowed) if (matchesPreferred(x.dc, preferred)) inPreferred.add(x);
            if (!inPreferred.isEmpty()) allowed = inPreferred;
        } else {
            double closest = Double.MAX_VALUE;
            for (Candidate x : allowed) closest = Math.min(closest, x.estimatedPing);
            List<Candidate> close = new ArrayList<>();
            for (Candidate x : allowed) if (x.estimatedPing <= closest + CLOSEST_BAND_MS) close.add(x);
            if (!close.isEmpty()) allowed = close;
            if (!preferEmpty) {
                List<Candidate> active = new ArrayList<>();
                for (Candidate x : allowed) if (x.playing >= 4) active.add(x);
                if (!active.isEmpty()) allowed = active;
                List<Candidate> headroom = new ArrayList<>();
                for (Candidate x : allowed) if (safeHeadroom(x)) headroom.add(x);
                if (!headroom.isEmpty()) allowed = headroom;
            }
        }
        Candidate winner = Collections.min(allowed, Comparator.comparingDouble(x -> x.score));
        winner.blockedClosestCity = blockedClosest;
        String players = winner.maxPlayers > 0 ? winner.playing + "/" + winner.maxPlayers + " players" : "player count unknown";
        boolean winnerPreferred = !preferred.isEmpty() && matchesPreferred(winner.dc, preferred);
        if (!winnerPreferred && winner.estimatedPing > HANDOFF_PING_MS && winner.estimatedPing > floorMs * HANDOFF_FLOOR_MULTIPLIER) {
            report("Every server found is far away, the best is about " + winner.estimatedPing + "ms, letting Roblox pick a fresh nearby server");
            return null;
        }
        report("Picked " + winner.dc.city + ", about " + winner.estimatedPing + "ms, " + players);
        return winner;
    }

    private static boolean safeHeadroom(Candidate x) {
        if (x.maxPlayers <= 0) return true;
        return x.maxPlayers - x.playing >= Math.max(2, (int) Math.ceil(x.maxPlayers * 0.08));
    }

    private static List<Server> stratify(List<Server> items, int target) {
        List<Server> picked = new ArrayList<>(target);
        double step = (double) items.size() / target;
        for (int i = 0; i < target; i++) picked.add(items.get(Math.min(items.size() - 1, (int) Math.floor(i * step))));
        return picked;
    }

    private static double populationPenalty(int playing, int max, boolean preferEmpty) {
        double fullness = max > 0 ? Math.max(0, Math.min(1, (double) playing / max)) : 0.5;
        if (preferEmpty) return fullness * EMPTY_PREFERENCE_MS + headroomPenalty(playing, max);
        double sparse;
        if (playing <= 0) sparse = 100;
        else if (playing == 1) sparse = 80;
        else if (playing == 2) sparse = 60;
        else if (playing == 3) sparse = 45;
        else sparse = fullness < 0.15 ? (0.15 - fullness) * 80.0 : 0;
        double crowded = fullness > 0.85 ? (fullness - 0.85) * 120.0 : 0;
        return sparse + crowded + Math.abs(fullness - 0.65) * FULLNESS_TIEBREAK_MS + headroomPenalty(playing, max);
    }

    private static double headroomPenalty(int playing, int max) {
        if (max <= 0) return 0;
        int open = max - playing;
        if (open <= 1) return 120;
        if (open == 2) return 35;
        return 0;
    }

    private static List<Candidate> probe(Context c, long placeId, List<Server> servers, String cookie, Geo geo, String preferred, Set<String> blocked, boolean preferEmpty, double floorMs, boolean v2, long deadline) {
        List<Candidate> results = Collections.synchronizedList(new ArrayList<>());
        java.util.concurrent.ConcurrentLinkedQueue<Server> queue = new java.util.concurrent.ConcurrentLinkedQueue<>(servers);
        AtomicBoolean stop = new AtomicBoolean();
        AtomicInteger goodEnough = new AtomicInteger();
        int workers = Math.min(PROBE_CONCURRENCY, Math.max(1, servers.size()));
        CountDownLatch done = new CountDownLatch(workers);
        ExecutorService pool = Executors.newFixedThreadPool(workers);
        for (int w = 0; w < workers; w++) {
            pool.execute(() -> {
                try {
                    Server sv;
                    while (!stop.get() && System.currentTimeMillis() < deadline && (sv = queue.poll()) != null) {
                        Object[] resolved = resolve(placeId, sv.jobId, cookie, v2, deadline);
                        if (resolved == null) continue;
                        String ip = (String) resolved[0];
                        Datacenter dc = lookup(c, ip, deadline);
                        if (dc == null) continue;
                        double km = haversineKm(geo.lat, geo.lon, dc.lat, dc.lon);
                        double ping = estimateRtt(km);
                        Candidate cand = new Candidate();
                        cand.jobId = sv.jobId;
                        cand.ip = ip;
                        cand.port = (Integer) resolved[1];
                        cand.dc = dc;
                        cand.km = km;
                        cand.playing = sv.playing;
                        cand.maxPlayers = sv.maxPlayers;
                        cand.estimatedPing = (int) Math.max(1, Math.min(999, Math.round(ping)));
                        cand.score = ping + populationPenalty(sv.playing, sv.maxPlayers, preferEmpty);
                        results.add(cand);
                        boolean usable = !blocked.contains(dc.key());
                        boolean onTarget = !preferred.isEmpty() ? matchesPreferred(dc, preferred) : ping <= floorMs + CLOSEST_BAND_MS;
                        boolean populated = preferEmpty || sv.playing >= 4 || (sv.maxPlayers > 0 && sv.playing >= Math.ceil(sv.maxPlayers * 0.15));
                        int required = !preferred.isEmpty() ? 3 : EARLY_EXIT_CLOSEST;
                        if (usable && onTarget && populated && goodEnough.incrementAndGet() >= required && results.size() >= EARLY_EXIT_MIN_RESULTS) {
                            stop.set(true);
                        }
                    }
                } finally {
                    done.countDown();
                }
            });
        }
        try {
            done.await(Math.max(1, deadline - System.currentTimeMillis()), TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        stop.set(true);
        pool.shutdownNow();
        synchronized (results) {
            return new ArrayList<>(results);
        }
    }

    private static List<Server> listServers(long placeId, String cookie, boolean preferEmpty, long deadline) {
        List<Server> items = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        String cursor = null;
        int[] backoff = {0, 750, 2000};
        for (int page = 0; page < MAX_PAGES; page++) {
            String url = "https://games.roblox.com/v1/games/" + placeId + "/servers/Public?excludeFullGames=true&limit=100&sortOrder=" + (preferEmpty ? "Asc" : "Desc");
            if (cursor != null) url += "&cursor=" + enc(cursor);
            String next = null;
            boolean ok = false;
            for (int attempt = 0; attempt < backoff.length && !ok; attempt++) {
                if (System.currentTimeMillis() > deadline) return sort(items, preferEmpty);
                if (backoff[attempt] > 0) sleep(backoff[attempt]);
                Response r = request("GET", url, cookie, null, null, "Voidstrap/1.0", 10000);
                if (r == null) {
                    if (attempt == backoff.length - 1) return sort(items, preferEmpty);
                    continue;
                }
                if (r.code == 429) {
                    if (attempt == backoff.length - 1) return sort(items, preferEmpty);
                    continue;
                }
                if (r.code != 200) {
                    Log.i(TAG, "Server list page " + (page + 1) + " returned HTTP " + r.code);
                    return sort(items, preferEmpty);
                }
                try {
                    JSONObject o = new JSONObject(r.body);
                    JSONArray data = o.optJSONArray("data");
                    if (data != null) for (int i = 0; i < data.length(); i++) {
                        JSONObject e = data.optJSONObject(i);
                        if (e == null) continue;
                        String id = e.optString("id", "");
                        if (id.isEmpty() || !seen.add(id)) continue;
                        int playing = e.optInt("playing", 0);
                        int max = e.optInt("maxPlayers", 0);
                        if (max > 0 && playing >= max) continue;
                        Server sv = new Server();
                        sv.jobId = id;
                        sv.playing = playing;
                        sv.maxPlayers = max;
                        items.add(sv);
                    }
                    next = o.isNull("nextPageCursor") ? null : o.optString("nextPageCursor", null);
                    ok = true;
                } catch (JSONException e) {
                    if (attempt == backoff.length - 1) return sort(items, preferEmpty);
                }
            }
            if (next == null || next.isEmpty()) break;
            cursor = next;
        }
        return sort(items, preferEmpty);
    }

    private static List<Server> sort(List<Server> items, boolean preferEmpty) {
        List<Server> out = new ArrayList<>(items);
        if (preferEmpty) out.sort(Comparator.comparingInt(x -> x.playing));
        else out.sort((a, b) -> {
            double fa = a.maxPlayers > 0 ? (double) a.playing / a.maxPlayers : a.playing > 0 ? 0.5 : 0;
            double fb = b.maxPlayers > 0 ? (double) b.playing / b.maxPlayers : b.playing > 0 ? 0.5 : 0;
            int cmp = Double.compare(fb, fa);
            return cmp != 0 ? cmp : Integer.compare(b.playing, a.playing);
        });
        return out;
    }

    private static void primeCsrf(long placeId, String cookie, boolean v2) {
        if (csrf != null) return;
        Response r = join(v2, placeId, "00000000-0000-0000-0000-000000000000", cookie, null, JOIN_TIMEOUT_MS);
        if (r != null && r.csrf != null) csrf = r.csrf;
    }

    static Object[] resolveJob(Context c, long placeId, String jobId) {
        String cookie = RobloxLogin.cookie(c);
        if (cookie == null || placeId <= 0 || jobId == null || jobId.isEmpty()) return null;
        boolean v2 = useV2(Store.get(c));
        primeCsrf(placeId, cookie, v2);
        return resolve(placeId, jobId, cookie, v2, System.currentTimeMillis() + 10_000);
    }

    private static Object[] resolve(long placeId, String jobId, String cookie, boolean v2, long deadline) {
        String key = placeId + ":" + jobId;
        Object[] cached = RESOLVED.get(key);
        if (cached != null && System.currentTimeMillis() - (Long) cached[2] < RESOLVED_TTL_MS) return cached;
        int[] result = new int[1];
        Object[] r = attempt(v2, placeId, jobId, cookie, deadline, result);
        if (r == null && result[0] == 1 && System.currentTimeMillis() < deadline) r = attempt(!v2, placeId, jobId, cookie, deadline, result);
        if (r != null) {
            if (RESOLVED.size() > 2048) RESOLVED.clear();
            RESOLVED.put(key, r);
        }
        return r;
    }

    private static Object[] attempt(boolean v2, long placeId, String jobId, String cookie, long deadline, int[] allowAlternate) {
        allowAlternate[0] = 0;
        for (int i = 0; i < 2; i++) {
            long wait = backoffUntil - System.currentTimeMillis();
            if (wait > 0) sleep(wait + 25 + (long) (Math.random() * 175));
            long left = deadline - System.currentTimeMillis();
            if (left <= 0) return null;
            Response r = join(v2, placeId, jobId, cookie, csrf, (int) Math.min(JOIN_TIMEOUT_MS, left));
            if (r == null) {
                allowAlternate[0] = 1;
                return null;
            }
            if (r.code == 403 && r.csrf != null) {
                csrf = r.csrf;
                continue;
            }
            if (r.code == 429) {
                long delay = r.retryAfterMs > 0 ? r.retryAfterMs : 1500;
                backoffUntil = Math.max(backoffUntil, System.currentTimeMillis() + Math.min(8000, delay));
                return null;
            }
            if (r.code < 200 || r.code >= 300) {
                if (r.code == 401) {
                    RobloxLogin.reject();
                    report("Roblox rejected your login, sign in again");
                }
                allowAlternate[0] = (r.code == 400 || r.code == 404 || r.code == 405 || r.code == 408 || r.code >= 500) ? 1 : 0;
                return null;
            }
            JSONObject o = parseJoin(r);
            if (o == null || !hasJoinScript(o)) {
                allowAlternate[0] = 1;
                return null;
            }
            Object[] ipPort = parseJoinResponse(o);
            String ip = (String) ipPort[0];
            if (ip.isEmpty() || isPrivate(ip)) {
                allowAlternate[0] = 1;
                return null;
            }
            return new Object[]{ip, ipPort[1], System.currentTimeMillis()};
        }
        return null;
    }

    private static Response join(boolean v2, long placeId, String jobId, String cookie, String token, int timeout) {
        try {
            JSONObject body = new JSONObject().put("placeId", placeId).put("gameId", jobId).put("gameJoinAttemptId", UUID.randomUUID().toString());
            if (v2) body.put("joinOrigin", "VoidstrapFetchInfo");
            String url = v2 ? "https://gamejoin.roblox.com/v2/join-game-instance" : "https://gamejoin.roblox.com/v1/join-game-instance";
            return request("POST", url, cookie, token, body.toString(), "Roblox/WinInet", timeout);
        } catch (JSONException e) {
            return null;
        }
    }

    private static JSONObject parseJoin(Response r) {
        String text = r.body;
        if (text == null || text.trim().isEmpty()) return null;
        if (r.contentType != null && r.contentType.toLowerCase(Locale.ROOT).contains("text/event-stream")) text = sseJson(text);
        try {
            return new JSONObject(text);
        } catch (JSONException e) {
            return null;
        }
    }

    private static String sseJson(String text) {
        String first = "";
        for (String block : text.split("\\r?\\n\\r?\\n")) {
            if (block.trim().isEmpty()) continue;
            String event = "message";
            StringBuilder data = new StringBuilder();
            for (String raw : block.split("\n")) {
                String line = raw.endsWith("\r") ? raw.substring(0, raw.length() - 1) : raw;
                if (line.isEmpty() || line.charAt(0) == ':') continue;
                int sep = line.indexOf(':');
                String field = sep < 0 ? line : line.substring(0, sep);
                String val = sep < 0 ? "" : line.substring(sep + 1).replaceFirst("^ +", "");
                if (field.equals("event")) event = val;
                else if (field.equals("data")) data.append(data.length() > 0 ? "\n" : "").append(val);
            }
            if (data.length() == 0) continue;
            if (first.isEmpty()) first = data.toString();
            if (event.equals("ResponseReady")) return data.toString();
        }
        return first;
    }

    private static boolean hasJoinScript(JSONObject o) {
        return o.optJSONObject("joinScript") != null || o.optJSONArray("UdmuxEndpoints") != null || !o.optString("MachineAddress", "").isEmpty();
    }

    private static Object[] parseJoinResponse(JSONObject root) {
        String ip = null;
        int port = 0;
        JSONObject script = root.optJSONObject("joinScript");
        JSONArray endpoints = root.optJSONArray("UdmuxEndpoints");
        if (endpoints == null && script != null) endpoints = script.optJSONArray("UdmuxEndpoints");
        if (endpoints != null && endpoints.length() > 0) {
            JSONObject first = endpoints.optJSONObject(0);
            if (first != null) {
                ip = first.optString("Address", null);
                port = Math.max(0, first.optInt("Port", 0));
            }
        }
        if (ip == null || ip.isEmpty()) {
            ip = root.optString("MachineAddress", "");
            if (ip.isEmpty() && script != null) ip = script.optString("MachineAddress", "");
        }
        if (port == 0) port = root.has("ServerPort") ? root.optInt("ServerPort") : script != null ? script.optInt("ServerPort", 0) : 0;
        return new Object[]{ip == null ? "" : ip, port};
    }

    private static final class Response {
        int code;
        String body;
        String csrf;
        String contentType;
        long retryAfterMs;
    }

    static boolean robloxHost(java.net.URL u) {
        if (u == null || !"https".equals(u.getProtocol()) || u.getHost() == null) return false;
        String host = u.getHost().toLowerCase(Locale.ROOT);
        return host.equals("roblox.com") || host.endsWith(".roblox.com");
    }

    private static Response request(String method, String url, String cookie, String token, String body, String agent, int timeout) {
        HttpURLConnection con = null;
        try {
            con = (HttpURLConnection) new URL(url).openConnection();
            con.setInstanceFollowRedirects(false);
            con.setConnectTimeout(timeout);
            con.setReadTimeout(timeout);
            con.setRequestMethod(method);
            con.setRequestProperty("User-Agent", agent);
            con.setRequestProperty("Accept", "application/json");
            if (cookie != null && robloxHost(con.getURL())) con.setRequestProperty("Cookie", ".ROBLOSECURITY=" + cookie);
            if (url.startsWith("https://gamejoin.")) con.setRequestProperty("Referer", "https://www.roblox.com/");
            if (token != null) con.setRequestProperty("X-CSRF-TOKEN", token);
            if (body != null) {
                con.setDoOutput(true);
                con.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                byte[] b = body.getBytes(StandardCharsets.UTF_8);
                con.setFixedLengthStreamingMode(b.length);
                try (OutputStream out = con.getOutputStream()) {
                    out.write(b);
                }
            }
            Response r = new Response();
            r.code = con.getResponseCode();
            r.csrf = con.getHeaderField("x-csrf-token");
            r.contentType = con.getContentType();
            String retry = con.getHeaderField("Retry-After");
            if (retry != null) {
                try {
                    r.retryAfterMs = (long) (Double.parseDouble(retry.trim()) * 1000);
                } catch (NumberFormatException ignored) {
                }
            }
            InputStream in = r.code >= 400 ? con.getErrorStream() : con.getInputStream();
            r.body = in == null ? "" : new String(readAll(in, MAX_JOIN_BYTES * 4), StandardCharsets.UTF_8);
            return r;
        } catch (IOException | RuntimeException e) {
            return null;
        } finally {
            if (con != null) con.disconnect();
        }
    }

    private static String get(String url, String cookie, int timeout) throws IOException {
        Response r = request("GET", url, cookie, null, null, "Voidstrap/1.0", timeout);
        if (r == null || r.code != 200) throw new IOException("HTTP " + (r == null ? 0 : r.code));
        return r.body;
    }

    private static byte[] readAll(InputStream in, int limit) throws IOException {
        try (InputStream s = in) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] b = new byte[16384];
            int n;
            while ((n = s.read(b)) > 0) {
                if (out.size() + n > limit) throw new IOException("too large");
                out.write(b, 0, n);
            }
            return out.toByteArray();
        }
    }

    private static String enc(String s) {
        try {
            return URLEncoder.encode(s, "UTF-8");
        } catch (java.io.UnsupportedEncodingException e) {
            return s;
        }
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }
}
