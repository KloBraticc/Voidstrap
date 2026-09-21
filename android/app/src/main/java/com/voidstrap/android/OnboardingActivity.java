package com.voidstrap.android;

import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.view.animation.DecelerateInterpolator;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.activity.OnBackPressedCallback;
import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.app.NotificationManagerCompat;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.materialswitch.MaterialSwitch;

public final class OnboardingActivity extends AppCompatActivity {
    static final String SETTING = "onboarded";
    private static final String STATE_STEP = "step";
    private static final int WELCOME = 0;
    private static final int APPEARANCE = 1;
    private static final int ROOT = 2;
    private static final int ROBLOX = 3;
    private static final int ALERTS = 4;
    private static final int DONE = 5;
    private static final int[] ICONS = {0, R.drawable.ic_options, R.drawable.ic_shield_checkmark, R.drawable.ic_games, R.drawable.ic_alert, R.drawable.ic_checkmark_circle};
    private static final int[] TITLES = {R.string.onboarding_welcome, R.string.settings_appearance, R.string.onboarding_root, R.string.onboarding_roblox, R.string.settings_notifications, R.string.onboarding_done};
    private static final int[] BODIES = {R.string.onboarding_welcome_body, R.string.onboarding_appearance_body, R.string.onboarding_root_body, R.string.onboarding_roblox_body, R.string.onboarding_alerts_body, R.string.onboarding_done_body};

    private Store store;
    private int step;
    private View page;
    private ImageView icon;
    private ColorStateList iconTint;
    private TextView title;
    private TextView body;
    private LinearLayout content;
    private LinearLayout dots;
    private MaterialButton back;
    private MaterialButton next;
    private MaterialButton skip;
    private ScrollPane scroll;
    private View target;
    private TextView helper;
    private TextView allow;
    private boolean launching;
    private ActivityResultLauncher<String> notifyPermission;
    private final Runnable refresh = this::bind;

    static boolean due(Context c) {
        Store s = Store.get(c);
        if ("1".equals(s.setting(SETTING, "0"))) return false;
        boolean fresh;
        try {
            PackageInfo p = c.getPackageManager().getPackageInfo(c.getPackageName(), 0);
            fresh = p.firstInstallTime == p.lastUpdateTime;
        } catch (PackageManager.NameNotFoundException e) {
            fresh = false;
        }
        if (!fresh) s.putSetting(SETTING, "1");
        return fresh;
    }

