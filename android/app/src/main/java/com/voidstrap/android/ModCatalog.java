package com.voidstrap.android;

import android.content.Context;
import android.text.Html;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.regex.Pattern;

public final class ModCatalog {
    public static final String GAMEBANANA = "GameBanana";
    public static final String MARKETPLACE = "Kliko Mods";
    public static final String[] GB_SORTS = {"Featured", "Newest", "Recently updated"};
    public static final String[] MARKET_SORTS = {"Featured", "Name"};
    public static final int MIN_SEARCH = 2;
    private static final Pattern TAG = Pattern.compile("<[^>]+>");
    private static final Pattern SPACE = Pattern.compile("\\s+");

    public static final class Category {
        public long id;
        public String name;
        public int count;
    }

    public static final class Person {
        public String name = "";
        public String avatar = "";
        public String profile = "";
        public String title = "";
        public int points;

        static Person from(JSONObject o) {
            if (o == null) return null;
            Person p = new Person();
            p.name = o.optString("name");
            p.avatar = o.optString("avatar");
            p.profile = o.optString("profile");
            p.title = o.optString("title");
            p.points = o.optInt("points");
            return p;
        }

        JSONObject json() {
            return Core.args("name", name, "avatar", avatar, "profile", profile, "title", title, "points", points);
        }
    }

    public static final class Image {
        public String full;
        public String thumb;
        public String caption;
    }

    public static final class Post {
        public String title = "";
        public String author = "";
        public String status = "";
        public long time;
        public String body = "";
        public long id;
        public int replyCount;
        public boolean removed;
        public String avatar = "";
        public String authorTitle = "";
        public String profile = "";
        public boolean pinned;
        public int stamps;
        public final List<String[]> changes = new ArrayList<>();
        public final List<Post> replies = new ArrayList<>();

        static Post from(JSONObject o) {
            Post p = new Post();
            p.apply(o);
            return p;
        }

        void apply(JSONObject o) {
            title = o.optString("title");
            author = o.optString("author");
            status = o.optString("status");
            time = o.optLong("time");
            body = o.optString("body");
            id = o.optLong("id");
            replyCount = o.optInt("replyCount");
            removed = o.optBoolean("removed");
            avatar = o.optString("avatar");
            authorTitle = o.optString("authorTitle");
            profile = o.optString("profile");
            pinned = o.optBoolean("pinned");
            stamps = o.optInt("stamps");
            pairs(o.optJSONArray("changes"), changes);
            replies.clear();
            for (JSONObject r : Core.objects(o.optJSONArray("replies"))) replies.add(from(r));
        }

        JSONObject json() {
            JSONArray r = new JSONArray();
            for (Post p : replies) r.put(p.json());
            return Core.args("title", title, "author", author, "status", status, "time", time, "body", body, "id", id, "replyCount", replyCount,
                    "removed", removed, "avatar", avatar, "authorTitle", authorTitle, "profile", profile, "pinned", pinned, "stamps", stamps,
                    "changes", pairs(changes), "replies", r);
        }
    }

    public static final class ModFile {
        public long id;
        public String name = "";
        public String url = "";
        public String md5 = "";
        public long size;
        public String description = "";
        public String avState = "";
        public String avResult = "";
        public String analysisState = "";
        public String analysisResult = "";
        public boolean executable;
        public boolean probed;
        public boolean listingKnown;
        public boolean listingRoblox;
        public boolean listingConfig;
        public boolean listingAssets;
        public boolean listingCache;
        public boolean lacksRoblox;

        static ModFile from(JSONObject o) {
            ModFile f = new ModFile();
            f.id = o.optLong("id");
            f.name = o.optString("name");
            f.url = o.optString("url");
            f.md5 = o.optString("md5");
            f.size = o.optLong("size");
            f.description = o.optString("description");
            f.avState = o.optString("avState");
            f.avResult = o.optString("avResult");
            f.analysisState = o.optString("analysisState");
            f.analysisResult = o.optString("analysisResult");
            f.executable = o.optBoolean("executable");
            f.probed = o.optBoolean("probed");
            f.listingKnown = o.optBoolean("listingKnown");
            f.listingRoblox = o.optBoolean("listingRoblox");
            f.listingConfig = o.optBoolean("listingConfig");
            f.listingAssets = o.optBoolean("listingAssets");
            f.listingCache = o.optBoolean("listingCache");
            f.lacksRoblox = o.optBoolean("lacksRoblox");
            return f;
        }

