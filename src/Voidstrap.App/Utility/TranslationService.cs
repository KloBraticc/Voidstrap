using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.Utility
{
    public static class TranslationService
    {
        private const int BatchCount = 48;
        private const int BatchChars = 4000;

        private static readonly HttpClient _httpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(20));
        private static string CachePath => Path.Combine(Paths.Initialized ? Paths.Cache : Paths.Temp, "Translations.json");

        private static string RemoteApi => App.WebsiteBaseUrl + "/api/translations";
        private static string RemoteMetaPath => Path.Combine(Paths.Initialized ? Paths.Cache : Paths.Temp, "TranslationsRemote.json");
        private static readonly ConcurrentDictionary<string, long> _remoteSeen = new();
        private static readonly ConcurrentDictionary<string, byte> _remoteFetchStarted = new();

        private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _cache = new();
        private static readonly ConcurrentDictionary<string, byte> _outputs = new();
        private static readonly ConcurrentDictionary<string, string> _sources = new();
        private static readonly ConcurrentDictionary<string, byte> _pending = new();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingTranslations = new();
        private static readonly ConcurrentQueue<(string Text, string TargetLang)> _queue = new();
        private static readonly SemaphoreSlim _net = new SemaphoreSlim(6, 6);

        private static bool _initialized;
        private static int _processing;
        private static DispatcherTimer? _saveTimer;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            LoadCache();
            LoadRemoteMeta();

            try
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
                    _saveTimer.Tick += OnSaveTimerTick;
                }));
            }
            catch { }
        }

        public static void Shutdown()
        {
            var timer = _saveTimer;
            _saveTimer = null;
            if (timer != null)
            {
                try
                {
                    timer.Stop();
                    timer.Tick -= OnSaveTimerTick;
                }
                catch { }
            }
            SaveCacheBlocking();
        }

        private static void OnSaveTimerTick(object? sender, EventArgs e)
        {
            _saveTimer?.Stop();
            SaveCache();
        }

        private static string Key(string lang, string text) => lang + "\x01" + text;

        public static string Translate(string text, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            targetLanguage = NormalizeLang(targetLanguage);
            if (string.IsNullOrEmpty(targetLanguage) || targetLanguage == "en") return text;
            if (LooksLikeUrl(text) || !ContainsTranslatableText(text)) return text;

            if (!_initialized) Initialize();

            var bucket = _cache.GetOrAdd(targetLanguage, _ => new ConcurrentDictionary<string, string>());
            if (bucket.TryGetValue(text, out string? cached))
            {
                RememberTranslation(targetLanguage, text, cached);
                return cached;
            }

            if (_pending.TryAdd(Key(targetLanguage, text), 0))
            {
                _queue.Enqueue((text, targetLanguage));
                StartProcessor();
            }

            return text;
        }

        public static async Task<string> TranslateAsync(string text, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            targetLanguage = NormalizeLang(targetLanguage);
            if (string.IsNullOrEmpty(targetLanguage) || targetLanguage == "en") return text;
            if (LooksLikeUrl(text) || !ContainsTranslatableText(text)) return text;

            if (!_initialized) Initialize();

            var bucket = _cache.GetOrAdd(targetLanguage, _ => new ConcurrentDictionary<string, string>());
            if (bucket.TryGetValue(text, out string? cached))
            {
                RememberTranslation(targetLanguage, text, cached);
                return cached;
            }

            string key = Key(targetLanguage, text);
            var tcs = _pendingTranslations.GetOrAdd(key, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

            if (bucket.TryGetValue(text, out cached))
            {
                _pendingTranslations.TryRemove(key, out _);
                RememberTranslation(targetLanguage, text, cached);
                return cached;
            }

            if (_pending.TryAdd(key, 0))
            {
                _queue.Enqueue((text, targetLanguage));
                StartProcessor();
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return text;
            }
        }

        public static bool IsTranslated(string? text, string targetLanguage)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return _outputs.ContainsKey(Key(NormalizeLang(targetLanguage), text));
        }

        public static bool TryGetOriginal(string? text, string targetLanguage, out string original)
        {
            original = "";
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return _sources.TryGetValue(Key(NormalizeLang(targetLanguage), text), out original!);
        }

        private static void RememberTranslation(string lang, string source, string translated)
        {
            string outputKey = Key(NormalizeLang(lang), translated);
            _outputs.TryAdd(outputKey, 0);
            _sources.TryAdd(outputKey, source);
        }

        private static bool LooksLikeUrl(string text)
        {
            return text.Contains("://", StringComparison.OrdinalIgnoreCase)
                || text.Contains("www.", StringComparison.OrdinalIgnoreCase)
                || text.Contains(".com", StringComparison.OrdinalIgnoreCase)
                || text.Contains(".net", StringComparison.OrdinalIgnoreCase)
                || text.Contains(".org", StringComparison.OrdinalIgnoreCase)
                || text.Contains(".gg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsTranslatableText(string text)
        {
            bool containsLetter = false;
            for (int index = 0; index < text.Length; index++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, index);
                if (category == UnicodeCategory.PrivateUse)
                {
                    return false;
                }
                if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter)
                {
                    containsLetter = true;
                }
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }
            }
            return containsLetter;
        }

        private static string NormalizeLang(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "";
            switch (lang)
            {
                case "nil": return "";
                case "en-US": return "en";
                case "es-ES": return "es";
                case "pt-BR": return "pt";
                case "sv-SE": return "sv";
                case "fil": return "tl";
                case "zh-CN": return "zh-CN";
                case "zh-TW": return "zh-TW";
            }
            int dash = lang.IndexOf('-');
            if (dash > 0 && !lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return lang.Substring(0, dash);
            return lang;
        }

        private static void StartProcessor()
        {
            if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;
            _ = Task.Run(ProcessQueueAsync);
        }

        private static async Task ProcessQueueAsync()
        {
            try
            {
                while (!_queue.IsEmpty)
                {
                    var byLang = new Dictionary<string, List<string>>();
                    int taken = 0;
                    while (taken < BatchCount * 6 && _queue.TryDequeue(out var item))
                    {
                        if (!byLang.TryGetValue(item.TargetLang, out var list))
                            byLang[item.TargetLang] = list = new List<string>();
                        list.Add(item.Text);
                        taken++;
                    }

                    if (taken == 0) break;

                    foreach (string lang in byLang.Keys)
                        EnsureRemoteFetched(lang);

                    var jobs = new List<Task<bool>>();
                    foreach (var kv in byLang)
                        foreach (var chunk in Chunk(kv.Value))
                            jobs.Add(TranslateChunkAsync(chunk, kv.Key));

                    bool any = false;
                    foreach (bool changed in await Task.WhenAll(jobs).ConfigureAwait(false))
                        any |= changed;

                    if (any)
                    {
                        QueueSave();
                        Voidstrap.UI.LiveLanguageRefresher.TranslateOpenWindows();
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("TranslationService::ProcessQueue", $"Error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _processing, 0);
                if (!_queue.IsEmpty) StartProcessor();
            }
        }

        private static IEnumerable<List<string>> Chunk(List<string> texts)
        {
            var current = new List<string>();
            int chars = 0;

            foreach (string text in texts)
            {
                if (current.Count > 0 && (current.Count >= BatchCount || chars + text.Length > BatchChars))
                {
                    yield return current;
                    current = new List<string>();
                    chars = 0;
                }
                current.Add(text);
                chars += text.Length;
            }

            if (current.Count > 0)
                yield return current;
        }

        private static async Task<bool> TranslateChunkAsync(List<string> texts, string lang)
        {
            var bucket = _cache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>());
            var need = texts.Where(t => !bucket.ContainsKey(t)).Distinct().ToList();

            bool any = false;
            bool stored = false;
            var uploads = new Dictionary<string, string>();

            try
            {
                if (need.Count > 0)
                {
                    string?[] results = await TranslateBatchAsync(need, lang).ConfigureAwait(false);
                    for (int i = 0; i < need.Count; i++)
                    {
                        string? translated = results[i];
                        if (string.IsNullOrEmpty(translated))
                            continue;

                        bucket[need[i]] = translated!;
                        stored = true;

                        if (translated == need[i])
                            continue;

                        RememberTranslation(lang, need[i], translated!);
                        uploads[need[i]] = translated!;
                        any = true;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("TranslationService::TranslateChunk", $"Error: {ex.Message}");
            }
            finally
            {
                foreach (string text in texts)
                {
                    string key = Key(lang, text);
                    bucket.TryGetValue(text, out string? done);
                    if (_pendingTranslations.TryRemove(key, out var tcs))
                        tcs.TrySetResult(done ?? text);
                    _pending.TryRemove(key, out _);
                }
            }

            if (stored)
                QueueSave();

            if (uploads.Count > 0)
                _ = PushRemoteAsync(lang, uploads);

            return any;
        }

        private static async Task<string?[]> TranslateBatchAsync(List<string> texts, string targetLanguage)
        {
            var results = new string?[texts.Count];

            await _net.WaitAsync().ConfigureAwait(false);
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/t?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}";

                var form = new List<KeyValuePair<string, string>>(texts.Count);
                foreach (string text in texts)
                    form.Add(new KeyValuePair<string, string>("q", text));

                using var content = new FormUrlEncodedContent(form);
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string body = await Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, CancellationToken.None).ConfigureAwait(false);
                var parsed = ParseBatch(body, texts.Count);
                if (parsed != null)
                    return parsed;

                App.Logger.WriteLine("TranslationService::TranslateBatch", $"Unexpected batch shape for {texts.Count} entries, falling back");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("TranslationService::TranslateBatch", $"Error: {ex.Message}");
            }
            finally
            {
                _net.Release();
            }

            var singles = await Task.WhenAll(texts.Select(t => TranslateOneAsync(t, targetLanguage))).ConfigureAwait(false);
            for (int i = 0; i < singles.Length; i++)
                results[i] = singles[i];
            return results;
        }

        private static string?[]? ParseBatch(string json, int expected)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                    return null;

                if (expected == 1 && root.GetArrayLength() >= 1 && root[0].ValueKind == JsonValueKind.String)
                    return new[] { root[0].GetString() };

                if (root.GetArrayLength() != expected)
                    return null;

                var results = new string?[expected];
                int index = 0;
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        results[index] = element.GetString();
                    else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0 && element[0].ValueKind == JsonValueKind.String)
                        results[index] = element[0].GetString();
                    else
                        return null;
                    index++;
                }
                return results;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> TranslateOneAsync(string text, string targetLanguage)
        {
            await _net.WaitAsync().ConfigureAwait(false);
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";
                string response = await Http.GetStringBoundedAsync(_httpClient, url, maxBytes: 1024 * 1024).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(response);
                var segments = doc.RootElement[0];
                var builder = new StringBuilder();
                foreach (var item in segments.EnumerateArray())
                {
                    if (item.GetArrayLength() > 0 && item[0].ValueKind == JsonValueKind.String)
                        builder.Append(item[0].GetString());
                }
                return builder.ToString();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("TranslationService::TranslateOneAsync", $"Error: {ex.Message}");
                return text;
            }
            finally
            {
                _net.Release();
            }
        }

        private static void LoadCache()
        {
            string cachePath = CachePath;
            if (!File.Exists(cachePath)) return;
            try
            {
                var dict = JsonFile.Deserialize<Dictionary<string, Dictionary<string, string>>>(cachePath, JsonOptions.Tolerant);
                if (dict != null)
                {
                    var rebuilt = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
                    foreach (var outer in dict)
                    {
                        rebuilt[outer.Key] = new ConcurrentDictionary<string, string>(outer.Value);
                        foreach (var pair in outer.Value)
                            if (!string.IsNullOrEmpty(pair.Value)) RememberTranslation(outer.Key, pair.Key, pair.Value);
                    }
                    _cache = rebuilt;
                }
            }
            catch
            {
                _cache = new();
            }
        }

        private static void QueueSave()
        {
            try { Application.Current?.Dispatcher.BeginInvoke(new Action(() => { _saveTimer?.Stop(); _saveTimer?.Start(); })); }
            catch { SaveCache(); }
        }

        private static int _saving;

        private static void SaveCache()
        {
            if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
                return;

            var snapshot = _cache.ToDictionary(k => k.Key, v => v.Value.ToDictionary(ik => ik.Key, iv => iv.Value));

            _ = Task.Run(() =>
            {
                try
                {
                    string cachePath = CachePath;
                    string directory = Path.GetDirectoryName(cachePath)!;
                    if (!Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    JsonFile.SerializeAtomic(cachePath, snapshot);
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _saving, 0);
                }
            });
        }

        private static void SaveCacheBlocking()
        {
            try
            {
                string cachePath = CachePath;
                string directory = Path.GetDirectoryName(cachePath)!;
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var snapshot = _cache.ToDictionary(k => k.Key, v => v.Value.ToDictionary(ik => ik.Key, iv => iv.Value));
                JsonFile.SerializeAtomic(cachePath, snapshot);
            }
            catch
            {
            }
        }

        private static void EnsureRemoteFetched(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang == "en") return;
            if (!_remoteFetchStarted.TryAdd(lang, 0)) return;
            _ = FetchLanguageAsync(lang);
        }

        private static async Task FetchLanguageAsync(string lang)
        {
            try
            {
                long since = _remoteSeen.TryGetValue(lang, out var s) ? s : 0;
                string url = RemoteApi + "?lang=" + Uri.EscapeDataString(lang) + "&since=" + since;
                string response = await Http.GetStringBoundedAsync(_httpClient, url, maxBytes: 32 * 1024 * 1024).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("unchanged", out var unchanged) && unchanged.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("updated", out var up0) && up0.TryGetInt64(out long upv0))
                        _remoteSeen[lang] = upv0;
                    SaveRemoteMeta();
                    return;
                }

                if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                    return;

                var bucket = _cache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>());
                bool any = false;
                foreach (var kv in entries.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.String) continue;
                    string src = kv.Name;
                    string tr = kv.Value.GetString() ?? "";
                    if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tr)) continue;
                    if (bucket.TryAdd(src, tr))
                    {
                        RememberTranslation(lang, src, tr);
                        any = true;
                    }
                }

                if (root.TryGetProperty("updated", out var up) && up.TryGetInt64(out long upv))
                    _remoteSeen[lang] = upv;
                SaveRemoteMeta();

                if (any)
                {
                    QueueSave();
                    try { Voidstrap.UI.LiveLanguageRefresher.TranslateOpenWindows(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("TranslationService::FetchLanguageAsync", $"Error: {ex.Message}");
            }
        }

        private static async Task PushRemoteAsync(string lang, Dictionary<string, string> pairs)
        {
            if (pairs == null || pairs.Count == 0) return;
            try
            {
                var payload = new { lang = lang, entries = pairs };
                string body = JsonSerializer.Serialize(payload);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _httpClient.PostAsync(RemoteApi, content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("TranslationService::PushRemoteAsync", $"Error: {ex.Message}");
            }
        }

        private static void LoadRemoteMeta()
        {
            try
            {
                string remoteMetaPath = RemoteMetaPath;
                if (!File.Exists(remoteMetaPath)) return;
                var dict = JsonFile.Deserialize<Dictionary<string, long>>(remoteMetaPath, JsonOptions.Tolerant, 16777216);
                if (dict != null)
                    foreach (var kv in dict)
                        _remoteSeen[kv.Key] = kv.Value;
            }
            catch
            {
            }
        }

        private static void SaveRemoteMeta()
        {
            try
            {
                string remoteMetaPath = RemoteMetaPath;
                string directory = Path.GetDirectoryName(remoteMetaPath)!;
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                var snapshot = _remoteSeen.ToDictionary(k => k.Key, v => v.Value);
                JsonFile.SerializeAtomic(remoteMetaPath, snapshot);
            }
            catch
            {
            }
        }

        public static readonly Dictionary<string, string> AvailableLanguages = new()
        {
            { "af", "Afrikaans" },
            { "sq", "Albanian" },
            { "am", "Amharic" },
            { "ar", "Arabic" },
            { "hy", "Armenian" },
            { "az", "Azerbaijani" },
            { "eu", "Basque" },
            { "be", "Belarusian" },
            { "bn", "Bengali" },
            { "bs", "Bosnian" },
            { "bg", "Bulgarian" },
            { "ca", "Catalan" },
            { "ceb", "Cebuano" },
            { "ny", "Chichewa" },
            { "zh-CN", "Chinese (Simplified)" },
            { "zh-TW", "Chinese (Traditional)" },
            { "co", "Corsican" },
            { "hr", "Croatian" },
            { "cs", "Czech" },
            { "da", "Danish" },
            { "nl", "Dutch" },
            { "en", "English" },
            { "eo", "Esperanto" },
            { "et", "Estonian" },
            { "tl", "Filipino" },
            { "fi", "Finnish" },
            { "fr", "French" },
            { "fy", "Frisian" },
            { "gl", "Galician" },
            { "ka", "Georgian" },
            { "de", "German" },
            { "el", "Greek" },
            { "gu", "Gujarati" },
            { "ht", "Haitian Creole" },
            { "ha", "Hausa" },
            { "haw", "Hawaiian" },
            { "iw", "Hebrew" },
            { "hi", "Hindi" },
            { "hmn", "Hmong" },
            { "hu", "Hungarian" },
            { "is", "Icelandic" },
            { "ig", "Igbo" },
            { "id", "Indonesian" },
            { "ga", "Irish" },
            { "it", "Italian" },
            { "ja", "Japanese" },
            { "jw", "Javanese" },
            { "kn", "Kannada" },
            { "kk", "Kazakh" },
            { "km", "Khmer" },
            { "ko", "Korean" },
            { "ku", "Kurdish (Kurmanji)" },
            { "ky", "Kyrgyz" },
            { "lo", "Lao" },
            { "la", "Latin" },
            { "lv", "Latvian" },
            { "lt", "Lithuanian" },
            { "lb", "Luxembourgish" },
            { "mk", "Macedonian" },
            { "mg", "Malagasy" },
            { "ms", "Malay" },
            { "ml", "Malayalam" },
            { "mt", "Maltese" },
            { "mi", "Maori" },
            { "mr", "Marathi" },
            { "mn", "Mongolian" },
            { "my", "Myanmar (Burmese)" },
            { "ne", "Nepali" },
            { "no", "Norwegian" },
            { "or", "Odia" },
            { "ps", "Pashto" },
            { "fa", "Persian" },
            { "pl", "Polish" },
            { "pt", "Portuguese" },
            { "pa", "Punjabi" },
            { "ro", "Romanian" },
            { "ru", "Russian" },
            { "rw", "Kinyarwanda" },
            { "sm", "Samoan" },
            { "gd", "Scots Gaelic" },
            { "sr", "Serbian" },
            { "st", "Sesotho" },
            { "sn", "Shona" },
            { "sd", "Sindhi" },
            { "si", "Sinhala" },
            { "sk", "Slovak" },
            { "sl", "Slovenian" },
            { "so", "Somali" },
            { "es", "Spanish" },
            { "su", "Sundanese" },
            { "sw", "Swahili" },
            { "sv", "Swedish" },
            { "tg", "Tajik" },
            { "ta", "Tamil" },
            { "te", "Telugu" },
            { "tt", "Tatar" },
            { "th", "Thai" },
            { "tr", "Turkish" },
            { "tk", "Turkmen" },
            { "uk", "Ukrainian" },
            { "ur", "Urdu" },
            { "ug", "Uyghur" },
            { "uz", "Uzbek" },
            { "vi", "Vietnamese" },
            { "cy", "Welsh" },
            { "xh", "Xhosa" },
            { "yi", "Yiddish" },
            { "yo", "Yoruba" },
            { "zu", "Zulu" }
        };
    }
}
