package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.io.UnsupportedEncodingException;
import java.net.URLEncoder;
import java.text.NumberFormat;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

public final class HomeFeed {
    public static final String NEWS_PAGE = "https://devforum.roblox.com/c/updates/announcements/36";
    private static final String NEWS_FEED = NEWS_PAGE + ".json";
    private static final String CATALOG = "https://catalog.roblox.com/v1/search/items/details?Category=1&Limit=10&SortType=3";
    private static final long MINUTE = 60_000L;
    private static final int MAX_GAMES = 9;
    private static final Set<String> ACRONYMS = new HashSet<>(Arrays.asList("rdc", "ip", "ugc", "api", "sdk", "ai", "ui", "ux", "vr", "ar", "os", "fps", "cdn", "npc", "id"));
    private static final Set<String> LIMITED = new HashSet<>(Arrays.asList("limited", "limitedunique", "collectible"));
    private static final Map<Integer, String> TYPES = new HashMap<>();

    static {
        Object[][] t = {{1, "Image"}, {2, "T-Shirt"}, {3, "Audio"}, {4, "Mesh"}, {8, "Hat"}, {9, "Place"}, {10, "Model"}, {11, "Shirt"}, {12, "Pants"}, {13, "Decal"},
                {17, "Head"}, {18, "Face"}, {19, "Gear"}, {21, "Badge"}, {24, "Animation"}, {27, "Torso"}, {28, "Right Arm"}, {29, "Left Arm"}, {30, "Left Leg"}, {31, "Right Leg"},
                {32, "Package"}, {34, "Game Pass"}, {38, "Plugin"}, {40, "MeshPart"}, {41, "Hair Accessory"}, {42, "Face Accessory"}, {43, "Neck Accessory"}, {44, "Shoulder Accessory"},
                {45, "Front Accessory"}, {46, "Back Accessory"}, {47, "Waist Accessory"}, {48, "Climb Animation"}, {49, "Death Animation"}, {50, "Fall Animation"}, {51, "Idle Animation"},
                {52, "Jump Animation"}, {53, "Run Animation"}, {54, "Swim Animation"}, {55, "Walk Animation"}, {56, "Pose Animation"}, {61, "Emote"}, {62, "Video"},
                {64, "T-Shirt Accessory"}, {65, "Shirt Accessory"}, {66, "Pants Accessory"}, {67, "Jacket Accessory"}, {68, "Sweater Accessory"}, {69, "Shorts Accessory"},
                {70, "Left Shoe Accessory"}, {71, "Right Shoe Accessory"}, {72, "Dress Skirt Accessory"}, {76, "Eyebrow Accessory"}, {77, "Eyelash Accessory"},
                {78, "Mood Animation"}, {79, "Dynamic Head"}};
        for (Object[] e : t) TYPES.put((Integer) e[0], (String) e[1]);
    }

    private HomeFeed() {
    }

    public static final class News {
        public long id;
        public String title;
        public String summary;
        public String url;
        public String tag;
        public String image;
        public long created;
    }

    public static final class Item {
        public long id;
        public String name;
        public String creator;
        public String type;
        public String image;
        public int price;
        public boolean limited;
        public boolean bundle;

        public String link() {
            return "https://www.roblox.com/" + (bundle ? "bundles/" : "catalog/") + id;
        }

        public String appLink() {
            return bundle ? "roblox://navigation/item_details?itemType=Bundle&itemId=" + id : link();
        }
    }

    public static final class GameCard {
        public long placeId;
        public long universeId;
        public String name;
        public String creator;
        public String thumbnail;
        public String likes = "?";
        public String players = "?";
        public long launched;
    }

    public static List<News> allNews(Context c) throws IOException, JSONException {
        JSONArray topics = Net.cachedJson(c, NEWS_FEED, 30 * MINUTE).optJSONObject("topic_list").optJSONArray("topics");
        List<News> out = new ArrayList<>();
        if (topics == null) return out;
        for (int i = 0; i < topics.length(); i++) {
            JSONObject t = topics.optJSONObject(i);
            if (t == null || t.optBoolean("pinned") || t.optLong("id") <= 0) continue;
            String title = t.optString("title", "").trim();
            if (title.isEmpty()) continue;
            News n = new News();
            n.id = t.optLong("id");
            n.title = title.length() <= 90 ? title : title.substring(0, 89).trim() + "...";
            String excerpt = t.optString("excerpt", "").replace('\n', ' ').replace('\r', ' ').replaceAll(" {2,}", " ").trim();
            n.summary = excerpt.length() <= 150 ? excerpt : excerpt.substring(0, 149).trim() + "...";
            String slug = t.optString("slug", "");
            n.url = "https://devforum.roblox.com/t/" + (slug.isEmpty() ? "topic" : slug) + "/" + n.id;
            n.image = image(t.optString("image_url", ""));
            n.tag = tag(t.optJSONArray("tags"));
            n.created = time(t.optString("created_at", ""));
            out.add(n);
        }
        out.sort((a, b) -> Long.compare(b.created, a.created));
        return out;
    }

