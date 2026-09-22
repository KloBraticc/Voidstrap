package com.voidstrap.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.List;

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

    private static volatile boolean loaded;

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

    static void load(Context c) {
        if (loaded) return;
        loaded = true;
        try {
            Core.run("translate.load", Core.args("cache", c.getCacheDir()));
        } catch (IOException ignored) {
        }
    }

    static Object[] lookup(String lang, List<String> texts) {
        Object[] out = new Object[texts.size()];
        java.util.Arrays.fill(out, Boolean.FALSE);
        try {
            Object v = Core.value("translate.lookup", Core.args("lang", lang, "texts", new JSONArray(texts)));
            JSONArray a = v instanceof JSONArray ? (JSONArray) v : new JSONArray();
            for (int i = 0; i < out.length && i < a.length(); i++) {
                Object o = a.opt(i);
                out[i] = o == JSONObject.NULL ? null : o;
            }
        } catch (IOException ignored) {
        }
        return out;
    }

    static void fetch(Context c, Store store, String lang, List<String> texts, Runnable done) {
        Context app = c.getApplicationContext();
        store.work.execute(() -> {
            try {
                Core.run("translate.fetch", Core.args("cache", app.getCacheDir(), "lang", lang, "texts", new JSONArray(texts)));
            } catch (IOException ignored) {
            }
            if (done != null) store.main.post(done);
        });
    }
}
