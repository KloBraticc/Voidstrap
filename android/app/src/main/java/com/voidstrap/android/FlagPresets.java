package com.voidstrap.android;

import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.appcompat.app.AppCompatActivity;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.slider.Slider;

import java.util.Map;

public final class FlagPresets {
    private static final String GRASS_MOTION = "FIntGrassMovementReducedMotionFactor";
    private static final String[] LOD = {
            "DFIntCSGLevelOfDetailSwitchingDistance",
            "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23",
            "DFIntCSGLevelOfDetailSwitchingDistanceL34"};
    private static final String FRM = "DFIntDebugFRMQualityLevelOverride";
    private static final String SKY = "FFlagDebugSkyGray";
    private static final String MSAA = "FIntDebugForceMSAASamples";
    private static final String MSAA_OPTIONAL = "FIntDebugFRMOptionalMSAALevelOverride";
    private static final String TEXTURE = "DFIntTextureQualityOverride";
    private static final String TEXTURE_ON = "DFFlagTextureQualityOverrideEnabled";
    private static final String DPI = "DFFlagDisableDPIScale";
    private static final String GRASS_MIN = "FIntFRMMinGrassDistance";
    private static final String GRASS_MAX = "FIntFRMMaxGrassDistance";
    private static final String VULKAN = "FFlagDebugGraphicsPreferVulkan";
    private static final String OPENGL = "FFlagDebugGraphicsPreferOpenGL";
    private static final String D3D11 = "FFlagDebugGraphicsPreferD3D11";
    private static final String NO_D3D11 = "FFlagDebugGraphicsDisableDirect3D11";
    private static final String TARGET_FPS = "DFIntTaskSchedulerTargetFps";
    private static final String VSYNC = "FFlagEnableAndroidVsync";
    private static final String LIMIT_240 = "FFlagTaskSchedulerLimitTargetFpsTo2402";
    private static final String SHOW_FPS = "FFlagDebugDisplayFPS";
    private static final long[] FPS_VALUES = {0, 30, 60, 90, 120, 144, 165, 240, 360, 480, 9999};
    private static final long[] MSAA_VALUES = {0, 1, 2, 4, 8};
    private static final String[] SHADOWS = {"DFFlagDebugPauseVoxelizer", "FIntRenderShadowmapBias"};
    private static final String RESOLUTION = "DFIntDebugDynamicRenderKiloPixels";
    private static final int[] RESOLUTIONS = {0, 144, 240, 360, 480, 720, 1080, 1440, 2160, 4320};
    private static final String BLUR = "FIntRobloxGuiBlurIntensity";
    private static final String HIDE_GUI_GROUP = "DFIntCanHideGuiGroupId";
    private static final String HIDE_GUI_TOGGLES = "FFlagUserShowGuiHideToggles";
    private static final String FONT_PADDING = "FIntFontSizePadding";
    private static final String[] LAG_SPIKES = {"DFIntBandwidthManagerApplicationDefaultBps", "DFIntBandwidthManagerDataSenderMaxWorkCatchupMs"};
    private static final String[] FAST_LOAD = {"DFIntNumAssetsMaxToPreload", "FStringGetPlayerImageDefaultTimeout"};
    static final String[] UNDECLARED = {"FFlagDisablePostFx", "FIntRenderShadowIntensity", "FIntRenderGrassDetailStrands",
            "DFFlagDebugRenderForceTechnologyVoxel", "FFlagDebugForceFutureIsBrightPhase2", "FFlagDebugForceFutureIsBrightPhase3",
            "FFlagRenderUnifiedLighting12", "DFIntAssetPreloading", "FFlagEnablePreferredTextSizeScale",
            "FFlagEnablePreferredTextSizeSettingInMenus2", "DFIntCSGLevelOfDetailSwitchingDistanceStatic"};
    private static final String MTU = "DFIntConnectionMTUSize";
    private static final String FLAG_STATE = "FStringDebugShowFlagState";
    private static final String PING = "DFFlagDebugPrintDataPingBreakDown";
    private static final long[][] PROFILES = {{2, 2}, {0, 1}, {3, 4}};

    static final java.util.Set<String> KEYS = new java.util.HashSet<>();