    public static List<News> latestNews(Context c, int count) throws IOException, JSONException {
        List<News> all = allNews(c);
        List<News> out = new ArrayList<>();
        for (News n : all) if (!n.image.isEmpty() && out.size() < count) out.add(n);
        for (News n : all) if (n.image.isEmpty() && out.size() < count) out.add(n);
        return out;
    }

    public static String thumbnail(String image, int width) {
        if (image == null || image.isEmpty()) return "";
        try {
            return "https://wsrv.nl/?url=" + URLEncoder.encode(image, "UTF-8") + "&w=" + width + "&output=webp&q=80&we";
        } catch (UnsupportedEncodingException e) {
            return image;
        }
    }

    public static String cardTitle(String title) {
        String v = title.trim();
        if (v.length() <= 44) return v;
        int cut = v.lastIndexOf(' ', 43);
        if (cut < 24) cut = 43;
        return v.substring(0, cut).replaceAll("[ ,:;]+$", "");
    }

    private static String image(String raw) {
        String v = raw == null ? "" : raw.trim();
        if (v.startsWith("//")) return "https:" + v;
        return v.startsWith("https://") ? v : "";
    }

    private static String tag(JSONArray tags) {
        if (tags == null) return "";
        for (int i = 0; i < tags.length(); i++) {
            Object o = tags.opt(i);
            String t = o instanceof JSONObject ? ((JSONObject) o).optString("name", "") : String.valueOf(o);
            t = t.trim();
            if (t.isEmpty() || t.equalsIgnoreCase("featured") || t.equals("null")) continue;
            StringBuilder sb = new StringBuilder();
            for (String w : t.replace('-', ' ').split(" +")) {
                if (w.isEmpty()) continue;
                if (sb.length() > 0) sb.append(' ');
                if (ACRONYMS.contains(w.toLowerCase(Locale.ROOT))) sb.append(w.toUpperCase(Locale.ROOT));
                else sb.append(Character.toUpperCase(w.charAt(0))).append(w.substring(1));
            }
            return sb.toString();
        }
        return "";
    }

    private static long time(String iso) {
        try {
            return Instant.parse(iso).toEpochMilli();
        } catch (RuntimeException e) {
            return 0;
        }
    }

    public static List<Item> catalog(Context c) throws IOException, JSONException {
        JSONArray data = Net.cachedJson(c, CATALOG, 10 * MINUTE).optJSONArray("data");
        List<Item> out = new ArrayList<>();
        if (data == null) return out;
        StringBuilder ids = new StringBuilder();
        StringBuilder bundleIds = new StringBuilder();
        for (int i = 0; i < data.length(); i++) {
            JSONObject o = data.optJSONObject(i);
            if (o == null || o.optLong("id") <= 0) continue;
            Item it = new Item();
            it.id = o.optLong("id");
            it.name = Store.clip(o.optString("name", ""), 120);
            it.creator = Store.clip(o.optString("creatorName", ""), 80);
            it.price = o.isNull("price") ? o.optInt("lowestPrice", 0) : o.optInt("price", 0);
            it.bundle = "Bundle".equalsIgnoreCase(o.optString("itemType", ""));
            int type = o.optInt("assetType", 0);
            it.type = it.bundle ? "Bundle" : type <= 0 ? "" : TYPES.containsKey(type) ? TYPES.get(type) : "Asset";
            JSONArray r = o.optJSONArray("itemRestrictions");
            if (r != null) for (int k = 0; k < r.length(); k++) if (LIMITED.contains(r.optString(k).toLowerCase(Locale.ROOT))) it.limited = true;
            it.image = "";
            out.add(it);
            StringBuilder target = it.bundle ? bundleIds : ids;
            if (target.length() > 0) target.append(',');
            target.append(it.id);
        }
        Map<Long, String> images = new HashMap<>();
        thumbnails(c, "https://thumbnails.roblox.com/v1/assets?assetIds=", ids, images);
        thumbnails(c, "https://thumbnails.roblox.com/v1/bundles/thumbnails?bundleIds=", bundleIds, images);
        for (Item it : out) if (images.containsKey(it.id)) it.image = images.get(it.id);
        return out;
    }

    private static void thumbnails(Context c, String base, CharSequence ids, Map<Long, String> images) {
        if (ids.length() == 0) return;
        try {
            JSONArray thumbs = Net.cachedJson(c, base + ids + "&size=150x150&format=Png&isCircular=false", 10 * MINUTE).optJSONArray("data");
            if (thumbs == null) return;
            for (int i = 0; i < thumbs.length(); i++) {
                JSONObject t = thumbs.optJSONObject(i);
                if (t != null && "Completed".equals(t.optString("state")) && t.optString("imageUrl", "").startsWith("https://")) images.put(t.optLong("targetId"), t.optString("imageUrl"));
            }
        } catch (IOException | JSONException ignored) {
        }
    }

