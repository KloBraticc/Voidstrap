package com.voidstrap.android;

import android.content.Context;
import android.util.Log;

import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

final class Reroute {
    interface Alert {
        void show(String text);
    }

    private static final double MIN_GAIN_MS = 25.0;
    private static final double ACCEPTABLE_PING_MS = 60.0;
    private static final long SETTLE_MS = 2000;
    private static final long ATTEMPT_RESET_MS = 10 * 60_000L;
    private static final long HOP_COOLDOWN_MS = 90_000L;

    private static final Map<Long, int[]> ATTEMPTS = new HashMap<>();
    private static final Map<Long, Long> ATTEMPT_AT = new HashMap<>();
    private static final Map<Long, Set<String>> TRIED = new HashMap<>();
    private static long lastHop;
    private static volatile long rejoiningUntil;
    private static String rejoinTarget;
    private static long rejoinPlace;

    private Reroute() {
    }

    private static synchronized void clearAttempt(long placeId) {
        ATTEMPTS.remove(placeId);
        ATTEMPT_AT.remove(placeId);
        TRIED.remove(placeId);
    }

    private static synchronized int nextAttempt(long placeId) {
        Long at = ATTEMPT_AT.get(placeId);
        long now = System.currentTimeMillis();
        if (at == null || now - at > ATTEMPT_RESET_MS) ATTEMPTS.put(placeId, new int[]{0});
        ATTEMPT_AT.put(placeId, now);
        int[] n = ATTEMPTS.get(placeId);
        return ++n[0];
    }

    private static synchronized Set<String> tried(long placeId) {
        Set<String> t = TRIED.get(placeId);
        return t == null ? new HashSet<>() : new HashSet<>(t);
    }

    private static synchronized void addTried(long placeId, String jobId) {
        if (jobId == null || jobId.isEmpty()) return;
        Set<String> t = TRIED.get(placeId);
        if (t == null) TRIED.put(placeId, t = new HashSet<>());
        t.add(jobId);
    }

