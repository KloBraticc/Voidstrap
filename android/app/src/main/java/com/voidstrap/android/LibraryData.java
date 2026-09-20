package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

final class LibraryData {
    private static final long MINUTE = 60_000L;
    static final String JOINS = "/data/local/tmp/voidstrap_joins.txt";
    private static final Pattern JOIN = Pattern.compile("(\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d(?:\\.\\d+)?Z).*Joining game '[0-9a-fA-F-]+' place (\\d+)");

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

    static void enrich(Context c, List<Game> games) {
        for (Game g : games) {
            if (g.universeId > 0) continue;
            try {
                g.universeId = Net.cachedJson(c, "https://apis.roblox.com/universes/v1/places/" + g.placeId + "/universe", 7 * 24 * 60 * MINUTE).optLong("universeId", 0);
            } catch (IOException | JSONException ignored) {
            }
        }
        Map<Long, List<Game>> byUniverse = new LinkedHashMap<>();
        for (Game g : games) {
            if (g.universeId <= 0) continue;
            List<Game> list = byUniverse.get(g.universeId);
            if (list == null) byUniverse.put(g.universeId, list = new ArrayList<>());
            list.add(g);
        }
        List<Long> ids = new ArrayList<>(byUniverse.keySet());
        for (int i = 0; i < ids.size(); i += 50) {
            String chunk = join(ids.subList(i, Math.min(ids.size(), i + 50)));
            try {
                JSONArray d = Net.cachedJson(c, "https://games.roblox.com/v1/games?universeIds=" + chunk, 5 * MINUTE).optJSONArray("data");
                for (int k = 0; d != null && k < d.length(); k++) {
                    JSONObject o = d.getJSONObject(k);
                    List<Game> list = byUniverse.get(o.optLong("id"));
                    if (list == null) continue;
                    for (Game g : list) {
                        String name = o.optString("name", "");
                        if (!name.isEmpty()) g.name = Store.clip(name, 200);
                        g.description = o.optString("description", "");
                        JSONObject creator = o.optJSONObject("creator");
                        g.creator = creator == null ? "" : creator.optString("name", "");
                        g.playing = o.optLong("playing");
                        g.visits = o.optLong("visits");
                        g.maxPlayers = o.optInt("maxPlayers");
                        g.created = time(o.optString("created", ""));
                        g.updated = time(o.optString("updated", ""));
                        String genre = o.optString("genre_l1", "");
                        g.genre = genre.isEmpty() ? o.optString("genre", "") : genre;
                    }
                }
            } catch (IOException | JSONException ignored) {
            }
            try {
                JSONArray d = Net.cachedJson(c, "https://games.roblox.com/v1/games/votes?universeIds=" + chunk, 5 * MINUTE).optJSONArray("data");
                for (int k = 0; d != null && k < d.length(); k++) {
                    JSONObject o = d.getJSONObject(k);
                    List<Game> list = byUniverse.get(o.optLong("id"));
                    long up = o.optLong("upVotes");
                    long total = up + o.optLong("downVotes");
                    if (list != null && total > 0) for (Game g : list) g.likePercent = (int) Math.round(up * 100.0 / total);
                }
            } catch (IOException | JSONException ignored) {
            }
            try {
                JSONArray d = Net.cachedJson(c, "https://thumbnails.roblox.com/v1/games/icons?universeIds=" + chunk + "&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false", 24 * 60 * MINUTE).optJSONArray("data");
                for (int k = 0; d != null && k < d.length(); k++) {
                    JSONObject o = d.getJSONObject(k);
                    List<Game> list = byUniverse.get(o.optLong("targetId"));
                    String url = o.optString("imageUrl", "");
                    if (list != null && url.startsWith("https://")) for (Game g : list) g.iconUrl = url;
                }
            } catch (IOException | JSONException ignored) {
            }
            try {
                JSONArray d = Net.cachedJson(c, "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=" + chunk + "&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false", 24 * 60 * MINUTE).optJSONArray("data");
                for (int k = 0; d != null && k < d.length(); k++) {
                    JSONObject o = d.getJSONObject(k);
                    List<Game> list = byUniverse.get(o.optLong("universeId"));
                    JSONArray t = o.optJSONArray("thumbnails");
                    String url = t == null || t.length() == 0 ? "" : t.getJSONObject(0).optString("imageUrl", "");
                    if (list != null && url.startsWith("https://")) for (Game g : list) g.thumbUrl = url;
                }
            } catch (IOException | JSONException ignored) {
            }
        }
    }

