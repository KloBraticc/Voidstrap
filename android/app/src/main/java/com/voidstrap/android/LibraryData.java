package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;

final class LibraryData {
    private static final long MINUTE = 60_000L;
    static final String JOINS = "/data/local/tmp/voidstrap_joins.txt";

    static final class Game {
        long placeId;
        long universeId;
        String name = "";
        String iconUrl;
        String thumbUrl;
        String creator = "";
        String description = "";
        String genre = "";
        boolean pinned;
        long lastPlayed;
        int plays;
        long playing;
        long visits;
        int maxPlayers;
        long created;
        long updated;
        int likePercent = -1;

        String title() {
            return name == null || name.isEmpty() ? "Place " + placeId : name;
        }

        JSONObject json() {
            return Core.args("placeId", placeId, "universeId", universeId, "name", name, "iconUrl", iconUrl, "thumbUrl", thumbUrl, "creator", creator,
                    "description", description, "genre", genre, "playing", playing, "visits", visits, "maxPlayers", maxPlayers, "created", created,
                    "updated", updated, "likePercent", likePercent);
        }

        void apply(JSONObject o) {
            universeId = o.optLong("universeId");
            name = o.optString("name");
            iconUrl = Core.opt(o, "iconUrl");
            thumbUrl = Core.opt(o, "thumbUrl");
            creator = o.optString("creator");
            description = o.optString("description");
            genre = o.optString("genre");
            playing = o.optLong("playing");
            visits = o.optLong("visits");
            maxPlayers = o.optInt("maxPlayers");
            created = o.optLong("created");
            updated = o.optLong("updated");
            likePercent = o.optInt("likePercent", -1);
        }
    }

    static final class Event {
        String id;
        String title;
        String subtitle;
        long start;
        long mediaId;
        String thumbUrl;
    }

    static final class Pass {
        long id;
        String name;
        String price;
        long iconAsset;
        String iconUrl;
    }

    private LibraryData() {
    }

    static List<Game> base(Store store) {
        LinkedHashMap<Long, Game> games = new LinkedHashMap<>();
        for (Store.Game p : store.library) {
            Game g = new Game();
            g.placeId = p.placeId;
            g.universeId = p.universeId;
            g.name = p.name == null ? "" : p.name;
            g.iconUrl = p.iconUrl == null || p.iconUrl.isEmpty() ? null : p.iconUrl;
            g.pinned = true;
            games.put(g.placeId, g);
        }
        for (Store.Launch l : store.history) {
            if (l.placeId <= 0 || !played(l)) continue;
            Game g = games.get(l.placeId);
            if (g == null) {
                g = new Game();
                g.placeId = l.placeId;
                g.name = l.name == null ? "" : l.name;
                games.put(g.placeId, g);
            }
            if (g.name.isEmpty() && l.name != null) g.name = l.name;
            g.lastPlayed = Math.max(g.lastPlayed, l.time);
            g.plays++;
        }
        return new ArrayList<>(games.values());
    }

    static boolean played(Store.Launch l) {
        return Launcher.Result.HANDED_OFF.name().equals(l.result) || "PLAYED".equals(l.result);
    }

    private static JSONArray json(List<Game> games) {
        JSONArray a = new JSONArray();
        for (Game g : games) a.put(g.json());
        return a;
    }

    static void enrich(Context c, List<Game> games) {
        try {
            List<JSONObject> out = Core.objects(Core.value("feed.enrich", Core.feed(c, "games", json(games))));
            for (int i = 0; i < games.size() && i < out.size(); i++) games.get(i).apply(out.get(i));
        } catch (IOException ignored) {
        }
    }

    static List<Event> events(Context c, List<Game> games) {
        List<Event> found = new ArrayList<>();
        try {
            for (JSONObject o : Core.objects(Core.value("feed.events", Core.feed(c, "games", json(games))))) {
                Event e = new Event();
                e.id = o.optString("id");
                e.title = o.optString("title");
                e.subtitle = o.optString("subtitle");
                e.start = o.optLong("start");
                e.mediaId = o.optLong("mediaId");
                e.thumbUrl = Core.opt(o, "thumbUrl");
                found.add(e);
            }
        } catch (IOException ignored) {
        }
        return found;
    }

    static List<Pass> passes(Context c, long universeId) {
        List<Pass> out = new ArrayList<>();
        try {
            for (JSONObject o : Core.objects(Core.value("feed.passes", Core.feed(c, "universe", universeId)))) {
                Pass p = new Pass();
                p.id = o.optLong("id");
                p.name = o.optString("name");
                long price = o.optLong("price");
                p.price = !o.optBoolean("forSale") ? c.getString(R.string.library_off_sale) : price > 0 ? "R$ " + String.format(java.util.Locale.getDefault(), "%,d", price) : c.getString(R.string.library_for_sale);
                p.iconAsset = o.optLong("iconAsset");
                p.iconUrl = Core.opt(o, "iconUrl");
                out.add(p);
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    static void importJoins(Context c) {
        FlagWriter.Mode mode = FlagWriter.mode(c);
        if (mode != FlagWriter.Mode.HELPER && mode != FlagWriter.Mode.ROOT) return;
        String script = "logcat -d -s Roblox:I | grep 'Joining game' > " + JOINS + ".new; mv -f " + JOINS + ".new " + JOINS + "; chmod 0644 " + JOINS + "; exit 0\n";
        if (FlagWriter.shell(c, script, 15) != FlagWriter.OK) return;
        Object v;
        try {
            v = Core.value("feed.joins", Core.args("path", JOINS));
        } catch (IOException e) {
            return;
        }
        Store store = Store.get(c);
        String pkg = Targets.selected(c);
        List<Store.Launch> parsed = new ArrayList<>();
        JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
        for (int i = 0; i < a.length(); i++) {
            JSONArray j = a.optJSONArray(i);
            if (j != null) parsed.add(new Store.Launch(j.optLong(0), j.optLong(1), "", pkg, "PLAYED"));
        }
        if (parsed.isEmpty()) return;
        store.main.post(() -> {
            List<Store.Launch> fresh = new ArrayList<>();
            for (Store.Launch l : parsed) if (!known(store.history, l) && !known(fresh, l)) fresh.add(l);
            store.importLaunches(fresh);
        });
    }

    private static boolean known(List<Store.Launch> list, Store.Launch join) {
        for (Store.Launch l : list) if (l.placeId == join.placeId && Math.abs(l.time - join.time) < 2 * MINUTE) return true;
        return false;
    }
}
