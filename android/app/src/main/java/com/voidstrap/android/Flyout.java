package com.voidstrap.android;

import android.animation.TimeInterpolator;
import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Rect;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.GradientDrawable;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.PopupWindow;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

final class Flyout {
    static final TimeInterpolator QUINTIC_OUT = t -> 1f - (float) Math.pow(1f - t, 5);
    static final TimeInterpolator QUARTIC_IN_OUT = t -> t < 0.5f ? 8f * t * t * t * t : 1f - (float) Math.pow(-2f * t + 2f, 4) / 2f;
    static final TimeInterpolator CUBIC_OUT = t -> 1f - (float) Math.pow(1f - t, 3);
    static final TimeInterpolator LINEAR = t -> t;

    private static float pointerX = -1;
    private static float pointerY = -1;

    private static final class Entry {
        int icon;
        CharSequence text;
        Runnable action;
    }

    private final Context c;
    private final List<Entry> entries = new ArrayList<>();

    Flyout(Context c) {
        this.c = c;
    }

    static void track(MotionEvent e) {
        pointerX = e.getRawX();
        pointerY = e.getRawY();
    }

    static void bind(View v, java.util.function.Supplier<Flyout> menu) {
        v.setOnLongClickListener(x -> {
            menu.get().showAtPointer(x);
            return true;
        });
        v.setOnContextClickListener(x -> {
            menu.get().showAtPointer(x);
            return true;
        });
    }

    Flyout add(int icon, CharSequence text, Runnable action) {
        Entry e = new Entry();
        e.icon = icon;
        e.text = text;
        e.action = action;
        entries.add(e);
        return this;
    }

    Flyout add(int icon, int text, Runnable action) {
        return add(icon, c.getString(text), action);
    }

    Flyout separator() {
        entries.add(null);
        return this;
    }

    void showAtPointer(View anyView) {
        if (pointerX < 0) {
            show(anyView);
            return;
        }
        int x = Math.round(pointerX);
        int y = Math.round(pointerY);
        open(anyView, new Rect(x, y, x, y));
    }

    void show(View anchor) {
        int[] at = new int[2];
        anchor.getLocationOnScreen(at);
        open(anchor, new Rect(at[0], at[1], at[0] + anchor.getWidth(), at[1] + anchor.getHeight()));
    }

    private int dp(float v) {
        return Ui.dp(c, v);
    }

    private void open(View host, Rect anchor) {
        int shadow = dp(16);
        int edge = dp(8);
        FrameLayout root = new FrameLayout(c);
        root.setClipChildren(false);
        root.setClipToPadding(false);
        root.setPadding(shadow, shadow, shadow, shadow);

        ScrollView surface = new ScrollView(c);
        surface.setMinimumWidth(dp(140));
        surface.setPadding(0, dp(3), 0, dp(3));
        surface.setVerticalScrollBarEnabled(false);
        surface.setOverScrollMode(View.OVER_SCROLL_NEVER);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Ui.attr(c, R.attr.vsMenu));
        bg.setCornerRadius(dp(8));
        surface.setBackground(bg);
        surface.setElevation(dp(6));
        surface.setClipToOutline(true);

