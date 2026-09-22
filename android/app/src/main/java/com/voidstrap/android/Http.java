package com.voidstrap.android;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

final class Http {
    private Http() {
    }

    static Object[] request(String method, String url, String[] headers, byte[] body, int timeoutMs, int limit, boolean follow) {
        if (url == null || !url.startsWith("https://")) return null;
        HttpURLConnection con = null;
        try {
            con = (HttpURLConnection) new URL(url).openConnection();
            con.setInstanceFollowRedirects(follow);
            con.setConnectTimeout(timeoutMs);
            con.setReadTimeout(timeoutMs);
            con.setRequestMethod(method);
            if (headers != null) for (int i = 0; i + 1 < headers.length; i += 2) con.setRequestProperty(headers[i], headers[i + 1]);
            if (body != null) {
                con.setDoOutput(true);
                con.setFixedLengthStreamingMode(body.length);
                try (OutputStream out = con.getOutputStream()) {
                    out.write(body);
                }
            }
            int code = con.getResponseCode();
            List<String> pairs = new ArrayList<>();
            for (Map.Entry<String, List<String>> e : con.getHeaderFields().entrySet()) {
                if (e.getKey() == null || e.getValue() == null) continue;
                for (String v : e.getValue()) {
                    pairs.add(e.getKey());
                    pairs.add(v == null ? "" : v);
                }
            }
            InputStream in = code >= 400 ? con.getErrorStream() : con.getInputStream();
            byte[] data = in == null ? new byte[0] : read(in, limit);
            return new Object[]{new int[]{code}, pairs.toArray(new String[0]), data};
        } catch (IOException | RuntimeException e) {
            return null;
        } finally {
            if (con != null) con.disconnect();
        }
    }

    static String html(String text, boolean keepLines) {
        return keepLines ? ModCatalog.plainKeepLines(text) : ModCatalog.plain(text);
    }

    private static byte[] read(InputStream in, int limit) throws IOException {
        try (InputStream s = in) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] b = new byte[16384];
            int n;
            while ((n = s.read(b)) > 0) {
                if (out.size() + n > limit) throw new IOException("too large");
                out.write(b, 0, n);
            }
            return out.toByteArray();
        }
    }
}
