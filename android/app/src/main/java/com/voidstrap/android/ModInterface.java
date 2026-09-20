package com.voidstrap.android;

import android.content.Context;

import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

final class ModInterface {
    private ModInterface() {
    }

    static boolean ps4(Context c) {
        return false;
    }

    static boolean hideCoreGui(Context c) {
        return false;
    }

    static void setPs4(Context c, boolean on, AtomicBoolean cancel) throws IOException {
        throw new IOException("Not ready");
    }

    static void setHideCoreGui(Context c, boolean on, AtomicBoolean cancel) throws IOException {
        throw new IOException("Not ready");
    }
}
