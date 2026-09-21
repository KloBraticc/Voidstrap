package com.voidstrap.android;

import android.content.Context;
import android.os.Bundle;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import com.google.android.material.materialswitch.MaterialSwitch;

import java.text.NumberFormat;
import java.util.List;
import java.util.TimeZone;

public final class SmartJoinFragment extends Page {
    private static final int RECENT_LIMIT = 10;

    private MaterialSwitch toggle;
    private LinearLayout rows;
    private LinearLayout recent;
    private String recentKey = "";

    public SmartJoinFragment() {
        super(R.layout.fragment_smart_join);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle state) {
        super.onViewCreated(v, state);
        rows = v.findViewById(R.id.smart_rows);
        build();
    }

    private void build() {
        Context c = requireContext();
        rows.removeAllViews();
        LinearLayout section = SettingRows.section(rows, getString(R.string.smart_section));
        toggle = new MaterialSwitch(c);
        toggle.setChecked(SmartJoin.enabled(store));
        toggle.setContentDescription(getString(R.string.smart_enable));
        SettingRows.row(section, getString(R.string.smart_enable), getString(R.string.smart_enable_body), toggle)
                .setOnClickListener(x -> toggle.toggle());
        toggle.setOnCheckedChangeListener((b, on) -> store.putSetting(SmartJoin.ENABLED, on ? "1" : "0"));

        String[] regions = getResources().getStringArray(R.array.smart_regions);
        String[] regionLabels = new String[regions.length + 1];
        Regions.Region auto = Regions.fromTimeZone(TimeZone.getDefault());
        regionLabels[0] = getString(R.string.smart_region_auto, regions[Regions.index(auto)]);
        System.arraycopy(regions, 0, regionLabels, 1, regions.length);
        int regionIndex = SmartJoin.autoRegion(store) ? 0 : Regions.index(SmartJoin.region(store)) + 1;
        SettingRows.choice(section, getString(R.string.smart_region), getString(R.string.smart_region_body), regionLabels, regionIndex,
                i -> store.putSetting(SmartJoin.REGION, i == 0 ? "" : Regions.ALL[i - 1].key));

        String[] modes = {getString(R.string.smart_mode_near), getString(R.string.smart_mode_fast)};
        SettingRows.choice(section, getString(R.string.smart_mode), null, modes, SmartJoin.nearMode(store) ? 0 : 1,
                i -> store.putSetting(SmartJoin.MODE, i == 0 ? SmartJoin.MODE_NEAR : SmartJoin.MODE_FAST));

        String[] prefer = {getString(R.string.smart_prefer_quiet), getString(R.string.smart_prefer_busy)};
        SettingRows.choice(section, getString(R.string.smart_prefer), null, prefer, SmartJoin.preferEmpty(store) ? 0 : 1,
                i -> store.putSetting(SmartJoin.PREFER_EMPTY, i == 0 ? "1" : "0"));

        String[] fps = new String[SmartJoin.FPS_LIMITS.length];
        int fpsIndex = 0;
        for (int i = 0; i < fps.length; i++) {
            int limit = SmartJoin.FPS_LIMITS[i];
            fps[i] = limit == 0 ? getString(R.string.smart_fps_any) : getString(R.string.smart_fps_value, limit);
            if (limit == SmartJoin.minFps(store)) fpsIndex = i;
        }
        SettingRows.choice(section, getString(R.string.smart_fps), getString(R.string.smart_fps_body), fps, fpsIndex,
                i -> store.putSetting(SmartJoin.MIN_FPS, String.valueOf(SmartJoin.FPS_LIMITS[i])));

        LinearLayout how = SettingRows.section(rows, getString(R.string.smart_how));
        TextView note = new TextView(c);
        note.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
        note.setText(R.string.smart_how_body);
        note.setPadding(Ui.dp(c, 16), Ui.dp(c, 12), Ui.dp(c, 16), Ui.dp(c, 12));
        how.addView(note);

        recent = SettingRows.section(rows, getString(R.string.smart_recent));
        recentKey = "";
        bindRecent();
    }

    private void bindRecent() {
        Context c = requireContext();
        List<SmartJoin.Known> known = SmartJoin.known(c);
        Regions.Region me = SmartJoin.region(store);
        boolean tracking = Integrations.on(store, Integrations.TRACKING);
        StringBuilder key = new StringBuilder(me.key).append(tracking);
        for (int i = 0; i < Math.min(RECENT_LIMIT, known.size()); i++) key.append(known.get(i).job);
        if (key.toString().equals(recentKey)) return;
        recentKey = key.toString();
        recent.removeAllViews();
        if (!tracking) {
            TextView trackingOff = new TextView(c);
            trackingOff.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
            trackingOff.setText(R.string.smart_recent_tracking);
            trackingOff.setPadding(Ui.dp(c, 16), Ui.dp(c, 12), Ui.dp(c, 16), Ui.dp(c, 12));
            recent.addView(trackingOff);
        }
        if (known.isEmpty()) {
            TextView empty = new TextView(c);
            empty.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
            empty.setText(R.string.smart_recent_empty);
            empty.setPadding(Ui.dp(c, 16), Ui.dp(c, 12), Ui.dp(c, 16), Ui.dp(c, 12));
            recent.addView(empty);
            return;
        }
        NumberFormat nf = NumberFormat.getIntegerInstance();
        for (int i = 0; i < Math.min(RECENT_LIMIT, known.size()); i++) {
            SmartJoin.Known k = known.get(i);
            Row row = Row.inflate(recent);
            String place = k.city.isEmpty() ? k.country : k.country.isEmpty() ? k.city : k.city + ", " + k.country;
            String game = k.game.isEmpty() ? getString(R.string.smart_recent_place, k.place) : k.game;
            long km = Math.round(Regions.km(me.lat, me.lon, k.lat, k.lon));
            row.set(R.drawable.ic_globe, place, getString(R.string.smart_recent_detail, game, nf.format(km), Ui.ago(c, k.time)));
            row.detail.setMaxLines(2);
            recent.addView(row.view);
        }
        Row clear = Row.inflate(recent);
        clear.set(R.drawable.ic_delete, getString(R.string.smart_recent_clear), getString(R.string.smart_recent_clear_body));
        clear.view.setOnClickListener(x -> {
            SmartJoin.forget(requireContext());
            recentKey = "";
            bindRecent();
        });
        recent.addView(clear.view);
    }

    @Override
    protected void refresh() {
        if (toggle == null) return;
        boolean on = SmartJoin.enabled(store);
        if (toggle.isChecked() != on) toggle.setChecked(on);
        bindRecent();
    }
}
