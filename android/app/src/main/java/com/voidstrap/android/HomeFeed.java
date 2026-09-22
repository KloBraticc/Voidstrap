package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.text.NumberFormat;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;

public final class HomeFeed {
    public static final String NEWS_PAGE = "https://devforum.roblox.com/c/updates/announcements/36";
    private static final int MAX_GAMES = 9;

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

    public static List<News> allNews(Context c) throws IOException {
        return news(c, 0);
    }

    public static List<News> latestNews(Context c, int count) throws IOException {
        return news(c, count);
    }

    private static List<News> news(Context c, int latest) throws IOException {
        List<News> out = new ArrayList<>();
        for (JSONObject o : Core.objects(Core.value("feed.news", Core.feed(c, "latest", latest)))) {
            News n = new News();
            n.id = o.optLong("id");
            n.title = o.optString("title");
            n.summary = o.optString("summary");
            n.url = o.optString("url");
            n.tag = o.optString("tag");
            n.image = o.optString("image");
            n.created = o.optLong("created");
            out.add(n);
        }
        return out;
    }

    public static String thumbnail(String image, int width) {
        return text("feed.thumbnail", Core.args("image", image == null ? "" : image, "width", width), image == null ? "" : image);
    }

    public static String cardTitle(String title) {
        return text("feed.cardTitle", Core.args("title", title), title.trim());
    }

    private static String text(String op, JSONObject args, String fallback) {
        try {
            String v = Core.text(op, args);
            return v == null ? fallback : v;
        } catch (IOException e) {
            return fallback;
        }
    }

    public static List<Item> catalog(Context c) throws IOException {
        List<Item> out = new ArrayList<>();
        for (JSONObject o : Core.objects(Core.value("feed.catalog", Core.feed(c)))) {
            Item it = new Item();
            it.id = o.optLong("id");
            it.name = o.optString("name");
            it.creator = o.optString("creator");
            it.type = o.optString("type");
            it.image = o.optString("image");
            it.price = o.optInt("price");
            it.limited = o.optBoolean("limited");
            it.bundle = o.optBoolean("bundle");
            out.add(it);
        }
        return out;
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
        JSONArray in = new JSONArray();
        for (GameCard g : cards) in.put(Core.args("placeId", g.placeId, "universeId", g.universeId, "name", g.name, "creator", g.creator));
        try {
            List<JSONObject> out = Core.objects(Core.value("feed.cards", Core.feed(c, "cards", in)));
            for (int i = 0; i < cards.size() && i < out.size(); i++) {
                GameCard g = cards.get(i);
                JSONObject o = out.get(i);
                g.universeId = o.optLong("universeId");
                g.name = o.optString("name", g.name);
                g.creator = o.optString("creator", g.creator);
                g.players = o.optString("players", g.players);
                g.likes = o.optString("likes", g.likes);
                g.thumbnail = o.optString("thumbnail", g.thumbnail);
            }
        } catch (IOException ignored) {
        }
        return cards;
    }

    static String count(long n) {
        return text("feed.count", Core.args("n", n), String.valueOf(n));
    }
}
