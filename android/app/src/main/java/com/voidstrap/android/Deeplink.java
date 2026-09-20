package com.voidstrap.android;

import android.net.Uri;

import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;

public final class Deeplink {
    public static final int MAX_INPUT = 2048;

    private static final Pattern DIGITS = Pattern.compile("\\d{1,18}");
    private static final Pattern INSTANCE = Pattern.compile("[0-9A-Fa-f-]{8,64}");
    private static final Pattern CODE = Pattern.compile("[A-Za-z0-9_-]{1,128}");

    public final long placeId;
    public final String instanceId;
    public final String linkCode;
    public final Uri passthrough;

    private Deeplink(long placeId, String instanceId, String linkCode, Uri passthrough) {
        this.placeId = placeId;
        this.instanceId = instanceId;
        this.linkCode = linkCode;
        this.passthrough = passthrough;
    }

    public static Deeplink place(long placeId, String instanceId, String linkCode) {
        if (placeId <= 0) return null;
        instanceId = blankToNull(instanceId);
        linkCode = blankToNull(linkCode);
        if (instanceId != null && linkCode != null) return null;
        if (instanceId != null && !INSTANCE.matcher(instanceId).matches()) return null;
        if (linkCode != null && !CODE.matcher(linkCode).matches()) return null;
        return new Deeplink(placeId, instanceId, linkCode, null);
    }

    public static Deeplink findIn(String text) {
        Deeplink whole = parse(text);
        if (whole != null || text == null || text.length() > MAX_INPUT) return whole;
        for (String word : text.split("\\s+")) {
            String w = word.replaceAll("^[\"'(<\\[]+|[\"')>\\].,!?;:]+$", "");
            if (w.isEmpty()) continue;
            String lower = w.toLowerCase(Locale.ROOT);
            if (!lower.contains("roblox") && !DIGITS.matcher(w).matches()) continue;
            Deeplink d = parse(w);
            if (d != null) return d;
        }
        return null;
    }

    public static Deeplink parse(String input) {
        if (input == null) return null;
        String s = input.trim();
        if (s.isEmpty() || s.length() > MAX_INPUT) return null;
        for (int i = 0; i < s.length(); i++) if (Character.isISOControl(s.charAt(i)) || Character.isWhitespace(s.charAt(i))) return null;
        if (DIGITS.matcher(s).matches()) return place(parseId(s), null, null);
        String lower = s.toLowerCase(Locale.ROOT);
        if (lower.startsWith("roblox://") || lower.startsWith("robloxmobile://")) return fromScheme(s);
        if (lower.startsWith("www.roblox.com/") || lower.startsWith("roblox.com/")) s = "https://" + s;
        Uri u = Uri.parse(s);
        if (!"https".equalsIgnoreCase(u.getScheme()) || u.getHost() == null) return null;
        String host = u.getHost().toLowerCase(Locale.ROOT);
        if (host.equals("ro.blox.com")) return new Deeplink(0, null, null, u);
        if (!host.equals("roblox.com") && !host.equals("www.roblox.com")) return null;
        List<String> seg = u.getPathSegments();
        int g = seg.size() > 0 && seg.get(0).equalsIgnoreCase("games") ? 0 : seg.size() > 1 && seg.get(1).equalsIgnoreCase("games") ? 1 : -1;
        if (g >= 0 && seg.size() > g + 1 && seg.get(g + 1).equalsIgnoreCase("start")) {
            return place(parseId(u.getQueryParameter("placeId")), u.getQueryParameter("gameInstanceId"), u.getQueryParameter("linkCode"));
        }
        if (g >= 0 && seg.size() > g + 1 && DIGITS.matcher(seg.get(g + 1)).matches()) {
            return place(parseId(seg.get(g + 1)), null, u.getQueryParameter("privateServerLinkCode"));
        }
        if (!seg.isEmpty()) {
            String first = seg.get(0).toLowerCase(Locale.ROOT);
            if (first.equals("share") || first.equals("share-links") || first.equals("join") || first.equals("shortlink")) {
                return new Deeplink(0, null, null, u.buildUpon().scheme("https").authority("www.roblox.com").build());
            }
        }
        return null;
    }

    private static Deeplink fromScheme(String s) {
        int sep = s.indexOf("://");
        String rest = s.substring(sep + 3);
        if (rest.regionMatches(true, 0, "placeId=", 0, 8)) {
            Uri q = Uri.parse("roblox://experiences/start?placeId=" + rest.substring(8));
            return place(parseId(q.getQueryParameter("placeId")), q.getQueryParameter("gameInstanceId"), q.getQueryParameter("linkCode"));
        }
        Uri u = Uri.parse("roblox://" + rest);
        if ("experiences".equalsIgnoreCase(u.getHost()) && "/start".equalsIgnoreCase(u.getPath())) {
            return place(parseId(u.getQueryParameter("placeId")), u.getQueryParameter("gameInstanceId"), u.getQueryParameter("linkCode"));
        }
        return new Deeplink(0, null, null, u);
    }

    private static long parseId(String s) {
        if (s == null || !DIGITS.matcher(s).matches()) return 0;
        return Long.parseLong(s);
    }

    private static String blankToNull(String s) {
        if (s == null) return null;
        s = s.trim();
        return s.isEmpty() ? null : s;
    }

    public boolean isPlace() {
        return passthrough == null;
    }

    public Uri toUri() {
        if (passthrough != null) return passthrough;
        Uri.Builder b = new Uri.Builder().scheme("roblox").authority("experiences").path("start").appendQueryParameter("placeId", String.valueOf(placeId));
        if (instanceId != null) b.appendQueryParameter("gameInstanceId", instanceId);
        if (linkCode != null) b.appendQueryParameter("linkCode", linkCode);
        return b.build();
    }

    public String stableId() {
        if (passthrough != null) return "link_" + Integer.toHexString(passthrough.toString().hashCode());
        String id = "game_" + placeId;
        if (instanceId != null) id += "_i" + Integer.toHexString(instanceId.hashCode());
        if (linkCode != null) id += "_p" + Integer.toHexString(linkCode.hashCode());
        return id;
    }
}