    static {
        java.util.Collections.addAll(KEYS, GRASS_MOTION, FRM, SKY, MSAA, MSAA_OPTIONAL, TEXTURE, TEXTURE_ON, DPI, GRASS_MIN, GRASS_MAX,
                VULKAN, OPENGL, D3D11, NO_D3D11, TARGET_FPS, VSYNC, LIMIT_240, SHOW_FPS, RESOLUTION, BLUR, HIDE_GUI_GROUP,
                HIDE_GUI_TOGGLES, FONT_PADDING, MTU, FLAG_STATE, PING);
        java.util.Collections.addAll(KEYS, LOD);
        java.util.Collections.addAll(KEYS, SHADOWS);
        java.util.Collections.addAll(KEYS, LAG_SPIKES);
        java.util.Collections.addAll(KEYS, FAST_LOAD);
    }

    private static final int MESH_MIN = 1;
    private static final int MESH_MAX = 3;
    private static final int FRM_MIN = 1;
    private static final int FRM_MAX = 21;

    private final AppCompatActivity a;
    private final Store store;
    private final Runnable commit;
    private final LinearLayout root;
    private int profile;

    public FlagPresets(AppCompatActivity a, Store store, Runnable commit) {
        this.a = a;
        this.store = store;
        this.commit = commit;
        root = new LinearLayout(a);
        root.setOrientation(LinearLayout.VERTICAL);
    }

    public View view() {
        return root;
    }

    private Map<String, Object> values() {
        return store.flags.active().values;
    }

    private long num(String key, long fallback) {
        Object v = values().get(key);
        if (v instanceof Number) return ((Number) v).longValue();
        if (v instanceof String) {
            try {
                return Long.parseLong(((String) v).trim());
            } catch (NumberFormatException ignored) {
            }
        }
        return fallback;
    }

    private boolean on(String key) {
        Object v = values().get(key);
        return Boolean.TRUE.equals(v) || (v instanceof String && "true".equalsIgnoreCase((String) v));
    }

    private void put(String key, Object value) {
        if (value == null) values().remove(key);
        else values().put(key, value);
    }

    static boolean setFps(Map<String, Object> v, long fps) {
        boolean unlocked = fps > 60;
        if (fps <= 0) v.remove(TARGET_FPS);
        else v.put(TARGET_FPS, fps);
        if (unlocked) v.put(VSYNC, Boolean.FALSE);
        else v.remove(VSYNC);
        if (fps > 240) v.put(LIMIT_240, Boolean.FALSE);
        else v.remove(LIMIT_240);
        if (!unlocked || Boolean.TRUE.equals(v.get(OPENGL))) return false;
        v.remove(VULKAN);
        v.remove(D3D11);
        v.put(OPENGL, Boolean.TRUE);
        return true;
    }

    static boolean fixFps(Map<String, Object> v) {
        Object t = v.get(TARGET_FPS);
        long fps = t instanceof Number ? ((Number) t).longValue() : 0;
        if (fps <= 60) return false;
        boolean ready = Boolean.FALSE.equals(v.get(VSYNC)) && Boolean.TRUE.equals(v.get(OPENGL)) && (fps <= 240 || Boolean.FALSE.equals(v.get(LIMIT_240)));
        if (ready) return false;
        setFps(v, fps);
        return true;
    }

    private int pick(long[] options, String key) {
        if (!values().containsKey(key)) return 0;
        long value = num(key, Long.MIN_VALUE);
        for (int i = 1; i < options.length; i++) if (options[i] == value) return i;
        return -1;
    }

    private String[] labels(int array) {
        return a.getResources().getStringArray(array);
    }

