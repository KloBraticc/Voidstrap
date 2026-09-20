package com.voidstrap.android;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.drawable.Drawable;
import android.os.Build;

import androidx.core.content.pm.PackageInfoCompat;

import java.util.ArrayList;
import java.util.List;

public final class Targets {
    public static final String GLOBAL = "com.roblox.client";
    public static final String VN = "com.roblox.client.vnggames";
    public static final String[] ALL = {GLOBAL, VN};

    public static final class State {
        public String pkg;
        public boolean installed;
        public boolean enabled;
        public boolean launchable;
        public String versionName;
        public long versionCode;
        public Drawable icon;

        public boolean ready() {
            return installed && enabled && launchable;
        }
    }

    public static int nameRes(String pkg) {
        return VN.equals(pkg) ? R.string.target_vn : R.string.target_global;
    }

    public static State resolve(Context c, String pkg) {
        State s = new State();
        s.pkg = pkg;
        PackageManager pm = c.getPackageManager();
        try {
            PackageInfo info = Build.VERSION.SDK_INT >= 33
                    ? pm.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
                    : pm.getPackageInfo(pkg, 0);
            ApplicationInfo ai = info.applicationInfo;
            s.installed = true;
            s.enabled = ai == null || ai.enabled;
            s.versionName = info.versionName;
            s.versionCode = PackageInfoCompat.getLongVersionCode(info);
            s.launchable = pm.getLaunchIntentForPackage(pkg) != null;
            if (ai != null) s.icon = ai.loadIcon(pm);
        } catch (PackageManager.NameNotFoundException ignored) {
            s.installed = false;
        }
        return s;
    }

    public static String selected(Context c) {
        String chosen = Store.get(c).setting("target", null);
        if (chosen != null) {
            for (String p : ALL) if (p.equals(chosen)) return p;
        }
        for (String p : ALL) if (resolve(c, p).installed) return p;
        return GLOBAL;
    }

    public static List<String> offered(Context c) {
        List<String> out = new ArrayList<>();
        String sel = selected(c);
        for (String p : ALL) {
            if (p.equals(GLOBAL) || p.equals(sel) || resolve(c, p).installed) out.add(p);
        }
        return out;
    }
}
