package com.voidstrap.android;

import android.content.Context;
import android.net.Uri;
import android.text.Html;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class ModCatalog {
    public static final String GAMEBANANA = "GameBanana";
    public static final String MARKETPLACE = "Kliko Mods";
    public static final String GAMEBANANA_URL = "https://gamebanana.com/games/2879";
    public static final int ROBLOX_GAME = 2879;
    public static final String[] GB_SORTS = {"Featured", "Newest", "Recently updated"};
    public static final String[] MARKET_SORTS = {"Featured", "Name"};
    public static final int MIN_SEARCH = 2;
    private static final String API = "https://gamebanana.com/apiv11";
    private static final int PAGE_SIZE = 35;
    private static final String DETAIL = "_idRow,_sText,_aFiles,_aGame,_nDownloadCount,_nLikeCount,_nPostCount,_sLicense,_aSubmitter,_aLicenseChecklist,_aPreviewMedia";
    private static final String MARKET_SOURCE = "https://github.com/klikos-modloader/marketplace";
    private static final String MARKET_DOWNLOAD = "https://github.com/klikos-modloader/marketplace/raw/refs/heads/main/";
    private static final String MARKET_RAW = "https://raw.githubusercontent.com/klikos-modloader/marketplace/main/";
    private static final String MARKET_RAW_REFS = "https://raw.githubusercontent.com/klikos-modloader/marketplace/refs/heads/main/";
    private static final String MARKET_TREE = "https://api.github.com/repos/klikos-modloader/marketplace/git/trees/main?recursive=1";
    private static final Set<String> HIDDEN_CATEGORIES = new HashSet<>(Arrays.asList("maps", "effects", "castaways"));
    private static final Set<String> PACKAGE_EXTENSIONS = new HashSet<>(Arrays.asList("zip", "rar", "7z", "json"));
    private static final Map<Long, String[]> LISTINGS = new ConcurrentHashMap<>();
    private static Map<String, String> blobHashes;

    private static final String[] BLOCKED = {
            "exploit", "executor", "script hub", "scripthub", "cheat", "cheats", "cheating", "aimbot",
            "auto farm", "autofarm", "autofarming", "esp", "wallhack", "wall hack", "bypass anticheat",
            "anti cheat bypass", "anticheat bypass", "injector", "dll inject", "synapse", "krnl", "fluxus",
            "delta executor", "keyless", "no key system", "robux generator", "free robux", "account generator",
            "token grabber", "cookie logger", "remote access trojan", "keylogger", "stealer",
            "keygen", "serial key", "byfron bypass", "hyperion bypass", "silent aim",
            "lag switch", "dupe glitch", "duplication glitch", "fly hack", "speed hack", "god mode script"};
    private static final String[] EXTERNAL = {
            "python", "pip install", ".py", "py file", "py script",
            "browser extension", "chrome extension", "firefox extension", "edge extension", "opera extension",
            "browser addon", "browser add on", "chrome web store", "load unpacked", "unpacked extension",
            "userscript", "user script", "tampermonkey", "greasemonkey", "violentmonkey",
            ".crx", ".xpi", "web extension", "webextension"};
    private static final String[] FLAG_TERMS = {
            "fflag", "dfflag", "fint", "dfint", "fstring", "dfstring", "flog",
            "fastflag", "fastflags", "fast flag", "fast flags",
            "flag list", "flags list", "flag preset", "flags preset", "clientappsettings"};
    private static final String[] REPLACEMENT_HINTS = {"fleasion", "assetwarp", "asset warp"};
    private static final Pattern BLOCKED_PATTERN = terms(BLOCKED);
    private static final Pattern EXTERNAL_PATTERN = terms(EXTERNAL);
    private static final Pattern FLAG_PATTERN = terms(FLAG_TERMS);
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
    }

    public static final class Page {
        public final List<Entry> entries = new ArrayList<>();
        public int total;
        public int page;
        public int pageSize;
        public int returned;
    }

    private ModCatalog() {
    }

    private static Pattern terms(String[] list) {
        StringBuilder sb = new StringBuilder();
        for (String t : list) {
            if (sb.length() > 0) sb.append('|');
            String[] words = t.split(" ");
            for (int i = 0; i < words.length; i++) {
                if (i > 0) sb.append("[ _-]+");
                sb.append(Pattern.quote(words[i]));
            }
        }
        return Pattern.compile("(?<![a-z0-9])(" + sb + ")(?![a-z0-9])", Pattern.CASE_INSENSITIVE);
    }

    private static String flatten(String... parts) {
        StringBuilder sb = new StringBuilder(" ");
        for (String p : parts) if (p != null && !p.isEmpty()) sb.append(p).append(' ');
        return sb.toString().toLowerCase(Locale.ROOT);
    }

    private static String find(Pattern p, String haystack) {
        Matcher m = p.matcher(haystack);
        return m.find() ? m.group().trim() : null;
    }

    static boolean gameBananaHost(String host) {
        return host != null && (host.equalsIgnoreCase("gamebanana.com") || host.toLowerCase(Locale.ROOT).endsWith(".gamebanana.com"));
    }

    static boolean marketplaceUrl(String url) {
        return url != null && (url.startsWith(MARKET_DOWNLOAD) || url.startsWith(MARKET_RAW) || url.startsWith(MARKET_RAW_REFS));
    }

    static boolean trustedUrl(String url) {
        if (url == null || !url.startsWith("https://")) return false;
        Uri u = Uri.parse(url);
        String host = u.getHost();
        if ("github.com".equalsIgnoreCase(host) || "raw.githubusercontent.com".equalsIgnoreCase(host))
            return url.replaceAll("/+$", "").equals(MARKET_SOURCE) || marketplaceUrl(url);
        return gameBananaHost(host);
    }

    static boolean trustedDownload(String url) {
        if (marketplaceUrl(url)) return true;
        if (url == null || !url.startsWith("https://")) return false;
        return gameBananaHost(Uri.parse(url).getHost());
    }

    public static String inspectListing(Entry e) {
        if (e == null || e.id <= 0 || e.name.trim().isEmpty()) return "The entry is incomplete.";
        if (!trustedUrl(e.profileUrl)) return "The entry does not come from a trusted community source.";
        if (HIDDEN_CATEGORIES.contains(e.category.toLowerCase(Locale.ROOT)) || HIDDEN_CATEGORIES.contains(e.superCategory.toLowerCase(Locale.ROOT)))
            return "The entry is in a category Voidstrap does not show.";
        String hay = flatten(e.name, e.summary, e.category, e.superCategory, String.join(" ", e.tags));
        String t = find(BLOCKED_PATTERN, hay);
        if (t != null) return "The entry looks like a cheat or an exploit, matched on " + t + ".";
        t = find(EXTERNAL_PATTERN, hay);
        if (t != null) return "The entry is a separate program or a browser extension, matched on " + t + ".";
        return null;
    }

    public static String inspectDetail(Entry e) {
        String listing = inspectListing(e);
        if (listing != null) return listing;
        String hay = flatten(e.html);
        String t = find(BLOCKED_PATTERN, hay);
        if (t != null) return "The description advertises cheating, matched on " + t + ".";
        t = find(EXTERNAL_PATTERN, hay);
        if (t != null) return "The description describes a separate program or a browser extension, matched on " + t + ".";
        boolean any = false;
        for (ModFile f : e.files) if (inspectFile(f) == null) any = true;
        if (!any) return "No scanned package is available for this mod.";
        if (flagList(e)) return "The entry is a fast flag list, not a Roblox mod.";
        return null;
    }

    public static String inspectFile(ModFile f) {
        if (f == null) return "The download is missing.";
        if (f.lacksRoblox) return "The package holds no Roblox client files, so it is not a Roblox mod.";
        if (f.executable) return "The package contains a program or a script, so it is not a Roblox mod.";
        if (!trustedDownload(f.url)) return "The download does not come from a verified GameBanana file host over https.";
        String ext = Mods.extension(f.name);
        if (ext.equals("json") && flagFile(f.name)) return "The file is a fast flag list, not a mod.";
        if (!PACKAGE_EXTENSIONS.contains(ext)) return "Only zip, rar and 7z packages and Fleasion JSON configs can be installed, this one is ." + ext + ".";
        if (marketplaceUrl(f.url)) return null;
        if (f.size <= 0 || f.size > ModArchives.MAX_PACKAGE) return "The package is larger than the 256 MB limit.";
        if (ext.equals("json") && f.size > 4 * 1024 * 1024) return "The replacement config is larger than the 4 MB limit.";
        if (!"done".equalsIgnoreCase(f.avState) || !"clean".equalsIgnoreCase(f.avResult)) return "The GameBanana virus scan did not report the file as clean.";
        if (!"done".equalsIgnoreCase(f.analysisState) || !"ok".equalsIgnoreCase(f.analysisResult)) return "The GameBanana content analysis did not pass.";
        if (!f.md5.matches("[0-9a-fA-F]{32}")) return "The package does not publish a usable checksum.";
        return null;
    }

    public static String androidBlock(Entry e, ModFile f) {
        if (f == null) return null;
        String ext = Mods.extension(f.name);
        if (ext.equals("json")) return "This is a Fleasion or AssetWarp replacement config. Those replace online Roblox assets through a desktop proxy, which Android cannot run.";
        if (f.listingKnown && !f.listingRoblox && !f.listingCache && f.listingConfig) return "This pack only has a Fleasion or AssetWarp replacement config, which needs the desktop app.";
        if (f.listingKnown && !f.listingRoblox && !f.listingCache) return "This pack replaces online Roblox assets by ID through Fleasion or AssetWarp, which only run on desktop.";
        return null;
    }

    static boolean flagFile(String name) {
        String stem = name.contains(".") ? name.substring(0, name.lastIndexOf('.')) : name;
        String compact = stem.replaceAll("[^A-Za-z0-9]", "").toLowerCase(Locale.ROOT);
        return compact.contains("fflag") || compact.contains("fastflag") || compact.contains("clientappsettings") || compact.contains("ixpsettings");
    }

    private static boolean flagList(Entry e) {
        List<ModFile> ok = new ArrayList<>();
        for (ModFile f : e.files) if (inspectFile(f) == null) ok.add(f);
        if (ok.isEmpty()) return false;
        for (ModFile f : ok) if (!Mods.extension(f.name).equals("json")) return false;
        return FLAG_PATTERN.matcher(flatten(e.name, e.summary, e.html)).find();
    }

    static boolean hashName(String name) {
        return name.length() == 32 && name.matches("[0-9a-fA-F]{32}");
    }

    static void summarize(Context c, ModFile f, String[] entries) {
        boolean roblox = false, config = false, assets = false, cache = false;
        for (String entry : entries) {
            String rel = ModArchives.normalize(entry);
            String name = rel.substring(rel.lastIndexOf('/') + 1);
            if (name.isEmpty()) continue;
            if (hashName(name)) {
                if (!rel.toUpperCase(Locale.ROOT).contains("RESTORE")) cache = true;
                continue;
            }
            String ext = Mods.extension(name);
            if (ext.equals("json")) {
                if (!flagFile(name) && ContentPlacer.resolve(c, rel) == null) config = true;
                continue;
            }
            if (!ModArchives.installable(rel)) continue;
            assets = true;
            if (!roblox && ContentPlacer.resolve(c, rel) != null) roblox = true;
        }
        f.listingKnown = true;
        f.listingRoblox = roblox;
        f.listingConfig = config;
        f.listingAssets = assets;
        f.listingCache = cache;
    }

    static boolean dangerousListing(Context c, String[] entries) {
        for (String e : entries) {
            String rel = ModArchives.normalize(e);
            String ext = Mods.extension(rel);
            if (ModArchives.DANGEROUS.contains(ext) && !ContentPlacer.clientCopy(c, rel)) return true;
        }
        return false;
    }

    static boolean robloxMod(Context c, String[] entries, boolean replacement) {
        for (String e : entries) {
            String rel = ModArchives.normalize(e);
            String name = rel.substring(rel.lastIndexOf('/') + 1);
            if (name.isEmpty()) continue;
            if (Mods.extension(name).equals("json") || hashName(name)) return true;
            if (!ModArchives.installable(rel)) continue;
            if (replacement || ContentPlacer.resolve(c, rel) != null) return true;
        }
        return false;
    }

    private static JSONObject json(String url) throws IOException {
        try {
            return new JSONObject(Net.text(url, 8 * 1024 * 1024));
        } catch (JSONException e) {
            throw new IOException("The catalog returned an unexpected reply.", e);
        }
    }

    private static JSONArray jsonArray(String url) throws IOException {
        try {
            return new JSONArray(Net.text(url, 8 * 1024 * 1024));
        } catch (JSONException e) {
            throw new IOException("The catalog returned an unexpected reply.", e);
        }
    }

    public static List<Category> categories() throws IOException {
        JSONArray arr = jsonArray(API + "/Mod/Categories?_idGameRow=" + ROBLOX_GAME + "&_sSort=a_to_z&_bShowEmpty=false");
        List<Category> out = new ArrayList<>();
        for (int i = 0; i < arr.length(); i++) {
            JSONObject o = arr.optJSONObject(i);
            if (o == null || o.optBoolean("_bIsObsolete")) continue;
            Category c = new Category();
            c.id = o.optLong("_idRow");
            c.name = o.optString("_sName");
            c.count = o.optInt("_nItemCount");
            if (c.id <= 0 || c.name.trim().isEmpty() || HIDDEN_CATEGORIES.contains(c.name.toLowerCase(Locale.ROOT))) continue;
            out.add(c);
        }
        return out;
    }

    public static Page browse(Context c, int page, String sort, Category category, String search) throws IOException {
        String q = search == null ? "" : search.trim();
        String url;
        if (q.length() >= MIN_SEARCH) {
            url = API + "/Util/Search/Results?_sSearchString=" + Uri.encode(q) + "&_idGameRow=" + ROBLOX_GAME + "&_sModelName=Mod&_nPage=" + page;
        } else if (category != null && category.id > 0) {
            url = API + "/Mod/Index?_nPage=" + page + "&_nPerpage=" + PAGE_SIZE + "&" + Uri.encode("_aFilters[Generic_Game]") + "=" + ROBLOX_GAME + "&" + Uri.encode("_aFilters[Generic_Category]") + "=" + category.id;
        } else {
            String key = "Newest".equals(sort) ? "Generic_Newest" : "Recently updated".equals(sort) ? "Generic_LatestModified" : "";
            url = API + "/Mod/Index?_nPage=" + page + "&_nPerpage=" + PAGE_SIZE + "&" + Uri.encode("_aFilters[Generic_Game]") + "=" + ROBLOX_GAME + (key.isEmpty() ? "" : "&_sSort=" + key);
        }
        JSONObject root = json(url);
        Page result = new Page();
        JSONObject meta = root.optJSONObject("_aMetadata");
        result.total = meta == null ? 0 : meta.optInt("_nRecordCount");
        result.page = page;
        result.pageSize = q.length() >= MIN_SEARCH ? 15 : PAGE_SIZE;
        JSONArray records = root.optJSONArray("_aRecords");
        if (records == null) return result;
        Set<Long> seen = new HashSet<>();
        List<Entry> candidates = new ArrayList<>();
        for (int i = 0; i < records.length(); i++) {
            result.returned++;
            Entry e = readListing(records.optJSONObject(i));
            if (e == null || inspectListing(e) != null) continue;
            if (seen.add(e.id)) candidates.add(e);
        }
        batchDetails(candidates);
        probe(c, candidates);
        for (Entry e : candidates) if (e.detailLoaded && inspectDetail(e) == null) result.entries.add(e);
        return result;
    }

    public static Entry detail(Context c, Entry e) throws IOException {
        if (GAMEBANANA.equals(e.source)) {
            if (!e.detailLoaded) {
                JSONObject root = json(API + "/Mod/" + e.id + "/ProfilePage");
                if (!applyProfile(e, root)) return null;
            }
            List<Entry> one = new ArrayList<>();
            one.add(e);
            probe(c, one);
            if (inspectDetail(e) != null) return null;
            community(e);
            return e;
        }
        if (inspectDetail(e) != null) return null;
        if (!e.iconUrl.isEmpty() && e.images.isEmpty()) {
            Image img = new Image();
            img.full = e.iconUrl;
            img.thumb = e.iconUrl;
            img.caption = e.name;
            e.images.add(img);
        }
        return e;
    }

    private static void batchDetails(List<Entry> entries) throws IOException {
        for (int off = 0; off < entries.size(); off += PAGE_SIZE) {
            List<Entry> chunk = entries.subList(off, Math.min(entries.size(), off + PAGE_SIZE));
            StringBuilder ids = new StringBuilder();
            for (Entry e : chunk) {
                if (ids.length() > 0) ids.append(',');
                ids.append(e.id);
            }
            JSONArray arr = jsonArray(API + "/Mod/Multi?_csvRowIds=" + ids + "&_csvProperties=" + DETAIL);
            Map<Long, JSONObject> byId = new HashMap<>();
            for (int i = 0; i < arr.length(); i++) {
                JSONObject o = arr.optJSONObject(i);
                if (o != null && o.optLong("_idRow") > 0) byId.put(o.optLong("_idRow"), o);
            }
            for (Entry e : chunk) {
                JSONObject o = byId.get(e.id);
                if (o != null) applyProfile(e, o);
            }
        }
    }

    private static void probe(Context c, List<Entry> entries) {
        List<Object[]> work = new ArrayList<>();
        for (Entry e : entries) {
            for (ModFile f : e.files) {
                String ext = Mods.extension(f.name);
                if (f.probed || !(ext.equals("zip") || ext.equals("rar") || ext.equals("7z")) || inspectFile(f) != null) continue;
                work.add(new Object[]{e, f});
            }
        }
        if (work.isEmpty()) return;
        ExecutorService pool = Executors.newFixedThreadPool(Math.min(8, work.size()));
        try {
            List<Future<?>> tasks = new ArrayList<>();
            for (Object[] w : work) {
                Entry e = (Entry) w[0];
                ModFile f = (ModFile) w[1];
                tasks.add(pool.submit(() -> {
                    String[] listing = listing(f.id);
                    f.probed = true;
                    if (listing == null || listing.length == 0) return;
                    summarize(c, f, listing);
                    if (dangerousListing(c, listing)) {
                        f.executable = true;
                        return;
                    }
                    if (!robloxMod(c, listing, e.replacement)) f.lacksRoblox = true;
                }));
            }
            for (Future<?> t : tasks) {
                try {
                    t.get();
                } catch (Exception ignored) {
                }
            }
        } finally {
            pool.shutdownNow();
        }
    }

    private static String[] listing(long fileId) {
        if (fileId <= 0) return null;
        String[] cached = LISTINGS.get(fileId);
        if (cached != null) return cached;
        try {
            String text = Net.text(API + "/File/" + fileId + "/RawFileList", 2 * 1024 * 1024);
            List<String> lines = new ArrayList<>();
            for (String l : text.split("\n")) if (!l.trim().isEmpty()) lines.add(l.trim());
            String[] out = lines.toArray(new String[0]);
            LISTINGS.put(fileId, out);
            return out;
        } catch (IOException e) {
            return null;
        }
    }

    private static boolean applyProfile(Entry e, JSONObject root) {
        JSONObject game = root.optJSONObject("_aGame");
        if (game != null && game.has("_idRow") && game.optInt("_idRow") != ROBLOX_GAME) return false;
        e.comments.clear();
        e.updates.clear();
        e.issues.clear();
        e.html = root.optString("_sText");
        e.summary = summarizeHtml(e.html);
        e.downloads = root.optInt("_nDownloadCount");
        e.likes = Math.max(e.likes, root.optInt("_nLikeCount"));
        e.commentCount = root.optInt("_nPostCount");
        e.updateCount = root.optInt("_nUpdatesCount");
        e.issueCount = root.optInt("_nAllTodosCount");
        e.license = plain(root.optString("_sLicense"));
        JSONObject sub = root.optJSONObject("_aSubmitter");
        e.submitter = sub == null ? null : person(sub);
        e.licenseRules.clear();
        JSONObject check = root.optJSONObject("_aLicenseChecklist");
        if (check != null) {
            String[][] groups = {{"yes", "Allowed"}, {"ask", "Ask first"}, {"no", "Not allowed"}};
            for (String[] g : groups) {
                JSONArray items = check.optJSONArray(g[0]);
                if (items == null) continue;
                for (int i = 0; i < items.length(); i++) {
                    Object it = items.opt(i);
                    String text = it instanceof JSONObject ? ((JSONObject) it).optString("_sText") : String.valueOf(it);
                    if (!text.trim().isEmpty()) e.licenseRules.add(new String[]{g[1], plain(text)});
                }
            }
        }
        e.images.clear();
        e.files.clear();
        JSONObject media = root.optJSONObject("_aPreviewMedia");
        JSONArray images = media == null ? null : media.optJSONArray("_aImages");
        if (images != null) for (int i = 0; i < images.length(); i++) {
            JSONObject im = images.optJSONObject(i);
            if (im == null) continue;
            String base = im.optString("_sBaseUrl");
            String file = im.optString("_sFile");
            String thumb = im.optString("_sFile220");
            if (base.isEmpty() || file.isEmpty()) continue;
            Image img = new Image();
            img.full = base.replaceAll("/+$", "") + "/" + file;
            img.thumb = thumb.isEmpty() ? img.full : base.replaceAll("/+$", "") + "/" + thumb;
            img.caption = im.optString("_sCaption");
            if (trustedUrl(img.full) && trustedUrl(img.thumb)) e.images.add(img);
        }
        JSONArray files = root.optJSONArray("_aFiles");
        if (files != null) for (int i = 0; i < files.length(); i++) {
            JSONObject f = files.optJSONObject(i);
            if (f == null) continue;
            ModFile m = new ModFile();
            m.id = f.optLong("_idRow");
            m.name = f.optString("_sFile");
            m.url = f.optString("_sDownloadUrl");
            m.md5 = f.optString("_sMd5Checksum");
            m.size = f.optLong("_nFilesize");
            m.description = f.optString("_sDescription");
            m.avState = f.optString("_sAvState");
            m.avResult = f.optString("_sAvResult");
            m.analysisState = f.optString("_sAnalysisState");
            m.analysisResult = f.optString("_sAnalysisResult");
            JSONObject warnings = f.optJSONObject("_aAnalysisWarnings");
            if (warnings != null) {
                java.util.Iterator<String> keys = warnings.keys();
                while (keys.hasNext()) {
                    String k = keys.next().toLowerCase(Locale.ROOT);
                    if (k.contains("exe") || k.contains("executable") || k.contains("script")) m.executable = true;
                }
            }
            e.files.add(m);
        }
        e.replacement = looksReplacement(e);
        e.detailLoaded = true;
        return true;
    }

    private static boolean looksReplacement(Entry e) {
        for (ModFile f : e.files) if (Mods.extension(f.name).equals("json") && !flagFile(f.name)) return true;
        String text = (e.name + " " + e.summary + " " + e.html).toLowerCase(Locale.ROOT);
        for (String h : REPLACEMENT_HINTS) if (text.contains(h)) return true;
        return false;
    }

    private static Person person(JSONObject o) {
        Person p = new Person();
        p.name = o.optString("_sName");
        p.avatar = o.optString("_sAvatarUrl");
        p.profile = o.optString("_sProfileUrl");
        p.title = o.optString("_sUserTitle");
        p.points = o.optInt("_nPoints");
        return p;
    }

    private static Entry readListing(JSONObject r) {
        if (r == null || !"Mod".equalsIgnoreCase(r.optString("_sModelName"))) return null;
        if (r.optBoolean("_bIsObsolete") || r.optBoolean("_bHasContentRatings") || !r.optBoolean("_bHasFiles")) return null;
        JSONObject game = r.optJSONObject("_aGame");
        if (game != null && game.has("_idRow") && game.optInt("_idRow") != ROBLOX_GAME) return null;
        Entry e = new Entry();
        e.id = r.optLong("_idRow");
        e.name = r.optString("_sName");
        e.version = r.optString("_sVersion");
        e.profileUrl = r.optString("_sProfileUrl");
        JSONObject root = r.optJSONObject("_aRootCategory");
        JSONObject subc = r.optJSONObject("_aSubCategory");
        JSONObject sub = r.optJSONObject("_aSubmitter");
        e.superCategory = root == null ? "" : root.optString("_sName");
        e.category = subc == null ? "" : subc.optString("_sName");
        e.author = sub == null ? "" : sub.optString("_sName");
        e.authorAvatar = sub == null ? "" : sub.optString("_sAvatarUrl");
        e.likes = r.optInt("_nLikeCount");
        e.views = r.optInt("_nViewCount");
        long ts = r.optLong("_tsDateUpdated", r.optLong("_tsDateModified", r.optLong("_tsDateAdded")));
        e.updated = ts * 1000;
        JSONArray tags = r.optJSONArray("_aTags");
        if (tags != null) for (int i = 0; i < tags.length(); i++) {
            Object t = tags.opt(i);
            String v = t instanceof JSONObject ? ((JSONObject) t).optString("_sTitle") : String.valueOf(t);
            if (!v.trim().isEmpty()) e.tags.add(v);
        }
        JSONObject media = r.optJSONObject("_aPreviewMedia");
        JSONArray images = media == null ? null : media.optJSONArray("_aImages");
        if (images != null) for (int i = 0; i < images.length(); i++) {
            JSONObject im = images.optJSONObject(i);
            if (im == null) continue;
            String base = im.optString("_sBaseUrl");
            String file = im.optString("_sFile220");
            if (file.isEmpty()) file = im.optString("_sFile");
            if (base.isEmpty() || file.isEmpty()) continue;
            String url = base.replaceAll("/+$", "") + "/" + file;
            if (trustedUrl(url)) {
                e.iconUrl = url;
                break;
            }
        }
        return e;
    }

    private static void community(Entry e) {
        e.comments.clear();
        e.updates.clear();
        e.issues.clear();
        for (JSONObject r : records(API + "/Mod/" + e.id + "/Posts", false)) {
            Post p = new Post();
            JSONObject poster = r.optJSONObject("_aPoster");
            p.author = poster == null ? "" : poster.optString("_sName");
            p.body = plainKeepLines(r.optString("_sText"));
            p.time = r.optLong("_tsDateAdded", r.optLong("_tsDateModified")) * 1000;
            e.comments.add(p);
        }
        for (JSONObject r : records(API + "/Mod/" + e.id + "/Updates", true)) {
            Post p = new Post();
            p.title = r.optString("_sName");
            String version = r.optString("_sVersion");
            if (!version.isEmpty()) p.status = version;
            p.body = plainKeepLines(r.optString("_sText"));
            p.time = r.optLong("_tsDateAdded", r.optLong("_tsDateModified")) * 1000;
            e.updates.add(p);
        }
        for (JSONObject r : records(API + "/Mod/" + e.id + "/Todos", true)) {
            Post p = new Post();
            p.title = r.optString("_sName", r.optString("_sTitle"));
            if (p.title.isEmpty()) p.title = r.optString("_sTitle");
            p.status = r.optString("_sStatus");
            String body = r.optString("_sText");
            if (body.trim().isEmpty()) body = r.optString("_sDescription");
            p.body = plainKeepLines(body);
            p.time = r.optLong("_tsDateAdded", r.optLong("_tsDateModified")) * 1000;
            e.issues.add(p);
        }
        e.commentCount = Math.max(e.commentCount, e.comments.size());
        e.updateCount = Math.max(e.updateCount, e.updates.size());
        e.issueCount = Math.max(e.issueCount, e.issues.size());
    }

    private static List<JSONObject> records(String url, boolean all) {
        List<JSONObject> out = new ArrayList<>();
        for (int page = 1; page <= 4; page++) {
            try {
                JSONObject root = json(url + (url.contains("?") ? "&" : "?") + "_nPage=" + page);
                JSONArray recs = root.optJSONArray("_aRecords");
                if (recs == null) break;
                int before = out.size();
                for (int i = 0; i < recs.length(); i++) if (recs.optJSONObject(i) != null) out.add(recs.optJSONObject(i));
                if (!all || out.size() == before) break;
                JSONObject meta = root.optJSONObject("_aMetadata");
                if (meta == null || out.size() >= meta.optInt("_nRecordCount") || meta.optBoolean("_bIsComplete")) break;
            } catch (IOException e) {
                break;
            }
        }
        return out;
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

    private static String summarizeHtml(String html) {
        String t = plain(html);
        return t.length() > 240 ? t.substring(0, 240).trim() + "..." : t;
    }

    public static Page marketplace(String sort, String search) throws IOException {
        JSONArray arr = jsonArray(MARKET_RAW + "index.json");
        List<Entry> found = new ArrayList<>();
        String q = search == null ? "" : search.trim().toLowerCase(Locale.ROOT);
        for (int i = 0; i < arr.length(); i++) {
            JSONObject o = arr.optJSONObject(i);
            if (o == null) continue;
            String id = o.optString("id");
            String name = o.optString("name");
            String download = o.optString("download");
            if (id.trim().isEmpty() || name.trim().isEmpty() || !marketplaceUrl(download)) continue;
            String thumb = o.optString("thumbnail");
            Entry e = new Entry();
            e.id = stableId(id);
            e.slug = id;
            e.name = name;
            e.author = o.optString("author");
            e.profileUrl = MARKET_SOURCE;
            e.iconUrl = marketplaceUrl(thumb) ? thumb : "";
            e.category = "Roblox file mod";
            e.superCategory = "Marketplace";
            e.source = MARKETPLACE;
            e.html = o.optString("description");
            String summary = e.html.replace('\n', ' ').trim();
            e.summary = summary.length() > 240 ? summary.substring(0, 240).trim() + "..." : summary;
            ModFile f = new ModFile();
            f.name = id + ".zip";
            f.url = download;
            f.avState = "done";
            f.avResult = "clean";
            f.analysisState = "done";
            f.analysisResult = "ok";
            e.files.add(f);
            e.detailLoaded = true;
            if (!q.isEmpty() && !e.name.toLowerCase(Locale.ROOT).contains(q) && !e.summary.toLowerCase(Locale.ROOT).contains(q) && !e.author.toLowerCase(Locale.ROOT).contains(q)) continue;
            if (inspectDetail(e) != null) continue;
            found.add(e);
        }
        if ("Name".equals(sort)) found.sort((a, b) -> a.name.compareToIgnoreCase(b.name));
        Page p = new Page();
        p.entries.addAll(found);
        p.total = found.size();
        p.page = 1;
        p.pageSize = found.size();
        return p;
    }

    private static long stableId(String value) {
        long hash = 5381;
        for (char ch : value.toCharArray()) hash = ((hash << 5) + hash + Character.toLowerCase(ch)) & 0x7FFFFFFF;
        return hash;
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
            ModArchives.Inspection i;
            try {
                i = ModArchives.inspect(c, pkg, cancel);
            } catch (ModArchives.Rejected r) {
                throw r;
            }
            ManagedMods.Pack pack = new ManagedMods.Pack();
            pack.source = e.source;
            pack.id = e.id;
            pack.name = e.name;
            pack.author = e.author;
            pack.iconUrl = e.iconUrl;
            pack.profileUrl = e.profileUrl;
            pack.category = e.category;
            if (!i.robloxContent) {
                if (ModAssetCache.hasEntries(c, pkg, cancel)) {
                    pack.installKind = "Asset cache";
                    return ModAssetCache.installPack(c, pkg, safeName(e) + " (Asset cache)", pack, cancel);
                }
                throw new ModArchives.Rejected("Nothing in the package could be matched to a Roblox client folder and it holds no replacement config, so there is nothing to install.");
            }
            pack.installKind = "My Mods";
            ManagedMods.Record r = ModArchives.installManaged(c, pkg, safeName(e), pack, cancel);
            try {
                ModVariants.capture(c, r.id, pkg, cancel);
            } catch (IOException ignored) {
            }
            return r;
        } finally {
            Mods.delete(staging);
        }
    }

    private static String safeName(Entry e) {
        String n = e.name.replaceAll("[\\\\/:*?\"<>|]", " ").replaceAll("\\s+", " ").trim();
        if (n.length() > 60) n = n.substring(0, 60).trim();
        return n.isEmpty() ? "Community mod " + e.id : n;
    }

    private static void acquire(Context c, Entry e, ModFile f, File dest, AtomicBoolean cancel) throws IOException {
        if (marketplaceUrl(f.url)) {
            Net.download(f.url, dest, ModArchives.MAX_PACKAGE, cancel);
            verifyBlob(e, dest);
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
                prune(dir);
            } catch (IOException ignored) {
            }
        }
    }

    private static void prune(File dir) {
        File[] files = dir == null ? null : dir.listFiles();
        if (files == null) return;
        Arrays.sort(files, (a, b) -> Long.compare(a.lastModified(), b.lastModified()));
        long total = 0;
        for (File f : files) total += f.length();
        for (File f : files) {
            if (total <= 256L * 1024 * 1024) break;
            total -= f.length();
            f.delete();
        }
    }

    private static synchronized Map<String, String> blobs() {
        if (blobHashes != null) return blobHashes;
        try {
            JSONArray tree = json(MARKET_TREE).optJSONArray("tree");
            Map<String, String> out = new HashMap<>();
            if (tree != null) for (int i = 0; i < tree.length(); i++) {
                JSONObject n = tree.optJSONObject(i);
                if (n != null) out.put(n.optString("path").toLowerCase(Locale.ROOT), n.optString("sha"));
            }
            blobHashes = out;
        } catch (IOException ignored) {
        }
        return blobHashes;
    }

    private static void verifyBlob(Entry e, File f) throws IOException {
        Map<String, String> hashes = blobs();
        String expected = hashes == null ? null : hashes.get(("mods/" + e.slug + ".zip").toLowerCase(Locale.ROOT));
        if (expected == null) return;
        try (InputStream in = new FileInputStream(f)) {
            MessageDigest sha = MessageDigest.getInstance("SHA-1");
            sha.update(("blob " + f.length() + "\0").getBytes(StandardCharsets.US_ASCII));
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) sha.update(buf, 0, n);
            if (!expected.equalsIgnoreCase(ModPresets.hex(sha.digest()))) throw new ModArchives.Rejected("The download did not match the hash published by the marketplace repository, so it was discarded.");
        } catch (NoSuchAlgorithmException ex) {
            throw new IOException(ex);
        }
    }

    static String md5(File f) {
        try (InputStream in = new FileInputStream(f)) {
            MessageDigest md = MessageDigest.getInstance("MD5");
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) md.update(buf, 0, n);
            return ModPresets.hex(md.digest());
        } catch (IOException | NoSuchAlgorithmException e) {
            return "";
        }
    }

    static ModFile fileFor(long modId, long fileId) throws IOException {
        JSONObject root = json(API + "/Mod/" + modId + "?_csvProperties=_aFiles");
        JSONArray files = root.optJSONArray("_aFiles");
        if (files != null) for (int i = 0; i < files.length(); i++) {
            JSONObject f = files.optJSONObject(i);
            if (f == null || f.optLong("_idRow") != fileId) continue;
            ModFile m = new ModFile();
            m.id = fileId;
            m.name = f.optString("_sFile");
            m.url = f.optString("_sDownloadUrl");
            m.md5 = f.optString("_sMd5Checksum");
            m.size = f.optLong("_nFilesize");
            m.avState = f.optString("_sAvState");
            m.avResult = f.optString("_sAvResult");
            m.analysisState = f.optString("_sAnalysisState");
            m.analysisResult = f.optString("_sAnalysisResult");
            return m;
        }
        throw new IOException("That GameBanana file is no longer available.");
    }

    public static ManagedMods.Record installById(Context c, long modId, long fileId, String name, AtomicBoolean cancel) throws IOException {
        JSONObject profile = json(API + "/Mod/" + modId + "/ProfilePage");
        Entry e = new Entry();
        e.id = modId;
        e.name = name;
        applyProfile(e, profile);
        e.profileUrl = profile.optString("_sProfileUrl", "https://gamebanana.com/mods/" + modId);
        JSONObject sub = profile.optJSONObject("_aSubmitter");
        e.author = sub == null ? "" : sub.optString("_sName");
        ModFile f = null;
        for (ModFile m : e.files) if (m.id == fileId) f = m;
        if (f == null) f = fileFor(modId, fileId);
        return install(c, e, f, cancel, null);
    }

    public static File fetchFile(Context c, long modId, long fileId, File dest, AtomicBoolean cancel) throws IOException {
        ModFile f = fileFor(modId, fileId);
        String bad = inspectFile(f);
        if (bad != null) throw new ModArchives.Rejected(bad);
        Entry e = new Entry();
        e.id = modId;
        File pkg = new File(dest, "package." + Mods.extension(f.name));
        acquire(c, e, f, pkg, cancel);
        return pkg;
    }
}
