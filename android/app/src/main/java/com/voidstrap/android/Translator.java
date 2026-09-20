package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;

final class Translator {
    static final String SETTING = "language";
    static final String DEFAULT = "en";

    static final String[] CODES = {
            "af", "sq", "am", "ar", "hy", "az", "eu", "be", "bn", "bs", "bg", "ca", "ceb", "ny", "zh-CN",
            "zh-TW", "co", "hr", "cs", "da", "nl", "en", "eo", "et", "tl", "fi", "fr", "fy", "gl", "ka",
            "de", "el", "gu", "ht", "ha", "haw", "iw", "hi", "hmn", "hu", "is", "ig", "id", "ga", "it",
            "ja", "jw", "kn", "kk", "km", "ko", "ku", "ky", "lo", "la", "lv", "lt", "lb", "mk", "mg", "ms",
            "ml", "mt", "mi", "mr", "mn", "my", "ne", "no", "or", "ps", "fa", "pl", "pt", "pa", "ro", "ru",
            "rw", "sm", "gd", "sr", "st", "sn", "sd", "si", "sk", "sl", "so", "es", "su", "sw", "sv", "tg",
            "ta", "te", "tt", "th", "tr", "tk", "uk", "ur", "ug", "uz", "vi", "cy", "xh", "yi", "yo", "zu"
    };

    static final String[] NAMES = {
            "Afrikaans", "Albanian", "Amharic", "Arabic", "Armenian", "Azerbaijani", "Basque", "Belarusian",
            "Bengali", "Bosnian", "Bulgarian", "Catalan", "Cebuano", "Chichewa", "Chinese (Simplified)",
            "Chinese (Traditional)", "Corsican", "Croatian", "Czech", "Danish", "Dutch", "English",
            "Esperanto", "Estonian", "Filipino", "Finnish", "French", "Frisian", "Galician", "Georgian",
            "German", "Greek", "Gujarati", "Haitian Creole", "Hausa", "Hawaiian", "Hebrew", "Hindi",
            "Hmong", "Hungarian", "Icelandic", "Igbo", "Indonesian", "Irish", "Italian", "Japanese",
            "Javanese", "Kannada", "Kazakh", "Khmer", "Korean", "Kurdish (Kurmanji)", "Kyrgyz", "Lao",
            "Latin", "Latvian", "Lithuanian", "Luxembourgish", "Macedonian", "Malagasy", "Malay",
            "Malayalam", "Maltese", "Maori", "Marathi", "Mongolian", "Myanmar (Burmese)", "Nepali",
            "Norwegian", "Odia", "Pashto", "Persian", "Polish", "Portuguese", "Punjabi", "Romanian",
            "Russian", "Kinyarwanda", "Samoan", "Scots Gaelic", "Serbian", "Sesotho", "Shona", "Sindhi",
            "Sinhala", "Slovak", "Slovenian", "Somali", "Spanish", "Sundanese", "Swahili", "Swedish",
            "Tajik", "Tamil", "Telugu", "Tatar", "Thai", "Turkish", "Turkmen", "Ukrainian", "Urdu",
            "Uyghur", "Uzbek", "Vietnamese", "Welsh", "Xhosa", "Yiddish", "Yoruba", "Zulu"
    };

    private static final String ENDPOINT = "https://translate.googleapis.com/translate_a/t?client=gtx&sl=auto&tl=";
    private static final int MAX_BATCH = 64;
    private static final int MAX_BODY = 4 * 1024 * 1024;
    private static final int TIMEOUT_MS = 15000;
    private static final long OFFLINE_PAUSE_MS = 30000;
    private static final int MAX_ATTEMPTS = 4;
    private static final int BASE_BACKOFF_MS = 700;
    private static final int MAX_BACKOFF_MS = 8000;
    private static final String[] PROTECTED = {"Roblox Studio", "Voidstrap", "Roblox", "Discord", "Bloxstrap", "Chevstrap"};
    private static final String USER_AGENT = "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";
    private static final String CACHE_FILE = "Translations.json";

    private static final ConcurrentHashMap<String, String> memory = new ConcurrentHashMap<>();
    private static final java.util.Set<String> inFlight = java.util.Collections.newSetFromMap(new ConcurrentHashMap<>());
    private static final java.util.concurrent.Semaphore net = new java.util.concurrent.Semaphore(1, true);
    private static final AtomicBoolean loaded = new AtomicBoolean();
    private static final AtomicBoolean saveQueued = new AtomicBoolean();
    private static volatile long offlineUntil;

