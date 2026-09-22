package com.voidstrap.android;

import android.content.Context;
import android.os.SystemClock;

import org.json.JSONObject;

final class AppPresence {
    static final String SETTING = "voidRpc";
    private static final long THROTTLE_MS = 1500;

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

    private static android.app.Application app;
    private static boolean visible;
    private static int page;
    private static Scene scene;
    private static long sessionStart;
    private static long lastUpdate;
    private static boolean scheduled;
    private static boolean suppressed;
    private static String lastSignature = "";
    private static JSONObject avatar;
    private static long avatarFor;

    private AppPresence() {
    }

    static boolean enabled(Store s) {
        return "1".equals(s.setting(SETTING, "1"));
    }

    static void shown(Context c, int currentPage) {
        app = (android.app.Application) c.getApplicationContext();
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
        if (id == R.id.nav_smart && SmartJoin.presence() != null) return SmartJoin.presence();
        if (id == R.id.nav_settings) return new String[]{"Settings", "App settings & updates"};
        return new String[]{"Home", "On the Voidstrap home screen"};
    }

    private static Integrations.Presence build() {
        Scene ctx = scene != null && scene.owner == page ? scene : null;
        String[] info = ctx != null ? new String[]{ctx.details, ctx.state} : pageInfo(page);
        JSONObject args = Core.args("scene", ctx != null, "details", info[0], "state", info[1], "version", BuildConfig.VERSION_NAME, "start", sessionStart);
        if (ctx != null) args = Core.merge(args, Core.args("image", ctx.image, "imageText", ctx.imageText, "buttonLabel", ctx.buttonLabel, "buttonUrl", ctx.buttonUrl));
        if (avatar != null) args = Core.merge(args, Core.args("avatar", avatar));
        return Integrations.presence("presence.app", args);
    }

    private static void fetchAvatar(Store s) {
        String saved = s.setting(Integrations.USER_ID, "");
        long userId = saved.matches("[0-9]{1,18}") ? Long.parseLong(saved) : 0;
        if (userId <= 0 || userId == avatarFor) return;
        avatarFor = userId;
        s.work.execute(() -> {
            JSONObject found = Integrations.account(app, userId, true);
            if (found == null) return;
            s.main.post(() -> {
                avatar = found;
                lastSignature = "";
                update();
            });
        });
    }
}