        JSONObject json() {
            return Core.args("id", id, "name", name, "url", url, "md5", md5, "size", size, "description", description, "avState", avState, "avResult", avResult,
                    "analysisState", analysisState, "analysisResult", analysisResult, "executable", executable, "probed", probed, "listingKnown", listingKnown,
                    "listingRoblox", listingRoblox, "listingConfig", listingConfig, "listingAssets", listingAssets, "listingCache", listingCache, "lacksRoblox", lacksRoblox);
        }
    }

    public static final class Entry {
        public long id;
        public String slug = "";
        public String name = "";
        public String summary = "";
        public String html = "";
        public String author = "";
        public String authorAvatar = "";
        public String profileUrl = "";
        public String iconUrl = "";
        public String category = "";
        public String superCategory = "";
        public String source = GAMEBANANA;
        public String version = "";
        public int likes;
        public int views;
        public int downloads;
        public int commentCount;
        public int updateCount;
        public int issueCount;
        public long updated;
        public List<String> tags = new ArrayList<>();
        public final List<Image> images = new ArrayList<>();
        public final List<ModFile> files = new ArrayList<>();
        public String license = "";
        public final List<String[]> licenseRules = new ArrayList<>();
        public Person submitter;
        public final List<Post> comments = new ArrayList<>();
        public final List<Post> updates = new ArrayList<>();
        public final List<Post> issues = new ArrayList<>();
        public boolean detailLoaded;
        public boolean replacement;

        static Entry from(JSONObject o) {
            Entry e = new Entry();
            e.apply(o);
            return e;
        }

        void apply(JSONObject o) {
            id = o.optLong("id");
            slug = o.optString("slug");
            name = o.optString("name");
            summary = o.optString("summary");
            html = o.optString("html");
            author = o.optString("author");
            authorAvatar = o.optString("authorAvatar");
            profileUrl = o.optString("profileUrl");
            iconUrl = o.optString("iconUrl");
            category = o.optString("category");
            superCategory = o.optString("superCategory");
            source = o.optString("source", GAMEBANANA);
            version = o.optString("version");
            likes = o.optInt("likes");
            views = o.optInt("views");
            downloads = o.optInt("downloads");
            commentCount = o.optInt("commentCount");
            updateCount = o.optInt("updateCount");
            issueCount = o.optInt("issueCount");
            updated = o.optLong("updated");
            tags = Core.strings(o.optJSONArray("tags"));
            images.clear();
            for (JSONObject i : Core.objects(o.optJSONArray("images"))) {
                Image img = new Image();
                img.full = i.optString("full");
                img.thumb = i.optString("thumb");
                img.caption = i.optString("caption");
                images.add(img);
            }
            files.clear();
            for (JSONObject f : Core.objects(o.optJSONArray("files"))) files.add(ModFile.from(f));
            license = o.optString("license");
            pairs(o.optJSONArray("licenseRules"), licenseRules);
            submitter = Person.from(o.optJSONObject("submitter"));
            posts(o.optJSONArray("comments"), comments);
            posts(o.optJSONArray("updates"), updates);
            posts(o.optJSONArray("issues"), issues);
            detailLoaded = o.optBoolean("detailLoaded");
            replacement = o.optBoolean("replacement");
        }

        JSONObject json() {
            JSONArray img = new JSONArray();
            for (Image i : images) img.put(Core.args("full", i.full, "thumb", i.thumb, "caption", i.caption));
            JSONArray fs = new JSONArray();
            for (ModFile f : files) fs.put(f.json());
            return Core.args("id", id, "slug", slug, "name", name, "summary", summary, "html", html, "author", author, "authorAvatar", authorAvatar,
                    "profileUrl", profileUrl, "iconUrl", iconUrl, "category", category, "superCategory", superCategory, "source", source, "version", version,
                    "likes", likes, "views", views, "downloads", downloads, "commentCount", commentCount, "updateCount", updateCount, "issueCount", issueCount,
                    "updated", updated, "tags", new JSONArray(tags), "images", img, "files", fs, "license", license, "licenseRules", pairs(licenseRules),
                    "submitter", submitter == null ? null : submitter.json(), "comments", posts(comments), "updates", posts(updates), "issues", posts(issues),
                    "detailLoaded", detailLoaded, "replacement", replacement);
        }
    }

