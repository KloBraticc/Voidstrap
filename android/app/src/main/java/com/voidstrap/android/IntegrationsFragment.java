package com.voidstrap.android;

import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.checkbox.MaterialCheckBox;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.imageview.ShapeableImageView;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.shape.ShapeAppearanceModel;
import com.google.android.material.textfield.TextInputLayout;

import java.text.DateFormat;
import java.util.List;

public final class IntegrationsFragment extends Page {
    private LinearLayout root;
    private TextView discordStatus;
    private ShapeableImageView previewLarge;
    private TextView previewDetails;
    private TextView previewState;
    private String shape = "";
    private String previewIcon;
    private ActivityResultLauncher<String> notifyPermission;

    private interface BoolChoice {
        void on(boolean checked);
    }

    public IntegrationsFragment() {
        super(R.layout.fragment_integrations);
    }

    @Override
    public void onCreate(@Nullable Bundle saved) {
        super.onCreate(saved);
        notifyPermission = registerForActivityResult(new ActivityResultContracts.RequestPermission(), granted -> {
            if (!granted && isAdded()) Ui.say(host(), R.string.integrations_notify_blocked);
        });
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        root = v.findViewById(R.id.integrations_sections);
        shape = "";
    }

    private String shape() {
        return Integrations.on(store, Integrations.TRACKING) + " " + Integrations.on(store, Integrations.RPC) + " " + Integrations.playerLogs(store) + " " + Integrations.on(store, Integrations.NAME) + " " + Integrations.on(store, Integrations.NOTIFY) + " " + Integrations.on(store, Integrations.LOCATION);
    }

    @Override
    protected void refresh() {
        String now = shape();
        if (!now.equals(shape)) {
            shape = now;
            build();
        }
        bindDiscord();
        bindPreview();
    }

    private void build() {
        root.removeAllViews();
        Context c = requireContext();
        boolean tracking = Integrations.on(store, Integrations.TRACKING);
        boolean rpc = tracking && Integrations.on(store, Integrations.RPC);

        LinearLayout activity = SettingRows.section(root, getString(R.string.integrations_activity));
        toggle(activity, R.string.integrations_tracking, R.string.integrations_tracking_body, Integrations.TRACKING, true, on -> {
            if (on) askNotifications();
            else ActivityService.stop(c);
        });
        toggle(activity, R.string.integrations_location, R.string.integrations_location_body, Integrations.LOCATION, tracking, null);
        toggle(activity, R.string.integrations_notify, R.string.integrations_notify_body, Integrations.NOTIFY, tracking, on -> {
            if (on) askNotifications();
        });
        MaterialSwitch logs = new MaterialSwitch(c);
        logs.setChecked(Integrations.playerLogs(store));
        logs.setEnabled(tracking);
        logs.setContentDescription(getString(R.string.integrations_player_logs));
        LinearLayout logsRow = SettingRows.row(activity, getString(R.string.integrations_player_logs), getString(R.string.integrations_player_logs_body), logs);
        logs.setOnCheckedChangeListener((b, on) -> Integrations.setPlayerLogs(store, on));
        clickable(logsRow, logs, tracking);
        LinearLayout logRow = SettingRows.row(activity, getString(R.string.integrations_log), getString(R.string.integrations_log_body), chevron(c));
        logRow.setOnClickListener(x -> showLog());

        LinearLayout discord = SettingRows.section(root, getString(R.string.integrations_discord));
        LinearLayout statusRow = SettingRows.row(discord, getString(R.string.integrations_discord_status), getString(R.string.integrations_discord_note), null);
        discordStatus = (TextView) ((LinearLayout) statusRow.getChildAt(0)).getChildAt(1);
        statusRow.setOnClickListener(x -> openDiscord());
        toggle(discord, R.string.integrations_discord_rpc, R.string.integrations_discord_rpc_body, Integrations.RPC, tracking, null);
        toggle(discord, R.string.integrations_discord_account, R.string.integrations_discord_account_body, Integrations.ACCOUNT, rpc, null);
        toggle(discord, R.string.integrations_discord_join, R.string.integrations_discord_join_body, Integrations.JOINING, rpc, null);

        LinearLayout custom = SettingRows.section(root, getString(R.string.integrations_custom_rpc));
        custom.addView(preview(c));
        TextView note = new TextView(c);
        note.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        note.setText(R.string.integrations_custom_rpc_body);
        note.setPadding(Ui.dp(c, 16), 0, Ui.dp(c, 16), Ui.dp(c, 8));
        custom.addView(note);
        text(custom, R.string.integrations_custom_name, R.string.integrations_custom_name_body, Integrations.CUSTOM_NAME, rpc && Integrations.on(store, Integrations.NAME), false);
        text(custom, R.string.integrations_custom_icon, R.string.integrations_custom_icon_body, Integrations.CUSTOM_ICON, rpc, true);
        String[] idle = getResources().getStringArray(R.array.integrations_idle_icons);
        int selected = java.util.Arrays.asList(Integrations.IDLE_KEYS).indexOf(store.setting(Integrations.IDLE_ICON, "blue"));
        SettingRows.choice(custom, getString(R.string.integrations_idle_icon), getString(R.string.integrations_idle_icon_body), idle, Math.max(0, selected), i -> store.putSetting(Integrations.IDLE_ICON, Integrations.IDLE_KEYS[i]));
        enable(custom.getChildAt(custom.getChildCount() - 1), rpc);

        LinearLayout advanced = SettingRows.section(root, getString(R.string.integrations_advanced));
        TextView advancedNote = new TextView(c);
        advancedNote.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        advancedNote.setText(R.string.integrations_advanced_body);
        advancedNote.setPadding(Ui.dp(c, 16), Ui.dp(c, 8), Ui.dp(c, 16), Ui.dp(c, 4));
        advanced.addView(advancedNote);
        check(advanced, R.string.integrations_rpc_name, 0, Integrations.NAME, rpc);
        check(advanced, R.string.integrations_rpc_icon, 0, Integrations.ICON, rpc);
        check(advanced, R.string.integrations_rpc_creator, 0, Integrations.CREATOR, rpc);
        check(advanced, R.string.integrations_rpc_server, R.string.integrations_rpc_server_body, Integrations.SERVER, rpc);
        check(advanced, R.string.integrations_rpc_location, 0, Integrations.RPC_LOCATION, rpc);
        check(advanced, R.string.integrations_rpc_beta, R.string.integrations_rpc_beta_body, Integrations.BETA, rpc);
        check(advanced, R.string.integrations_rpc_flags, 0, Integrations.FLAG_COUNT, rpc);
        previewIcon = null;
    }

