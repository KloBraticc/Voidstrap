package com.voidstrap.android;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.LocalSocket;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;

import androidx.core.app.NotificationCompat;
import androidx.core.app.ServiceCompat;
import androidx.core.content.ContextCompat;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicReference;

public final class ActivityService extends Service implements ActivityWatcher.Listener {
    private static final String CHANNEL_TRACKING = "activity";
    private static final String CHANNEL_JOIN = "activity_join";
    private static final int ID_TRACKING = 41;
    private static final int ID_JOIN = 42;
    private static final int ID_MATCHMAKER = 43;
    private static final long NO_ROBLOX_MS = 90_000;
    private static final long RETRY_MS = 3000;
    private static final int MAX_FAILURES = 5;
    private static final int LOG_LIMIT = 500;

    public static final class Entry {
        public final long time;
        public final String kind;
        public final String text;

        Entry(String kind, String text) {
            this.time = System.currentTimeMillis();
            this.kind = kind;
            this.text = text;
        }
    }

    private static final ArrayDeque<Entry> LOG = new ArrayDeque<>();
    private static volatile boolean running;
    private static volatile String nowPlaying = "";

    private final ActivityWatcher watcher = new ActivityWatcher(this);
    private final AtomicReference<LocalSocket> socket = new AtomicReference<>();
    private Store store;
    private Thread worker;
    private volatile boolean stopping;
    private volatile boolean sawRoblox;
    private boolean discord;
    private long sessionStart;
    private Integrations.Presence presence;
    private Integrations.Presence original;
    private String lastSignature = "";
    private volatile Integrations.Game user;
    private int generation;

    public static boolean available(Context c) {
        return FlagWriter.mode(c) != FlagWriter.Mode.NONE;
    }

    public static boolean running() {
        return running;
    }

    public static String nowPlaying() {
        return nowPlaying;
    }

    public static List<Entry> log() {
        synchronized (LOG) {
            return new ArrayList<>(LOG);
        }
    }

    public static void clearLog() {
        synchronized (LOG) {
            LOG.clear();
        }
    }

    private static void add(String kind, String text) {
        synchronized (LOG) {
            LOG.addLast(new Entry(kind, text));
            while (LOG.size() > LOG_LIMIT) LOG.removeFirst();
        }
    }

    public static void startNow(Context c) {
        if (running) return;
        Context app = c.getApplicationContext();
        try {
            ContextCompat.startForegroundService(app, new Intent(app, ActivityService.class));
        } catch (IllegalStateException | SecurityException ignored) {
        }
    }

    public static void stop(Context c) {
        c.getApplicationContext().stopService(new Intent(c.getApplicationContext(), ActivityService.class));
    }

    @Override
    public void onCreate() {
        super.onCreate();
        store = Store.get(this);
        running = true;
        channels();
        int type = Build.VERSION.SDK_INT >= 34 ? ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE : 0;
        ServiceCompat.startForeground(this, ID_TRACKING, tracking(getString(R.string.activity_waiting)), type);
        sessionStart = System.currentTimeMillis();
        String target = Targets.selected(this);
        worker = new Thread(() -> loop(target), "activity");
        worker.start();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        return START_NOT_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onDestroy() {
        stopping = true;
        running = false;
        nowPlaying = "";
        closeSocket();
        if (worker != null) worker.interrupt();
        stopDiscord();
        store.changed();
        super.onDestroy();
    }

    private void closeSocket() {
        LocalSocket s = socket.get();
        if (s == null) return;
        try {
            s.close();
        } catch (IOException ignored) {
        }
    }

    private void loop(String target) {
        int failures = 0;
        while (!stopping) {
            if (!Helper.running()) Helper.keepRootHelper(this);
            try {
                Helper.stream(target, this::line, socket);
            } catch (IOException e) {
                if (stopping) return;
            }
            if (++failures >= MAX_FAILURES) break;
            SystemClock.sleep(RETRY_MS);
        }
        finish();
    }

    private void finish() {
        if (stopping) return;
        store.main.post(this::stopSelf);
    }

    private void line(String line) {
        if (line.startsWith(HelperServer.STREAM_START)) {
            if (!sawRoblox) {
                sawRoblox = true;
                sessionStart = System.currentTimeMillis();
                store.main.post(this::startDiscord);
            }
            return;
        }
        if (line.equals(HelperServer.STREAM_EXIT)) {
            watcher.reset();
            if (Reroute.rejoining()) return;
            closeSocket();
            stopping = true;
            store.main.post(this::stopSelf);
            return;
        }
        if (line.equals(HelperServer.STREAM_IDLE)) {
            if (Reroute.rejoining()) return;
            if (sawRoblox || System.currentTimeMillis() - sessionStart > NO_ROBLOX_MS) {
                closeSocket();
                stopping = true;
                store.main.post(this::stopSelf);
            }
            return;
        }
        watcher.feed(line);
    }

    private void channels() {
        NotificationManager nm = getSystemService(NotificationManager.class);
        NotificationChannel tracking = new NotificationChannel(CHANNEL_TRACKING, getString(R.string.activity_channel), NotificationManager.IMPORTANCE_MIN);
        tracking.setShowBadge(false);
        NotificationChannel join = new NotificationChannel(CHANNEL_JOIN, getString(R.string.activity_join_channel), NotificationManager.IMPORTANCE_HIGH);
        join.setShowBadge(false);
        nm.createNotificationChannel(tracking);
        nm.createNotificationChannel(join);
    }

    private PendingIntent openApp() {
        Intent i = new Intent(this, MainActivity.class).putExtra(MainActivity.EXTRA_TAB, R.id.nav_integrations).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        return PendingIntent.getActivity(this, 0, i, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    private Notification tracking(String text) {
        return new NotificationCompat.Builder(this, CHANNEL_TRACKING)
                .setSmallIcon(R.drawable.ic_stat_activity)
                .setContentTitle(getString(R.string.activity_notification_title))
                .setContentText(text)
                .setOngoing(true)
                .setSilent(true)
                .setPriority(NotificationCompat.PRIORITY_MIN)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .setContentIntent(openApp())
                .build();
    }

    private void updateTracking(String text) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm != null && !stopping) nm.notify(ID_TRACKING, tracking(text));
    }

    private boolean canNotify() {
        return Build.VERSION.SDK_INT < 33 || checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) == android.content.pm.PackageManager.PERMISSION_GRANTED;
    }

