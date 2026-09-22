package com.voidstrap.android;

import android.app.Activity;
import android.app.Dialog;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.text.format.DateUtils;
import android.text.format.Formatter;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import com.google.android.material.bottomsheet.BottomSheetBehavior;
import com.google.android.material.bottomsheet.BottomSheetDialog;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.snackbar.Snackbar;

public final class Ui {
    private Ui() {
    }

    public static MaterialAlertDialogBuilder alert(Context c) {
        return new MaterialAlertDialogBuilder(c) {
            @Override
            public androidx.appcompat.app.AlertDialog show() {
                androidx.appcompat.app.AlertDialog dialog = super.show();
                android.view.Window w = dialog.getWindow();
                if (w != null) Crash.run("dialog decoration", () -> {
                    AppFont.watch(w.getDecorView());
                    w.getDecorView().post(() -> Crash.run("dialog translation", () -> LiveTranslator.translateTree(w.getDecorView())));
                });
                return dialog;
            }
        };
    }

    public static int attr(Context c, int attr) {
        if (c == null) return 0;
        return Crash.call("theme attribute", () -> com.google.android.material.color.MaterialColors.getColor(c, attr, 0), 0);
    }

    public static int dp(Context c, float v) {
        if (c == null) return Math.round(v);
        return Math.round(v * c.getResources().getDisplayMetrics().density);
    }

