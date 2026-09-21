package com.voidstrap.android;

import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.PopupMenu;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.fragment.app.Fragment;
import androidx.fragment.app.FragmentManager;
import androidx.fragment.app.FragmentTransaction;

import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.navigation.NavigationBarView;

public class MainActivity extends AppCompatActivity {
    public static final String EXTRA_TAB = "tab";
    public static final String EXTRA_PROBLEM = "problem";

    private static final int[] TABS = {R.id.nav_home, R.id.nav_library, R.id.nav_mods, R.id.nav_flags, R.id.nav_flag_editor, R.id.nav_integrations, R.id.nav_smart, R.id.nav_settings};

    private Store store;
    private int current = R.id.nav_home;
    private NavigationBarView nav;
    private LinearLayout sidebarItems;
    private com.google.android.material.button.MaterialButton titleLibrary;
    private static long lastJoinImport;
    private com.google.android.material.button.MaterialButton barTarget;
    private View barSave;
    private View barSaveLaunch;
    private boolean saving;
    private final Runnable barRefresh = this::updateBar;
    private final Runnable presenceRefresh = AppPresence::update;

    @Override
    protected void onCreate(Bundle saved) {
        store = Store.get(this);
        String theme = store.setting("theme", "system");
        setTheme(useBrandAccent() ? R.style.Theme_Voidstrap_Brand : R.style.Theme_Voidstrap);
        int overlay = VoidstrapApp.overlay(theme);
        if (overlay != 0) getTheme().applyStyle(overlay, true);
        getDelegate().setLocalNightMode(VoidstrapApp.nightMode(theme));
        WindowCompat.setDecorFitsSystemWindows(getWindow(), false);
        super.onCreate(saved);
        setContentView(R.layout.activity_main);
        ThemeFade.play(this);
        boolean night = (getResources().getConfiguration().uiMode & android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES;
        androidx.core.view.WindowInsetsControllerCompat bars = WindowCompat.getInsetsController(getWindow(), getWindow().getDecorView());
        bars.setAppearanceLightStatusBars(!night);
        bars.setAppearanceLightNavigationBars(!night);
        if (saved != null) current = saved.getInt(EXTRA_TAB, R.id.nav_home);
        nav = findViewById(R.id.nav);
        sidebarItems = findViewById(R.id.sidebar_items);
        if (nav != null) {
            nav.setOnItemSelectedListener(item -> {
                show(item.getItemId());
                return true;
            });
            nav.setOnItemReselectedListener(item -> {
                if (current != item.getItemId() && parent(current) == item.getItemId()) show(item.getItemId());
            });
        }
        if (sidebarItems != null) buildSidebar();
        titleLibrary = findViewById(R.id.title_library);
        if (titleLibrary != null) titleLibrary.setOnClickListener(v -> show(R.id.nav_library));
        setupBar();
        applyInsets();
        getOnBackPressedDispatcher().addCallback(this, editorBack);
        select(current);
        if (saved == null && (getIntent().getFlags() & Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY) == 0) handle(getIntent());
    }

    @Override
    protected void onRestoreInstanceState(@NonNull Bundle saved) {
        super.onRestoreInstanceState(saved);
        if (nav == null) return;
        MenuItem item = nav.getMenu().findItem(current);
        if (item == null) item = nav.getMenu().findItem(parent(current));
        if (item != null) item.setChecked(true);
    }

    @Override
    public boolean dispatchTouchEvent(android.view.MotionEvent ev) {
        Flyout.track(ev);
        return super.dispatchTouchEvent(ev);
    }

    @Override
    public boolean dispatchGenericMotionEvent(android.view.MotionEvent ev) {
        Flyout.track(ev);
        return super.dispatchGenericMotionEvent(ev);
    }

    private boolean useBrandAccent() {
        return Build.VERSION.SDK_INT < 31 || "brand".equals(store.setting("accent", "system"));
    }

    @Override
    protected void onNewIntent(@NonNull Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handle(intent);
    }

    private boolean isString(int id) {
        try {
            return "string".equals(getResources().getResourceTypeName(id));
        } catch (android.content.res.Resources.NotFoundException e) {
            return false;
        }
    }

    private void handle(Intent intent) {
        if (intent == null) return;
        if (Intent.ACTION_SEND.equals(intent.getAction())) {
            CharSequence text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT);
            if (text != null) {
                LibraryFragment.incoming = Store.clip(text.toString().trim(), Deeplink.MAX_INPUT);
                select(R.id.nav_library);
                store.changed();
            }
            intent.setAction(null);
        }
        int tab = intent.getIntExtra(EXTRA_TAB, 0);
        if (tab != 0 && java.util.Arrays.stream(TABS).anyMatch(t -> t == tab)) {
            select(tab);
            intent.removeExtra(EXTRA_TAB);
        }
        int problem = intent.getIntExtra(EXTRA_PROBLEM, 0);
        if (problem != 0 && isString(problem)) {
            intent.removeExtra(EXTRA_PROBLEM);
            findViewById(R.id.content).post(() -> Ui.say(this, problem));
        }
    }

