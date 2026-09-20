package com.voidstrap.android;

import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.RadioButton;
import android.widget.TextView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.imageview.ShapeableImageView;

public final class Row {
    public final View view;
    public final ImageView icon;
    public final ShapeableImageView image;
    public final TextView title;
    public final TextView detail;
    public final TextView badge;
    public final MaterialButton more;
    public final View chevron;
    public final RadioButton radio;

    public Row(View v) {
        view = v;
        icon = v.findViewById(R.id.row_icon);
        image = v.findViewById(R.id.row_image);
        title = v.findViewById(R.id.row_title);
        detail = v.findViewById(R.id.row_detail);
        badge = v.findViewById(R.id.row_badge);
        more = v.findViewById(R.id.row_more);
        chevron = v.findViewById(R.id.row_chevron);
        radio = v.findViewById(R.id.row_radio);
    }

    public static Row inflate(ViewGroup parent) {
        return new Row(LayoutInflater.from(parent.getContext()).inflate(R.layout.item_row, parent, false));
    }

    public Row set(int iconRes, CharSequence t, CharSequence d) {
        icon.setVisibility(View.VISIBLE);
        icon.setImageResource(iconRes);
        if (image != null) image.setVisibility(View.GONE);
        title.setText(t);
        detail.setText(d);
        detail.setVisibility(d == null || d.length() == 0 ? View.GONE : View.VISIBLE);
        return this;
    }

    public Row showImage(String url, int fallback) {
        image.setVisibility(View.VISIBLE);
        icon.setVisibility(View.GONE);
        Net.image(image, url, fallback, Ui.dp(view.getContext(), 40));
        return this;
    }
}
