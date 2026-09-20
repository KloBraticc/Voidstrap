package com.voidstrap.android;

import android.view.View;
import android.view.ViewGroup;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.RecyclerView;

final class HeaderAdapter extends RecyclerView.Adapter<RecyclerView.ViewHolder> {
    private final View view;

    HeaderAdapter(View view) {
        this.view = view;
    }

    @NonNull
    @Override
    public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
        if (view.getParent() instanceof ViewGroup) ((ViewGroup) view.getParent()).removeView(view);
        RecyclerView.ViewHolder h = new RecyclerView.ViewHolder(view) {
        };
        h.setIsRecyclable(false);
        return h;
    }

    @Override
    public void onBindViewHolder(@NonNull RecyclerView.ViewHolder holder, int position) {
    }

    @Override
    public int getItemCount() {
        return 1;
    }
}
