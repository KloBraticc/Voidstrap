package com.voidstrap.android;

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

    static final Region[] ALL = {
            new Region("us_east", 39.04, -77.49),
            new Region("us_central", 32.78, -96.80),
            new Region("us_west", 34.05, -118.24),
            new Region("south_america", -23.55, -46.63),
            new Region("uk", 51.51, -0.13),
            new Region("europe_west", 52.37, 4.90),
            new Region("europe_central", 50.11, 8.68),
            new Region("europe_east", 44.43, 26.10),
            new Region("middle_east", 25.20, 55.27),
            new Region("india", 19.08, 72.88),
            new Region("southeast_asia", 1.35, 103.82),
            new Region("east_asia", 22.32, 114.17),
            new Region("japan", 35.68, 139.69),
            new Region("korea", 37.57, 126.98),
            new Region("australia", -33.87, 151.21),
            new Region("new_zealand", -36.85, 174.76),
            new Region("south_africa", -26.20, 28.05),
    };

    private static final String[] SOUTH_AMERICA = {"Sao_Paulo", "Argentina", "Buenos_Aires", "Santiago", "Montevideo", "Bogota", "Lima", "Caracas", "La_Paz", "Asuncion", "Guayaquil", "Manaus", "Recife", "Fortaleza", "Belem", "Cuiaba", "Porto_Velho", "Campo_Grande", "Bahia", "Maceio", "Araguaina", "Cayenne", "Paramaribo", "Guyana", "Punta_Arenas"};
    private static final String[] UK = {"London", "Dublin", "Belfast", "Guernsey", "Isle_of_Man", "Jersey"};
    private static final String[] EUROPE_WEST = {"Paris", "Brussels", "Amsterdam", "Luxembourg", "Madrid", "Monaco", "Andorra", "Lisbon", "Canary", "Madeira", "Faroe", "Reykjavik"};
    private static final String[] EAST_ASIA = {"Hong_Kong", "Taipei", "Shanghai", "Macau", "Chongqing", "Harbin", "Urumqi"};
    private static final String[] SOUTHERN_AFRICA = {"Johannesburg", "Maputo", "Harare", "Lusaka", "Gaborone", "Windhoek", "Maseru", "Mbabane", "Blantyre", "Lubumbashi", "Nairobi", "Kampala", "Dar_es_Salaam", "Addis_Ababa", "Luanda", "Kinshasa"};

    private Regions() {
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
        String id = tz.getID();
        double hours = tz.getRawOffset() / 3_600_000.0;
        if (id.startsWith("America/") || id.startsWith("US/") || id.startsWith("Canada/") || id.startsWith("Brazil/") || id.startsWith("Chile/")) {
            if (id.startsWith("Brazil/") || id.startsWith("Chile/") || has(id, SOUTH_AMERICA) || hours >= -3.25) return byKey("south_america");
            if (hours >= -5.5) return byKey("us_east");
            if (hours >= -7) return byKey("us_central");
            return byKey("us_west");
        }
        if (id.startsWith("Europe/") || id.startsWith("Atlantic/")) {
            if (has(id, UK)) return byKey("uk");
            if (has(id, EUROPE_WEST) || hours <= 0) return byKey("europe_west");
            if (hours >= 2) return byKey("europe_east");
            return byKey("europe_central");
        }
        if (id.startsWith("Australia/")) return byKey("australia");
        if (id.startsWith("Pacific/")) {
            if (hours >= 11.5) return byKey("new_zealand");
            if (hours <= -8) return byKey("us_west");
            return byKey("australia");
        }
        if (id.startsWith("Africa/")) {
            if (has(id, SOUTHERN_AFRICA)) return byKey("south_africa");
            if (hours >= 2) return byKey("middle_east");
            return byKey("europe_west");
        }
        if (id.startsWith("Asia/") || id.startsWith("Indian/")) {
            if (id.endsWith("Tokyo")) return byKey("japan");
            if (id.endsWith("Seoul") || id.endsWith("Pyongyang")) return byKey("korea");
            if (has(id, EAST_ASIA)) return byKey("east_asia");
            if (hours >= 9) return byKey("japan");
            if (hours >= 7) return byKey("southeast_asia");
            if (hours >= 5) return byKey("india");
            if (hours >= 3) return byKey("middle_east");
            return byKey("europe_east");
        }
        Region best = ALL[0];
        double lon = hours * 15;
        double bestGap = Double.MAX_VALUE;
        for (Region r : ALL) {
            double gap = Math.abs(((r.lon - lon) % 360 + 540) % 360 - 180);
            if (gap < bestGap) {
                bestGap = gap;
                best = r;
            }
        }
        return best;
    }

    static double km(double lat1, double lon1, double lat2, double lon2) {
        double p1 = Math.toRadians(lat1);
        double p2 = Math.toRadians(lat2);
        double dp = Math.toRadians(lat2 - lat1);
        double dl = Math.toRadians(lon2 - lon1);
        double a = Math.sin(dp / 2) * Math.sin(dp / 2) + Math.cos(p1) * Math.cos(p2) * Math.sin(dl / 2) * Math.sin(dl / 2);
        return 6371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    private static boolean has(String id, String[] names) {
        for (String n : names) if (id.contains(n)) return true;
        return false;
    }
}
