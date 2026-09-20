package com.voidstrap.android;

import android.content.Context;
import android.util.AttributeSet;
import android.view.MotionEvent;

import androidx.core.widget.NestedScrollView;

public class ScrollPane extends NestedScrollView {
    private final Scrollbar bar;

    public ScrollPane(Context context) {
        this(context, null);
    }

    public ScrollPane(Context context, AttributeSet attrs) {
        super(context, attrs);
        bar = Scrollbar.attach(this, offset -> scrollTo(getScrollX(), offset));
    }

    public ScrollPane(Context context, AttributeSet attrs, int defStyleAttr) {
        super(context, attrs, defStyleAttr);
        bar = Scrollbar.attach(this, offset -> scrollTo(getScrollX(), offset));
    }

    @Override
    protected void onScrollChanged(int l, int t, int oldl, int oldt) {
        super.onScrollChanged(l, t, oldl, oldt);
        if (bar != null) bar.onScrolled();
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent ev) {
        return bar.handle(ev) || super.dispatchTouchEvent(ev);
    }
}
