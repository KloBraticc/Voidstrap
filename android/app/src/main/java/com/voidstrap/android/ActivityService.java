package com.voidstrap.android;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;

import androidx.core.app.NotificationCompat;
import androidx.core.app.ServiceCompat;
import androidx.core.content.ContextCompat;

import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ActivityService extends Service implements ActivityWatcher.Listener {
    private static final String CHANNEL_TRACKING = "activity";
    private static final String CHANNEL_JOIN = "activity_join";
    private static final String CHANNEL_JOIN_QUIET = "activity_join_quiet";
    private static final int ID_TRACKING = 41;
    private static final int ID_JOIN = 42;
    private static final int ID_JOIN_ALERT = 43;
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
    private final AtomicBoolean streamClosed = new AtomicBoolean();
    private Store store;
    private Thread worker;
    private volatile boolean stopping;
    private volatile boolean sawRoblox;
    private boolean discord;
    private long sessionStart;
    private Integrations.Presence presence;
    private Integrations.Presence original;
    private JSONObject account;
    private final ArrayDeque<String[]> rpcQueue = new ArrayDeque<>();
    private String lastSignature = "";
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
        Crash.run("notification channels", () -> channels(this));
        int type = Build.VERSION.SDK_INT >= 34 ? ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE : 0;
        Boolean foreground = Crash.call("foreground service", () -> {
            ServiceCompat.startForeground(this, ID_TRACKING, tracking(getString(R.string.activity_waiting)), type);
            return Boolean.TRUE;
        }, Boolean.FALSE);
        if (!Boolean.TRUE.equals(foreground)) {
            running = false;
            stopSelf();
            return;
        }
        sessionStart = System.currentTimeMillis();
        String target = Targets.selected(this);
        worker = new Thread(() -> Crash.run("activity watcher", () -> loop(target)), "activity");
        worker.setDaemon(true);
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
        if (worker != null) Crash.run("watcher stop", worker::interrupt);
        Crash.run("discord stop", this::stopDiscord);
        Crash.run("watcher release", watcher::release);
        if (store != null) store.changed();
        super.onDestroy();
    }

    private void closeSocket() {
        streamClosed.set(true);
    }

    private void loop(String target) {
        int failures = 0;
        while (!stopping) {
            if (!Helper.running()) Helper.keepRootHelper(this);
            try {
                Helper.stream(target, this::line, streamClosed);
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
        if (line == null) return;
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
            if (SmartJoin.rejoining()) return;
            closeSocket();
            stopping = true;
            store.main.post(this::stopSelf);
            return;
        }
        if (line.equals(HelperServer.STREAM_IDLE)) {
            if (SmartJoin.rejoining()) return;
            if (sawRoblox || System.currentTimeMillis() - sessionStart > NO_ROBLOX_MS) {
                closeSocket();
                stopping = true;
                store.main.post(this::stopSelf);
            }
            return;
        }
        watcher.feed(line);
    }

    static void channels(Context c) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationManager nm = c.getSystemService(NotificationManager.class);
        if (nm == null) return;
        NotificationChannel tracking = new NotificationChannel(CHANNEL_TRACKING, c.getString(R.string.activity_channel), NotificationManager.IMPORTANCE_MIN);
        tracking.setShowBadge(false);
        NotificationChannel join = new NotificationChannel(CHANNEL_JOIN, c.getString(R.string.activity_join_channel), NotificationManager.IMPORTANCE_HIGH);
        join.setShowBadge(false);
        NotificationChannel quiet = new NotificationChannel(CHANNEL_JOIN_QUIET, c.getString(R.string.activity_join_quiet_channel), NotificationManager.IMPORTANCE_LOW);
        quiet.setShowBadge(false);
        nm.createNotificationChannel(tracking);
        nm.createNotificationChannel(join);
        nm.createNotificationChannel(quiet);
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
        if (nm == null || stopping) return;
        Crash.run("tracking notification", () -> nm.notify(ID_TRACKING, tracking(text)));
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
        store.work.execute(() -> SmartJoin.onJoined(this, data, this::joinAlert));
        store.work.execute(() -> {
            Integrations.Game g = fetch(data);
            JSONObject who = data.userId > 0 && Integrations.on(store, Integrations.ACCOUNT) ? Integrations.account(this, data.userId, false) : null;
            store.main.post(() -> {
                synchronized (this) {
                    if (gen != generation || stopping) return;
                }
                if (who != null) account = who;
                if (data.userId > 0 && !String.valueOf(data.userId).equals(store.setting(Integrations.USER_ID, ""))) store.putSetting(Integrations.USER_ID, String.valueOf(data.userId));
                String shown = Integrations.shownName(store, g);
                nowPlaying = shown;
                store.changed();
                if (Notify.on(store, Notify.TRACK_GAME)) updateTracking(getString(R.string.activity_playing, shown));
                if (Integrations.on(store, Integrations.NOTIFY) && canNotify()) joinNotification(shown, Integrations.on(store, Integrations.LOCATION) ? g.location : "");
                if (discord) {
                    int flags = store.flags.active().values.size();
                    setPresence(Integrations.game(store, data, g, account, flags));
                    while (!rpcQueue.isEmpty()) {
                        String[] queued = rpcQueue.poll();
                        rpc(queued[0], queued[1]);
                    }
                }
            });
        });
    }

    private NotificationCompat.Builder alert(String title, String text, long automatic) {
        boolean popup = Notify.on(store, Notify.POPUP);
        NotificationCompat.Builder b = new NotificationCompat.Builder(this, popup ? CHANNEL_JOIN : CHANNEL_JOIN_QUIET)
                .setSmallIcon(R.drawable.ic_stat_activity)
                .setContentTitle(title)
                .setContentText(text)
                .setAutoCancel(true)
                .setPriority(popup ? NotificationCompat.PRIORITY_HIGH : NotificationCompat.PRIORITY_LOW)
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                .setContentIntent(openApp());
        long timeout = Notify.joinTimeout(store, automatic);
        if (timeout > 0) b.setTimeoutAfter(timeout);
        return b;
    }

    private void joinAlert(String text) {
        if (stopping || !canNotify() || !Notify.on(store, Notify.SMART_ALERTS)) return;
        Notification n = alert(SmartJoin.title(this), text, 10000)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .build();
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm != null) Crash.run("join alert", () -> nm.notify(ID_JOIN_ALERT, n));
    }

    private void joinNotification(String game, String location) {
        String text = location.isEmpty() ? getString(R.string.activity_join_body) : getString(R.string.activity_join_location, location);
        Notification n = alert(getString(R.string.activity_join_title, game), text, 8000).build();
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm != null) Crash.run("join notification", () -> nm.notify(ID_JOIN, n));
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
            if (!discord || !watcher.inGame()) return;
            if (presence == null || original == null) {
                while (rpcQueue.size() >= 64) rpcQueue.poll();
                rpcQueue.add(new String[]{command, json});
                return;
            }
            rpc(command, json);
        });
    }

    private void rpc(String command, String json) {
        if (command.equals("SetLaunchData")) {
            ActivityWatcher.Data d = watcher.current();
            if (d == null) return;
            Integrations.Presence p = presence.copy();
            p.buttons.clear();
            Integrations.Presence rebuilt = Integrations.game(store, d, new Integrations.Game(), account, 0);
            p.buttons.addAll(rebuilt.buttons);
            push(p);
            return;
        }
        Integrations.Presence p = Integrations.applyRpc(presence, original, json);
        if (p != null) push(p);
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
        rpcQueue.clear();
        long userId = watcher.userId();
        if (account != null || userId <= 0 || !Integrations.on(store, Integrations.ACCOUNT)) {
            setPresence(Integrations.idle(store, sessionStart, account));
            return;
        }
        store.work.execute(() -> {
            JSONObject who = Integrations.account(this, userId, false);
            store.main.post(() -> {
                if (who != null) account = who;
                if (discord && !watcher.inGame()) setPresence(Integrations.idle(store, sessionStart, account));
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
        boolean wantLocation = Integrations.on(store, Integrations.LOCATION) || Integrations.on(store, Integrations.RPC_LOCATION);
        try {
            JSONObject o = Core.run("feed.game", Core.feed(this, "place", d.placeId, "universe", d.universeId, "address", d.serverAddress, "location", wantLocation));
            g.name = o.optString("name", g.name);
            g.description = o.optString("description", g.description);
            g.creator = o.optString("creator", g.creator);
            g.verified = o.optBoolean("verified");
            g.icon = o.optString("icon", g.icon);
            JSONObject info = o.optJSONObject("info");
            if (info != null) g.location = Integrations.location(info);
        } catch (IOException ignored) {
        }
        return g;
    }
}