    public static final class Page {
        public final List<Entry> entries = new ArrayList<>();
        public int total;
        public int page;
        public int pageSize;
        public int returned;

        static Page from(JSONObject o) {
            Page p = new Page();
            for (JSONObject e : Core.objects(o.optJSONArray("entries"))) p.entries.add(Entry.from(e));
            p.total = o.optInt("total");
            p.page = o.optInt("page");
            p.pageSize = o.optInt("pageSize");
            p.returned = o.optInt("returned");
            return p;
        }
    }

    private ModCatalog() {
    }

    private static void pairs(JSONArray a, List<String[]> out) {
        out.clear();
        for (int i = 0; a != null && i < a.length(); i++) {
            JSONArray p = a.optJSONArray(i);
            if (p != null) out.add(new String[]{p.optString(0), p.optString(1)});
        }
    }

    private static JSONArray pairs(List<String[]> list) {
        JSONArray a = new JSONArray();
        for (String[] p : list) a.put(new JSONArray().put(p[0]).put(p[1]));
        return a;
    }

    private static void posts(JSONArray a, List<Post> out) {
        out.clear();
        for (JSONObject o : Core.objects(a)) out.add(Post.from(o));
    }

    private static JSONArray posts(List<Post> list) {
        JSONArray a = new JSONArray();
        for (Post p : list) a.put(p.json());
        return a;
    }

    private static String agent() {
        return "Voidstrap Android/" + BuildConfig.VERSION_NAME;
    }

    public static String inspectFile(ModFile f) {
        if (f == null) return "The download is missing.";
        try {
            return Core.text("catalog.inspectFile", Core.args("file", f.json()));
        } catch (IOException e) {
            return "The download is missing.";
        }
    }

    public static String androidBlock(Entry e, ModFile f) {
        if (f == null) return null;
        try {
            return Core.text("catalog.androidBlock", Core.args("file", f.json()));
        } catch (IOException ex) {
            return null;
        }
    }

    public static List<Category> categories() throws IOException {
        List<Category> out = new ArrayList<>();
        for (JSONObject o : Core.objects(Core.value("catalog.categories", Core.args("agent", agent())))) {
            Category c = new Category();
            c.id = o.optLong("id");
            c.name = o.optString("name");
            c.count = o.optInt("count");
            out.add(c);
        }
        return out;
    }

    public static Page browse(Context c, int page, String sort, Category category, String search) throws IOException {
        return Page.from(Core.run("catalog.browse", Core.indexed(c, "agent", agent(), "page", page, "sort", sort, "category", category == null ? 0 : category.id, "search", search)));
    }

    public static Entry detail(Context c, Entry e) throws IOException {
        JSONObject o = Core.run("catalog.detail", Core.indexed(c, "agent", agent(), "entry", e.json()));
        JSONObject updated = o.optJSONObject("entry");
        if (updated != null) e.apply(updated);
        return o.optBoolean("ok") ? e : null;
    }

    public static void loadReplies(List<Post> posts) {
        try {
            List<JSONObject> out = Core.objects(Core.value("catalog.replies", Core.args("agent", agent(), "posts", ModCatalog.posts(posts))));
            for (int i = 0; i < posts.size() && i < out.size(); i++) posts.get(i).apply(out.get(i));
        } catch (IOException ignored) {
        }
    }

    public static int count(List<Post> posts) {
        int n = 0;
        for (Post p : posts) n += 1 + count(p.replies);
        return n;
    }

    public static String plain(String html) {
        if (html == null || html.trim().isEmpty()) return "";
        String text = Html.fromHtml(TAG.matcher(html).replaceAll(" "), Html.FROM_HTML_MODE_LEGACY).toString();
        return SPACE.matcher(text.replace('\n', ' ').replace('\r', ' ')).replaceAll(" ").trim();
    }

    static String plainKeepLines(String html) {
        if (html == null || html.trim().isEmpty()) return "";
        return Html.fromHtml(html, Html.FROM_HTML_MODE_COMPACT).toString().trim();
    }