    private void toggle(LinearLayout parent, int title, int summary, String key, boolean enabled, BoolChoice after) {
        MaterialSwitch s = new MaterialSwitch(requireContext());
        s.setChecked(Integrations.on(store, key));
        s.setEnabled(enabled);
        s.setContentDescription(getString(title));
        LinearLayout row = SettingRows.row(parent, getString(title), getString(summary), s);
        s.setOnCheckedChangeListener((b, on) -> {
            Integrations.set(store, key, on);
            if (after != null) after.on(on);
        });
        clickable(row, s, enabled);
    }

    private void check(LinearLayout parent, int title, int summary, String key, boolean enabled) {
        MaterialCheckBox box = new MaterialCheckBox(requireContext());
        box.setChecked(Integrations.on(store, key));
        box.setEnabled(enabled);
        box.setContentDescription(getString(title));
        LinearLayout row = SettingRows.row(parent, getString(title), summary == 0 ? null : getString(summary), box);
        box.setOnCheckedChangeListener((b, on) -> Integrations.set(store, key, on));
        clickable(row, box, enabled);
    }

    private static void clickable(LinearLayout row, android.widget.CompoundButton control, boolean enabled) {
        enable(row, enabled);
        if (enabled) row.setOnClickListener(x -> control.toggle());
    }

    private static void enable(View row, boolean enabled) {
        row.setEnabled(enabled);
        row.setClickable(enabled);
        row.setAlpha(enabled ? 1f : 0.5f);
    }

    private ImageView chevron(Context c) {
        ImageView v = new ImageView(c);
        v.setImageResource(R.drawable.ic_chevron_right);
        v.setImageTintList(android.content.res.ColorStateList.valueOf(Ui.attr(c, android.R.attr.textColorSecondary)));
        v.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        return v;
    }