    @Override
    protected void onCreate(Bundle saved) {
        store = Store.get(this);
        VoidstrapApp.style(this);
        WindowCompat.setDecorFitsSystemWindows(getWindow(), false);
        super.onCreate(saved);
        setContentView(R.layout.activity_onboarding);
        ThemeFade.play(this);
        VoidstrapApp.bars(this);
        if (saved == null) store.putSetting(SETTING, "1");
        step = saved == null ? WELCOME : Math.max(WELCOME, Math.min(DONE, saved.getInt(STATE_STEP, WELCOME)));
        notifyPermission = registerForActivityResult(new ActivityResultContracts.RequestPermission(), granted -> bind());
        page = findViewById(R.id.onboarding_page);
        icon = findViewById(R.id.onboarding_icon);
        iconTint = icon.getImageTintList();
        title = findViewById(R.id.onboarding_title);
        body = findViewById(R.id.onboarding_body);
        content = findViewById(R.id.onboarding_content);
        dots = findViewById(R.id.onboarding_dots);
        back = findViewById(R.id.onboarding_back);
        next = findViewById(R.id.onboarding_next);
        skip = findViewById(R.id.onboarding_skip);
        scroll = findViewById(R.id.onboarding_scroll);
        ViewCompat.setOnApplyWindowInsetsListener(findViewById(R.id.onboarding_root), (v, insets) -> {
            Insets bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            return insets;
        });
        skip.setOnClickListener(v -> finish());
        back.setOnClickListener(v -> go(step - 1));
        next.setOnClickListener(v -> {
            if (step == DONE) finish();
            else go(step + 1);
        });
        getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) {
            @Override
            public void handleOnBackPressed() {
                if (step > WELCOME) go(step - 1);
                else finish();
            }
        });
        show(0);
    }

    @Override
    protected void onStart() {
        super.onStart();
        store.observe(refresh);
        bind();
    }

    @Override
    protected void onStop() {
        store.unobserve(refresh);
        super.onStop();
    }

    @Override
    protected void onSaveInstanceState(@NonNull Bundle out) {
        super.onSaveInstanceState(out);
        out.putInt(STATE_STEP, step);
    }

    private void go(int to) {
        if (to < WELCOME || to > DONE || to == step) return;
        int dir = to > step ? 1 : -1;
        step = to;
        show(dir);
    }

    private void show(int dir) {
        content.removeAllViews();
        target = null;
        helper = null;
        allow = null;
        if (ICONS[step] == 0) {
            icon.setImageTintList(null);
            icon.setImageResource(R.drawable.vs_logo);
        } else {
            icon.setImageTintList(iconTint);
            icon.setImageResource(ICONS[step]);
        }
        title.setText(TITLES[step]);
        body.setText(BODIES[step]);
        if (step == APPEARANCE) SettingRows.appearance(card(0), this, 0);
        else if (step == ROOT) root();
        else if (step == ROBLOX) roblox();
        else if (step == ALERTS) alerts();
        else if (step == DONE) done();
        back.setVisibility(step == WELCOME ? View.INVISIBLE : View.VISIBLE);
        skip.setVisibility(step == DONE ? View.INVISIBLE : View.VISIBLE);
        next.setText(step == WELCOME ? R.string.onboarding_start : step == DONE ? R.string.onboarding_finish : R.string.onboarding_next);
        dots();
        bind();
        scroll.scrollTo(0, 0);
        ViewCompat.setAccessibilityPaneTitle(page, title.getText());
        LiveTranslator.apply(this);
        if (dir == 0) return;
        boolean rtl = getResources().getConfiguration().getLayoutDirection() == View.LAYOUT_DIRECTION_RTL;
        page.setAlpha(0f);
        page.setTranslationX(Ui.dp(this, 24) * dir * (rtl ? -1 : 1));
        page.animate().alpha(1f).translationX(0f).setDuration(220).setInterpolator(new DecelerateInterpolator(1.5f)).withLayer().start();
    }

    private void dots() {
        dots.removeAllViews();
        int on = Ui.attr(this, androidx.appcompat.R.attr.colorPrimary);
        int off = Ui.attr(this, R.attr.vsTextTertiary);
        for (int i = WELCOME; i <= DONE; i++) {
            View d = new View(this);
            GradientDrawable shape = new GradientDrawable();
            shape.setCornerRadius(Ui.dp(this, 4));
            shape.setColor(i == step ? on : off);
            shape.setAlpha(i == step ? 255 : 110);
            d.setBackground(shape);
            d.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(Ui.dp(this, i == step ? 20 : 8), Ui.dp(this, 8));
            lp.setMarginEnd(Ui.dp(this, 6));
            dots.addView(d, lp);
        }
        dots.setContentDescription(getString(R.string.onboarding_step, step + 1, DONE + 1));
    }

    private LinearLayout card(int side) {
        MaterialCardView card = new MaterialCardView(this);
        LinearLayout inner = new LinearLayout(this);
        inner.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(this, 4);
        inner.setPadding(Ui.dp(this, side), pad, Ui.dp(this, side), pad);
        card.addView(inner);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        if (content.getChildCount() > 0) lp.topMargin = Ui.dp(this, 12);
        content.addView(card, lp);
        return inner;
    }

    private void root() {
        LinearLayout unlocks = card(4);
        info(unlocks, R.drawable.ic_wrench_screwdriver, R.string.onboarding_root_mods, R.string.onboarding_root_mods_body);
        info(unlocks, R.drawable.ic_options, R.string.onboarding_root_flags, R.string.onboarding_root_flags_body);
        info(unlocks, R.drawable.ic_people, R.string.onboarding_root_activity, R.string.onboarding_root_activity_body);
        FlagSync.rootRow(card(0), this);
        TextView note = new TextView(this);
        note.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        note.setText(R.string.onboarding_root_note);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Ui.dp(this, 16);
        content.addView(note, lp);
    }

    private void info(LinearLayout parent, int iconRes, int titleRes, int detailRes) {
        Row row = Row.inflate(parent);
        row.set(iconRes, getString(titleRes), getString(detailRes));
        row.detail.setMaxLines(4);
        row.view.setBackground(null);
        row.view.setClickable(false);
        row.view.setFocusable(false);
        parent.addView(row.view);
    }

    private void roblox() {
        LinearLayout card = card(4);
        target = getLayoutInflater().inflate(R.layout.view_target_button, card, false);
        target.setOnClickListener(v -> Actions.targetSheet(this));
        card.addView(target);
        Row access = Row.inflate(card);
        access.set(R.drawable.ic_shield_checkmark, getString(R.string.settings_helper), FlagSync.status(this));
        access.detail.setMaxLines(4);
        access.chevron.setVisibility(View.VISIBLE);
        access.view.setOnClickListener(v -> FlagSync.setup(this));
        helper = access.detail;
        card.addView(access.view);
    }

    private void alerts() {
        LinearLayout card = card(0);
        LinearLayout row = SettingRows.row(card, getString(R.string.notify_allow), getString(R.string.notify_blocked), null);
        allow = (TextView) ((LinearLayout) row.getChildAt(0)).getChildAt(1);
        row.setOnClickListener(v -> {
            if (Build.VERSION.SDK_INT >= 33 && !NotificationManagerCompat.from(this).areNotificationsEnabled()) notifyPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS);
            else Notify.openSettings(this);
        });
        MaterialSwitch join = new MaterialSwitch(this);
        join.setChecked(Notify.on(store, Integrations.NOTIFY));
        join.setContentDescription(getString(R.string.integrations_notify));
        SettingRows.row(card, getString(R.string.integrations_notify), getString(R.string.integrations_notify_body), join).setOnClickListener(v -> join.toggle());
        join.setOnCheckedChangeListener((b, on) -> Notify.set(store, Integrations.NOTIFY, on));
    }

    private void done() {
        String pkg = Targets.selected(this);
        if (!Targets.resolve(this, pkg).ready()) return;
        String name = getString(Targets.nameRes(pkg));
        LinearLayout card = card(4);
        Row launch = Row.inflate(card);
        launch.set(R.drawable.ic_play, getString(R.string.onboarding_launch, name), getString(R.string.onboarding_launch_body));
        launch.chevron.setVisibility(View.VISIBLE);
        launch.view.setOnClickListener(v -> launch(name));
        card.addView(launch.view);
    }

    private void launch(String name) {
        if (launching) return;
        launching = true;
        Launcher.launch(this, null, name, r -> {
            launching = false;
            if (isFinishing() || isDestroyed()) return;
            if (r == Launcher.Result.HANDED_OFF) finish();
            else if (r != Launcher.Result.BUSY) Ui.say(this, Launcher.message(r));
        });
    }

    private void bind() {
        if (target != null) Actions.bindTarget(target, true);
        if (helper != null) helper.setText(FlagSync.status(this));
        if (allow != null) allow.setText(NotificationManagerCompat.from(this).areNotificationsEnabled() ? R.string.notify_allowed : R.string.notify_blocked);
    }
}
