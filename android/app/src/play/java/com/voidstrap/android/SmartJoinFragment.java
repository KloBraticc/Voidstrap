package com.voidstrap.android;

import android.content.Context;
import android.os.Bundle;
import android.view.View;
import android.widget.LinearLayout;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import com.google.android.material.materialswitch.MaterialSwitch;

public final class SmartJoinFragment extends Page {
    private MaterialSwitch toggle;

    public SmartJoinFragment() {
        super(R.layout.fragment_smart_join);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle state) {
        super.onViewCreated(v, state);
        Context c = requireContext();
        LinearLayout rows = v.findViewById(R.id.smart_rows);
        LinearLayout section = SettingRows.section(rows, getString(R.string.smart_section));
        toggle = new MaterialSwitch(c);
        toggle.setChecked(SmartJoin.enabled(store));
        toggle.setContentDescription(getString(R.string.smart_enable));
        SettingRows.row(section, getString(R.string.smart_enable), getString(R.string.smart_enable_body), toggle)
                .setOnClickListener(x -> toggle.toggle());
        toggle.setOnCheckedChangeListener((b, on) -> store.putSetting(SmartJoin.ENABLED, on ? "1" : "0"));
        String[] labels = {getString(R.string.smart_prefer_quiet), getString(R.string.smart_prefer_busy)};
        SettingRows.choice(section, getString(R.string.smart_prefer), null, labels,
                SmartJoin.preferEmpty(store) ? 0 : 1,
                i -> store.putSetting(SmartJoin.PREFER_EMPTY, i == 0 ? "1" : "0"));
    }

    @Override
    protected void refresh() {
        if (toggle == null) return;
        boolean on = SmartJoin.enabled(store);
        if (toggle.isChecked() != on) toggle.setChecked(on);
    }
}
