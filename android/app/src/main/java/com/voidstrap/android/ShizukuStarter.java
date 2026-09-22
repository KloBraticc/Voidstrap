package com.voidstrap.android;

import android.os.Binder;
import android.os.IBinder;
import android.os.Parcel;

public final class ShizukuStarter extends Binder {
    static final String DESCRIPTOR = "com.voidstrap.android.ShizukuStarter";
    static final int START = IBinder.FIRST_CALL_TRANSACTION;
    static final int DESTROY = 16777115;

    public ShizukuStarter() {
        attachInterface(null, DESCRIPTOR);
    }

    @Override
    protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) {
        if (code == START) {
            data.enforceInterface(DESCRIPTOR);
            String pkg = data.readString();
            String uid = data.readString();
            String token = data.readString();
            int result = HelperServer.start(pkg, uid, token);
            if (reply != null) {
                reply.writeNoException();
                reply.writeInt(result);
            }
            return true;
        }
        if (code == DESTROY) {
            System.exit(0);
            return true;
        }
        try {
            return super.onTransact(code, data, reply, flags);
        } catch (android.os.RemoteException e) {
            return false;
        }
    }
}
