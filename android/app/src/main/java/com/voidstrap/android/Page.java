package com.voidstrap.android;

import android.content.Context;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.fragment.app.Fragment;

public abstract class Page extends Fragment {
    protected Store store;
    private final Runnable listener = this::refreshIfShown;

    protected Page(int layout) {
        super(layout);
    }

    @Override
    public void onAttach(@NonNull Context context) {
        super.onAttach(context);
        store = Store.get(context);
    }

    @Override
    public void onStart() {
        super.onStart();
        store.observe(listener);
        refreshIfShown();
    }

    @Override
    public void onStop() {
        store.unobserve(listener);
        super.onStop();
    }

    @Override
    public void onHiddenChanged(boolean hidden) {
        super.onHiddenChanged(hidden);
        if (!hidden) refreshIfShown();
    }

    private void refreshIfShown() {
        if (getView() != null && !isHidden()) refresh();
    }

    protected AppCompatActivity host() {
        return (AppCompatActivity) requireActivity();
    }

    protected abstract void refresh();
}