    private void text(LinearLayout parent, int title, int summary, String key, boolean enabled, boolean url) {
        String value = store.setting(key, "");
        LinearLayout row = SettingRows.row(parent, getString(title), value.isEmpty() ? getString(summary) : value, null);
        ((TextView) ((LinearLayout) row.getChildAt(0)).getChildAt(1)).setMaxLines(2);
        enable(row, enabled);
        if (!enabled) return;
        row.setOnClickListener(x -> {
            View body = LayoutInflater.from(requireContext()).inflate(R.layout.dialog_input, null);
            TextInputLayout layout = body.findViewById(R.id.input_layout);
            EditText input = body.findViewById(R.id.input);
            layout.setHint(title);
            input.setText(store.setting(key, ""));
            input.setSelection(input.getText().length());
            if (url) input.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
            Ui.clearErrorOnEdit(layout, input);
            androidx.appcompat.app.AlertDialog d = Ui.alert(requireContext())
                    .setTitle(title)
                    .setMessage(summary)
                    .setView(body)
                    .setPositiveButton(R.string.common_ok, null)
                    .setNeutralButton(R.string.common_clear, (di, w) -> save(key, ""))
                    .setNegativeButton(R.string.common_cancel, null)
                    .show();
            d.getButton(androidx.appcompat.app.AlertDialog.BUTTON_POSITIVE).setOnClickListener(b -> {
                String v = Store.clip(input.getText().toString().trim(), url ? 500 : 128);
                if (url && !v.isEmpty() && !v.startsWith("https://")) {
                    layout.setError(getString(R.string.integrations_custom_icon_invalid));
                    return;
                }
                save(key, v);
                d.dismiss();
            });
            Ui.focus(d, input);
        });
    }

    private void save(String key, String value) {
        store.putSetting(key, value.isEmpty() ? null : value);
        shape = "";
        refresh();
    }