    public static void clearErrorOnEdit(com.google.android.material.textfield.TextInputLayout layout, android.widget.EditText input) {
        input.addTextChangedListener(new android.text.TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {
            }

            @Override
            public void onTextChanged(CharSequence s, int start, int before, int count) {
            }

            @Override
            public void afterTextChanged(android.text.Editable s) {
                if (layout.getError() != null) layout.setError(null);
            }
        });
    }

    public static void focus(android.app.Dialog d, android.widget.EditText input) {
        input.requestFocus();
        if (d.getWindow() != null) d.getWindow().setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
    }

    public static void stackWhenCramped(android.widget.LinearLayout row, TextView text, int maxLines) {
        row.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> {
            if (row.getOrientation() != android.widget.LinearLayout.HORIZONTAL || text.getLayout() == null || text.getLineCount() <= maxLines) return;
            row.setOrientation(android.widget.LinearLayout.VERTICAL);
            row.setGravity(android.view.Gravity.START);
            for (int i = 0; i < row.getChildCount(); i++) {
                View child = row.getChildAt(i);
                android.widget.LinearLayout.LayoutParams lp = (android.widget.LinearLayout.LayoutParams) child.getLayoutParams();
                lp.weight = 0;
                lp.width = child == text ? ViewGroup.LayoutParams.MATCH_PARENT : ViewGroup.LayoutParams.WRAP_CONTENT;
                child.setLayoutParams(lp);
            }
        });
    }

    public static boolean wide(Context c) {
        return c != null && c.getResources().getConfiguration().screenWidthDp >= 600;
    }

    public static void openWeb(Context c, String url) {
        if (c == null) return;
        if (url == null || !url.startsWith("https://")) {
            noBrowser(c);
            return;
        }
        try {
            c.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)).addCategory(Intent.CATEGORY_BROWSABLE).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (ActivityNotFoundException e) {
            noBrowser(c);
        } catch (RuntimeException e) {
            Crash.report("open link", e);
            noBrowser(c);
        }
    }

    private static void noBrowser(Context c) {
        Notify.toast(c, null, R.string.error_no_browser);
    }

    public static Snackbar say(Activity a, CharSequence text) {
        Snackbar bar = make(a, text);
        if (bar != null) Crash.run("snackbar", bar::show);
        return bar;
    }

    private static View anchor(Activity a) {
        if (a == null || a.isFinishing()) return null;
        View v = a.findViewById(R.id.snackbar_anchor);
        if (v == null) v = a.findViewById(android.R.id.content);
        if (v == null && a.getWindow() != null) v = a.getWindow().getDecorView();
        return v;
    }

    public static Snackbar make(Activity a, CharSequence text) {
        View parent = anchor(a);
        if (parent == null) return null;
        Snackbar bar;
        try {
            bar = Snackbar.make(parent, text == null ? "" : text, Snackbar.LENGTH_LONG).setDuration(Notify.length(Store.get(a)));
        } catch (RuntimeException e) {
            Crash.report("snackbar", e);
            return null;
        }
        Crash.run("snackbar style", () -> style(a, bar));
        return bar;
    }

    private static void style(Activity a, Snackbar bar) {
        View v = bar.getView();
        ViewGroup.LayoutParams lp = v.getLayoutParams();
        if (lp != null) {
            lp.width = ViewGroup.LayoutParams.WRAP_CONTENT;
            v.setMinimumWidth(0);
            if (lp instanceof androidx.coordinatorlayout.widget.CoordinatorLayout.LayoutParams) {
                ((androidx.coordinatorlayout.widget.CoordinatorLayout.LayoutParams) lp).gravity = android.view.Gravity.CENTER_HORIZONTAL | android.view.Gravity.BOTTOM;
            } else if (lp instanceof android.widget.FrameLayout.LayoutParams) {
                ((android.widget.FrameLayout.LayoutParams) lp).gravity = android.view.Gravity.CENTER_HORIZONTAL | android.view.Gravity.BOTTOM;
            }
        }
        TextView label = v.findViewById(com.google.android.material.R.id.snackbar_text);
        android.graphics.drawable.Drawable logo = androidx.core.content.ContextCompat.getDrawable(a, R.drawable.vs_logo);
        if (label != null && logo != null) {
            int size = dp(a, 20);
            logo.setBounds(0, 0, size, size);
            label.setCompoundDrawablesRelative(logo, null, null, null);
            label.setCompoundDrawablePadding(dp(a, 12));
        }
    }

    public static void replaced(androidx.recyclerview.widget.RecyclerView.Adapter<?> adapter, int oldCount) {
        if (adapter == null) return;
        Crash.run("list refresh", () -> {
            adapter.notifyItemRangeRemoved(0, Math.max(0, oldCount));
            adapter.notifyItemRangeInserted(0, adapter.getItemCount());
        });
    }

    public static Snackbar say(Activity a, int res) {
        if (a == null || res == 0) return null;
        return say(a, Crash.call("string " + res, () -> a.getString(res), ""));
    }

    public static void copy(Context c, String label, String text) {
        if (c == null || text == null) return;
        Crash.run("clipboard", () -> {
            ClipboardManager cm = (ClipboardManager) c.getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm != null) cm.setPrimaryClip(ClipData.newPlainText(label == null ? "" : label, text));
        });
    }

    public static CharSequence ago(Context c, long time) {
        if (time <= 0) return c.getString(R.string.common_never);
        if (Math.abs(System.currentTimeMillis() - time) < DateUtils.MINUTE_IN_MILLIS) return c.getString(R.string.common_just_now);
        return DateUtils.getRelativeTimeSpanString(time, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS);
    }

    public static int writeText(Context c, Uri uri, String text) {
        try (java.io.OutputStream out = c.getContentResolver().openOutputStream(uri, "wt")) {
            if (out == null) throw new java.io.IOException("no stream");
            out.write(text.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            return 0;
        } catch (java.io.IOException | RuntimeException e) {
            return writeFailure(uri, e);
        }
    }

    public static int writeFailure(Uri uri, Exception e) {
        android.util.Log.w("Voidstrap", "Write failed for " + uri.getAuthority(), e);
        return e instanceof SecurityException ? R.string.export_denied : R.string.export_failed;
    }

    public static String size(Context c, long bytes) {
        return Formatter.formatShortFileSize(c, bytes);
    }

    public static Dialog surface(Activity a, int title, int subtitle, View content) {
        return surface(a, title, subtitle, content, null);
    }

    public static Dialog surface(Activity a, int title, int subtitle, View content, Runnable onDismiss) {
        return surface(a, a.getString(title), subtitle == 0 ? null : a.getString(subtitle), content, onDismiss);
    }

    public static Dialog surface(Activity a, CharSequence title, CharSequence subtitle, View content, Runnable onDismiss) {
        View frame = LayoutInflater.from(a).inflate(R.layout.sheet_frame, null, false);
        ((TextView) frame.findViewById(R.id.sheet_title)).setText(title);
        TextView sub = frame.findViewById(R.id.sheet_subtitle);
        if (subtitle != null && subtitle.length() > 0) sub.setText(subtitle);
        else sub.setVisibility(View.GONE);
        ((ViewGroup) frame.findViewById(R.id.sheet_content)).addView(content);
        Dialog d;
        if (wide(a)) {
            d = new MaterialAlertDialogBuilder(a).setView(frame).create();
        } else {
            BottomSheetDialog sheet = new BottomSheetDialog(a);
            sheet.setContentView(frame);
            sheet.getBehavior().setState(BottomSheetBehavior.STATE_EXPANDED);
            sheet.getBehavior().setSkipCollapsed(true);
            d = sheet;
        }
        d.setOnShowListener(x -> {
            AppFont.watch(d.getWindow() == null ? frame : d.getWindow().getDecorView());
            LiveTranslator.translateTree(frame);
        });
        View close = frame.findViewById(R.id.sheet_close);
        close.setOnClickListener(v -> d.dismiss());
        if (a instanceof androidx.lifecycle.LifecycleOwner) {
            androidx.lifecycle.Lifecycle lifecycle = ((androidx.lifecycle.LifecycleOwner) a).getLifecycle();
            androidx.lifecycle.DefaultLifecycleObserver guard = new androidx.lifecycle.DefaultLifecycleObserver() {
                @Override
                public void onDestroy(@androidx.annotation.NonNull androidx.lifecycle.LifecycleOwner owner) {
                    if (d instanceof BottomSheetDialog) ((BottomSheetDialog) d).setDismissWithAnimation(false);
                    if (d.isShowing()) d.dismiss();
                }
            };
            lifecycle.addObserver(guard);
            d.setOnDismissListener(x -> {
                lifecycle.removeObserver(guard);
                if (onDismiss != null) onDismiss.run();
            });
        } else if (onDismiss != null) {
            d.setOnDismissListener(x -> onDismiss.run());
        }
        return d;
    }
}
