package com.voidstrap.android;

import android.content.Context;
import android.os.SystemClock;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;

final class AppPresence {
    static final String SETTING = "voidRpc";
    static final String USER_ID = "robloxUserId";
    private static final long THROTTLE_MS = 1500;
    private static final String DOWNLOAD = "https://github.com/KloBraticc/Voidstrap/releases";
    private static final String DISCORD = "https://discord.gg/bzdbHHytFR";
    private static final String GITHUB = "https://github.com/KloBraticc/Voidstrap";

    private static final class Scene {
        final int owner;
        final String details;
        final String state;
        final String image;
        final String imageText;
        final String buttonLabel;
        final String buttonUrl;

        Scene(int owner, String details, String state, String image, String imageText, String buttonLabel, String buttonUrl) {
            this.owner = owner;
            this.details = details;
            this.state = state;
            this.image = image;
            this.imageText = imageText;
            this.buttonLabel = buttonLabel;
            this.buttonUrl = buttonUrl;
        }
    }

    private static Context app;
    private static boolean visible;
    private static int page;
    private static Scene scene;
    private static long sessionStart;
    private static long lastUpdate;
    private static boolean scheduled;
    private static boolean suppressed;
    private static String lastSignature = "";
    private static String avatarUrl = "";
    private static String avatarText = "";
    private static long avatarFor;

    private AppPresence() {
    }

    static boolean enabled(Store s) {
        return "1".equals(s.setting(SETTING, "1"));
    }

    static void shown(Context c, int currentPage) {
        app = c.getApplicationContext();
        visible = true;
        page = currentPage;
        if (sessionStart == 0) sessionStart = System.currentTimeMillis();
        update();
    }

    static void hidden() {
        visible = false;
        lastSignature = "";
        suppressed = false;
        DiscordRpc.VOIDSTRAP.stop();
    }

    static void page(int id) {
        page = id;
        update();
    }

    static void set(int owner, String details, String state) {
        set(owner, details, state, "", "", "", "");
    }

    static void set(int owner, String details, String state, String image, String imageText, String buttonLabel, String buttonUrl) {
        scene = new Scene(owner, details, state, image, imageText, buttonLabel, buttonUrl);
        update();
    }

    static void update() {
        if (!visible || app == null) return;
        Store s = Store.get(app);
        if (!enabled(s) || !DiscordRpc.installed(app)) {
            DiscordRpc.VOIDSTRAP.stop();
            lastSignature = "";
            return;
        }
        DiscordRpc.VOIDSTRAP.start(app);
        if (ActivityService.running()) {
            if (!suppressed) {
                DiscordRpc.VOIDSTRAP.clear();
                suppressed = true;
                lastSignature = "";
            }
            return;
        }
        suppressed = false;
        long wait = lastUpdate + THROTTLE_MS - SystemClock.elapsedRealtime();
        if (wait > 0) {
            if (!scheduled) {
                scheduled = true;
                s.main.postDelayed(() -> {
                    scheduled = false;
                    update();
                }, wait);
            }
            return;
        }
        fetchAvatar(s);
        Integrations.Presence p = build();
        String signature = p.signature();
        if (signature.equals(lastSignature)) return;
        lastSignature = signature;
        lastUpdate = SystemClock.elapsedRealtime();
        DiscordRpc.VOIDSTRAP.update(p);
    }

    private static String[] pageInfo(int id) {
        if (id == R.id.nav_library) return new String[]{"Library", "Browsing the game library"};
        if (id == R.id.nav_mods) return new String[]{"Mods", "Cursors, sounds, overlays, skyboxes"};
        if (id == R.id.nav_flags) return new String[]{"FastFlag Settings", "Tweaking flag presets"};
        if (id == R.id.nav_flag_editor) return new String[]{"FastFlag Editor", "Editing fast flags"};
        if (id == R.id.nav_integrations) return new String[]{"Integrations", "Advanced integrations"};
        if (id == R.id.nav_matchmaker) return new String[]{"Voidstrap Matchmaker", "Picking the closest servers"};
        if (id == R.id.nav_settings) return new String[]{"Settings", "App settings & updates"};
        return new String[]{"Home", "On the Voidstrap home screen"};
    }

    private static Integrations.Presence build() {
        Scene ctx = scene != null && scene.owner == page ? scene : null;
        String details;
        String state;
        if (ctx != null) {
            details = ctx.details;
            state = ctx.state;
        } else {
            String[] info = pageInfo(page);
            details = info[0];
            state = info[1];
        }
        details = clip(details);
        state = clip(state);
        if (details.length() < 2) details = "Voidstrap";
        if (state.length() < 2) state = "Exploring Voidstrap";
        String version = "Voidstrap Android v" + BuildConfig.VERSION_NAME;
        String image = ctx != null && ctx.image.startsWith("https://") ? ctx.image : "";
        String buttonUrl = ctx != null && ctx.buttonUrl.startsWith("https://") ? ctx.buttonUrl : "";
        Integrations.Presence p = new Integrations.Presence();
        p.details = details;
        p.state = state;
        p.detailsStatus = true;
        p.start = sessionStart;
        if (!image.isEmpty()) {
            p.largeImage = image;
            p.largeText = clip(ctx.imageText.isEmpty() ? details : ctx.imageText);
            p.smallImage = DiscordRpc.VOIDSTRAP_LOGO;
            p.smallText = version;
        } else {
            p.largeImage = DiscordRpc.VOIDSTRAP_LOGO;
            p.largeText = version;
            p.smallImage = avatarUrl;
            p.smallText = avatarUrl.isEmpty() ? "" : avatarText.isEmpty() ? "Roblox" : avatarText;
        }
        if (!buttonUrl.isEmpty()) {
            String label = ctx.buttonLabel.isEmpty() ? "Open" : ctx.buttonLabel;
            p.buttons.add(new String[]{label.length() > 32 ? label.substring(0, 32) : label, buttonUrl});
            p.buttons.add(new String[]{"Get Voidstrap", DOWNLOAD});
        } else {
            p.buttons.add(new String[]{"Discord", DISCORD});
            p.buttons.add(new String[]{"Github", GITHUB});
        }
        return p;
    }

    private static String clip(String value) {
        String t = (value == null ? "" : value).replace('\r', ' ').replace('\n', ' ').trim().replaceAll(" {2,}", " ");
        return t.length() > 128 ? t.substring(0, 125).trim() + "..." : t;
    }

    private static void fetchAvatar(Store s) {
        long userId;
        try {
            userId = Long.parseLong(s.setting(USER_ID, "0"));
        } catch (NumberFormatException e) {
            return;
        }
        if (userId <= 0 || userId == avatarFor) return;
        avatarFor = userId;
        s.work.execute(() -> {
            try {
                JSONObject user = Net.json("https://users.roblox.com/v1/users/" + userId);
                JSONArray heads = Net.json("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + userId + "&size=150x150&format=Png&isCircular=true").optJSONArray("data");
                JSONObject head = heads == null ? null : heads.optJSONObject(0);
                String url = head == null ? "" : head.optString("imageUrl", "");
                if (!url.startsWith("https://") || url.length() > 250) return;
                String host = android.net.Uri.parse(url).getHost();
                if (host == null || !(host.endsWith(".rbxcdn.com") || host.endsWith(".roblox.com"))) return;
                String display = user.optString("displayName", "");
                s.main.post(() -> {
                    avatarUrl = url;
                    avatarText = display.isEmpty() ? user.optString("name", "") : display;
                    lastSignature = "";
                    update();
                });
            } catch (IOException | JSONException ignored) {
            }
        });
    }
}
