package com.voidstrap.android;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.RectF;
import android.graphics.drawable.Drawable;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.animation.DecelerateInterpolator;
import android.view.animation.Interpolator;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.view.ScrollingView;

final class Scrollbar extends Drawable {
    private static final float LANE = 14;
    private static final float INSET = 14;
    private static final float REST = 4;
    private static final float SCROLL = 6;
    private static final float EXPANDED = 8;
    private static final float PANEL = 12;
    private static final float MIN_THUMB = 24;
    private static final float TOUCH = 28;
    private static final long SETTLE_MS = 600;
    private static final long QUIET_MS = 300;
    private static final long GROW_MS = 150;
    private static final long SHRINK_MS = 280;
    private static final Interpolator EASE = new DecelerateInterpolator(1.5f);

    interface Host {
        void scrollToOffset(int offset);
    }

    private final View view;
    private final ScrollingView scroller;
    private final Host host;
    private final float density;
    private final Paint thumbPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint highlightPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint panelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint strokePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF rect = new RectF();
    private final long attachedAt = SystemClock.uptimeMillis();
    private final int touchSlop;
    private final Runnable settle = this::settle;
    private final int thumbBase;
    private final int highlightBase;
    private final int panelBase;
    private final int strokeBase;

    private boolean scrolling;
    private boolean dragging;
    private boolean armed;
    private float armY;
    private float grabOffset;
    private float size = REST;
    private float thumbAlpha;
    private float panelAlpha;
    private float highlightAlpha;
    private ValueAnimator animator;

