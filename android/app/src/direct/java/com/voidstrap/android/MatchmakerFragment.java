package com.voidstrap.android;

import android.content.Context;
import android.content.Intent;
import android.graphics.drawable.Drawable;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.slider.Slider;
import com.google.android.material.textfield.TextInputLayout;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

public final class MatchmakerFragment extends Page {
    private LinearLayout root;
    private TextView location;
    private List<Matchmaker.Datacenter> datacenters = new ArrayList<>();
    private Matchmaker.Geo geo;
    private RobloxLogin.Source login = RobloxLogin.Source.NONE;
    private boolean loading;
    private boolean loaded;
    private String shape = "";

    public MatchmakerFragment() {
        super(R.layout.fragment_matchmaker);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        root = v.findViewById(R.id.matchmaker_sections);
        location = v.findViewById(R.id.matchmaker_location);
        shape = "";
    }

    @Override
    public void onStart() {
        super.onStart();
        load();
    }

    private void load() {
        if (loading) return;
        loading = true;
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            List<Matchmaker.Datacenter> dcs = Matchmaker.datacenters(app);
            Matchmaker.Geo g = Matchmaker.geo(app);
            RobloxLogin.Source src = RobloxLogin.source(app);
            store.main.post(() -> {
                loading = false;
                loaded = true;
                datacenters = dcs;
                geo = g;
                login = src;
                shape = "";
                if (getView() != null && !isHidden()) refresh();
            });
        });
    }

    private String shape() {
        return Matchmaker.enabled(store) + " " + mode() + " " + Matchmaker.preferred(store) + " " + login + " " + Matchmaker.blocked(store).size() + " " + Matchmaker.excluded(store).size() + " " + store.setting(Matchmaker.AUTO, "1") + " " + store.setting(Matchmaker.MAX, "14") + " " + store.setting(Matchmaker.RETRIES, "3") + " " + store.setting(Matchmaker.API, "1") + " " + Matchmaker.lastReport() + " " + datacenters.size();
    }

    @Override
    protected void refresh() {
        bindLocation();
        String now = shape();
        if (now.equals(shape)) return;
        shape = now;
        build();
    }

    private int mode() {
        if (!Matchmaker.preferred(store).isEmpty()) return 2;
        return Matchmaker.preferEmpty(store) ? 1 : 0;
    }

    private void setMode(int value) {
        store.putSetting(Matchmaker.PREFER_EMPTY, value == 1 ? "1" : "0");
        if (value != 2) store.putSetting(Matchmaker.PREFERRED, null);
        else if (Matchmaker.preferred(store).isEmpty()) {
            List<Matchmaker.Datacenter> sorted = sorted();
            if (!sorted.isEmpty()) store.putSetting(Matchmaker.PREFERRED, sorted.get(0).key());
        }
    }

    private double km(Matchmaker.Datacenter dc) {
        return geo == null ? -1 : Matchmaker.haversineKm(geo.lat, geo.lon, dc.lat, dc.lon);
    }

    private List<Matchmaker.Datacenter> sorted() {
        List<Matchmaker.Datacenter> list = new ArrayList<>(datacenters);
        if (geo != null) list.sort((a, b) -> Double.compare(km(a), km(b)));
        else list.sort((a, b) -> a.display().compareToIgnoreCase(b.display()));
        return list;
    }

    private String detail(Matchmaker.Datacenter dc) {
        double km = km(dc);
        if (km < 0) return "";
        return getString(R.string.matchmaker_distance, (int) km, Matchmaker.estimatePingMs(km));
    }

    private void bindLocation() {
        Context c = requireContext();
        String text;
        if (!loaded || geo == null) text = getString(R.string.matchmaker_location_finding);
        else {
            Matchmaker.Datacenter nearest = sorted().isEmpty() ? null : sorted().get(0);
            text = nearest == null ? getString(R.string.matchmaker_location_learning) : getString(R.string.matchmaker_location, nearest.display(), Matchmaker.estimatePingMs(km(nearest)));
        }
        location.setText(text);
        Drawable icon = ContextCompat.getDrawable(c, R.drawable.ic_globe);
        if (icon != null) {
            icon = icon.mutate();
            int size = Ui.dp(c, 16);
            icon.setBounds(0, 0, size, size);
            icon.setTint(Ui.attr(c, android.R.attr.textColorSecondary));
        }
        location.setCompoundDrawablesRelative(icon, null, null, null);
    }

    private void build() {
        root.removeAllViews();
        Context c = requireContext();
        boolean on = Matchmaker.enabled(store);

        LinearLayout account = SettingRows.section(root, getString(R.string.matchmaker_login));
        MaterialButton action = new MaterialButton(c, null, com.google.android.material.R.attr.materialButtonTonalStyle);
        boolean signedInHere = login == RobloxLogin.Source.VOIDSTRAP;
        action.setText(signedInHere ? R.string.matchmaker_sign_out : R.string.matchmaker_sign_in);
        action.setOnClickListener(x -> {
            if (signedInHere) {
                RobloxLogin.signOut();
                Notify.say(host(), Notify.GENERAL, R.string.matchmaker_signed_out);
                load();
            } else {
                startActivity(new Intent(c, SignInActivity.class));
            }
        });
        int summary = login == RobloxLogin.Source.VOIDSTRAP ? R.string.matchmaker_login_voidstrap : R.string.matchmaker_login_none;
        SettingRows.row(account, getString(R.string.matchmaker_login), getString(summary), action);
        caption(account, getString(R.string.matchmaker_login_note));

        LinearLayout main = SettingRows.section(root, getString(R.string.matchmaker_section));
        MaterialSwitch enable = new MaterialSwitch(c);
        enable.setChecked(on);
        enable.setContentDescription(getString(R.string.matchmaker_enable));
        SettingRows.row(main, getString(R.string.matchmaker_enable), getString(R.string.matchmaker_enable_body), enable).setOnClickListener(x -> enable.toggle());
        enable.setOnCheckedChangeListener((b, v) -> store.putSetting(Matchmaker.ENABLED, v ? "1" : "0"));
        SettingRows.choice(main, getString(R.string.matchmaker_mode), getString(R.string.matchmaker_mode_body), getResources().getStringArray(R.array.matchmaker_modes), mode(), this::setMode);
        enable(main.getChildAt(main.getChildCount() - 1), on);
        if (mode() == 2) {
            List<Matchmaker.Datacenter> list = sorted();
            if (!list.isEmpty()) {
                String[] labels = new String[list.size()];
                int selected = 0;
                String preferred = Matchmaker.preferred(store);
                for (int i = 0; i < list.size(); i++) {
                    String d = detail(list.get(i));
                    labels[i] = d.isEmpty() ? list.get(i).display() : list.get(i).display() + " · " + d;
                    if (Matchmaker.matchesPreferred(list.get(i), preferred)) selected = i;
                }
                SettingRows.choice(main, getString(R.string.matchmaker_preferred), getString(R.string.matchmaker_preferred_body), labels, selected, i -> store.putSetting(Matchmaker.PREFERRED, list.get(i).key()));
                enable(main.getChildAt(main.getChildCount() - 1), on);
            }
        }

        LinearLayout lists = SettingRows.section(root, getString(R.string.matchmaker_lists));
        int blocked = 0;
        Set<String> blockedKeys = Matchmaker.blocked(store);
        for (Matchmaker.Datacenter dc : datacenters) if (blockedKeys.contains(dc.key())) blocked++;
        String dcSummary = datacenters.isEmpty() ? getString(R.string.matchmaker_datacenters_none) : blocked == 0 ? getResources().getQuantityString(R.plurals.matchmaker_datacenters_all, datacenters.size(), datacenters.size()) : getResources().getQuantityString(R.plurals.matchmaker_datacenters_some, datacenters.size(), datacenters.size(), blocked);
        SettingRows.row(lists, getString(R.string.matchmaker_datacenters), dcSummary, chevron(c)).setOnClickListener(x -> showDatacenters());
        Set<Long> skipped = Matchmaker.excluded(store);
        String skipSummary = skipped.isEmpty() ? getString(R.string.matchmaker_skipped_none) : skipped.size() == 1 ? getString(R.string.matchmaker_skipped_one) : getResources().getQuantityString(R.plurals.matchmaker_skipped_many, skipped.size(), skipped.size());
        SettingRows.row(lists, getString(R.string.matchmaker_skipped), skipSummary, chevron(c)).setOnClickListener(x -> showSkipped());

        LinearLayout advanced = SettingRows.section(root, getString(R.string.matchmaker_advanced));
        caption(advanced, getString(R.string.matchmaker_advanced_body));
        boolean auto = !"0".equals(store.setting(Matchmaker.AUTO, "1"));
        int count = Matchmaker.candidateCount(store);
        int speed = count <= 16 ? R.string.matchmaker_depth_fastest : count <= 32 ? R.string.matchmaker_depth_balanced : R.string.matchmaker_depth_thorough;
        MaterialSwitch autoSwitch = new MaterialSwitch(c);
        autoSwitch.setText(R.string.matchmaker_depth_auto);
        autoSwitch.setChecked(auto);
        SettingRows.row(advanced, getString(R.string.matchmaker_depth), getResources().getQuantityString(R.plurals.matchmaker_depth_body, count, count, getString(speed)), autoSwitch).setOnClickListener(x -> autoSwitch.toggle());
        autoSwitch.setOnCheckedChangeListener((b, v) -> store.putSetting(Matchmaker.AUTO, v ? "1" : "0"));
        if (!auto) advanced.addView(slider(c, Matchmaker.MIN_CANDIDATES, Matchmaker.MAX_CANDIDATES, 4, Matchmaker.manualCandidates(store), v -> store.putSetting(Matchmaker.MAX, String.valueOf(v))));
        int retries = Matchmaker.maxRetries(store);
        SettingRows.row(advanced, getString(R.string.matchmaker_retries), getString(R.string.matchmaker_retries_body, retries), null);
        advanced.addView(slider(c, 1, 20, 1, retries, v -> store.putSetting(Matchmaker.RETRIES, String.valueOf(v))));
        SettingRows.choice(advanced, getString(R.string.matchmaker_api), getString(R.string.matchmaker_api_body), getResources().getStringArray(R.array.matchmaker_apis), "2".equals(store.setting(Matchmaker.API, "1")) ? 1 : 0, i -> store.putSetting(Matchmaker.API, i == 1 ? "2" : "1"));
        String last = Matchmaker.lastReport();
        if (!last.isEmpty()) caption(advanced, getString(R.string.matchmaker_last, last));
    }

    private interface IntChange {
        void on(int value);
    }

    private View slider(Context c, int min, int max, int step, int value, IntChange change) {
        LinearLayout box = new LinearLayout(c);
        box.setGravity(Gravity.CENTER_VERTICAL);
        box.setPadding(Ui.dp(c, 16), 0, Ui.dp(c, 16), Ui.dp(c, 8));
        Slider s = new Slider(c);
        s.setValueFrom(min);
        s.setValueTo(max);
        s.setStepSize(step);
        s.setTickVisibilityMode(com.google.android.material.slider.TickVisibilityMode.TICK_VISIBILITY_HIDDEN);
        int snapped = min + Math.round((float) (Math.max(min, Math.min(max, value)) - min) / step) * step;
        s.setValue(Math.min(max, snapped));
        s.addOnSliderTouchListener(new Slider.OnSliderTouchListener() {
            @Override
            public void onStartTrackingTouch(@NonNull Slider slider) {
            }

            @Override
            public void onStopTrackingTouch(@NonNull Slider slider) {
                change.on((int) slider.getValue());
            }
        });
        box.addView(s, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        return box;
    }

    private static void enable(View row, boolean enabled) {
        row.setEnabled(enabled);
        row.setClickable(enabled);
        row.setAlpha(enabled ? 1f : 0.5f);
    }

    private void caption(LinearLayout parent, String text) {
        Context c = requireContext();
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        t.setText(text);
        t.setPadding(Ui.dp(c, 16), Ui.dp(c, 4), Ui.dp(c, 16), Ui.dp(c, 8));
        parent.addView(t);
    }

    private View chevron(Context c) {
        android.widget.ImageView v = new android.widget.ImageView(c);
        v.setImageResource(R.drawable.ic_chevron_right);
        v.setImageTintList(android.content.res.ColorStateList.valueOf(Ui.attr(c, android.R.attr.textColorSecondary)));
        v.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        return v;
    }

    private void showDatacenters() {
        Context c = requireContext();
        LinearLayout list = new LinearLayout(c);
        list.setOrientation(LinearLayout.VERTICAL);
        View searchBox = LayoutInflater.from(c).inflate(R.layout.dialog_input, list, false);
        TextInputLayout searchLayout = searchBox.findViewById(R.id.input_layout);
        EditText search = searchBox.findViewById(R.id.input);
        searchLayout.setHint(R.string.matchmaker_datacenters_search);
        searchBox.setPadding(Ui.dp(c, 16), 0, Ui.dp(c, 16), Ui.dp(c, 4));
        list.addView(searchBox);
        MaterialButton allowAll = new MaterialButton(c, null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
        allowAll.setText(R.string.matchmaker_allow_all);
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        ap.setMarginStart(Ui.dp(c, 16));
        list.addView(allowAll, ap);
        LinearLayout rows = new LinearLayout(c);
        rows.setOrientation(LinearLayout.VERTICAL);
        list.addView(rows);
        List<MaterialSwitch> switches = new ArrayList<>();
        List<Matchmaker.Datacenter> dcs = sorted();
        List<View> views = new ArrayList<>();
        Set<String> blocked = Matchmaker.blocked(store);
        for (Matchmaker.Datacenter dc : dcs) {
            MaterialSwitch sw = new MaterialSwitch(c);
            sw.setChecked(!blocked.contains(dc.key()));
            sw.setContentDescription(dc.display());
            LinearLayout row = SettingRows.row(rows, dc.display(), detail(dc), sw);
            row.setOnClickListener(x -> sw.toggle());
            sw.setOnCheckedChangeListener((b, allowed) -> {
                Set<String> now = Matchmaker.blocked(store);
                if (allowed) now.remove(dc.key());
                else now.add(dc.key());
                Matchmaker.setBlocked(store, now);
            });
            switches.add(sw);
            views.add(row);
        }
        allowAll.setOnClickListener(x -> {
            Matchmaker.setBlocked(store, new HashSet<>());
            for (MaterialSwitch sw : switches) sw.setChecked(true);
        });
        search.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence s, int a, int b, int d) {
            }

            @Override
            public void onTextChanged(CharSequence s, int a, int b, int d) {
            }

            @Override
            public void afterTextChanged(Editable e) {
                String q = e.toString().trim().toLowerCase(Locale.ROOT);
                for (int i = 0; i < dcs.size(); i++) {
                    Matchmaker.Datacenter dc = dcs.get(i);
                    boolean match = q.isEmpty() || (dc.city + " " + dc.region + " " + dc.country + " " + dc.display()).toLowerCase(Locale.ROOT).contains(q);
                    views.get(i).setVisibility(match ? View.VISIBLE : View.GONE);
                }
            }
        });
        Ui.surface(host(), R.string.matchmaker_datacenters, R.string.matchmaker_datacenters_note, list).show();
    }

    private void showSkipped() {
        Context c = requireContext();
        LinearLayout list = new LinearLayout(c);
        list.setOrientation(LinearLayout.VERTICAL);
        LinearLayout buttons = new LinearLayout(c);
        buttons.setPadding(Ui.dp(c, 16), 0, Ui.dp(c, 16), Ui.dp(c, 4));
        MaterialButton add = new MaterialButton(c, null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
        add.setText(R.string.matchmaker_skip_add);
        MaterialButton fromLibrary = new MaterialButton(c, null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
        fromLibrary.setText(R.string.matchmaker_skip_add_library);
        buttons.addView(add);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMarginStart(Ui.dp(c, 8));
        buttons.addView(fromLibrary, lp);
        list.addView(buttons);
        LinearLayout rows = new LinearLayout(c);
        rows.setOrientation(LinearLayout.VERTICAL);
        list.addView(rows);
        Runnable[] fill = new Runnable[1];
        fill[0] = () -> {
            rows.removeAllViews();
            for (long placeId : Matchmaker.excluded(store)) {
                Store.Game g = store.game(placeId);
                Row row = Row.inflate(rows);
                row.set(R.drawable.ic_games, g != null && g.name != null && !g.name.isEmpty() ? g.name : getString(R.string.matchmaker_place, placeId), String.valueOf(placeId));
                if (g != null && g.iconUrl != null && g.iconUrl.startsWith("https://")) row.showImage(g.iconUrl, R.drawable.ic_games);
                row.more.setVisibility(View.VISIBLE);
                row.more.setIconResource(R.drawable.ic_delete);
                row.more.setContentDescription(getString(R.string.matchmaker_skip_remove));
                row.more.setOnClickListener(x -> {
                    Matchmaker.setExcluded(store, placeId, false);
                    fill[0].run();
                });
                row.view.setClickable(false);
                rows.addView(row.view);
            }
        };
        fill[0].run();
        add.setOnClickListener(x -> {
            View body = LayoutInflater.from(c).inflate(R.layout.dialog_input, null);
            TextInputLayout layout = body.findViewById(R.id.input_layout);
            EditText input = body.findViewById(R.id.input);
            layout.setHint(R.string.matchmaker_skip_hint);
            Ui.clearErrorOnEdit(layout, input);
            androidx.appcompat.app.AlertDialog d = Ui.alert(c)
                    .setTitle(R.string.matchmaker_skip_add)
                    .setView(body)
                    .setPositiveButton(R.string.common_ok, null)
                    .setNegativeButton(R.string.common_cancel, null)
                    .show();
            d.getButton(androidx.appcompat.app.AlertDialog.BUTTON_POSITIVE).setOnClickListener(b -> {
                Deeplink link = Deeplink.findIn(input.getText().toString());
                if (link == null || link.placeId <= 0) {
                    layout.setError(getString(R.string.matchmaker_skip_invalid));
                    return;
                }
                Matchmaker.setExcluded(store, link.placeId, true);
                fill[0].run();
                d.dismiss();
            });
            Ui.focus(d, input);
        });
        fromLibrary.setOnClickListener(x -> {
            List<Store.Game> games = new ArrayList<>(store.library);
            if (games.isEmpty()) return;
            String[] names = new String[games.size()];
            for (int i = 0; i < names.length; i++) names[i] = games.get(i).name == null || games.get(i).name.isEmpty() ? getString(R.string.matchmaker_place, games.get(i).placeId) : games.get(i).name;
            Ui.alert(c)
                    .setTitle(R.string.matchmaker_skip_add_library)
                    .setItems(names, (dlg, i) -> {
                        Matchmaker.setExcluded(store, games.get(i).placeId, true);
                        fill[0].run();
                    })
                    .show();
        });
        Ui.surface(host(), R.string.matchmaker_skipped, R.string.matchmaker_skipped_note, list).show();
    }
}
