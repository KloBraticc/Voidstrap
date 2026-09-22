package com.voidstrap.android;

import android.animation.TimeInterpolator;
import android.animation.ValueAnimator;
import android.content.Context;
import android.content.res.Configuration;
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

final class Dropdown {
    private static final TimeInterpolator QUARTIC_OUT = t -> 1f - (float) Math.pow(1f - t, 4);

    final LinearLayout view;
    private final Context c;
    private final TextView label;
    private final ImageView chevron;
    private final GradientDrawable fill;
    private final boolean dark;
    private final int[] fillColor = {0};
    private final CharSequence[] labels;
    private int selected;
    private final SettingRows.IntChoice change;
    private PopupWindow window;
    private ValueAnimator fillAnimator;
    private boolean hovered;

    Dropdown(Context c, CharSequence[] labels, int selected, SettingRows.IntChoice change) {
        this.c = c;
        this.labels = labels;
        this.selected = selected;
        this.change = change;
        dark = (c.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES;

        view = new LinearLayout(c) {
            @Override
            public void setPressed(boolean pressed) {
                boolean changed = pressed != isPressed();
                super.setPressed(pressed);
                if (changed) boxState();
            }
        };
        view.setOrientation(LinearLayout.HORIZONTAL);
        view.setGravity(Gravity.CENTER_VERTICAL);
        view.setMinimumHeight(dp(40));
        view.setMinimumWidth(dp(168));
        view.setPadding(dp(12), 0, dp(8), 0);
        view.setClickable(true);
        view.setFocusable(true);
        fill = new GradientDrawable();
        fill.setCornerRadius(dp(4));
        fill.setStroke(Math.max(1, dp(1)), dark ? 0x18FFFFFF : 0x1F000000);
        fillColor[0] = controlFill();
        fill.setColor(fillColor[0]);
        view.setBackground(fill);

        label = new TextView(c);
        label.setTextSize(14);
        label.setTextColor(Ui.attr(c, R.attr.vsTextPrimary));
        label.setMaxLines(1);
        label.setEllipsize(TextUtils.TruncateAt.END);
        view.addView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        chevron = new ImageView(c);
        chevron.setImageResource(R.drawable.ic_chevron_down);
        chevron.setImageTintList(android.content.res.ColorStateList.valueOf(Ui.attr(c, R.attr.vsTextSecondary)));
        chevron.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(dp(16), dp(16));
        cp.setMarginStart(dp(10));
        view.addView(chevron, cp);

        view.setOnHoverListener((v, ev) -> {
            if (ev.getAction() == MotionEvent.ACTION_HOVER_ENTER) hovered = true;
            else if (ev.getAction() == MotionEvent.ACTION_HOVER_EXIT) hovered = false;
            else return false;
            boxState();
            return false;
        });
        view.setOnClickListener(v -> open());
        label.setText(labels[selected]);
    }

    private int dp(float v) {
        return Ui.dp(c, v);
    }

    private int controlFill() {
        return dark ? 0x0FFFFFFF : 0xB3FFFFFF;
    }

    private void boxState() {
        boolean pressed = view.isPressed();
        int to = pressed ? (dark ? 0x08FFFFFF : 0x4DF9F9F9) : hovered || window != null ? (dark ? 0x15FFFFFF : 0x80F9F9F9) : controlFill();
        fillAnimator = tint(fill, fillColor, to, 187, QUARTIC_OUT, fillAnimator);
        float a = pressed ? 0.7f : 1f;
        label.animate().alpha(a).setDuration(150).setInterpolator(QUARTIC_OUT).start();
        chevron.animate().alpha(a).setDuration(150).setInterpolator(QUARTIC_OUT).start();
    }

    private static ValueAnimator tint(GradientDrawable d, int[] current, int to, long ms, TimeInterpolator ease, ValueAnimator running) {
        if (running != null) running.cancel();
        if (current[0] == to) return null;
        ValueAnimator anim = ValueAnimator.ofArgb(current[0], to);
        anim.setDuration(ms);
        anim.setInterpolator(ease);
        anim.addUpdateListener(u -> {
            current[0] = (int) u.getAnimatedValue();
            d.setColor(current[0]);
        });
        anim.start();
        return anim;
    }

    void open() {
        if (window != null) return;
        int shadow = dp(16);
        int gap = dp(4);
        int edge = dp(8);
        FrameLayout root = new FrameLayout(c);
        root.setClipChildren(false);
        root.setClipToPadding(false);
        root.setPadding(shadow, shadow, shadow, shadow);

        FrameLayout surface = new FrameLayout(c);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Ui.attr(c, R.attr.vsMenu));
        bg.setCornerRadius(dp(8));
        bg.setStroke(Math.max(1, dp(1)), dark ? 0x12FFFFFF : 0x0F000000);
        surface.setBackground(bg);
        surface.setElevation(dp(8));
        surface.setPadding(0, dp(2), 0, dp(5));
        surface.setClipToOutline(true);

        ScrollView scroll = new ScrollView(c);
        scroll.setVerticalScrollBarEnabled(false);
        scroll.setOverScrollMode(View.OVER_SCROLL_NEVER);
        LinearLayout list = new LinearLayout(c);
        list.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(list);
        surface.addView(scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        root.addView(surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        PopupWindow w = new PopupWindow(root, ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, true);
        AppFont.watch(root);
        window = w;
        w.setBackgroundDrawable(new ColorDrawable(0));
        w.setOutsideTouchable(true);
        w.setAnimationStyle(0);
        w.setClippingEnabled(false);

        Item[] items = new Item[labels.length];
        boolean[] closing = {false};
        Runnable[] close = new Runnable[1];
        for (int i = 0; i < labels.length; i++) {
            int index = i;
            items[i] = new Item(labels[i], i == selected);
            items[i].view.setOnClickListener(v -> {
                if (closing[0]) return;
                v.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
                if (index != selected) {
                    items[selected].select(false);
                    items[index].select(true);
                    selected = index;
                    label.setText(labels[index]);
                    change.on(index);
                }
                v.postDelayed(close[0], 55);
            });
            list.addView(items[i].view);
        }

        Rect frame = new Rect();
        view.getWindowVisibleDisplayFrame(frame);
        int[] at = new int[2];
        view.getLocationOnScreen(at);
        root.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
        int maxWidth = frame.width() - 2 * edge;
        FrameLayout.LayoutParams surfaceParams = (FrameLayout.LayoutParams) surface.getLayoutParams();
        surfaceParams.width = Math.min(maxWidth, Math.max(view.getWidth(), surface.getMeasuredWidth()));
        surface.setLayoutParams(surfaceParams);
        root.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
        int contentH = surface.getMeasuredHeight();
        int below = frame.bottom - (at[1] + view.getHeight() + gap) - edge;
        int above = at[1] - gap - frame.top - edge;
        boolean up = contentH > below && above > below;
        int limit = Math.min(frame.height() - 2 * edge, Math.min(dp(300), Math.max(dp(56), up ? above : below)));
        if (contentH > limit) {
            scroll.getLayoutParams().height = Math.max(dp(48), limit - surface.getPaddingTop() - surface.getPaddingBottom());
            root.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
        }
        int surfaceWidth = surface.getMeasuredWidth();
        int surfaceHeight = surface.getMeasuredHeight();
        int surfaceLeft = at[0];
        if (surfaceLeft + surfaceWidth > frame.right - edge) surfaceLeft = at[0] + view.getWidth() - surfaceWidth;
        surfaceLeft = Math.max(frame.left + edge, Math.min(surfaceLeft, frame.right - edge - surfaceWidth));
        int surfaceTop = up ? at[1] - gap - surfaceHeight : at[1] + view.getHeight() + gap;
        surfaceTop = Math.max(frame.top + edge, Math.min(surfaceTop, frame.bottom - edge - surfaceHeight));
        int popupLeft = surfaceLeft - shadow;
        int popupTop = surfaceTop - shadow;
        Rect anchor = new Rect(at[0], at[1], at[0] + view.getWidth(), at[1] + view.getHeight());

        int placedLeft = surfaceLeft;
        int placedTop = surfaceTop;
        close[0] = () -> {
            if (closing[0]) return;
            closing[0] = true;
            Flyout.animateOut(surface, anchor, placedLeft, placedTop, surfaceWidth, surfaceHeight, () -> {
                if (w.isShowing()) w.dismiss();
            });
        };

        w.setTouchInterceptor((v, ev) -> {
            if (ev.getAction() == MotionEvent.ACTION_OUTSIDE) {
                close[0].run();
                v.performClick();
                return true;
            }
            return false;
        });
        w.setOnDismissListener(() -> {
            window = null;
            chevron.animate().rotation(0f).setDuration(375).setInterpolator(QUARTIC_OUT).start();
            boxState();
        });
        if (view.getWindowToken() == null) return;
        try {
            w.showAtLocation(view, Gravity.NO_GRAVITY, popupLeft, popupTop);
        } catch (RuntimeException e) {
            Crash.report("dropdown", e);
            window = null;
            return;
        }

        if (contentH > limit) {
            View sel = items[selected].view;
            scroll.post(() -> scroll.scrollTo(0, Math.max(0, sel.getTop() - (scroll.getHeight() - sel.getHeight()) / 2)));
        }
        chevron.animate().rotation(180f).setDuration(375).setInterpolator(QUARTIC_OUT).start();
        boxState();
        Flyout.animateIn(surface, anchor, surfaceLeft, surfaceTop, surfaceWidth, surfaceHeight);
    }

    private final class Item {
        final FrameLayout view;
        final View pill;
        final GradientDrawable bg = new GradientDrawable();
        final int[] bgColor = {0};
        ValueAnimator bgAnimator;
        boolean isSelected;
        boolean hover;

        Item(CharSequence text, boolean on) {
            isSelected = on;
            view = new FrameLayout(c) {
                @Override
                public void setPressed(boolean pressed) {
                    boolean changed = pressed != isPressed();
                    super.setPressed(pressed);
                    if (changed) state(pressed ? 83 : 187);
                }
            };
            view.setClickable(true);
            view.setFocusable(true);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.setMargins(dp(5), dp(3), dp(5), 0);
            view.setLayoutParams(lp);
            bg.setCornerRadius(dp(4));
            bgColor[0] = target();
            bg.setColor(bgColor[0]);
            view.setBackground(bg);

            pill = new View(c);
            GradientDrawable pd = new GradientDrawable();
            pd.setColor(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary));
            pd.setCornerRadius(dp(1.5f));
            pill.setBackground(pd);
            FrameLayout.LayoutParams pp = new FrameLayout.LayoutParams(dp(3), dp(16), Gravity.START | Gravity.CENTER_VERTICAL);
            pp.setMarginStart(dp(3));
            pill.setPivotY(dp(8));
            pill.setScaleY(on ? 1f : 5f / 16f);
            pill.setAlpha(on ? 1f : 0f);
            view.addView(pill, pp);

            TextView t = new TextView(c);
            t.setText(text);
            t.setTextSize(15);
            t.setTextColor(Ui.attr(c, R.attr.vsTextPrimary));
            t.setMaxLines(1);
            t.setEllipsize(TextUtils.TruncateAt.END);
            t.setPadding(dp(12), dp(13), dp(16), dp(13));
            view.addView(t, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

            view.setOnHoverListener((v, ev) -> {
                if (ev.getAction() == MotionEvent.ACTION_HOVER_ENTER) hover = true;
                else if (ev.getAction() == MotionEvent.ACTION_HOVER_EXIT) hover = false;
                else return false;
                state(187);
                return false;
            });
            view.setContentDescription(text);
            view.setSelected(on);
        }

        private int target() {
            if (view != null && view.isPressed()) return dark ? 0x15FFFFFF : 0x0F000000;
            return isSelected || hover ? (dark ? 0x0FFFFFFF : 0x09000000) : 0;
        }

        void state(long ms) {
            bgAnimator = tint(bg, bgColor, target(), ms, ms < 100 ? QUARTIC_OUT : Flyout.QUARTIC_IN_OUT, bgAnimator);
        }

        void select(boolean on) {
            isSelected = on;
            view.setSelected(on);
            state(on ? 187 : 100);
            pill.animate().cancel();
            if (on) {
                pill.animate().alpha(1f).setDuration(120).setInterpolator(Flyout.LINEAR).start();
                pill.animate().scaleY(1f).setDuration(220).setInterpolator(Flyout.CUBIC_OUT).start();
            } else {
                pill.animate().alpha(0f).scaleY(5f / 16f).setDuration(100).setInterpolator(Flyout.LINEAR).start();
            }
        }
    }
}