    static void onJoined(Context c, ActivityWatcher.Data d, Alert alert) {
        Store s = Store.get(c);
        if (d.serverAddress.isEmpty() || Matchmaker.isPrivate(d.serverAddress)) return;
        if (Matchmaker.excluded(s).contains(d.placeId)) {
            clearAttempt(d.placeId);
            return;
        }
        if (!Matchmaker.enabled(s)) return;
        if (d.serverType != ActivityWatcher.ServerType.PUBLIC) {
            clearAttempt(d.placeId);
            return;
        }
        if (RobloxLogin.cookie(c) == null) return;
        try {
            Thread.sleep(SETTLE_MS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return;
        }
        Matchmaker.Geo geo = Matchmaker.geo(c);
        if (geo == null) return;
        Matchmaker.Datacenter current = Matchmaker.lookup(c, d.serverAddress);
        if (current == null) {
            Log.i(Matchmaker.TAG, "Current datacenter could not be resolved, staying put");
            clearAttempt(d.placeId);
            return;
        }
        int currentPing = Matchmaker.estimatePingMs(Matchmaker.haversineKm(geo.lat, geo.lon, current.lat, current.lon));
        boolean currentBlocked = Matchmaker.blocked(s).contains(current.key());
        String preferred = Matchmaker.preferred(s);
        boolean hasPreferred = !preferred.isEmpty();
        boolean wantsOther = hasPreferred && !Matchmaker.matchesPreferred(current, preferred);
        if (hasPreferred && !wantsOther && !currentBlocked) {
            clearRejoin();
            clearAttempt(d.placeId);
            alert.show(c.getString(R.string.matchmaker_alert_preferred, current.city, currentPing));
            return;
        }
        boolean viaRejoin = rejoinTarget != null && rejoinPlace == d.placeId;
        boolean landedOnTarget = !viaRejoin || rejoinTarget.isEmpty() || rejoinTarget.equalsIgnoreCase(current.city);
        if (viaRejoin && !currentBlocked && !wantsOther && landedOnTarget) {
            clearRejoin();
            clearAttempt(d.placeId);
            alert.show(c.getString(R.string.matchmaker_alert_connected, current.city, currentPing));
            return;
        }
        if (!viaRejoin && System.currentTimeMillis() - lastHop < HOP_COOLDOWN_MS) return;
        Set<String> tried = tried(d.placeId);
        if (!d.jobId.isEmpty()) tried.add(d.jobId);
        Matchmaker.Candidate best = Matchmaker.pick(c, d.placeId, tried, preferred);
        if (best == null) {
            clearRejoin();
            clearAttempt(d.placeId);
            if (currentBlocked) alert.show(c.getString(R.string.matchmaker_alert_blocked_none, current.city));
            return;
        }
        boolean sameDc = current.key().equalsIgnoreCase(best.dc.key());
        boolean bestPreferred = hasPreferred && Matchmaker.matchesPreferred(best.dc, preferred);
        double gain = currentPing - best.estimatedPing;
        boolean move = (currentBlocked && !sameDc) || (wantsOther && bestPreferred) || (!sameDc && !hasPreferred && currentPing > ACCEPTABLE_PING_MS && gain >= MIN_GAIN_MS);
        if (!move) {
            clearRejoin();
            clearAttempt(d.placeId);
            if (currentBlocked) alert.show(c.getString(R.string.matchmaker_alert_blocked_same, current.city));
            else if (wantsOther) alert.show(c.getString(R.string.matchmaker_alert_preferred_empty, preferred.split("\\|")[0], current.city));
            else alert.show(c.getString(R.string.matchmaker_alert_good, currentPing));
            return;
        }
        int max = Matchmaker.maxRetries(s);
        int attempt = nextAttempt(d.placeId);
        if (attempt > max) {
            clearRejoin();
            clearAttempt(d.placeId);
            alert.show(c.getString(R.string.matchmaker_alert_limit, max, current.city));
            return;
        }
        addTried(d.placeId, d.jobId);
        addTried(d.placeId, best.jobId);
        String text = c.getString(R.string.matchmaker_alert_moving, best.dc.city, best.estimatedPing);
        if (attempt > 1) text += " " + c.getString(R.string.matchmaker_alert_attempt, attempt, max);
        if (best.blockedClosestCity != null) text += "\n" + c.getString(R.string.matchmaker_alert_blocked_closer, best.blockedClosestCity);
        alert.show(text);
        lastHop = System.currentTimeMillis();
        synchronized (Reroute.class) {
            rejoinTarget = best.dc.city;
            rejoinPlace = d.placeId;
        }
        Log.i(Matchmaker.TAG, "Rerouting from " + current.city + " (" + currentPing + "ms) to " + best.dc.city + " (" + best.estimatedPing + "ms)");
        rejoiningUntil = System.currentTimeMillis() + 45_000;
        rejoin(c, d.placeId, best.jobId);
    }

    static synchronized void expect(long placeId, String city) {
        rejoinTarget = city;
        rejoinPlace = placeId;
    }

    static boolean rejoining() {
        return System.currentTimeMillis() < rejoiningUntil;
    }

    private static synchronized void clearRejoin() {
        rejoinTarget = null;
        rejoinPlace = 0;
    }

    private static void rejoin(Context c, long placeId, String jobId) {
        Deeplink link = Deeplink.place(placeId, jobId, null);
        if (link == null) return;
        String pkg = Targets.selected(c);
        String script = "am force-stop " + ModEngine.quote(pkg) + "\n"
                + "sleep 1\n"
                + "am start -a android.intent.action.VIEW -c android.intent.category.BROWSABLE -d " + ModEngine.quote(link.toUri().toString()) + " " + ModEngine.quote(pkg) + " >/dev/null 2>&1 || exit 1\n"
                + "exit 0\n";
        int r = FlagWriter.shell(c, script, 30);
        if (r != 0) Log.w(Matchmaker.TAG, "Rejoin failed with " + r);
    }
}
