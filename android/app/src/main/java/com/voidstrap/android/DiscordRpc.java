package com.voidstrap.android;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.os.Binder;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Parcel;
import android.os.RemoteException;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.UUID;

final class DiscordRpc {
    static final String PACKAGE = "com.discord";
    static final String VOIDSTRAP_LOGO = "https://raw.githubusercontent.com/KloBraticc/Voidstrap/main/src/Voidstrap.App/Voidstrap.png";
    static final DiscordRpc ROBLOX = new DiscordRpc(1005469189907173486L, "Roblox");
    static final DiscordRpc VOIDSTRAP = new DiscordRpc(1459679943498661910L, "Voidstrap Android");

    private static final String SERVICE = "com.discord.socialsdk.rpc.IDiscordRpcService";
    private static final String CONNECTION = "com.discord.socialsdk.rpc.IDiscordRpcConnection";
    private static final String CALLBACK = "com.discord.socialsdk.rpc.IDiscordRpcCallback";
    private static final String VERSION = "1.10";
    private static final int CONNECT = IBinder.FIRST_CALL_TRANSACTION;
    private static final int SEND_FRAME = IBinder.FIRST_CALL_TRANSACTION;
    private static final int DISCONNECT = IBinder.FIRST_CALL_TRANSACTION + 1;
    private static final int ON_FRAME = IBinder.FIRST_CALL_TRANSACTION;
    private static final int ON_CLOSE = IBinder.FIRST_CALL_TRANSACTION + 1;

    private final long applicationId;
    private final String name;
    private android.app.Application app;
    private HandlerThread thread;
    private Handler handler;
    private IBinder connection;
    private boolean bound;
    private JSONObject last;
    private boolean hasLast;
    private volatile String status = "";

    private DiscordRpc(long applicationId, String name) {
        this.applicationId = applicationId;
        this.name = name;
    }

    static boolean installed(Context c) {
        try {
            c.getPackageManager().getPackageInfo(PACKAGE, 0);
            return true;
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }

    String status() {
        return status;
    }

    private void changed() {
        if (app != null) Store.get(app).changed();
    }

    private void post(Runnable r) {
        Handler h = handler;
        if (h != null) h.post(r);
    }

    private final Binder callback = new Binder() {
        {
            attachInterface(null, CALLBACK);
        }

        @Override
        protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
            if (code == ON_FRAME) {
                data.enforceInterface(CALLBACK);
                String frame = data.readString();
                if (reply != null) reply.writeNoException();
                onFrame(frame);
                return true;
            }
            if (code == ON_CLOSE) {
                data.enforceInterface(CALLBACK);
                int reason = data.readInt();
                String message = data.readString();
                if (reply != null) reply.writeNoException();
                status = message == null || message.isEmpty() ? "Closed (" + reason + ")" : message;
                post(() -> connection = null);
                changed();
                return true;
            }
            return super.onTransact(code, data, reply, flags);
        }
    };