    public static Page marketplace(String sort, String search) throws IOException {
        return Page.from(Core.run("catalog.marketplace", Core.args("agent", agent(), "sort", sort, "search", search)));
    }

    public interface Progress {
        void on(String message);
    }

    public static ManagedMods.Record install(Context c, Entry e, ModFile f, AtomicBoolean cancel, Progress progress) throws IOException {
        String bad = inspectFile(f);
        if (bad != null) throw new ModArchives.Rejected(bad);
        String block = androidBlock(e, f);
        if (block != null) throw new ModArchives.Rejected(block);
        File staging = new File(c.getCacheDir(), "community_" + System.nanoTime());
        if (!staging.mkdirs()) throw new IOException("mkdir");
        try {
            File pkg = new File(staging, "package." + Mods.extension(f.name));
            if (progress != null) progress.on(c.getString(R.string.mods_packs_downloading, f.name));
            acquire(c, e, f, pkg, cancel);
            if (progress != null) progress.on(c.getString(R.string.mods_packs_installing));
            ModArchives.Inspection i = ModArchives.inspect(c, pkg, cancel);
            ManagedMods.Pack pack = new ManagedMods.Pack();
            pack.source = e.source;
            pack.id = e.id;
            pack.name = e.name;
            pack.author = e.author;
            pack.iconUrl = e.iconUrl;
            pack.profileUrl = e.profileUrl;
            pack.category = e.category;
            String name = Core.text("catalog.safeName", Core.args("name", e.name, "id", e.id));
            if (!i.robloxContent) {
                if (ModAssetCache.hasEntries(c, pkg, cancel)) {
                    pack.installKind = "Asset cache";
                    return ModAssetCache.installPack(c, pkg, name + " (Asset cache)", pack, cancel);
                }
                throw new ModArchives.Rejected("Nothing in the package could be matched to a Roblox client folder and it holds no replacement config, so there is nothing to install.");
            }
            pack.installKind = "My Mods";
            ManagedMods.Record r = ModArchives.installManaged(c, pkg, name, pack, cancel);
            try {
                ModVariants.capture(c, r.id, pkg, cancel);
            } catch (IOException ignored) {
            }
            return r;
        } finally {
            Mods.delete(staging);
        }
    }

    private static String md5(File f) throws IOException {
        String v = Core.text("catalog.md5", Core.args("path", f));
        return v == null ? "" : v;
    }

    private static void acquire(Context c, Entry e, ModFile f, File dest, AtomicBoolean cancel) throws IOException {
        if (Core.flag("catalog.marketplaceUrl", Core.args("url", f.url))) {
            Net.download(f.url, dest, ModArchives.MAX_PACKAGE, cancel);
            Core.run("catalog.verifyBlob", Core.args("agent", agent(), "slug", e.slug, "path", dest));
            return;
        }
        File cache = f.md5.matches("[0-9a-fA-F]{32}") ? new File(new File(c.getCacheDir(), "packages"), f.md5.toUpperCase(Locale.ROOT) + ".package") : null;
        if (cache != null && cache.isFile() && f.md5.equalsIgnoreCase(md5(cache))) {
            ModPresets.copy(cache, dest);
            cache.setLastModified(System.currentTimeMillis());
            return;
        }
        Net.download(f.url, dest, ModArchives.MAX_PACKAGE, cancel);
        if (!f.md5.equalsIgnoreCase(md5(dest))) {
            dest.delete();
            throw new ModArchives.Rejected("The download did not match the checksum GameBanana published, so it was discarded.");
        }
        if (cache != null) {
            try {
                File dir = cache.getParentFile();
                if (dir != null) dir.mkdirs();
                ModPresets.copy(dest, cache);
                if (dir != null) Core.run("catalog.prune", Core.args("dir", dir));
            } catch (IOException ignored) {
            }
        }
    }

    public static ManagedMods.Record installById(Context c, long modId, long fileId, String name, AtomicBoolean cancel) throws IOException {
        JSONObject o = Core.run("catalog.byId", Core.args("agent", agent(), "modId", modId, "fileId", fileId, "name", name));
        JSONObject entry = o.optJSONObject("entry");
        JSONObject file = o.optJSONObject("file");
        if (entry == null || file == null) throw new IOException("That GameBanana file is no longer available.");
        return install(c, Entry.from(entry), ModFile.from(file), cancel, null);
    }
}
