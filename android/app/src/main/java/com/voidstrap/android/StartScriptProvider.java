package com.voidstrap.android;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.ParcelFileDescriptor;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

public final class StartScriptProvider extends ContentProvider {
    static final String PATH = "start.sh";

    static String authority(String pkg) {
        return pkg + ".start";
    }

    static String script(String pkg, int uid) {
        return HelperServer.launchLine(pkg, uid) + "sleep 1\necho 'Voidstrap helper start requested'\n";
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Nullable
    @Override
    public ParcelFileDescriptor openFile(@NonNull Uri uri, @NonNull String mode) throws FileNotFoundException {
        int caller = Binder.getCallingUid();
        if (caller != HelperServer.SHELL_UID && caller != 0) throw new SecurityException("Only adb can read the start script");
        if (!"r".equals(mode) || !PATH.equals(uri.getLastPathSegment())) throw new FileNotFoundException(uri.toString());
        byte[] body = script(getContext().getPackageName(), android.os.Process.myUid()).getBytes(StandardCharsets.UTF_8);
        try {
            ParcelFileDescriptor[] pipe = ParcelFileDescriptor.createPipe();
            try (OutputStream out = new ParcelFileDescriptor.AutoCloseOutputStream(pipe[1])) {
                out.write(body);
            }
            return pipe[0];
        } catch (IOException e) {
            throw new FileNotFoundException(e.getMessage());
        }
    }

    @Nullable
    @Override
    public Cursor query(@NonNull Uri uri, @Nullable String[] projection, @Nullable String selection, @Nullable String[] selectionArgs, @Nullable String sortOrder) {
        return null;
    }

    @Nullable
    @Override
    public String getType(@NonNull Uri uri) {
        return "text/plain";
    }

    @Nullable
    @Override
    public Uri insert(@NonNull Uri uri, @Nullable ContentValues values) {
        return null;
    }

    @Override
    public int delete(@NonNull Uri uri, @Nullable String selection, @Nullable String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(@NonNull Uri uri, @Nullable ContentValues values, @Nullable String selection, @Nullable String[] selectionArgs) {
        return 0;
    }
}
