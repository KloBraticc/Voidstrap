package com.voidstrap.android;

import android.app.Application;

import androidx.appcompat.app.AppCompatDelegate;

public final class VoidstrapApp extends Application {
    @Override
    public void onCreate() {
        super.onCreate();
        ModLog.init(getFilesDir());
        applyTheme(Store.get(this).setting("theme", "system"));
        auditFlags(Store.get(this));
        Store.get(this).work.execute(() -> {
            java.io.File ext = getExternalFilesDir(null);
            if (ext != null) new java.io.File(ext, "start.sh").delete();
        });
        Shortcuts.watch(this);
        Translator.load(this);
        Helper.token(this);
        Helper.onChanged = () -> Store.get(this).changed();
        registerActivityLifecycleCallbacks(new TranslationCallbacks());
    }

    private static final class TranslationCallbacks implements ActivityLifecycleCallbacks {
        @Override
        public void onActivityCreated(android.app.Activity a, android.os.Bundle saved) {
        }

        @Override
        public void onActivityStarted(android.app.Activity a) {
        }

        @Override
        public void onActivityResumed(android.app.Activity a) {
            LiveTranslator.apply(a);
        }

        @Override
        public void onActivityPaused(android.app.Activity a) {
        }

        @Override
        public void onActivityStopped(android.app.Activity a) {
        }

        @Override
        public void onActivitySaveInstanceState(android.app.Activity a, android.os.Bundle out) {
        }

        @Override
        public void onActivityDestroyed(android.app.Activity a) {
        }
    }

    private static void auditFlags(Store store) {
        if ("1".equals(store.setting("flagAudit", "0"))) return;
        for (Flags.Profile p : store.flags.profiles) {
            for (String k : FlagPresets.UNDECLARED) p.values.remove(k);
            FlagPresets.fixFps(p.values);
        }
        store.saveFlags();
        store.putSetting("flagAudit", "1");
    }

    public static final String[] THEMES = {"system", "dark", "light", "voidstrap", "ultragray", "berry", "blue", "cyan", "green", "orange", "pink", "purple", "red", "yellow"};
    private static final int[] OVERLAYS = {0, 0, 0,
            R.style.ThemeOverlay_Voidstrap_Color_Voidstrap, R.style.ThemeOverlay_Voidstrap_Color_UltraGray,
            R.style.ThemeOverlay_Voidstrap_Color_Berry, R.style.ThemeOverlay_Voidstrap_Color_Blue,
            R.style.ThemeOverlay_Voidstrap_Color_Cyan, R.style.ThemeOverlay_Voidstrap_Color_Green,
            R.style.ThemeOverlay_Voidstrap_Color_Orange, R.style.ThemeOverlay_Voidstrap_Color_Pink,
            R.style.ThemeOverlay_Voidstrap_Color_Purple, R.style.ThemeOverlay_Voidstrap_Color_Red,
            R.style.ThemeOverlay_Voidstrap_Color_Yellow};

    private static int index(String value) {
        for (int i = 0; i < THEMES.length; i++) if (THEMES[i].equals(value)) return i;
        return 0;
    }

    public static int themeIndex(String value) {
        return index(value);
    }

    public static int overlay(String value) {
        return OVERLAYS[index(value)];
    }

    public static boolean colorTheme(String value) {
        return overlay(value) != 0;
    }

    public static int nightMode(String value) {
        if ("light".equals(value)) return AppCompatDelegate.MODE_NIGHT_NO;
        if ("system".equals(value) || index(value) == 0) return AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM;
        return AppCompatDelegate.MODE_NIGHT_YES;
    }

    public static void applyTheme(String value) {
        int mode = nightMode(value);
        if (AppCompatDelegate.getDefaultNightMode() != mode) AppCompatDelegate.setDefaultNightMode(mode);
    }
}