        LinearLayout list = new LinearLayout(c);
        list.setOrientation(LinearLayout.VERTICAL);
        surface.addView(list, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        root.addView(surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        PopupWindow window = new PopupWindow(root, ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, true);
        AppFont.watch(root);
        window.setBackgroundDrawable(new ColorDrawable(0));
        window.setOutsideTouchable(true);
        window.setAnimationStyle(0);
        window.setClippingEnabled(false);

        Runnable[] close = new Runnable[1];
        Runnable[] pending = new Runnable[1];
        boolean[] closing = {false};
        for (Entry e : entries) {
            if (e == null) {
                list.addView(separatorView());
            } else {
                list.addView(itemView(e, () -> {
                    if (closing[0]) return;
                    pending[0] = e.action;
                    close[0].run();
                }));
            }
        }

        Rect frame = new Rect();
        host.getWindowVisibleDisplayFrame(frame);
        root.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
        int below = frame.bottom - edge - anchor.bottom;
        int above = anchor.top - frame.top - edge;
        int naturalHeight = surface.getMeasuredHeight();
        boolean down = naturalHeight <= below || below >= above;
        int maxHeight = frame.height() - 2 * edge;
        int maxWidth = frame.width() - 2 * edge;
        FrameLayout.LayoutParams surfaceParams = (FrameLayout.LayoutParams) surface.getLayoutParams();
        if (naturalHeight > maxHeight) surfaceParams.height = maxHeight;
        if (surface.getMeasuredWidth() > maxWidth) surfaceParams.width = maxWidth;
        surface.setLayoutParams(surfaceParams);
        root.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);

        int surfaceWidth = surface.getMeasuredWidth();
        int surfaceHeight = surface.getMeasuredHeight();
        int surfaceLeft = anchor.left;
        if (surfaceLeft + surfaceWidth > frame.right - edge) surfaceLeft = anchor.right - surfaceWidth;
        surfaceLeft = Math.max(frame.left + edge, Math.min(surfaceLeft, frame.right - edge - surfaceWidth));
        int surfaceTop = down ? anchor.bottom : anchor.top - surfaceHeight;
        surfaceTop = Math.max(frame.top + edge, Math.min(surfaceTop, frame.bottom - edge - surfaceHeight));
        int popupLeft = surfaceLeft - shadow;
        int popupTop = surfaceTop - shadow;
        int placedLeft = surfaceLeft;
        int placedTop = surfaceTop;

        close[0] = () -> {
            if (closing[0]) return;
            closing[0] = true;
            animateOut(surface, anchor, placedLeft, placedTop, surfaceWidth, surfaceHeight, () -> {
                if (window.isShowing()) window.dismiss();
                Runnable action = pending[0];
                pending[0] = null;
                if (action != null) action.run();
            });
        };
        window.setTouchInterceptor((v, event) -> {
            if (event.getAction() != MotionEvent.ACTION_OUTSIDE) return false;
            close[0].run();
            v.performClick();
            return true;
        });
        window.setOnDismissListener(() -> surface.animate().cancel());
        if (host.getWindowToken() == null) return;
        try {
            window.showAtLocation(host, Gravity.NO_GRAVITY, popupLeft, popupTop);
        } catch (RuntimeException e) {
            Crash.report("menu", e);
            return;
        }
        Crash.run("menu animation", () -> animateIn(surface, anchor, placedLeft, placedTop, surfaceWidth, surfaceHeight));
    }

    static float pivot(float anchorCenter, int surfaceStart, int surfaceSize) {
        return Math.max(0f, Math.min(surfaceSize, anchorCenter - surfaceStart));
    }

    static float shift(float anchorCenter, float surfaceCenter, float distance) {
        return anchorCenter < surfaceCenter ? -distance : anchorCenter > surfaceCenter ? distance : 0f;
    }

    static void animateIn(View surface, Rect anchor, int left, int top, int width, int height) {
        float distance = Ui.dp(surface.getContext(), 10);
        float x = shift(anchor.exactCenterX(), left + width / 2f, distance);
        float y = shift(anchor.exactCenterY(), top + height / 2f, distance);
        surface.animate().cancel();
        surface.setPivotX(pivot(anchor.exactCenterX(), left, width));
        surface.setPivotY(pivot(anchor.exactCenterY(), top, height));
        surface.setAlpha(0f);
        surface.setScaleX(0.9f);
        surface.setScaleY(0.82f);
        surface.setTranslationX(x);
        surface.setTranslationY(y);
        surface.animate()
                .alpha(1f)
                .scaleX(1f)
                .scaleY(1f)
                .translationX(0f)
                .translationY(0f)
                .setDuration(300)
                .setInterpolator(QUINTIC_OUT)
                .withLayer()
                .start();
    }

    static void animateOut(View surface, Rect anchor, int left, int top, int width, int height, Runnable end) {
        float distance = Ui.dp(surface.getContext(), 6);
        surface.animate().cancel();
        surface.setPivotX(pivot(anchor.exactCenterX(), left, width));
        surface.setPivotY(pivot(anchor.exactCenterY(), top, height));
        surface.animate()
                .alpha(0f)
                .scaleX(0.94f)
                .scaleY(0.9f)
                .translationX(shift(anchor.exactCenterX(), left + width / 2f, distance))
                .translationY(shift(anchor.exactCenterY(), top + height / 2f, distance))
                .setDuration(130)
                .setInterpolator(QUARTIC_IN_OUT)
                .withLayer()
                .withEndAction(end)
                .start();
    }

