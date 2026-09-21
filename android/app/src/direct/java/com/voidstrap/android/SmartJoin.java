package com.voidstrap.android;

import android.content.Context;

import java.util.List;

final class SmartJoin {
    interface Alert {
        void show(String text);
    }

    private SmartJoin() {
    }

    static boolean available() {
        return true;
    }

    static String title(Context c) {
        return c.getString(R.string.matchmaker_title);
    }

    static String alertsTitle(Context c) {
        return c.getString(R.string.matchmaker_notify_alerts);
    }

    static String alertsBody(Context c) {
        return c.getString(R.string.matchmaker_notify_alerts_body);
    }

    static String openBody(Context c) {
        return c.getString(R.string.matchmaker_open_body);
    }

    static Page page() {
        return new MatchmakerFragment();
    }

    static String[] presence() {
        return new String[]{"Voidstrap Matchmaker", "Picking the closest servers"};
    }

    static Deeplink rewrite(Context app, Deeplink destination) {
        Store store = Store.get(app);
        if (destination == null || !destination.isPlace() || destination.instanceId != null || destination.linkCode != null) return destination;
        if (!Matchmaker.enabled(store) || Matchmaker.excluded(store).contains(destination.placeId)) return destination;
        if (RobloxLogin.cookie(app) == null) {
            toast(app, app.getString(R.string.matchmaker_no_login));
            return destination;
        }
        toast(app, app.getString(R.string.matchmaker_finding));
        Matchmaker.Candidate best = Matchmaker.pick(app, destination.placeId, null, null);
        if (best == null) {
            String why = Matchmaker.lastReport();
            if (!why.isEmpty()) toast(app, app.getString(R.string.matchmaker_fallback, why));
            return destination;
        }
        Deeplink picked = Deeplink.place(destination.placeId, best.jobId, null);
        if (picked == null) return destination;
        Reroute.expect(destination.placeId, best.dc.city);
        toast(app, app.getString(R.string.matchmaker_joining, best.dc.city, best.estimatedPing));
        return picked;
    }

    private static void toast(Context app, String text) {
        Store.get(app).main.post(() -> Notify.toast(app, Notify.SMART, text));
    }

    static boolean rejoining() {
        return Reroute.rejoining();
    }

    static void onJoined(Context c, ActivityWatcher.Data data, Alert alert) {
        Reroute.onJoined(c, data, alert);
    }
}
