package com.voidstrap.android;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class ActivityWatcher {
    public enum ServerType { PUBLIC, PRIVATE, RESERVED }

    public static final class Data {
        public long placeId;
        public String jobId = "";
        public long universeId;
        public long userId;
        public ServerType serverType = ServerType.PUBLIC;
        public String serverAddress = "";
        public long joined;
        public boolean teleport;
        public String launchData = "";

        Data copy() {
            Data d = new Data();
            d.placeId = placeId;
            d.jobId = jobId;
            d.universeId = universeId;
            d.userId = userId;
            d.serverType = serverType;
            d.serverAddress = serverAddress;
            d.joined = joined;
            d.teleport = teleport;
            d.launchData = launchData;
            return d;
        }

        public String inviteLink() {
            String link = "https://www.roblox.com/games/start?placeId=" + placeId + "&gameInstanceId=" + jobId;
            if (!launchData.isEmpty()) link += "&launchData=" + android.net.Uri.encode(launchData);
            return link;
        }
    }

    public interface Listener {
        void onJoined(Data data);

        void onLeft(Data data);

        void onMenu();

        void onLog(String kind, String text);

        void onRpc(String command, String json);
    }

    private static final Pattern JOINING = Pattern.compile("! Joining game '([0-9a-f\\-]{36})' place ([0-9]+) at ([0-9.]+)");
    private static final Pattern REFERRAL = Pattern.compile("referral_page:([^,]+)");
    private static final Pattern UNIVERSE = Pattern.compile("universeid:([0-9]+)");
    private static final Pattern USER = Pattern.compile("userid:([0-9]+)");
    private static final Pattern UDMUX = Pattern.compile("UDMUX Address = ([0-9.]+), Port = [0-9]+ \\| RCC Server Address = ([0-9.]+), Port = [0-9]+");
    private static final Pattern JOIN_TYPE = Pattern.compile("JoinTypeId(?:\"|%22)?(?::|%3a)(\\d+)");
    private static final Pattern PLAYER = Pattern.compile("(added|removed): (.*) ([0-9]+)\\s*$");
    private static final Pattern MESSAGE = Pattern.compile("Success Text: (.*)");
    private static final Pattern ADDED = Pattern.compile("playerAdded:\\s*userId=([0-9]+)");
    private static final Pattern REMOVED = Pattern.compile("(?:playerRemoving:\\s*userId=|Purging (?:social counterparties|age group|compatibility tokens) for player\\s+)([0-9]+)", Pattern.CASE_INSENSITIVE);
    private static final Pattern RPC = Pattern.compile("\\[(?:Voidstrap|Bloxstrap)RPC\\] (.*)");
    private static final int NEW_PRIVATE = 2;
    private static final int SPECIFIC_PRIVATE = 3;

    private final Listener listener;
    private Data data = new Data();
    private boolean inGame;
    private boolean teleportMarker;
    private boolean reservedMarker;
    private long lastUserId;
    private long lastRpc;

    public ActivityWatcher(Listener listener) {
        this.listener = listener;
    }

    public synchronized boolean inGame() {
        return inGame;
    }

    public synchronized Data current() {
        return inGame ? data.copy() : null;
    }

    public synchronized long userId() {
        return lastUserId;
    }

    public synchronized void reset() {
        boolean was = inGame;
        Data old = data;
        data = new Data();
        inGame = false;
        teleportMarker = false;
        reservedMarker = false;
        if (was) listener.onLeft(old);
    }

    public void feed(String line) {
        Runnable event = null;
        synchronized (this) {
            if (line.contains("! Joining game")) {
                Matcher m = JOINING.matcher(line);
                if (m.find()) {
                    boolean was = inGame;
                    Data old = data;
                    data = new Data();
                    inGame = false;
                    data.jobId = m.group(1);
                    data.placeId = parseLong(m.group(2));
                    String address = m.group(3);
                    data.serverAddress = isPrivateIp(address) ? "" : address;
                    data.teleport = teleportMarker;
                    teleportMarker = false;
                    if (reservedMarker) data.serverType = ServerType.RESERVED;
                    reservedMarker = false;
                    if (was) event = () -> listener.onLeft(old);
                }
            } else if (!inGame && data.placeId != 0) {
                if (line.contains("game_join_loadtime")) {
                    Matcher r = REFERRAL.matcher(line);
                    if (r.find() && data.serverType != ServerType.RESERVED) {
                        String ref = r.group(1);
                        if (ref.contains("RequestPrivateGame") || ref.contains("GameDetailPageJSHybridEvent")) data.serverType = ServerType.PRIVATE;
                    }
                    Matcher u = UNIVERSE.matcher(line);
                    Matcher id = USER.matcher(line);
                    if (u.find()) data.universeId = parseLong(u.group(1));
                    if (id.find()) {
                        data.userId = parseLong(id.group(1));
                        lastUserId = data.userId;
                    }
                } else if (line.contains("UDMUX Address")) {
                    Matcher m = UDMUX.matcher(line);
                    if (m.find() && !isPrivateIp(m.group(1))) data.serverAddress = m.group(1);
                } else if (line.contains("[FLog::Network] serverId:")) {
                    inGame = true;
                    data.joined = System.currentTimeMillis();
                    Data joined = data.copy();
                    event = () -> listener.onJoined(joined);
                }
            } else if (inGame) {
                if (line.contains("Time to disconnect replication data") || line.contains("leaveUGCGameInternal")) {
                    Data old = data;
                    data = new Data();
                    inGame = false;
                    event = () -> listener.onLeft(old);
                } else if (line.contains("doTeleport: joinScriptUrl")) {
                    teleportMarker = true;
                    Matcher m = JOIN_TYPE.matcher(line);
                    if (m.find()) {
                        int type = (int) parseLong(m.group(1));
                        if (type == NEW_PRIVATE || type == SPECIFIC_PRIVATE) reservedMarker = true;
                    }
                } else if (line.contains("[VoidstrapRPC]") || line.contains("[BloxstrapRPC]")) {
                    Matcher m = RPC.matcher(line);
                    long now = System.currentTimeMillis();
                    if (m.find() && now - lastRpc > 1000) {
                        try {
                            org.json.JSONObject o = new org.json.JSONObject(m.group(1));
                            String command = o.optString("command", "");
                            if (command.equals("SetLaunchData")) {
                                String launch = o.optString("data", "");
                                if (launch.length() <= 200) data.launchData = launch;
                            }
                            if (command.equals("SetRichPresence") || command.equals("SetLaunchData")) {
                                lastRpc = now;
                                String body = o.optJSONObject("data") == null ? "{}" : o.optJSONObject("data").toString();
                                event = () -> listener.onRpc(command, body);
                            }
                        } catch (org.json.JSONException ignored) {
                        }
                    }
                } else if (line.contains("[DFLog::SocialCounterpartyManager]")) {
                    Matcher a = ADDED.matcher(line);
                    Matcher r = REMOVED.matcher(line);
                    if (a.find()) {
                        String id = a.group(1);
                        event = () -> listener.onLog("join", id);
                    } else if (r.find()) {
                        String id = r.group(1);
                        event = () -> listener.onLog("leave", id);
                    }
                } else if (line.contains("ExpChat/mountClientApp")) {
                    Matcher p = PLAYER.matcher(line);
                    Matcher msg = MESSAGE.matcher(line);
                    if (line.contains("- Player ") && p.find()) {
                        String kind = p.group(1).equals("added") ? "join" : "leave";
                        String text = p.group(2).trim() + " (" + p.group(3) + ")";
                        event = () -> listener.onLog(kind, text);
                    } else if (line.contains("MessageReceived") && msg.find()) {
                        String text = msg.group(1).trim();
                        event = () -> listener.onLog("chat", text);
                    }
                }
            }
            if (event == null && !inGame && line.contains("setStage: (stage:LuaApp)")) event = listener::onMenu;
        }
        if (event != null) event.run();
    }

    static boolean isPrivateIp(String ip) {
        if (ip == null || ip.isEmpty()) return true;
        String[] p = ip.split("\\.");
        if (p.length != 4) return true;
        int a = (int) parseLong(p[0]);
        int b = (int) parseLong(p[1]);
        return a == 10 || a == 127 || a == 0 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168) || (a == 100 && b >= 64 && b <= 127) || (a == 169 && b == 254);
    }

    private static long parseLong(String s) {
        try {
            return Long.parseLong(s);
        } catch (NumberFormatException e) {
            return 0;
        }
    }
}
