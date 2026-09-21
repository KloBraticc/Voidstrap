package com.voidstrap.android;

import android.content.Intent;
import android.os.Bundle;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.appcompat.app.AppCompatActivity;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;

import com.google.android.material.button.MaterialButton;

public final class LaunchMenuActivity extends AppCompatActivity {
    static final int LAUNCH = RESULT_FIRST_USER;
    static final int SETTINGS = RESULT_FIRST_USER + 1;
    static final int ABOUT = RESULT_FIRST_USER + 2;
    private static final String DONATE = "https://github.com/sponsors/KloBraticc";
    private static final String GITHUB = "https://github.com/KloBraticc/Voidstrap";

    static boolean due(Intent intent) {
        return intent != null && Intent.ACTION_MAIN.equals(intent.getAction()) && intent.hasCategory(Intent.CATEGORY_LAUNCHER);
    }

    @Override
    protected void onCreate(Bundle saved) {
        VoidstrapApp.style(this);
        WindowCompat.setDecorFitsSystemWindows(getWindow(), false);
        super.onCreate(saved);
        setContentView(R.layout.activity_launch_menu);
        VoidstrapApp.bars(this);
        ViewCompat.setOnApplyWindowInsetsListener(findViewById(R.id.launch_page), (v, insets) -> {
            Insets bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            return insets;
        });
        ((TextView) findViewById(R.id.launch_version)).setText(getString(R.string.launch_version, BuildConfig.VERSION_NAME));
        MaterialButton launch = findViewById(R.id.launch_roblox);
        launch.setText(getString(R.string.onboarding_launch, getString(Targets.nameRes(Targets.selected(this)))));
        launch.setOnClickListener(v -> done(LAUNCH));
        findViewById(R.id.launch_open).setOnClickListener(v -> finish());
        LinearLayout rows = findViewById(R.id.launch_rows);
        row(rows, R.drawable.ic_settings, R.string.settings_title, R.string.launch_settings_body, () -> done(SETTINGS));
        row(rows, R.drawable.ic_question_circle, R.string.launch_about, R.string.launch_about_body, () -> done(ABOUT));
        row(rows, R.drawable.ic_code, R.string.launch_github, R.string.launch_github_body, () -> Ui.openWeb(this, GITHUB));
        row(rows, R.drawable.ic_heart, R.string.launch_donate, R.string.launch_donate_body, () -> Ui.openWeb(this, DONATE));
    }

    private void done(int code) {
        setResult(code);
        finish();
    }

    private void row(LinearLayout parent, int icon, int title, int detail, Runnable click) {
        Row row = Row.inflate(parent);
        row.set(icon, getString(title), getString(detail));
        row.chevron.setVisibility(android.view.View.VISIBLE);
        row.view.setOnClickListener(v -> click.run());
        parent.addView(row.view);
    }
}