    private final ServiceConnection binding = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName component, IBinder service) {
            post(() -> connect(service));
        }

        @Override
        public void onServiceDisconnected(ComponentName component) {
            post(() -> connection = null);
            status = "Discord closed the connection";
            changed();
        }
    };

    synchronized void start(Context c) {
        if (thread != null) return;
        app = (android.app.Application) c.getApplicationContext();
        status = "";
        thread = new HandlerThread("discord");
        thread.start();
        handler = new Handler(thread.getLooper());
        try {
            bound = app.bindService(new Intent(SERVICE).setPackage(PACKAGE), binding, Context.BIND_AUTO_CREATE);
        } catch (SecurityException e) {
            bound = false;
        }
        if (!bound) status = installed(app) ? "This Discord version cannot show activity from other apps. Update Discord." : "Discord is not installed";
    }

    synchronized void update(Integrations.Presence p) {
        if (handler == null) return;
        JSONObject activity = activity(p);
        handler.post(() -> send(activity));
    }

    synchronized void clear() {
        if (handler != null) handler.post(() -> send(null));
    }

    synchronized void stop() {
        if (thread == null) return;
        Handler h = handler;
        HandlerThread t = thread;
        h.post(() -> {
            send(null);
            IBinder conn = connection;
            connection = null;
            if (conn != null) {
                Parcel data = Parcel.obtain();
                Parcel reply = Parcel.obtain();
                try {
                    data.writeInterfaceToken(CONNECTION);
                    conn.transact(DISCONNECT, data, reply, 0);
                    reply.readException();
                } catch (RemoteException | RuntimeException ignored) {
                } finally {
                    data.recycle();
                    reply.recycle();
                }
            }
            if (bound) {
                try {
                    app.unbindService(binding);
                } catch (IllegalArgumentException ignored) {
                }
                bound = false;
            }
            hasLast = false;
            last = null;
            t.quitSafely();
        });
        thread = null;
        handler = null;
    }

    private void connect(IBinder service) {
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(SERVICE);
            data.writeLong(applicationId);
            data.writeString(VERSION);
            data.writeStrongBinder(callback);
            service.transact(CONNECT, data, reply, 0);
            reply.readException();
            connection = reply.readStrongBinder();
            if (connection == null) {
                status = "Discord refused the connection. Sign in to Discord and try again.";
                changed();
                return;
            }
            if (hasLast) send(last);
        } catch (RemoteException | RuntimeException e) {
            status = "Could not reach Discord: " + e.getMessage();
            changed();
        } finally {
            data.recycle();
            reply.recycle();
        }
    }

    private void send(JSONObject activity) {
        last = activity;
        hasLast = true;
        IBinder conn = connection;
        if (conn == null) return;
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            JSONObject args = new JSONObject();
            args.put("pid", android.os.Process.myPid());
            if (activity != null) args.put("activity", activity);
            JSONObject frame = new JSONObject()
                    .put("cmd", "SET_ACTIVITY")
                    .put("args", args)
                    .put("nonce", UUID.randomUUID().toString());
            data.writeInterfaceToken(CONNECTION);
            data.writeString(frame.toString());
            conn.transact(SEND_FRAME, data, reply, 0);
            reply.readException();
        } catch (JSONException ignored) {
        } catch (RemoteException | RuntimeException e) {
            connection = null;
            status = "Lost the connection to Discord";
            changed();
        } finally {
            data.recycle();
            reply.recycle();
        }
    }

    private void onFrame(String frame) {
        if (frame == null) return;
        try {
            JSONObject o = new JSONObject(frame);
            String evt = o.optString("evt", "");
            if (evt.equals("ERROR")) {
                JSONObject d = o.optJSONObject("data");
                status = d == null ? "Discord returned an error" : d.optString("message", "Discord returned an error");
            } else if (evt.equals("READY") || o.optString("cmd").equals("SET_ACTIVITY")) {
                status = "ok";
            }
        } catch (JSONException ignored) {
            return;
        }
        changed();
    }

    JSONObject activity(Integrations.Presence p) {
        try {
            JSONObject a = new JSONObject();
            a.put("name", name);
            a.put("type", 0);
            if (!p.details.isEmpty()) a.put("details", clip(p.details));
            if (!p.state.isEmpty()) a.put("state", clip(p.state));
            a.put("status_display_type", p.detailsStatus ? 2 : 0);
            if (p.start > 0) a.put("timestamps", new JSONObject().put("start", p.start));
            JSONObject assets = new JSONObject();
            if (!p.largeImage.isEmpty()) assets.put("large_image", p.largeImage);
            if (!p.largeText.isEmpty()) assets.put("large_text", clip(p.largeText));
            if (assets.length() > 0) a.put("assets", assets);
            if (!p.buttons.isEmpty()) {
                JSONArray buttons = new JSONArray();
                for (int i = 0; i < p.buttons.size() && i < 2; i++) buttons.put(new JSONObject().put("label", p.buttons.get(i)[0]).put("url", p.buttons.get(i)[1]));
                a.put("buttons", buttons);
            }
            return a;
        } catch (JSONException e) {
            return new JSONObject();
        }
    }

    private static String clip(String s) {
        String t = s.length() > 128 ? s.substring(0, 128) : s;
        return t.length() < 2 ? t + "  " : t;
    }
}
