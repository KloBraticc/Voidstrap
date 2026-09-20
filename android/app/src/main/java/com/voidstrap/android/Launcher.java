package com.voidstrap.android;

import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.SystemClock;

public final class Launcher {
    public enum Result { HANDED_OFF, NOT_INSTALLED, DISABLED, NO_HANDLER, DESTINATION_UNSUPPORTED, BUSY, FAILED }

    public interface Done {
        void run(Result result);
    }

    private static final long REPEAT_GUARD_MS = 1500;
    private static long lastHandoff;
    private static boolean busy;

    private Launcher() {
    }

    public static void launch(Context c, Deeplink destination, String name, Done done) {
        if (busy || SystemClock.elapsedRealtime() - lastHandoff < REPEAT_GUARD_MS) {
            done.run(Result.BUSY);
            return;
        }
        Context app = c.getApplicationContext();
        Store store = Store.get(app);
        String pkg = Targets.selected(app);
        Targets.State state = Targets.resolve(app, pkg);
        if (!state.installed || !state.enabled) {
            done.run(finish(app, destination, name, pkg, state.installed ? Result.DISABLED : Result.NOT_INSTALLED));
            return;
        }
        busy = true;
        String flags = store.flags.active().valuesJson().toString();
        store.work.execute(() -> {
            int flagIssue = FlagWriter.syncForLaunch(app, pkg, flags);
            boolean mods = ModEngine.syncForLaunch(app, pkg);
            boolean track = Integrations.on(store, Integrations.TRACKING) && ActivityService.available(app);
            Deeplink target = smartJoin(app, destination);
            store.main.post(() -> {
                busy = false;
                if (track) ActivityService.startNow(app);
                Result r = start(app, target, pkg);
                if (flagIssue != 0 && r == Result.HANDED_OFF) android.widget.Toast.makeText(app, flagIssue, android.widget.Toast.LENGTH_LONG).show();
                else if (!mods && r == Result.HANDED_OFF) android.widget.Toast.makeText(app, R.string.mods_launch_not_applied, android.widget.Toast.LENGTH_LONG).show();
                done.run(finish(app, destination, name, pkg, r));
            });
        });
    }

    private static Deeplink smartJoin(Context app, Deeplink destination) {
        Store store = Store.get(app);
        if (destination == null || !destination.isPlace() || destination.instanceId != null || destination.linkCode != null) return destination;
        if (!Matchmaker.enabled(store) || Matchmaker.excluded(store).contains(destination.placeId)) return destination;
        if (RobloxLogin.cookie(app) == null) {
            store.main.post(() -> android.widget.Toast.makeText(app, R.string.matchmaker_no_login, android.widget.Toast.LENGTH_LONG).show());
            return destination;
        }
        store.main.post(() -> android.widget.Toast.makeText(app, R.string.matchmaker_finding, android.widget.Toast.LENGTH_SHORT).show());
        Matchmaker.Candidate best = Matchmaker.pick(app, destination.placeId, null, null);
        if (best == null) return destination;
        Deeplink picked = Deeplink.place(destination.placeId, best.jobId, null);
        if (picked == null) return destination;
        Reroute.expect(destination.placeId, best.dc.city);
        String text = app.getString(R.string.matchmaker_joining, best.dc.city, best.estimatedPing);
        store.main.post(() -> android.widget.Toast.makeText(app, text, android.widget.Toast.LENGTH_LONG).show());
        return picked;
    }

    private static Result start(Context app, Deeplink destination, String pkg) {
        Intent intent = destination == null
                ? app.getPackageManager().getLaunchIntentForPackage(pkg)
                : new Intent(Intent.ACTION_VIEW, destination.toUri()).setPackage(pkg).addCategory(Intent.CATEGORY_BROWSABLE);
        if (intent == null) return Result.NO_HANDLER;
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            app.startActivity(intent);
            lastHandoff = SystemClock.elapsedRealtime();
            return Result.HANDED_OFF;
        } catch (ActivityNotFoundException e) {
            return destination == null ? Result.NO_HANDLER : Result.DESTINATION_UNSUPPORTED;
        } catch (SecurityException e) {
            return Result.FAILED;
        }
    }

    private static Result finish(Context app, Deeplink destination, String name, String pkg, Result result) {
        Store store = Store.get(app);
        if (result == Result.NOT_INSTALLED && destination != null) {
            store.putSetting("pending", destination.toUri().toString());
            store.putSetting("pendingName", name == null ? "" : name);
        }
        store.record(new Store.Launch(System.currentTimeMillis(), destination == null ? 0 : destination.placeId, name == null ? "" : name, pkg, result.name()));
        return result;
    }

    public static int message(Result r) {
        switch (r) {
            case HANDED_OFF: return R.string.launch_handed_off;
            case NOT_INSTALLED: return R.string.launch_not_installed;
            case DISABLED: return R.string.launch_disabled;
            case NO_HANDLER: return R.string.launch_no_handler;
            case DESTINATION_UNSUPPORTED: return R.string.launch_destination_unsupported;
            case BUSY: return R.string.launch_busy;
            default: return R.string.launch_failed;
        }
    }

    public static void openStore(Context c, String pkg) {
        Intent market = new Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=" + pkg)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            c.startActivity(market);
        } catch (ActivityNotFoundException e) {
            Ui.openWeb(c, "https://play.google.com/store/apps/details?id=" + pkg);
        }
    }
}