    static List<Event> events(Context c, List<Game> games) {
        List<Event> found = new ArrayList<>();
        long now = System.currentTimeMillis();
        int checked = 0;
        for (Game g : games) {
            if (g.universeId <= 0 || checked++ >= 12) continue;
            try {
                JSONArray d = Net.cachedJson(c, "https://apis.roblox.com/virtual-events/v1/universes/" + g.universeId + "/virtual-events", 20 * MINUTE).optJSONArray("data");
                for (int k = 0; d != null && k < d.length(); k++) {
                    JSONObject o = d.getJSONObject(k);
                    if (!"active".equals(o.optString("eventStatus")) || !"public".equals(o.optString("eventVisibility"))) continue;
                    JSONObject t = o.optJSONObject("eventTime");
                    long end = t == null ? 0 : time(t.optString("endUtc", ""));
                    if (end > 0 && end < now) continue;
                    Event e = new Event();
                    e.id = o.optString("id", "");
                    e.title = o.optString("displayTitle", o.optString("title", ""));
                    String sub = o.optString("displaySubtitle", o.optString("subtitle", ""));
                    e.subtitle = sub.isEmpty() ? g.title() : sub;
                    e.start = t == null ? 0 : time(t.optString("startUtc", ""));
                    JSONArray thumbs = o.optJSONArray("thumbnails");
                    if (thumbs != null && thumbs.length() > 0) e.mediaId = thumbs.getJSONObject(0).optLong("mediaId");
                    if (!e.id.isEmpty()) found.add(e);
                }
            } catch (IOException | JSONException ignored) {
            }
        }
        found.sort((x, y) -> Long.compare(x.start == 0 ? Long.MAX_VALUE : x.start, y.start == 0 ? Long.MAX_VALUE : y.start));
        if (found.size() > 24) found = new ArrayList<>(found.subList(0, 24));
        List<Long> media = new ArrayList<>();
        for (Event e : found) if (e.mediaId > 0) media.add(e.mediaId);
        Map<Long, String> images = assetImages(c, media, "768x432", "Jpeg");
        for (Event e : found) e.thumbUrl = images.get(e.mediaId);
        return found;
    }

    static List<Pass> passes(Context c, long universeId) {
        List<Pass> out = new ArrayList<>();
        try {
            JSONArray d = Net.cachedJson(c, "https://apis.roblox.com/game-passes/v1/universes/" + universeId + "/game-passes?passView=Full&pageSize=100", 10 * MINUTE).optJSONArray("gamePasses");
            for (int k = 0; d != null && k < d.length(); k++) {
                JSONObject o = d.getJSONObject(k);
                Pass p = new Pass();
                p.id = o.optLong("id");
                p.name = o.optString("displayName", o.optString("name", ""));
                long price = o.optLong("price", 0);
                p.price = !o.optBoolean("isForSale") ? c.getString(R.string.library_off_sale) : price > 0 ? "R$ " + String.format(java.util.Locale.getDefault(), "%,d", price) : c.getString(R.string.library_for_sale);
                p.iconAsset = o.optLong("displayIconImageAssetId");
                if (p.id > 0) out.add(p);
            }
        } catch (IOException | JSONException ignored) {
        }
        List<Long> assets = new ArrayList<>();
        for (Pass p : out) if (p.iconAsset > 0) assets.add(p.iconAsset);
        Map<Long, String> images = assetImages(c, assets, "150x150", "Png");
        for (Pass p : out) p.iconUrl = images.get(p.iconAsset);
        return out;
    }

    private static Map<Long, String> assetImages(Context c, List<Long> ids, String size, String format) {
        Map<Long, String> out = new HashMap<>();
        for (int i = 0; i < ids.size(); i += 50) {
            try {
                JSONArray d = Net.cachedJson(c, "https://thumbnails.roblox.com/v1/assets?assetIds=" + join(ids.subList(i, Math.min(ids.size(), i + 50))) + "&size=" + size + "&format=" + format + "&isCircular=false", 24 * 60 * MINUTE).optJSONArray("data");
                for (int k = 0; d != null && k < d.length(); k++) {
                    JSONObject o = d.getJSONObject(k);
                    String url = o.optString("imageUrl", "");
                    if (url.startsWith("https://")) out.put(o.optLong("targetId"), url);
                }
            } catch (IOException | JSONException ignored) {
            }
        }
        return out;
    }

    static void importJoins(Context c) {
        FlagWriter.Mode mode = FlagWriter.mode(c);
        if (mode != FlagWriter.Mode.HELPER && mode != FlagWriter.Mode.ROOT) return;
        String script = "logcat -d -s Roblox:I | grep 'Joining game' > " + JOINS + ".new; mv -f " + JOINS + ".new " + JOINS + "; chmod 0644 " + JOINS + "; exit 0\n";
        if (FlagWriter.shell(c, script, 15) != FlagWriter.OK) return;
        String text;
        try {
            text = new String(java.nio.file.Files.readAllBytes(new File(JOINS).toPath()), StandardCharsets.UTF_8);
        } catch (IOException | SecurityException e) {
            return;
        }
        Store store = Store.get(c);
        String pkg = Targets.selected(c);
        List<Store.Launch> parsed = new ArrayList<>();
        Matcher m = JOIN.matcher(text);
        while (m.find()) {
            long when = time(m.group(1));
            long placeId;
            try {
                placeId = Long.parseLong(m.group(2));
            } catch (NumberFormatException e) {
                continue;
            }
            if (when > 0 && placeId > 0) parsed.add(new Store.Launch(when, placeId, "", pkg, "PLAYED"));
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

    static long time(String iso) {
        if (iso == null || iso.isEmpty()) return 0;
        try {
            return Instant.parse(iso).toEpochMilli();
        } catch (RuntimeException e) {
            return 0;
        }
    }

    private static String join(List<Long> ids) {
        StringBuilder sb = new StringBuilder();
        for (Long id : ids) {
            if (sb.length() > 0) sb.append(',');
            sb.append(id);
        }
        return sb.toString();
    }
}