    private static ValueAnimator fade(GradientDrawable d, int to, long ms, TimeInterpolator ease, ValueAnimator current) {
        if (current != null) current.cancel();
        ValueAnimator a = ValueAnimator.ofInt(d.getAlpha(), to);
        a.setDuration(ms);
        a.setInterpolator(ease);
        a.addUpdateListener(x -> d.setAlpha((Integer) x.getAnimatedValue()));
        a.start();
        return a;
    }

    private View separatorView() {
        FrameLayout line = new FrameLayout(c);
        line.setBackgroundColor(c.getColor(R.color.vs_card_stroke));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 1);
        lp.topMargin = dp(1);
        lp.bottomMargin = dp(1);
        line.setLayoutParams(lp);
        return line;
    }

    private View itemView(Entry e, Runnable click) {
        Runnable[] press = new Runnable[2];
        ValueAnimator[] highlight = new ValueAnimator[1];
        FrameLayout item = new FrameLayout(c) {
            @Override
            public void setPressed(boolean pressed) {
                boolean changed = pressed != isPressed();
                super.setPressed(pressed);
                if (changed && press[0] != null) press[pressed ? 0 : 1].run();
            }
        };
        item.setClickable(true);
        item.setFocusable(true);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(dp(4), dp(1), dp(4), dp(1));
        item.setLayoutParams(lp);

        GradientDrawable hl = new GradientDrawable();
        hl.setColor(Ui.attr(c, com.google.android.material.R.attr.colorSecondary));
        hl.setCornerRadius(dp(4));
        hl.setAlpha(0);
        item.setBackground(hl);

        View pill = new View(c);
        GradientDrawable pd = new GradientDrawable();
        pd.setColor(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary));
        pd.setCornerRadius(dp(1.5f));
        pill.setBackground(pd);
        FrameLayout.LayoutParams pp = new FrameLayout.LayoutParams(dp(3), dp(16), Gravity.START | Gravity.CENTER_VERTICAL);
        pp.setMarginStart(dp(3));
        pill.setPivotY(dp(8));
        pill.setScaleY(5f / 16f);
        pill.setAlpha(0f);
        item.addView(pill, pp);

        LinearLayout content = new LinearLayout(c);
        content.setOrientation(LinearLayout.HORIZONTAL);
        content.setGravity(Gravity.CENTER_VERTICAL);
        content.setPadding(dp(8), dp(6), dp(8), dp(6));
        int text = Ui.attr(c, R.attr.vsTextPrimary);
        if (e.icon != 0) {
            ImageView icon = new ImageView(c);
            icon.setImageResource(e.icon);
            icon.setImageTintList(android.content.res.ColorStateList.valueOf(text));
            icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            LinearLayout.LayoutParams ip = new LinearLayout.LayoutParams(dp(16), dp(16));
            ip.setMarginEnd(dp(8));
            content.addView(icon, ip);
        }
        TextView label = new TextView(c);
        label.setText(e.text);
        label.setTextSize(14);
        label.setTextColor(text);
        label.setMaxLines(1);
        label.setEllipsize(TextUtils.TruncateAt.END);
        content.addView(label);
        item.addView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        Runnable on = () -> {
            highlight[0] = fade(hl, 41, 120, CUBIC_OUT, highlight[0]);
            content.animate().cancel();
            content.animate().translationX(dp(3)).setDuration(140).setInterpolator(CUBIC_OUT).start();
            pill.animate().cancel();
            pill.animate().alpha(1f).scaleY(1f).setDuration(180).setInterpolator(CUBIC_OUT).start();
        };
        Runnable off = () -> {
            highlight[0] = fade(hl, 0, 140, LINEAR, highlight[0]);
            content.animate().cancel();
            content.animate().translationX(0f).setDuration(140).setInterpolator(LINEAR).start();
            pill.animate().cancel();
            pill.animate().alpha(0f).scaleY(5f / 16f).setDuration(100).setInterpolator(LINEAR).start();
        };
        item.setOnHoverListener((v, ev) -> {
            if (ev.getAction() == MotionEvent.ACTION_HOVER_ENTER) on.run();
            else if (ev.getAction() == MotionEvent.ACTION_HOVER_EXIT) off.run();
            return false;
        });
        press[0] = on;
        press[1] = off;
        item.setOnClickListener(v -> {
            v.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
            click.run();
        });
        item.setContentDescription(e.text);
        return item;
    }
}