    private Translator() {
    }

    static String language(Store store) {
        String value = store.setting(SETTING, DEFAULT);
        return value == null || value.isEmpty() ? DEFAULT : value;
    }

    static boolean active(Store store) {
        return !DEFAULT.equals(language(store));
    }

    static int indexOf(String code) {
        for (int i = 0; i < CODES.length; i++) if (CODES[i].equals(code)) return i;
        return indexOf(DEFAULT);
    }

    static boolean skip(CharSequence text) {
        if (text == null) return true;
        String s = text.toString();
        if (s.trim().isEmpty()) return true;
        for (String term : PROTECTED) if (s.trim().equalsIgnoreCase(term)) return true;
        String lower = s.toLowerCase(Locale.ROOT);
        if (lower.contains("://") || lower.contains("www.") || lower.contains(".com")
                || lower.contains(".net") || lower.contains(".org") || lower.contains(".gg")) return true;
        for (int i = 0; i < s.length(); i++) if (Character.isLetter(s.charAt(i))) return false;
        return true;
    }

    private static String key(String lang, String text) {
        return lang + "|" + text;
    }

    static String cached(String lang, String text) {
        return memory.get(key(lang, text));
    }

    static void load(Context c) {
        if (!loaded.compareAndSet(false, true)) return;
        File f = new File(c.getCacheDir(), CACHE_FILE);
        if (!f.isFile()) return;
        try (InputStream in = new java.io.FileInputStream(f)) {
            JSONObject root = new JSONObject(new String(readAll(in), StandardCharsets.UTF_8));
            for (Iterator<String> it = root.keys(); it.hasNext(); ) {
                String k = it.next();
                String v = root.optString(k, null);
                if (v != null) memory.put(k, v);
            }
        } catch (IOException | RuntimeException | org.json.JSONException ignored) {
        }
    }

    private static void save(Context c, Store store) {
        if (!saveQueued.compareAndSet(false, true)) return;
        Context app = c.getApplicationContext();
        store.work.execute(() -> {
            saveQueued.set(false);
            try {
                JSONObject root = new JSONObject();
                for (java.util.Map.Entry<String, String> e : memory.entrySet()) root.put(e.getKey(), e.getValue());
                File tmp = new File(app.getCacheDir(), CACHE_FILE + ".tmp");
                try (OutputStream out = new FileOutputStream(tmp)) {
                    out.write(root.toString().getBytes(StandardCharsets.UTF_8));
                }
                File dest = new File(app.getCacheDir(), CACHE_FILE);
                if (!tmp.renameTo(dest)) {
                    tmp.delete();
                }
            } catch (IOException | RuntimeException | org.json.JSONException ignored) {
            }
        });
    }

    static void fetch(Context c, Store store, String lang, List<String> texts, Runnable done) {
        Context app = c.getApplicationContext();
        List<String> pending = new ArrayList<>();
        for (String t : texts) {
            if (skip(t) || memory.containsKey(key(lang, t))) continue;
            if (pending.contains(t)) continue;
            if (!inFlight.add(key(lang, t))) continue;
            pending.add(t);
        }
        if (pending.isEmpty()) {
            if (done != null) store.main.post(done);
            return;
        }
        store.work.execute(() -> {
            boolean any = false;
            try {
                for (int i = 0; i < pending.size(); i += MAX_BATCH) {
                    List<String> chunk = pending.subList(i, Math.min(pending.size(), i + MAX_BATCH));
                    any |= request(lang, chunk);
                }
            } finally {
                for (String t : pending) inFlight.remove(key(lang, t));
            }
            if (any) save(app, store);
            if (done != null) store.main.post(done);
        });
    }