    private View preview(Context c) {
        MaterialCardView card = new MaterialCardView(c);
        card.setCardBackgroundColor(Ui.attr(c, R.attr.vsInput));
        card.setStrokeWidth(0);
        LinearLayout box = new LinearLayout(c);
        box.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(c, 12);
        box.setPadding(pad, pad, pad, pad);
        TextView header = new TextView(c);
        header.setTextAppearance(R.style.TextAppearance_Voidstrap_Section);
        header.setText(R.string.integrations_preview_playing);
        box.addView(header);
        LinearLayout line = new LinearLayout(c);
        line.setOrientation(LinearLayout.HORIZONTAL);
        line.setGravity(Gravity.CENTER_VERTICAL);
        line.setPadding(0, Ui.dp(c, 8), 0, 0);
        android.widget.FrameLayout art = new android.widget.FrameLayout(c);
        int size = Ui.dp(c, 72);
        previewLarge = new ShapeableImageView(c);
        previewLarge.setScaleType(ImageView.ScaleType.CENTER_CROP);
        previewLarge.setShapeAppearanceModel(ShapeAppearanceModel.builder().setAllCornerSizes(Ui.dp(c, 8)).build());
        art.addView(previewLarge, new android.widget.FrameLayout.LayoutParams(size, size));
        ShapeableImageView small = new ShapeableImageView(c);
        small.setImageResource(R.mipmap.ic_launcher_round);
        small.setShapeAppearanceModel(ShapeAppearanceModel.builder().setAllCornerSizes(ShapeAppearanceModel.PILL).build());
        int smallSize = Ui.dp(c, 24);
        android.widget.FrameLayout.LayoutParams sp = new android.widget.FrameLayout.LayoutParams(smallSize, smallSize, Gravity.BOTTOM | Gravity.END);
        art.addView(small, sp);
        line.addView(art, new LinearLayout.LayoutParams(size + Ui.dp(c, 6), size + Ui.dp(c, 6)));
        LinearLayout texts = new LinearLayout(c);
        texts.setOrientation(LinearLayout.VERTICAL);
        texts.setPadding(Ui.dp(c, 12), 0, 0, 0);
        TextView app = new TextView(c);
        app.setTextAppearance(R.style.TextAppearance_Voidstrap_CardTitle);
        app.setText(R.string.integrations_preview_app);
        texts.addView(app);
        previewDetails = new TextView(c);
        previewDetails.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
        texts.addView(previewDetails);
        previewState = new TextView(c);
        previewState.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
        texts.addView(previewState);
        TextView elapsed = new TextView(c);
        elapsed.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        elapsed.setText(R.string.integrations_preview_elapsed);
        texts.addView(elapsed);
        line.addView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        box.addView(line);
        TextView label = new TextView(c);
        label.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
        label.setText(R.string.integrations_preview);
        label.setGravity(Gravity.END);
        box.addView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        card.addView(box);
        LinearLayout holder = new LinearLayout(c);
        holder.setPadding(Ui.dp(c, 12), Ui.dp(c, 8), Ui.dp(c, 12), Ui.dp(c, 8));
        holder.addView(card, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        return holder;
    }

    private void bindPreview() {
        if (previewDetails == null) return;
        ActivityWatcher.Data d = new ActivityWatcher.Data();
        d.placeId = 1;
        d.jobId = "preview";
        d.joined = System.currentTimeMillis();
        Integrations.Game g = new Integrations.Game();
        g.name = getString(R.string.integrations_preview_game);
        g.creator = getString(R.string.integrations_preview_creator);
        g.location = getString(R.string.integrations_preview_location);
        Integrations.Presence p = Integrations.game(store, d, g, store.flags.active().values.size());
        previewDetails.setText(p.details);
        previewDetails.setVisibility(p.details.isEmpty() ? View.GONE : View.VISIBLE);
        previewState.setText(p.state);
        previewState.setVisibility(p.state.isEmpty() ? View.GONE : View.VISIBLE);
        String custom = store.setting(Integrations.CUSTOM_ICON, "");
        boolean icon = !custom.isEmpty() || Integrations.on(store, Integrations.ICON);
        previewLarge.setVisibility(icon ? View.VISIBLE : View.INVISIBLE);
        String key = custom.isEmpty() ? "" : custom;
        if (key.equals(previewIcon)) return;
        previewIcon = key;
        if (key.isEmpty()) {
            previewLarge.setImageResource(R.mipmap.ic_launcher);
        } else {
            Net.image(previewLarge, key, R.mipmap.ic_launcher, Ui.dp(requireContext(), 72));
        }
    }

    private void bindDiscord() {
        if (discordStatus == null) return;
        String result = DiscordRpc.ROBLOX.status();
        String text;
        if (!DiscordRpc.installed(requireContext())) text = getString(R.string.integrations_discord_missing_app);
        else if (ActivityService.running() && "ok".equals(result)) text = getString(R.string.integrations_discord_live);
        else if (ActivityService.running() && !result.isEmpty()) text = getString(R.string.integrations_discord_error, result);
        else text = getString(R.string.integrations_discord_ready);
        discordStatus.setText(getString(R.string.integrations_discord_status_line, getString(R.string.integrations_discord_note), text));
    }

    private void openDiscord() {
        if (DiscordRpc.installed(requireContext())) return;
        Ui.openWeb(requireContext(), "https://play.google.com/store/apps/details?id=" + DiscordRpc.PACKAGE);
    }

    private void askNotifications() {
        if (Build.VERSION.SDK_INT < 33 || !Integrations.on(store, Integrations.NOTIFY)) return;
        if (ContextCompat.checkSelfPermission(requireContext(), android.Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return;
        notifyPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS);
    }

    private void showLog() {
        Context c = requireContext();
        List<ActivityService.Entry> entries = ActivityService.log();
        LinearLayout list = new LinearLayout(c);
        list.setOrientation(LinearLayout.VERTICAL);
        android.app.Dialog[] sheet = new android.app.Dialog[1];
        if (entries.isEmpty()) {
            TextView empty = new TextView(c);
            empty.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
            empty.setText(R.string.integrations_log_empty);
            empty.setPadding(Ui.dp(c, 16), Ui.dp(c, 8), Ui.dp(c, 16), Ui.dp(c, 16));
            list.addView(empty);
        } else {
            MaterialButton clear = new MaterialButton(c, null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
            clear.setText(R.string.integrations_log_clear);
            clear.setOnClickListener(x -> {
                ActivityService.clearLog();
                sheet[0].dismiss();
            });
            LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            cp.setMarginStart(Ui.dp(c, 16));
            cp.bottomMargin = Ui.dp(c, 4);
            list.addView(clear, cp);
            DateFormat time = android.text.format.DateFormat.getTimeFormat(c);
            int shown = 0;
            for (int i = entries.size() - 1; i >= 0 && shown < 200; i--, shown++) {
                ActivityService.Entry e = entries.get(i);
                Row row = Row.inflate(list);
                int icon = e.kind.equals("chat") ? R.drawable.ic_megaphone : e.kind.equals("game") ? R.drawable.ic_games : R.drawable.ic_people;
                int kind = e.kind.equals("chat") ? R.string.integrations_log_chat : e.kind.equals("join") ? R.string.integrations_log_join : e.kind.equals("leave") ? R.string.integrations_log_leave : 0;
                String when = time.format(new java.util.Date(e.time));
                row.set(icon, e.text, kind == 0 ? when : getString(R.string.pair, when, getString(kind)));
                row.title.setMaxLines(4);
                row.view.setClickable(false);
                list.addView(row.view);
            }
        }
        sheet[0] = Ui.surface(host(), R.string.integrations_log, R.string.integrations_log_body, list);
        sheet[0].show();
    }
}