    @Override
    public void onJoined(ActivityWatcher.Data data) {
        int gen;
        synchronized (this) {
            gen = ++generation;
        }
        add("game", getString(R.string.activity_log_joined, data.placeId));
        store.work.execute(() -> Reroute.onJoined(this, data, this::matchmakerAlert));
        store.work.execute(() -> {
            Integrations.Game g = fetch(data);
            store.main.post(() -> {
                synchronized (this) {
                    if (gen != generation || stopping) return;
                }
                String shown = Integrations.shownName(store, g);
                nowPlaying = shown;
                store.changed();
                updateTracking(getString(R.string.activity_playing, shown));
                if (Integrations.on(store, Integrations.NOTIFY) && canNotify()) joinNotification(shown, g.location);
                if (discord) {
                    int flags = store.flags.active().values.size();
                    setPresence(Integrations.game(store, data, g, flags));
                }
            });
        });
    }

    private void matchmakerAlert(String text) {
        if (stopping || !canNotify()) return;
        Notification n = new NotificationCompat.Builder(this, CHANNEL_JOIN)
                .setSmallIcon(R.drawable.ic_stat_activity)
                .setContentTitle(getString(R.string.matchmaker_title))
                .setContentText(text)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .setAutoCancel(true)
                .setTimeoutAfter(10000)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                .setContentIntent(openApp())
                .build();
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm != null) nm.notify(ID_MATCHMAKER, n);
    }

    private void joinNotification(String game, String location) {
        String text = location.isEmpty() ? getString(R.string.activity_join_body) : getString(R.string.activity_join_location, location);
        Notification n = new NotificationCompat.Builder(this, CHANNEL_JOIN)
                .setSmallIcon(R.drawable.ic_stat_activity)
                .setContentTitle(getString(R.string.activity_join_title, game))
                .setContentText(text)
                .setAutoCancel(true)
                .setTimeoutAfter(8000)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                .setContentIntent(openApp())
                .build();
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm != null) nm.notify(ID_JOIN, n);
    }

    @Override
    public void onLeft(ActivityWatcher.Data data) {
        synchronized (this) {
            generation++;
        }
        add("game", getString(R.string.activity_log_left));
        store.main.post(() -> {
            nowPlaying = "";
            store.changed();
            updateTracking(getString(R.string.activity_waiting));
            if (!stopping) idle();
        });
    }

    @Override
    public void onMenu() {
        store.main.post(() -> {
            if (presence == null && !stopping) idle();
        });
    }

    @Override
    public void onLog(String kind, String text) {
        add(kind, text);
        store.changed();
    }

    @Override
    public void onRpc(String command, String json) {
        store.main.post(() -> {
            if (!discord || presence == null || original == null || !watcher.inGame()) return;
            if (command.equals("SetLaunchData")) {
                ActivityWatcher.Data d = watcher.current();
                if (d == null) return;
                Integrations.Presence p = presence.copy();
                p.buttons.clear();
                Integrations.Presence rebuilt = Integrations.game(store, d, new Integrations.Game(), 0);
                p.buttons.addAll(rebuilt.buttons);
                push(p);
                return;
            }
            try {
                push(Integrations.applyRpc(presence, original, new JSONObject(json)));
            } catch (JSONException ignored) {
            }
        });
    }

    private void startDiscord() {
        if (stopping || !Integrations.on(store, Integrations.RPC) || !DiscordRpc.installed(this)) return;
        DiscordRpc.ROBLOX.start(this);
        discord = true;
        if (!watcher.inGame()) idle();
    }

    private void stopDiscord() {
        if (!discord) return;
        discord = false;
        presence = null;
        original = null;
        lastSignature = "";
        DiscordRpc.ROBLOX.stop();
    }

    private void idle() {
        if (!discord) return;
        long userId = watcher.userId();
        Integrations.Game cached = user;
        if (cached != null || userId <= 0 || !Integrations.on(store, Integrations.ACCOUNT)) {
            setPresence(Integrations.idle(store, cached, sessionStart));
            return;
        }
        store.work.execute(() -> {
            Integrations.Game u = new Integrations.Game();
            account(u, userId);
            store.main.post(() -> {
                user = u;
                if (discord && !watcher.inGame()) setPresence(Integrations.idle(store, u, sessionStart));
            });
        });
    }

    private void setPresence(Integrations.Presence p) {
        original = p.copy();
        push(p);
    }

    private void push(Integrations.Presence p) {
        presence = p;
        String signature = p.signature();
        if (signature.equals(lastSignature)) return;
        lastSignature = signature;
        DiscordRpc.ROBLOX.update(p);
    }

    private Integrations.Game fetch(ActivityWatcher.Data d) {
        Integrations.Game g = new Integrations.Game();
        long universe = d.universeId;
        try {
            if (universe <= 0) universe = Net.json("https://apis.roblox.com/universes/v1/places/" + d.placeId + "/universe").optLong("universeId", 0);
            if (universe > 0) {
                JSONArray games = Net.json("https://games.roblox.com/v1/games?universeIds=" + universe).optJSONArray("data");
                JSONObject game = games == null ? null : games.optJSONObject(0);
                if (game != null) {
                    g.name = Store.clip(game.optString("name", ""), 200);
                    g.description = Store.clip(game.optString("description", ""), 1000);
                    JSONObject creator = game.optJSONObject("creator");
                    if (creator != null) {
                        g.creator = Store.clip(creator.optString("name", ""), 100);
                        g.verified = creator.optBoolean("hasVerifiedBadge");
                    }
                }
                JSONArray icons = Net.json("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + universe + "&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false").optJSONArray("data");
                JSONObject icon = icons == null ? null : icons.optJSONObject(0);
                String url = icon == null ? "" : icon.optString("imageUrl", "");
                if (url.startsWith("https://")) g.icon = url;
            }
        } catch (IOException | JSONException ignored) {
        }
        boolean wantLocation = Integrations.on(store, Integrations.LOCATION) || Integrations.on(store, Integrations.RPC_LOCATION);
        if (wantLocation && d.serverAddress.matches("[0-9.]{7,15}")) {
            try {
                g.location = Integrations.location(Net.json("https://ipinfo.io/" + d.serverAddress + "/json"));
            } catch (IOException | JSONException ignored) {
            }
        }
        if (d.userId > 0 && !String.valueOf(d.userId).equals(store.setting(AppPresence.USER_ID, ""))) store.putSetting(AppPresence.USER_ID, String.valueOf(d.userId));
        if (Integrations.on(store, Integrations.ACCOUNT) && d.userId > 0) {
            Integrations.Game cached = user;
            if (cached != null && !cached.userImage.isEmpty()) {
                g.userImage = cached.userImage;
                g.userText = cached.userText;
            } else {
                account(g, d.userId);
                user = g;
            }
        }
        return g;
    }

    private static void account(Integrations.Game g, long userId) {
        try {
            JSONObject u = Net.json("https://users.roblox.com/v1/users/" + userId);
            JSONArray heads = Net.json("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + userId + "&size=180x180&format=Png&isCircular=false").optJSONArray("data");
            JSONObject head = heads == null ? null : heads.optJSONObject(0);
            String url = head == null ? "" : head.optString("imageUrl", "");
            if (url.startsWith("https://")) {
                g.userImage = url;
                g.userText = u.optString("displayName", "") + " (@" + u.optString("name", "") + ")";
            }
        } catch (IOException | JSONException ignored) {
        }
    }
}