    public void build() {
        root.removeAllViews();

        LinearLayout graphics = section(R.string.presets_section_graphics);
        long texture = num(TEXTURE, -1);
        choice(graphics, R.string.presets_texture, R.string.presets_texture_short, labels(R.array.presets_texture), !values().containsKey(TEXTURE) ? 0 : texture >= 0 && texture <= 3 ? (int) texture + 1 : -1, TEXTURE, i -> {
            put(TEXTURE, i == 0 ? null : (Object) (long) (i - 1));
            put(TEXTURE_ON, i == 0 ? null : Boolean.TRUE);
            commit.run();
        });
        choice(graphics, R.string.presets_msaa, R.string.presets_msaa_short, labels(R.array.presets_msaa), pick(MSAA_VALUES, MSAA), MSAA, i -> {
            put(MSAA, i == 0 ? null : (Object) MSAA_VALUES[i]);
            put(MSAA_OPTIONAL, null);
            commit.run();
        });
        choice(graphics, R.string.presets_render_mode, R.string.presets_render_mode_short, labels(R.array.presets_render_mode), on(VULKAN) ? 1 : on(OPENGL) ? 2 : 0, null, i -> {
            put(D3D11, null);
            put(VULKAN, i == 1 ? Boolean.TRUE : null);
            put(OPENGL, i == 2 ? Boolean.TRUE : null);
            put(NO_D3D11, null);
            commit.run();
            if (i != 2 && num(TARGET_FPS, 0) > 60) Ui.say(a, R.string.presets_fps_needs_opengl);
        });
        toggle(graphics, R.string.presets_gray_sky, R.string.presets_gray_sky_short, on(SKY), checked -> {
            put(SKY, checked ? Boolean.TRUE : null);
            commit.run();
        });
        toggle(graphics, R.string.presets_dpi_title, R.string.presets_dpi_short, on(DPI), checked -> {
            put(DPI, checked ? Boolean.TRUE : null);
            commit.run();
        });

        choice(graphics, R.string.presets_resolution, R.string.presets_resolution_short, labels(R.array.presets_resolution), resolutionIndex(), null, i -> {
            put(RESOLUTION, i == 0 ? null : (Object) kiloPixels(RESOLUTIONS[i]));
            commit.run();
        });
        toggle(graphics, R.string.presets_shadows, R.string.presets_shadows_short, on(SHADOWS[0]) && values().containsKey(SHADOWS[1]), checked -> {
            put(SHADOWS[0], checked ? Boolean.TRUE : null);
            put(SHADOWS[1], checked ? (Object) (-1L) : null);
            commit.run();
        });

        LinearLayout performance = section(R.string.presets_performance);
        choice(performance, R.string.presets_fps_limit, R.string.presets_fps_limit_short, labels(R.array.presets_fps_limit), pick(FPS_VALUES, TARGET_FPS), TARGET_FPS, i -> {
            boolean switched = setFps(values(), FPS_VALUES[i]);
            commit.run();
            if (FPS_VALUES[i] > 240) {
                new com.google.android.material.dialog.MaterialAlertDialogBuilder(a)
                        .setMessage(R.string.presets_fps_high)
                        .setPositiveButton(R.string.common_ok, null)
                        .show();
            } else if (switched) {
                Ui.say(a, R.string.presets_fps_opengl);
            }
            build();
        });
        toggle(performance, R.string.presets_show_fps, R.string.presets_show_fps_short, on(SHOW_FPS), checked -> {
            put(SHOW_FPS, checked ? Boolean.TRUE : null);
            commit.run();
        });

        LinearLayout detail = section(R.string.presets_section_detail);
        boolean meshOn = values().containsKey(LOD[0]);
        View meshSlider = slider(MESH_MIN, MESH_MAX, (int) Math.max(MESH_MIN, Math.min(MESH_MAX, num(LOD[0], MESH_MAX))), v -> {
            for (int i = 0; i < LOD.length; i++) put(LOD[i], (long) Math.max(0, Math.min(3, v - i)));
            commit.run();
        });
        toggle(detail, R.string.presets_mesh, R.string.presets_mesh_short, meshOn, checked -> {
            if (checked) {
                for (int i = 0; i < LOD.length; i++) put(LOD[i], (long) Math.max(0, Math.min(3, MESH_MAX - i)));
            } else {
                for (String k : LOD) put(k, null);
            }
            commit.run();
            build();
        });
        if (meshOn) detail.addView(meshSlider);

        boolean frmOn = values().containsKey(FRM);
        View frmSlider = slider(FRM_MIN, FRM_MAX, (int) Math.max(FRM_MIN, Math.min(FRM_MAX, num(FRM, FRM_MAX))), v -> {
            put(FRM, (long) v);
            commit.run();
        });
        toggle(detail, R.string.presets_frm_title, R.string.presets_frm_short, frmOn, checked -> {
            put(FRM, checked ? (Object) (long) FRM_MAX : null);
            commit.run();
            build();
        });
        if (frmOn) detail.addView(frmSlider);

        long motion = num(GRASS_MOTION, -1);
        choice(detail, R.string.presets_grass_motion_title, R.string.presets_grass_motion_short, labels(R.array.presets_grass_motion), !values().containsKey(GRASS_MOTION) ? 5 : motion >= 0 && motion <= 4 ? (int) motion : -1, GRASS_MOTION, i -> {
            put(GRASS_MOTION, i == 5 ? null : (Object) (long) i);
            commit.run();
        });
        toggle(detail, R.string.presets_remove_grass, R.string.presets_remove_grass_short, num(GRASS_MIN, -1) == 0 && num(GRASS_MAX, -1) == 0, checked -> {
            put(GRASS_MIN, checked ? (Object) 0L : null);
            put(GRASS_MAX, checked ? (Object) 0L : null);
            commit.run();
            build();
        });
        grassDistance(detail);

        LinearLayout ui = section(R.string.presets_section_interface);
        toggle(ui, R.string.presets_blur, R.string.presets_blur_short, values().containsKey(BLUR) && num(BLUR, -1) == 0, checked -> {
            put(BLUR, checked ? (Object) 0L : null);
            commit.run();
        });
        input(ui, R.string.presets_hide_gui, R.string.presets_hide_gui_short, HIDE_GUI_GROUP, true, v -> {
            put(HIDE_GUI_GROUP, v);
            put(HIDE_GUI_TOGGLES, v == null ? null : Boolean.TRUE);
        });
        input(ui, R.string.presets_font_size, R.string.presets_font_size_short, FONT_PADDING, true, v -> put(FONT_PADDING, v));

        LinearLayout network = section(R.string.presets_section_network);
        toggle(network, R.string.presets_lag_spikes, R.string.presets_lag_spikes_short, values().containsKey(LAG_SPIKES[0]) && values().containsKey(LAG_SPIKES[1]), checked -> {
            put(LAG_SPIKES[0], checked ? (Object) 96000L : null);
            put(LAG_SPIKES[1], checked ? (Object) 50L : null);
            commit.run();
        });
        toggle(network, R.string.presets_fast_load, R.string.presets_fast_load_short, num(FAST_LOAD[0], 0) == 2147483647L, checked -> {
            put(FAST_LOAD[0], checked ? (Object) 2147483647L : null);
            put(FAST_LOAD[1], checked ? "1" : null);
            commit.run();
        });
        input(network, R.string.presets_mtu, R.string.presets_mtu_short, MTU, true, v -> put(MTU, v));

        LinearLayout debug = section(R.string.presets_section_debug);
        input(debug, R.string.presets_flag_state, R.string.presets_flag_state_short, FLAG_STATE, false, v -> put(FLAG_STATE, v));
        toggle(debug, R.string.presets_ping, R.string.presets_ping_short, on(PING), checked -> {
            put(PING, checked ? Boolean.TRUE : null);
            commit.run();
        });

        LinearLayout quick = section(R.string.presets_section_quick);
        recommended(quick);
        allowlistRow(quick);
    }

