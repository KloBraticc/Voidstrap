package com.voidstrap.android;

import android.content.Context;

import java.io.IOException;

public final class ContentPlacer {
    private ContentPlacer() {
    }

    public static String resolve(Context c, String relative) {
        try {
            return Core.text("mods.resolve", Core.indexed(c, "rel", relative));
        } catch (IOException e) {
            return null;
        }
    }

    public static boolean clientCopy(Context c, String relative) {
        return Core.flag("mods.clientCopy", Core.indexed(c, "rel", relative));
    }
}