    @Override
    protected void onDestroy() {
        if (isFinishing()) AppPresence.hidden();
        super.onDestroy();
    }

    @Override
    protected void onStart() {
        super.onStart();
        store.observe(barRefresh);
        store.observe(presenceRefresh);
        updateBar();
        AppPresence.shown(this, current);
    }

    @Override
    protected void onStop() {
        store.unobserve(barRefresh);
        store.unobserve(presenceRefresh);
        super.onStop();
        if (!isChangingConfigurations() && !isFinishing()) release();
    }

    private void setupBar() {
        View bar = findViewById(R.id.action_bar);
        bar.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> fitBar((ViewGroup) v));
        barTarget = findViewById(R.id.bar_target);
        barSave = findViewById(R.id.bar_save);
        barSaveLaunch = findViewById(R.id.bar_save_launch);
        barTarget.setOnClickListener(v -> Actions.targetSheet(this));
        barSave.setOnClickListener(v -> save(false));
        barSaveLaunch.setOnClickListener(v -> save(true));
    }

    private void fitBar(ViewGroup bar) {
        View last = null;
        for (int i = 0; i < bar.getChildCount(); i++) if (bar.getChildAt(i).getVisibility() == View.VISIBLE) last = bar.getChildAt(i);
        if (last == null || last.getRight() <= bar.getWidth() - bar.getPaddingEnd()) return;
        LinearLayout.LayoutParams lp = (LinearLayout.LayoutParams) barSaveLaunch.getLayoutParams();
        if (lp.weight > 0) return;
        lp.width = 0;
        lp.weight = 1;
        lp.setMarginStart(Ui.dp(this, 8));
        barSaveLaunch.setLayoutParams(lp);
        findViewById(R.id.bar_space).setVisibility(View.GONE);
    }

    private void updateBar() {
        String pkg = Targets.selected(this);
        String name = getString(Targets.nameRes(pkg));
        barTarget.setText(name);
        barTarget.setContentDescription(getString(R.string.bar_target_description, name, Actions.status(this, Targets.resolve(this, pkg))));
    }

    private void save(boolean launch) {
        if (saving) return;
        saving = true;
        store.saveAll(ok -> {
            saving = false;
            if (isFinishing() || isDestroyed()) return;
            if (!ok) {
                Ui.alert(this)
                        .setTitle(R.string.save_failed_title)
                        .setMessage(R.string.save_failed_body)
                        .setPositiveButton(R.string.common_ok, null)
                        .show();
                return;
            }
            if (launch) Actions.launch(this, null, getString(Targets.nameRes(Targets.selected(this))));
            else applySavedMods();
        });
    }

    private void applySavedMods() {
        Context app = getApplicationContext();
        if (!ModEngine.enabled(app) || !ModEngine.hasMods(app)) {
            Ui.say(this, R.string.bar_saved);
            return;
        }
        if (FlagWriter.rootMode(app) == FlagWriter.Mode.NONE) {
            Ui.say(this, R.string.bar_saved_no_root);
            return;
        }
        String pkg = Targets.selected(app);
        store.work.execute(() -> {
            ModEngine.Result r = ModEngine.apply(app, pkg, false, null, null);
            store.main.post(() -> {
                if (isFinishing() || isDestroyed()) return;
                Ui.say(this, modsResult(r));
            });
        });
    }

    private int modsResult(ModEngine.Result r) {
        switch (r) {
            case APPLIED:
                return R.string.mods_result_applied;
            case UNCHANGED:
                return R.string.mods_result_unchanged;
            case REMOVED:
                return R.string.mods_result_removed;
            case NO_ROOT:
                return R.string.mods_result_no_root;
            case NOT_INSTALLED:
                return R.string.launch_not_installed;
            case NO_SPACE:
                return R.string.mods_error_space_full;
            case CANCELLED:
                return R.string.mods_error_cancelled_full;
            default:
                return R.string.mods_result_failed;
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        store.changed();
        offerPendingLaunch();
        long now = android.os.SystemClock.elapsedRealtime();
        if (now - lastJoinImport > 15_000) {
            lastJoinImport = now;
            android.content.Context app = getApplicationContext();
            store.work.execute(() -> {
                Helper.keepRootHelper(app);
                LibraryData.importJoins(app);
            });
        }
    }

    @Override
    protected void onSaveInstanceState(@NonNull Bundle out) {
        super.onSaveInstanceState(out);
        out.putInt(EXTRA_TAB, current);
    }

    @Override
    public void onTrimMemory(int level) {
        super.onTrimMemory(level);
        if (level >= TRIM_MEMORY_UI_HIDDEN) release();
    }

    private void release() {
        releaseHiddenTabs();
        Net.trim();
        store.work.execute(System::gc);
    }

    private void releaseHiddenTabs() {
        FragmentManager fm = getSupportFragmentManager();
        FragmentTransaction t = null;
        for (int tab : TABS) {
            Fragment f = fm.findFragmentByTag("tab" + tab);
            if (f == null || !f.isHidden()) continue;
            if (t == null) t = fm.beginTransaction().setReorderingAllowed(true);
            t.remove(f);
        }
        if (t != null) t.commitNowAllowingStateLoss();
    }

    private void offerPendingLaunch() {
        String pending = store.setting("pending", null);
        if (pending == null) return;
        String pkg = Targets.selected(this);
        if (!Targets.resolve(this, pkg).ready()) return;
        Deeplink d = Deeplink.parse(pending);
        String name = store.setting("pendingName", "");
        if (d == null) {
            clearPending();
            return;
        }
        clearPending();
        Actions.launch(this, d, name);
    }

    private void clearPending() {
        store.putSetting("pending", null);
        store.putSetting("pendingName", null);
    }

    public void select(int id) {
        if (nav != null && nav.getMenu().findItem(id) != null && nav.getSelectedItemId() != id) nav.setSelectedItemId(id);
        else show(id);
    }

    private final androidx.activity.OnBackPressedCallback editorBack = new androidx.activity.OnBackPressedCallback(false) {
        @Override
        public void handleOnBackPressed() {
            show(parent(current));
        }
    };

    private static int parent(int id) {
        if (id == R.id.nav_flag_editor) return R.id.nav_flags;
        if (id == R.id.nav_integrations || id == R.id.nav_smart) return R.id.nav_settings;
        return id;
    }

    private void show(int id) {
        FragmentManager fm = getSupportFragmentManager();
        FragmentTransaction t = fm.beginTransaction().setReorderingAllowed(true);
        for (int tab : TABS) {
            String tag = "tab" + tab;
            Fragment f = fm.findFragmentByTag(tag);
            if (tab == id) {
                if (f == null) t.add(R.id.content, create(tab), tag);
                else t.show(f);
            } else if (f != null && !f.isHidden()) {
                t.hide(f);
            }
        }
        t.commitNow();
        if (id != current) {
            Fragment shown = fm.findFragmentByTag("tab" + id);
            if (shown != null && shown.getView() != null) enter(shown.getView());
        }
        current = id;
        findViewById(R.id.content).post(() -> LiveTranslator.apply(this));
        AppPresence.page(id);
        editorBack.setEnabled(parent(id) != id && nav != null && nav.getMenu().findItem(id) == null);
        if (nav != null && nav.getMenu().findItem(id) == null) {
            MenuItem owner = nav.getMenu().findItem(parent(id));
            if (owner != null) owner.setChecked(true);
        }
        updateSidebar();
    }

    private static final android.view.animation.Interpolator ENTER_EASE = new android.view.animation.DecelerateInterpolator(1.5f);

    private void enter(View v) {
        v.animate().cancel();
        v.setAlpha(0f);
        if (sidebarItems != null) {
            float offset = Ui.dp(this, 16);
            v.setTranslationX(v.getLayoutDirection() == View.LAYOUT_DIRECTION_RTL ? offset : -offset);
            v.setTranslationY(0f);
        } else {
            v.setTranslationX(0f);
            v.setTranslationY(Ui.dp(this, 18));
        }
        v.post(() -> v.animate().alpha(1f).translationX(0f).translationY(0f).setDuration(250).setInterpolator(ENTER_EASE).withLayer().start());
    }

    private static Fragment create(int id) {
        if (id == R.id.nav_library) return new LibraryFragment();
        if (id == R.id.nav_mods) return new ModsFragment();
        if (id == R.id.nav_flags) return new FlagSettingsFragment();
        if (id == R.id.nav_flag_editor) return new FlagsFragment();
        if (id == R.id.nav_settings) return new SettingsFragment();
        if (id == R.id.nav_integrations) return new IntegrationsFragment();
        if (id == R.id.nav_smart) {
            Page smart = SmartJoin.page();
            if (smart != null) return smart;
        }
        return new HomeFragment();
    }

    private void buildSidebar() {
        Menu menu = new PopupMenu(this, sidebarItems).getMenu();
        getMenuInflater().inflate(R.menu.nav_wide, menu);
        LayoutInflater inf = getLayoutInflater();
        LinearLayout footer = findViewById(R.id.sidebar_footer);
        for (int i = 0; i < menu.size(); i++) {
            MenuItem item = menu.getItem(i);
            int id = item.getItemId();
            LinearLayout parent = id == R.id.nav_settings ? footer : sidebarItems;
            View row = inf.inflate(R.layout.item_sidebar, parent, false);
            row.setId(id);
            ((TextView) row.findViewById(R.id.label)).setText(item.getTitle());
            ((android.widget.ImageView) row.findViewById(R.id.icon)).setImageDrawable(item.getIcon());
            row.setOnClickListener(v -> show(id));
            parent.addView(row);
        }
    }

    private void updateSidebar() {
        if (titleLibrary != null) titleLibrary.setBackgroundTintList(current == R.id.nav_library ? getColorStateList(R.color.vs_tonal) : android.content.res.ColorStateList.valueOf(android.graphics.Color.TRANSPARENT));
        if (sidebarItems == null) return;
        for (int tab : TABS) {
            View row = findViewById(tab);
            if (row == null) continue;
            boolean on = tab == current;
            if (row.isSelected() == on) continue;
            row.setSelected(on);
        }
    }

    private void applyInsets() {
        View root = findViewById(R.id.root);
        View content = findViewById(R.id.column);
        View bar = findViewById(R.id.action_bar);
        View divider = findViewById(R.id.bar_divider);
        boolean wide = sidebarItems != null;
        if (wide) getWindow().setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(Ui.attr(this, R.attr.vsSidebar)));
        ViewCompat.setOnApplyWindowInsetsListener(root, (v, insets) -> {
            Insets bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            Insets ime = insets.getInsets(WindowInsetsCompat.Type.ime());
            boolean imeVisible = ime.bottom > bars.bottom;
            int visibility = imeVisible ? View.GONE : View.VISIBLE;
            bar.setVisibility(visibility);
            divider.setVisibility(visibility);
            if (wide) {
                root.setPadding(bars.left, bars.top, bars.right, imeVisible ? ime.bottom : bars.bottom);
            } else {
                nav.setVisibility(visibility);
                content.setPadding(bars.left, bars.top, bars.right, imeVisible ? ime.bottom : 0);
            }
            return insets;
        });
    }
}