    private Scrollbar(View view, Host host) {
        this.view = view;
        this.scroller = (ScrollingView) view;
        this.host = host;
        Context c = view.getContext();
        density = c.getResources().getDisplayMetrics().density;
        touchSlop = ViewConfiguration.get(c).getScaledTouchSlop();
        thumbPaint.setColor(c.getColor(R.color.vs_scroll_thumb));
        highlightPaint.setColor(c.getColor(R.color.vs_scroll_highlight));
        panelPaint.setColor(c.getColor(R.color.vs_scroll_panel));
        strokePaint.setColor(c.getColor(R.color.vs_scroll_stroke));
        strokePaint.setStyle(Paint.Style.STROKE);
        strokePaint.setStrokeWidth(density);
        thumbBase = thumbPaint.getAlpha();
        highlightBase = highlightPaint.getAlpha();
        panelBase = panelPaint.getAlpha();
        strokeBase = strokePaint.getAlpha();
        view.setVerticalScrollBarEnabled(false);
        view.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> laneBounds(r - l, b - t));
        view.getOverlay().add(this);
    }

    static Scrollbar attach(View view, Host host) {
        return new Scrollbar(view, host);
    }

    private void laneBounds(int width, int height) {
        float half = dp(Math.max(LANE, PANEL)) / 2 + density;
        boolean rtl = view.getLayoutDirection() == View.LAYOUT_DIRECTION_RTL;
        float cx = rtl ? dp(LANE) / 2 : width - dp(LANE) / 2;
        setBounds((int) Math.max(0, Math.floor(cx - half)), 0, (int) Math.min(width, Math.ceil(cx + half)), height);
    }

    void onScrolled() {
        if (dragging || SystemClock.uptimeMillis() - attachedAt < QUIET_MS || !scrollable()) {
            invalidateSelf();
            return;
        }
        view.removeCallbacks(settle);
        view.postDelayed(settle, SETTLE_MS);
        if (!scrolling) {
            scrolling = true;
            update();
        } else {
            invalidateSelf();
        }
    }

    private void settle() {
        scrolling = false;
        update();
    }

    private boolean scrollable() {
        return scroller.computeVerticalScrollRange() > scroller.computeVerticalScrollExtent();
    }

    private float dp(float v) {
        return v * density;
    }

    private float trackTop() {
        return dp(INSET);
    }

    private float trackLength() {
        return Math.max(0, view.getHeight() - dp(INSET) * 2);
    }

    private float thumbLength() {
        int range = scroller.computeVerticalScrollRange();
        int extent = scroller.computeVerticalScrollExtent();
        float track = trackLength();
        if (range <= 0) return track;
        return Math.min(track, Math.max(dp(MIN_THUMB), track * extent / (float) range));
    }

    private float thumbTop() {
        int range = scroller.computeVerticalScrollRange();
        int extent = scroller.computeVerticalScrollExtent();
        int offset = scroller.computeVerticalScrollOffset();
        int max = range - extent;
        float travel = trackLength() - thumbLength();
        float fraction = max <= 0 ? 0 : Math.max(0, Math.min(1, offset / (float) max));
        return trackTop() + travel * fraction;
    }

    private float centerX() {
        boolean rtl = view.getLayoutDirection() == View.LAYOUT_DIRECTION_RTL;
        return rtl ? dp(LANE) / 2 : view.getWidth() - dp(LANE) / 2;
    }

    private void update() {
        boolean active = dragging || scrolling;
        float targetSize = dragging ? EXPANDED : scrolling ? SCROLL : REST;
        float targetThumb = active ? 1f : 0f;
        float targetPanel = dragging ? 1f : 0f;
        float targetHighlight = dragging ? 1f : 0f;
        if (animator != null) animator.cancel();
        float fromSize = size;
        float fromThumb = thumbAlpha;
        float fromPanel = panelAlpha;
        float fromHighlight = highlightAlpha;
        animator = ValueAnimator.ofFloat(0f, 1f);
        animator.setDuration(targetSize > fromSize || targetThumb > fromThumb ? GROW_MS : SHRINK_MS);
        animator.setInterpolator(EASE);
        animator.addUpdateListener(a -> {
            float t = (float) a.getAnimatedValue();
            size = fromSize + (targetSize - fromSize) * t;
            thumbAlpha = fromThumb + (targetThumb - fromThumb) * t;
            panelAlpha = fromPanel + (targetPanel - fromPanel) * t;
            highlightAlpha = fromHighlight + (targetHighlight - fromHighlight) * t;
            invalidateSelf();
        });
        animator.start();
    }

    boolean handle(MotionEvent e) {
        if (e.getActionMasked() == MotionEvent.ACTION_DOWN) return grab(e);
        return touch(e);
    }

    private boolean grab(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                armed = false;
                if (thumbAlpha < 0.5f || !scrollable()) return false;
                float x = e.getX();
                float y = e.getY();
                float top = thumbTop();
                float half = dp(TOUCH) / 2;
                if (Math.abs(x - centerX()) > half || y < top - half || y > top + thumbLength() + half) return false;
                armed = true;
                armY = y;
                grabOffset = y - top;
                return startDrag();
            default:
                return dragging;
        }
    }

    private boolean startDrag() {
        dragging = true;
        view.removeCallbacks(settle);
        if (view.getParent() != null) view.getParent().requestDisallowInterceptTouchEvent(true);
        update();
        return true;
    }

    private boolean touch(MotionEvent e) {
        if (!dragging) return false;
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_MOVE:
                if (armed && Math.abs(e.getY() - armY) < touchSlop) return true;
                armed = false;
                float travel = trackLength() - thumbLength();
                if (travel <= 0) return true;
                float fraction = Math.max(0, Math.min(1, (e.getY() - grabOffset - trackTop()) / travel));
                int max = scroller.computeVerticalScrollRange() - scroller.computeVerticalScrollExtent();
                host.scrollToOffset(Math.round(fraction * max));
                invalidateSelf();
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                armed = false;
                scrolling = true;
                view.removeCallbacks(settle);
                view.postDelayed(settle, SETTLE_MS);
                update();
                return true;
            default:
                return true;
        }
    }

    @Override
    public void draw(@NonNull Canvas canvas) {
        if ((thumbAlpha <= 0.001f && panelAlpha <= 0.001f) || !scrollable()) return;
        float dy = view.getScrollY();
        float dx = view.getScrollX();
        float cx = centerX() + dx;
        if (panelAlpha > 0.001f) {
            float w = dp(PANEL);
            rect.set(cx - w / 2, dy, cx + w / 2, dy + view.getHeight());
            panelPaint.setAlpha(Math.round(panelBase * panelAlpha));
            strokePaint.setAlpha(Math.round(strokeBase * panelAlpha));
            canvas.drawRoundRect(rect, dp(6), dp(6), panelPaint);
            rect.inset(density / 2, density / 2);
            canvas.drawRoundRect(rect, dp(6), dp(6), strokePaint);
        }
        float w = dp(size);
        float top = thumbTop() + dy;
        rect.set(cx - w / 2, top, cx + w / 2, top + thumbLength());
        float radius = Math.min(dp(4), w / 2);
        thumbPaint.setAlpha(Math.round(thumbBase * thumbAlpha));
        canvas.drawRoundRect(rect, radius, radius, thumbPaint);
        if (highlightAlpha > 0.001f) {
            highlightPaint.setAlpha(Math.round(highlightBase * highlightAlpha * thumbAlpha));
            canvas.drawRoundRect(rect, radius, radius, highlightPaint);
        }
    }

    @Override
    public void setAlpha(int alpha) {
    }

    @Override
    public void setColorFilter(@Nullable ColorFilter colorFilter) {
    }

    @Override
    public int getOpacity() {
        return PixelFormat.TRANSLUCENT;
    }
}