    private LinearLayout section(int title) {
        return SettingRows.section(root, a.getString(title));
    }

    private LinearLayout row(LinearLayout parent, int title, int summary, View control) {
        return SettingRows.row(parent, a.getString(title), summary == 0 ? null : a.getString(summary), control);
    }

    private interface IntChoice {
        void on(int index);
    }

    private interface BoolChoice {
        void on(boolean checked);
    }

    private void toggle(LinearLayout parent, int title, int summary, boolean checked, BoolChoice change) {
        MaterialSwitch s = new MaterialSwitch(a);
        s.setChecked(checked);
        s.setContentDescription(a.getString(title));
        s.setOnCheckedChangeListener((b, c) -> change.on(c));
        row(parent, title, summary, s).setOnClickListener(v -> s.toggle());
    }

    private void choice(LinearLayout parent, int title, int summary, String[] labels, int selected, String key, IntChoice change) {
        int custom = selected < 0 ? labels.length : -1;
        String[] shown = labels;
        if (custom >= 0) {
            shown = java.util.Arrays.copyOf(labels, labels.length + 1);
            shown[custom] = a.getString(R.string.presets_custom, String.valueOf(values().get(key)));
        }
        SettingRows.choice(parent, a.getString(title), summary == 0 ? null : a.getString(summary), shown, custom >= 0 ? custom : selected, i -> {
            if (i == custom) return;
            change.on(i);
            if (custom >= 0) build();
        });
    }

