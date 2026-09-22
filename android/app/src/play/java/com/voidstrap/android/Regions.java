package com.voidstrap.android;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.TimeZone;

final class Regions {
    static final class Region {
        final String key;
        final double lat;
        final double lon;

        Region(String key, double lat, double lon) {
            this.key = key;
            this.lat = lat;
            this.lon = lon;
        }
    }

    static final Region[] ALL = load();

    private Regions() {
    }

    private static Region[] load() {
        try {
            Object v = Core.value("smart.regions", Core.args());
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            Region[] out = new Region[a.length()];
            for (int i = 0; i < out.length; i++) {
                JSONObject o = a.optJSONObject(i);
                out[i] = o == null ? new Region("us_east", 39.04, -77.49) : new Region(o.optString("key"), o.optDouble("lat"), o.optDouble("lon"));
            }
            return out;
        } catch (IOException e) {
            return new Region[]{new Region("us_east", 39.04, -77.49)};
        }
    }

    static Region byKey(String key) {
        for (Region r : ALL) if (r.key.equals(key)) return r;
        return null;
    }

    static int index(Region r) {
        for (int i = 0; i < ALL.length; i++) if (ALL[i] == r) return i;
        return 0;
    }

    static Region fromTimeZone(TimeZone tz) {
        try {
            Region r = byKey(Core.text("smart.fromTimeZone", Core.args("id", tz.getID(), "offset", tz.getRawOffset())));
            return r != null ? r : ALL[0];
        } catch (IOException e) {
            return ALL[0];
        }
    }

    static double km(double lat1, double lon1, double lat2, double lon2) {
        try {
            Object v = Core.value("smart.km", Core.args("lat1", lat1, "lon1", lon1, "lat2", lat2, "lon2", lon2));
            return v instanceof Number ? ((Number) v).doubleValue() : Double.NaN;
        } catch (IOException e) {
            return Double.NaN;
        }
    }
}