    public static String price(Item it) {
        return it.price <= 0 ? null : "R$ " + NumberFormat.getIntegerInstance(Locale.getDefault()).format(it.price);
    }

    public static List<GameCard> base(List<Store.Launch> history) {
        LinkedHashMap<Long, GameCard> cards = new LinkedHashMap<>();
        for (Store.Launch l : history) {
            if (l.placeId <= 0 || cards.containsKey(l.placeId) || cards.size() >= MAX_GAMES) continue;
            if (!LibraryData.played(l)) continue;
            GameCard g = new GameCard();
            g.placeId = l.placeId;
            g.name = l.name == null || l.name.isEmpty() ? String.valueOf(l.placeId) : l.name;
            g.creator = "";
            g.launched = l.time;
            cards.put(l.placeId, g);
        }
        return new ArrayList<>(cards.values());
    }

    public static List<GameCard> enrich(Context c, List<GameCard> cards) {
        for (GameCard g : cards) {
            try {
                g.universeId = Net.cachedJson(c, "https://apis.roblox.com/universes/v1/places/" + g.placeId + "/universe", 7 * 24 * 60 * MINUTE).optLong("universeId", 0);
            } catch (IOException | JSONException ignored) {
            }
        }
        Map<Long, GameCard> byUniverse = new LinkedHashMap<>();
        for (GameCard g : cards) if (g.universeId > 0) byUniverse.put(g.universeId, g);
        if (!byUniverse.isEmpty()) {
            StringBuilder ids = new StringBuilder();
            for (Long id : byUniverse.keySet()) {
                if (ids.length() > 0) ids.append(',');
                ids.append(id);
            }
            try {
                JSONArray d = Net.cachedJson(c, "https://games.roblox.com/v1/games?universeIds=" + ids, 5 * MINUTE).optJSONArray("data");
                if (d != null) for (int i = 0; i < d.length(); i++) {
                    JSONObject o = d.optJSONObject(i);
                    GameCard g = o == null ? null : byUniverse.get(o.optLong("id"));
                    if (g == null) continue;
                    String name = o.optString("name", "");
                    if (!name.isEmpty()) g.name = Store.clip(name, 200);
                    JSONObject creator = o.optJSONObject("creator");
                    if (creator != null) g.creator = Store.clip(creator.optString("name", ""), 80);
                    long playing = o.optLong("playing", 0);
                    g.players = playing > 0 ? count(playing) : "?";
                }
            } catch (IOException | JSONException ignored) {
            }
            try {
                JSONArray d = Net.cachedJson(c, "https://games.roblox.com/v1/games/votes?universeIds=" + ids, 5 * MINUTE).optJSONArray("data");
                if (d != null) for (int i = 0; i < d.length(); i++) {
                    JSONObject o = d.optJSONObject(i);
                    GameCard g = o == null ? null : byUniverse.get(o.optLong("id"));
                    if (g == null) continue;
                    long up = o.optLong("upVotes", 0);
                    long total = up + o.optLong("downVotes", 0);
                    g.likes = total > 0 ? Math.round(up * 100.0 / total) + "%" : "?";
                }
            } catch (IOException | JSONException ignored) {
            }
            try {
                JSONArray d = Net.cachedJson(c, "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=" + ids + "&countPerUniverse=1&size=768x432&format=Png&isCircular=false", 24 * 60 * MINUTE).optJSONArray("data");
                if (d != null) for (int i = 0; i < d.length(); i++) {
                    JSONObject o = d.optJSONObject(i);
                    GameCard g = o == null ? null : byUniverse.get(o.optLong("universeId"));
                    JSONArray t = o == null ? null : o.optJSONArray("thumbnails");
                    if (g == null || t == null || t.length() == 0) continue;
                    String url = t.optJSONObject(0).optString("imageUrl", "");
                    if (url.startsWith("https://")) g.thumbnail = url;
                }
            } catch (IOException | JSONException | NullPointerException ignored) {
            }
        }
        return cards;
    }

    static String count(long n) {
        if (n >= 1_000_000_000) return trim(n / 1_000_000_000.0) + "B";
        if (n >= 1_000_000) return trim(n / 1_000_000.0) + "M";
        if (n >= 1_000) return trim(n / 1_000.0) + "K";
        return String.valueOf(n);
    }

    private static String trim(double v) {
        double r = Math.round(v * 10) / 10.0;
        return r == Math.floor(r) ? String.valueOf((long) r) : String.format(Locale.ROOT, "%.1f", r);
    }
}
