package com.voidstrap.android;

import android.os.Bundle;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

public final class FlagSettingsFragment extends Page {
    private FlagPresets presets;
    private TextView profile;
    private TextView status;
    private String shown = "";

    public FlagSettingsFragment() {
        super(R.layout.fragment_flag_settings);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        profile = v.findViewById(R.id.flag_settings_profile);
        status = v.findViewById(R.id.flag_settings_status);
        Ui.stackWhenCramped((android.widget.LinearLayout) status.getParent(), status, 2);
        View editor = v.findViewById(R.id.flag_settings_editor);
        editor.setVisibility(getResources().getConfiguration().screenWidthDp < 600 ? View.VISIBLE : View.GONE);
        editor.setOnClickListener(x -> ((MainActivity) host()).select(R.id.nav_flag_editor));
        presets = new FlagPresets(host(), store, this::commit);
        ((FrameLayout) v.findViewById(R.id.flag_settings_presets)).addView(presets.view());
        shown = "";
    }

    private String signature() {
        Flags.Profile p = store.flags.active();
        return p.id + p.valuesJson();
    }

    private void commit() {
        Flags.Profile p = store.flags.active();
        p.updated = System.currentTimeMillis();
        store.saveFlags();
        shown = signature();
        store.changed();
    }

    @Override
    protected void refresh() {
        profile.setText(getString(R.string.flag_settings_subtitle, store.flags.active().name));
        FlagSync.bindStatus(host(), status);
        FlagSync.ask(this);
        String now = signature();
        if (now.equals(shown)) return;
        shown = now;
        presets.build();
    }
}
