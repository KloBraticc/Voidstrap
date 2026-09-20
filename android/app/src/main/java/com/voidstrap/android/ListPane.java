package com.voidstrap.android;

import android.content.Context;
import android.util.AttributeSet;
import android.view.MotionEvent;

import androidx.recyclerview.widget.RecyclerView;

public class ListPane extends RecyclerView {
    private final Scrollbar bar;

    public ListPane(Context context) {
        this(context, null);
    }

    public ListPane(Context context, AttributeSet attrs) {
        super(context, attrs);
        bar = Scrollbar.attach(this, offset -> scrollBy(0, offset - computeVerticalScrollOffset()));
    }

    public ListPane(Context context, AttributeSet attrs, int defStyleAttr) {
        super(context, attrs, defStyleAttr);
        bar = Scrollbar.attach(this, offset -> scrollBy(0, offset - computeVerticalScrollOffset()));
    }

    @Override
    public void onScrolled(int dx, int dy) {
        super.onScrolled(dx, dy);
        if (bar != null && dy != 0) bar.onScrolled();
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent ev) {
        return bar.handle(ev) || super.dispatchTouchEvent(ev);
    }
}