    private View slider(int min, int max, int value, IntChoice change) {
        LinearLayout box = new LinearLayout(a);
        box.setOrientation(LinearLayout.HORIZONTAL);
        box.setGravity(Gravity.CENTER_VERTICAL);
        box.setPadding(Ui.dp(a, 16), 0, Ui.dp(a, 16), Ui.dp(a, 8));
        TextView low = new TextView(a);
        low.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
        low.setText(R.string.presets_low);
        TextView high = new TextView(a);
        high.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
        high.setText(R.string.presets_high);
        Slider s = new Slider(a);
        s.setValueFrom(min);
        s.setValueTo(max);
        s.setStepSize(1);
        s.setTickVisibilityMode(com.google.android.material.slider.TickVisibilityMode.TICK_VISIBILITY_HIDDEN);
        s.setValue(value);
        s.addOnChangeListener((slider, v, fromUser) -> {
            if (fromUser) change.on((int) v);
        });
        box.addView(low);
        box.addView(s, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        box.addView(high);
        return box;
    }

    private static long kiloPixels(int height) {
        long width = Math.round(height * 16.0 / 9.0);
        return width * height / 1000;
    }

    private int resolutionIndex() {
        long kp = num(RESOLUTION, -1);
        if (kp <= 0) return 0;
        int best = 1;
        for (int i = 1; i < RESOLUTIONS.length; i++) if (Math.abs(kiloPixels(RESOLUTIONS[i]) - kp) < Math.abs(kiloPixels(RESOLUTIONS[best]) - kp)) best = i;
        return best;
    }

    private interface ValueChange {
        void on(Object value);
    }

    private void input(LinearLayout parent, int title, int summary, String key, boolean numeric, ValueChange change) {
        EditText field = new EditText(a);
        field.setInputType(numeric ? InputType.TYPE_CLASS_NUMBER : InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        field.setSingleLine(true);
        Object current = values().get(key);
        field.setText(current == null ? "" : String.valueOf(current));
        field.setHint(R.string.presets_off);
        field.setGravity(Gravity.CENTER);
        field.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
        field.setBackgroundResource(R.drawable.vs_icon_tile);
        field.setMinWidth(Ui.dp(a, numeric ? 88 : 140));
        field.setMaxWidth(Ui.dp(a, 220));
        field.setMinHeight(Ui.dp(a, 40));
        field.setPadding(Ui.dp(a, 8), 0, Ui.dp(a, 8), 0);
        field.setContentDescription(a.getString(title));
        field.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {
            }

            @Override
            public void onTextChanged(CharSequence s, int start, int before, int count) {
            }

            @Override
            public void afterTextChanged(Editable s) {
                String text = s.toString().trim();
                if (text.isEmpty()) {
                    change.on(null);
                } else if (numeric) {
                    try {
                        change.on((long) Integer.parseInt(text));
                    } catch (NumberFormatException e) {
                        field.setError(a.getString(R.string.presets_whole_number));
                        return;
                    }
                } else {
                    change.on(text);
                }
                field.setError(null);
                commit.run();
            }
        });
        row(parent, title, summary, field);
    }

    private void grassDistance(LinearLayout parent) {
        LinearLayout fields = new LinearLayout(a);
        fields.setOrientation(LinearLayout.HORIZONTAL);
        fields.setGravity(Gravity.CENTER_VERTICAL);
        fields.addView(number(R.string.presets_grass_min, 100, GRASS_MIN));
        TextView dash = new TextView(a);
        dash.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
        dash.setText(R.string.presets_grass_to);
        dash.setPadding(Ui.dp(a, 6), 0, Ui.dp(a, 6), 0);
        fields.addView(dash);
        fields.addView(number(R.string.presets_grass_max, 290, GRASS_MAX));
        row(parent, R.string.presets_grass_distance, R.string.presets_grass_distance_short, fields);
    }