    private static boolean request(String lang, List<String> chunk) {
        for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
            if (System.currentTimeMillis() < offlineUntil) return false;
            try {
                net.acquire();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return false;
            }
            int wait;
            try {
                wait = send(lang, chunk);
            } finally {
                net.release();
            }
            if (wait == 0) return true;
            if (wait < 0) return false;
            try {
                Thread.sleep(wait);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
        return false;
    }

    private static int send(String lang, List<String> chunk) {
        HttpURLConnection con = null;
        try {
            con = (HttpURLConnection) new URL(ENDPOINT + URLEncoder.encode(lang, "UTF-8")).openConnection();
            con.setConnectTimeout(TIMEOUT_MS);
            con.setReadTimeout(TIMEOUT_MS);
            con.setRequestMethod("POST");
            con.setRequestProperty("Content-Type", "application/x-www-form-urlencoded; charset=utf-8");
            con.setRequestProperty("User-Agent", USER_AGENT);
            con.setRequestProperty("Accept", "*/*");
            con.setDoOutput(true);
            StringBuilder form = new StringBuilder();
            for (String t : chunk) {
                if (form.length() > 0) form.append('&');
                form.append("q=").append(URLEncoder.encode(mask(t), "UTF-8"));
            }
            byte[] body = form.toString().getBytes(StandardCharsets.UTF_8);
            con.setFixedLengthStreamingMode(body.length);
            try (OutputStream out = con.getOutputStream()) {
                out.write(body);
            }
            int code = con.getResponseCode();
            if (code == 429 || code / 100 == 5) return retryDelay(con);
            if (code / 100 != 2) {
                android.util.Log.w("Voidstrap", "Translation endpoint returned " + code);
                return -1;
            }
            String text;
            try (InputStream in = con.getInputStream()) {
                text = new String(readAll(in), StandardCharsets.UTF_8);
            }
            return parse(lang, chunk, text) ? 0 : -1;
        } catch (IOException | RuntimeException e) {
            offlineUntil = System.currentTimeMillis() + OFFLINE_PAUSE_MS;
            return -1;
        } finally {
            if (con != null) con.disconnect();
        }
    }

    private static boolean parse(String lang, List<String> chunk, String body) {
        try {
            JSONArray root = new JSONArray(body);
            if (chunk.size() == 1 && root.length() >= 1) {
                String one = value(root.opt(0));
                if (one == null) return false;
                memory.put(key(lang, chunk.get(0)), unmask(one, chunk.get(0)));
                return true;
            }
            if (root.length() != chunk.size()) return false;
            boolean any = false;
            for (int i = 0; i < chunk.size(); i++) {
                String v = value(root.opt(i));
                if (v == null) continue;
                memory.put(key(lang, chunk.get(i)), unmask(v, chunk.get(i)));
                any = true;
            }
            return any;
        } catch (org.json.JSONException | RuntimeException e) {
            return false;
        }
    }

    private static int retryDelay(HttpURLConnection con) {
        int header = con.getHeaderFieldInt("Retry-After", 0);
        if (header > 0) return Math.min(header * 1000, MAX_BACKOFF_MS);
        return BASE_BACKOFF_MS;
    }

    private static String token(int index) {
        return "VSTK" + index + "Z";
    }

    private static String mask(String text) {
        String out = text;
        for (int i = 0; i < PROTECTED.length; i++) {
            out = replaceAllIgnoreCase(out, PROTECTED[i], token(i));
        }
        return out;
    }

    private static String unmask(String translated, String source) {
        String out = translated;
        for (int i = 0; i < PROTECTED.length; i++) {
            String original = firstMatch(source, PROTECTED[i]);
            if (original == null) continue;
            out = replaceAllIgnoreCase(out, token(i), original);
        }
        return out;
    }

    private static String firstMatch(String source, String term) {
        int at = indexOfIgnoreCase(source, term, 0);
        return at < 0 ? null : source.substring(at, at + term.length());
    }

    private static int indexOfIgnoreCase(String haystack, String needle, int from) {
        int limit = haystack.length() - needle.length();
        for (int i = Math.max(0, from); i <= limit; i++) {
            if (haystack.regionMatches(true, i, needle, 0, needle.length())) return i;
        }
        return -1;
    }

    private static String replaceAllIgnoreCase(String text, String needle, String replacement) {
        StringBuilder out = null;
        int at = 0;
        int found;
        while ((found = indexOfIgnoreCase(text, needle, at)) >= 0) {
            if (out == null) out = new StringBuilder(text.length());
            out.append(text, at, found).append(replacement);
            at = found + needle.length();
        }
        if (out == null) return text;
        out.append(text, at, text.length());
        return out.toString();
    }

    private static String value(Object node) {
        if (node instanceof String) return (String) node;
        if (node instanceof JSONArray && ((JSONArray) node).length() > 0) {
            Object first = ((JSONArray) node).opt(0);
            if (first instanceof String) return (String) first;
        }
        return null;
    }

    private static byte[] readAll(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) {
            if (out.size() + n > MAX_BODY) throw new IOException("too large");
            out.write(buf, 0, n);
        }
        return out.toByteArray();
    }
}