    private View number(int label, long fallback, String key) {
        EditText field = new EditText(a);
        field.setInputType(InputType.TYPE_CLASS_NUMBER);
        field.setSingleLine(true);
        field.setText(values().containsKey(key) ? String.valueOf(num(key, fallback)) : "");
        field.setHint(String.valueOf(fallback));
        field.setGravity(Gravity.CENTER);
        field.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
        field.setBackgroundResource(R.drawable.vs_icon_tile);
        field.setMinWidth(Ui.dp(a, 64));
        field.setMinHeight(Ui.dp(a, 40));
        field.setPadding(Ui.dp(a, 8), 0, Ui.dp(a, 8), 0);
        field.setContentDescription(a.getString(label));
        field.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {
            }

            @Override
            public void onTextChanged(CharSequence s, int start, int before, int count) {
            }

            @Override
            public void afterTextChanged(Editable s) {
                String text = s.toString().trim();
                try {
                    put(key, text.isEmpty() ? null : (Object) (long) Integer.parseInt(text));
                    field.setError(null);
                    commit.run();
                } catch (NumberFormatException e) {
                    field.setError(a.getString(R.string.presets_whole_number));
                }
            }
        });
        return field;
    }

    private void recommended(LinearLayout parent) {
        String[] names = labels(R.array.presets_profiles);
        Dropdown pick = new Dropdown(a, names, profile, i -> profile = i);
        MaterialButton apply = new MaterialButton(a);
        apply.setText(R.string.presets_apply_profile);
        apply.setOnClickListener(v -> {
            put(TEXTURE_ON, Boolean.TRUE);
            put(TEXTURE, PROFILES[profile][0]);
            put(MSAA, PROFILES[profile][1]);
            commit.run();
            Ui.say(a, a.getString(R.string.presets_profile_applied, names[profile]));
            build();
        });
        LinearLayout controls = new LinearLayout(a);
        controls.setOrientation(LinearLayout.HORIZONTAL);
        controls.setGravity(Gravity.CENTER_VERTICAL);
        controls.addView(pick.view);
        LinearLayout.LayoutParams ap = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        ap.setMarginStart(Ui.dp(a, 4));
        controls.addView(apply, ap);
        row(parent, R.string.presets_recommended_title, R.string.presets_recommended_short, controls);
    }

    private void allowlistRow(LinearLayout parent) {
        android.widget.ImageView chevron = new android.widget.ImageView(a);
        chevron.setImageResource(R.drawable.ic_chevron_right);
        chevron.setImageTintList(android.content.res.ColorStateList.valueOf(Ui.attr(a, R.attr.vsTextTertiary)));
        chevron.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        LinearLayout row = row(parent, R.string.presets_allowlist, R.string.presets_allowlist_short, chevron);
        row.setOnClickListener(v -> {
            LinearLayout list = new LinearLayout(a);
            list.setOrientation(LinearLayout.VERTICAL);
            for (Map.Entry<String, String> e : FlagWriter.ALLOWLIST.entrySet()) {
                Row item = Row.inflate(list);
                item.set(R.drawable.ic_flag, e.getKey(), e.getValue());
                item.view.setOnClickListener(x -> {
                    Ui.copy(a, e.getKey(), e.getKey());
                    Ui.say(a, a.getString(R.string.presets_copied, e.getKey()));
                });
                list.addView(item.view);
            }
            MaterialButton announcement = new MaterialButton(a, null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
            announcement.setText(R.string.presets_announcement);
            announcement.setIconResource(R.drawable.ic_globe);
            announcement.setOnClickListener(x -> Ui.openWeb(a, "https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569"));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.topMargin = Ui.dp(a, 12);
            list.addView(announcement, lp);
            Ui.surface(a, R.string.presets_allowlist, R.string.presets_allowlist_body, list).show();
        });
    }
}
