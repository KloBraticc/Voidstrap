using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.GameChat
{
    public sealed class GameChatBridgeMessage
    {
        public string Kind = "";
        public string Sender = "";
        public string Text = "";
        public long SenderId;
        public bool Verified;
    }

    public sealed class GameChatBridgeChallenge
    {
        public string AuthUrl = "";
        public string SessionId = "";
        public int Ttl;
    }

    internal static class GameChatBridgeText
    {
        private const int MaxCombiningMarks = 3;

        public static string Clean(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            string input = value!.Length > maxLength ? value.Substring(0, maxLength) : value;
            if (!NeedsClean(input))
                return input;

            var builder = new StringBuilder(input.Length);
            int combining = 0;

            for (int i = 0; i < input.Length; i++)
            {
                char current = input[i];

                if (char.IsHighSurrogate(current))
                {
                    if (i + 1 >= input.Length || !char.IsLowSurrogate(input[i + 1]))
                        continue;
                    builder.Append(current);
                    builder.Append(input[i + 1]);
                    i++;
                    combining = 0;
                    continue;
                }

                if (char.IsLowSurrogate(current) || IsStripped(current))
                    continue;

                if (char.IsControl(current) || char.IsSeparator(current))
                {
                    AppendSpace(builder);
                    combining = 0;
                    continue;
                }

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(current);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
                {
                    if (combining >= MaxCombiningMarks)
                        continue;
                    combining++;
                }
                else
                {
                    combining = 0;
                }

                builder.Append(current);
            }

            return builder.ToString().Trim();
        }

        public static string CleanName(string? value)
        {
            string cleaned = Clean(value, 40);
            return cleaned.Length == 0 ? "Unknown" : cleaned;
        }

        private static void AppendSpace(StringBuilder builder)
        {
            if (builder.Length == 0 || builder[builder.Length - 1] == ' ')
                return;
            builder.Append(' ');
        }

        private static bool NeedsClean(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current < ' ' || current == '\u007F' || current >= '\u00AD')
                    return true;
            }
            return false;
        }

        private static bool IsStripped(char value)
        {
            return value == '\u00AD'
                || value == '\u061C'
                || value == '\u180E'
                || value == '\uFEFF'
                || value == '\uFFFC'
                || value == '\uFFFD'
                || (value >= '\u200B' && value <= '\u200F')
                || (value >= '\u202A' && value <= '\u202E')
                || (value >= '\u2060' && value <= '\u2064')
                || (value >= '\u2066' && value <= '\u2069');
        }
    }

    internal static class GameChatBridgeConfig
    {
        private const string Tag = "GameChatBridge";
        private const string ConfigUrl = "https://raw.githubusercontent.com/KloBraticc/Herm-chat/main/config.json";
        private const int MaxConfigBytes = 4096;
        private const long MemoMilliseconds = 600000;
        private const long RetryMilliseconds = 60000;

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static bool _enabled;
        private static long _resolvedAtMs;

        public static bool KnownEnabled => _enabled && Environment.TickCount64 - Volatile.Read(ref _resolvedAtMs) < MemoMilliseconds;

        public static async Task<bool> IsEnabledAsync(CancellationToken token)
        {
            long age = Environment.TickCount64 - Volatile.Read(ref _resolvedAtMs);
            if (_resolvedAtMs != 0 && age < (_enabled ? MemoMilliseconds : RetryMilliseconds))
                return _enabled;

            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                age = Environment.TickCount64 - Volatile.Read(ref _resolvedAtMs);
                if (_resolvedAtMs != 0 && age < (_enabled ? MemoMilliseconds : RetryMilliseconds))
                    return _enabled;

                bool value = await FetchAsync(token).ConfigureAwait(false);
                _enabled = value;
                Volatile.Write(ref _resolvedAtMs, Environment.TickCount64);
                return value;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<bool> FetchAsync(CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ConfigUrl);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return false;

                string body = await Utility.Http.ReadStringBoundedAsync(response.Content, MaxConfigBytes, token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.True)
                    return true;
                if (root.ValueKind == JsonValueKind.Object)
                    return GameChatBridgeTransport.ReadBool(root, "enabled");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Availability check failed: " + ex.Message);
                return false;
            }
        }
    }

    internal static class GameChatBridgeAuth
    {
        private static readonly object Sync = new object();

        private static string FilePath => Path.Combine(Paths.DocumentsData, "BootstrapperChat.json");

        private sealed class Store
        {
            public int v { get; set; } = 1;
            public string token { get; set; } = "";
        }

        public static string? GetToken()
        {
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(FilePath))
                        return null;
                    Store store = Utility.JsonFile.Deserialize<Store>(FilePath, Utility.JsonOptions.Tolerant, 65536);
                    if (string.IsNullOrEmpty(store.token))
                        return null;
                    string? plain = Utility.WebsiteAuth.UnprotectString(store.token);
                    return string.IsNullOrWhiteSpace(plain) ? null : plain;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void SaveToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            lock (Sync)
            {
                try
                {
                    string encoded = Utility.WebsiteAuth.ProtectString(token.Trim());
                    if (encoded.Length == 0)
                        return;
                    Directory.CreateDirectory(Paths.DocumentsData);
                    Utility.JsonFile.SerializeAtomic(FilePath, new Store { token = encoded }, null, false);
                }
                catch
                {
                }
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                try
                {
                    if (File.Exists(FilePath))
                        File.Delete(FilePath);
                }
                catch
                {
                }
            }
        }
    }

    internal readonly struct GameChatBridgeResponse : IDisposable
    {
        public readonly HttpStatusCode Status;
        private readonly JsonDocument _document;

        public GameChatBridgeResponse(HttpStatusCode status, JsonDocument document)
        {
            Status = status;
            _document = document;
        }

        public JsonElement Root => _document.RootElement;

        public void Dispose() => _document.Dispose();
    }

    internal static class GameChatBridgeTransport
    {
        public const string Host = "hermivore.cat";
        public const int MaxResponseBytes = 65536;

        private static readonly HttpClient Client = Create();

        private static HttpClient Create()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };

            var client = new HttpClient(handler, true)
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Voidstrap");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        public static Uri Api(string path)
        {
            var uri = new Uri("https://" + Host + path, UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing a request outside the chat host");
            return uri;
        }

        public static async Task<GameChatBridgeResponse> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!await GameChatBridgeConfig.IsEnabledAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("The community chat bridge is turned off");

            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            int code = (int)response.StatusCode;
            if (code >= 300 && code < 400)
                throw new HttpRequestException("Redirect refused");

            string body = await Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
            JsonDocument document = JsonDocument.Parse(body);

            if (!response.IsSuccessStatusCode
                && response.StatusCode != HttpStatusCode.Unauthorized
                && response.StatusCode != HttpStatusCode.ServiceUnavailable
                && response.StatusCode != HttpStatusCode.BadRequest)
            {
                document.Dispose();
                throw new HttpRequestException("HTTP " + code);
            }

            return new GameChatBridgeResponse(response.StatusCode, document);
        }

        public static StringContent JsonBody(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        public static string ReadString(JsonElement element, string name, int maxLength)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out JsonElement property)
                || property.ValueKind != JsonValueKind.String)
            {
                return "";
            }
            return GameChatBridgeText.Clean(property.GetString(), maxLength);
        }

        public static long ReadLong(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement property))
                return 0;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long number))
                return number;
            if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                return parsed;
            return 0;
        }

        public static bool ReadBool(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out JsonElement property)
                && property.ValueKind == JsonValueKind.True;
        }

        public static double ReadDouble(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out JsonElement property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetDouble(out double value)
                || double.IsNaN(value)
                || double.IsInfinity(value))
            {
                return 0;
            }
            return value;
        }
    }

    internal static class GameChatBridgeVerify
    {
        private const string Tag = "GameChatBridge";

        public static async Task<GameChatBridgeChallenge?> StartAsync(CancellationToken token)
        {
            try
            {
                if (!await GameChatBridgeConfig.IsEnabledAsync(token).ConfigureAwait(false))
                    return null;

                using var request = new HttpRequestMessage(HttpMethod.Post, GameChatBridgeTransport.Api("/api/roblox/oauth/challenge"));
                using GameChatBridgeResponse response = await GameChatBridgeTransport.SendAsync(request, token).ConfigureAwait(false);

                JsonElement root = response.Root;
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                    return null;

                string sessionId = GameChatBridgeTransport.ReadString(root, "session_id", 128);
                string authUrl = GameChatBridgeTransport.ReadString(root, "auth_url", 2048);
                long ttl = GameChatBridgeTransport.ReadLong(root, "ttl");

                if (sessionId.Length == 0 || !IsSafeAuthUrl(authUrl))
                    return null;

                return new GameChatBridgeChallenge
                {
                    SessionId = sessionId,
                    AuthUrl = authUrl,
                    Ttl = (int)Math.Clamp(ttl <= 0 ? 300 : ttl, 30, 900),
                };
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Verification challenge failed: " + ex.Message);
                return null;
            }
        }

        public static async Task<bool> WaitAsync(GameChatBridgeChallenge challenge, CancellationToken token)
        {
            if (!IsSafeSessionId(challenge.SessionId))
                return false;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(challenge.Ttl + 15));

            string path = "/api/roblox/oauth/status/" + Uri.EscapeDataString(challenge.SessionId);

            while (!deadline.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, GameChatBridgeTransport.Api(path));
                    using GameChatBridgeResponse response = await GameChatBridgeTransport.SendAsync(request, deadline.Token).ConfigureAwait(false);

                    JsonElement root = response.Root;
                    string status = GameChatBridgeTransport.ReadString(root, "status", 24);

                    if (status == "expired" || status == "error")
                        return false;
                    if (status != "ok")
                        continue;

                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("session_token", out JsonElement tokenElement)
                        || tokenElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    string sessionToken = tokenElement.GetString() ?? "";
                    if (!IsSafeJwt(sessionToken))
                        return false;

                    GameChatBridgeAuth.SaveToken(sessionToken);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(Tag, "Verification poll failed: " + ex.Message);
                }
            }

            return false;
        }

        private static readonly string[] AuthUrlHosts =
        [
            "roblox.com",
            "www.roblox.com",
            "apis.roblox.com",
            "authorize.roblox.com",
            GameChatBridgeTransport.Host,
            "www." + GameChatBridgeTransport.Host
        ];

        public static bool IsSafeAuthUrl(string value)
        {
            if (value.Length == 0 || value.Length > 2048)
                return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (string host in AuthUrlHosts)
            {
                if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsSafeSessionId(string value)
        {
            if (value.Length == 0 || value.Length > 128)
                return false;
            foreach (char current in value)
            {
                if (!char.IsAsciiLetterOrDigit(current) && current != '-' && current != '_')
                    return false;
            }
            return true;
        }

        private static bool IsSafeJwt(string value)
        {
            if (value.Length < 16 || value.Length > 4096)
                return false;

            int dots = 0;
            foreach (char current in value)
            {
                if (current == '.')
                {
                    dots++;
                    continue;
                }
                if (!char.IsAsciiLetterOrDigit(current) && current != '-' && current != '_')
                    return false;
            }
            return dots == 2;
        }
    }

    public sealed class GameChatBridgeClient : IDisposable
    {
        private const string Tag = "GameChatBridge";
        private const int MaxFrameBytes = 65536;
        private const int MaxHistoryMessages = 200;
        private const int MaxDisplayLength = 500;
        public const int MaxSendLength = 300;
        private const int SendBurst = 4;
        private const int SendWindowMs = 6000;
        private const int MinSendGapMs = 400;
        private const int KeepAliveMs = 30000;
        private const int MaxConsecutiveFailures = 8;
        private const int MaxBackoffMs = 60000;

        private const int MaxSeenMessages = 512;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly Queue<long> _sendTimes = new Queue<long>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly Queue<int> _seenOrder = new Queue<int>();

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private ClientWebSocket? _socket;
        private Task? _loop;
        private int _generation;
        private bool _disposed;
        private volatile bool _connected;
        private long _lastSendMs;

        public string RoomId { get; private set; } = "";
        public string Name { get; private set; } = "";
        public long UserId { get; private set; }
        public bool Connected => _connected;

        private volatile string _votekickTarget = "";

        public string ActiveVotekickTarget => _votekickTarget;

        public event EventHandler<GameChatBridgeMessage>? OnMessage;
        public event EventHandler<string>? OnSystem;
        public event EventHandler? OnVerificationRequired;

        private void Emit(string text) => OnSystem?.Invoke(this, text);

        private bool IsCurrent(int generation, CancellationToken token)
        {
            return !_disposed && !token.IsCancellationRequested && generation == Volatile.Read(ref _generation);
        }

        public static bool IsJoinableServer(string? jobId)
        {
            return !string.IsNullOrEmpty(jobId) && jobId!.Length == 36 && Guid.TryParseExact(jobId, "D", out _);
        }

        public void Start(string jobId)
        {
            if (_disposed || !IsJoinableServer(jobId))
                return;

            lock (_sync)
            {
                if (_loop != null && !_loop.IsCompleted)
                    return;
                int generation = ++_generation;
                CancellationToken token = _cts.Token;
                _loop = Task.Run(() => RunAsync(jobId, generation, token), CancellationToken.None);
            }
        }

        public void Stop()
        {
            if (_disposed)
                return;

            CancellationTokenSource old;
            lock (_sync)
            {
                _generation++;
                old = _cts;
                _cts = new CancellationTokenSource();
                _socket = null;
                _loop = null;
                _connected = false;
                RoomId = "";
                Name = "";
                UserId = 0;
                _sendTimes.Clear();
                _seen.Clear();
                _seenOrder.Clear();
            }

            _votekickTarget = "";

            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            old.Dispose();
        }

        private async Task RunAsync(string jobId, int generation, CancellationToken token)
        {
            int failures = 0;
            int delayMs = 3000;

            while (!token.IsCancellationRequested && generation == Volatile.Read(ref _generation))
            {
                bool available;
                try
                {
                    available = await GameChatBridgeConfig.IsEnabledAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!IsCurrent(generation, token))
                    return;

                if (!available)
                {
                    Emit(GameChatStrings.BridgeUnavailable);
                    return;
                }

                string? sessionToken = GameChatBridgeAuth.GetToken();
                if (string.IsNullOrEmpty(sessionToken))
                {
                    OnVerificationRequired?.Invoke(this, EventArgs.Empty);
                    return;
                }

                string wsToken = "";
                try
                {
                    wsToken = await HandshakeAsync(jobId, sessionToken!, generation, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(Tag, "Handshake failed: " + ex.Message);
                }

                if (token.IsCancellationRequested || generation != Volatile.Read(ref _generation))
                    return;

                if (wsToken.Length > 0)
                {
                    bool retry = await PumpAsync(wsToken, generation, token).ConfigureAwait(false);
                    if (!retry || token.IsCancellationRequested || generation != Volatile.Read(ref _generation))
                        return;
                    failures = 0;
                    delayMs = 3000;
                }
                else
                {
                    failures++;
                    if (failures >= MaxConsecutiveFailures)
                    {
                        Emit(GameChatStrings.BridgeGaveUp);
                        return;
                    }
                }

                try
                {
                    await Task.Delay(delayMs + Random.Shared.Next(250, 1500), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delayMs = Math.Min(MaxBackoffMs, delayMs * 2);
            }
        }

        private async Task<string> HandshakeAsync(string jobId, string sessionToken, int generation, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GameChatBridgeTransport.Api("/api/roblox/chat/" + Uri.EscapeDataString(jobId)));
            request.Content = GameChatBridgeTransport.JsonBody(new { session_token = sessionToken });

            using GameChatBridgeResponse response = await GameChatBridgeTransport.SendAsync(request, token).ConfigureAwait(false);
            if (!IsCurrent(generation, token))
                return "";

            JsonElement root = response.Root;

            if (root.ValueKind != JsonValueKind.Object)
                return "";

            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.True)
            {
                if (response.Status == HttpStatusCode.Unauthorized)
                {
                    GameChatBridgeAuth.Clear();
                    OnVerificationRequired?.Invoke(this, EventArgs.Empty);
                    return "";
                }
                string reason = GameChatBridgeTransport.ReadString(root, "reason", 120);
                Emit(reason.Length == 0 ? GameChatStrings.BridgeJoinFailed : reason);
                return "";
            }

            string roomId = GameChatBridgeTransport.ReadString(root, "room_id", 64);
            string name = GameChatBridgeText.CleanName(GameChatBridgeTransport.ReadString(root, "name", 40));
            long userId = GameChatBridgeTransport.ReadLong(root, "user_id");

            if (!IsHexRoom(roomId) || userId <= 0)
            {
                Emit(GameChatStrings.BridgeJoinFailed);
                return "";
            }

            if (!root.TryGetProperty("ws_token", out JsonElement wsTokenElement) || wsTokenElement.ValueKind != JsonValueKind.String)
            {
                Emit(GameChatStrings.BridgeJoinFailed);
                return "";
            }

            string wsToken = wsTokenElement.GetString() ?? "";
            if (wsToken.Length == 0 || wsToken.Length > 1024)
            {
                Emit(GameChatStrings.BridgeJoinFailed);
                return "";
            }

            RoomId = roomId;
            Name = name;
            UserId = userId;
            return wsToken;
        }

        private async Task<bool> PumpAsync(string wsToken, int generation, CancellationToken token)
        {
            var url = new Uri(
                "wss://" + GameChatBridgeTransport.Host + "/api/roblox/chat/room/" + RoomId
                + "?token=" + Uri.EscapeDataString(wsToken)
                + "&user_id=" + UserId.ToString(CultureInfo.InvariantCulture)
                + "&name=" + Uri.EscapeDataString(Name),
                UriKind.Absolute);

            if (!string.Equals(url.Host, GameChatBridgeTransport.Host, StringComparison.OrdinalIgnoreCase) || url.Scheme != "wss")
                return false;
            if (!GameChatBridgeConfig.KnownEnabled)
                return false;

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            try
            {
                await socket.ConnectAsync(url, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Socket connect failed: " + ex.Message);
                return true;
            }

            lock (_sync)
            {
                if (generation != _generation)
                    return false;
                _socket = socket;
                _connected = true;
            }

            Emit(GameChatStrings.BridgeConnected);

            using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task pinger = KeepAliveAsync(socket, keepAlive.Token);
            bool retry = true;

            try
            {
                retry = await ReadAsync(socket, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                retry = false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Socket read failed: " + ex.Message);
            }
            finally
            {
                keepAlive.Cancel();
                try
                {
                    await pinger.ConfigureAwait(false);
                }
                catch
                {
                }

                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                        _connected = false;
                    }
                }

                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return retry;
        }

        private async Task KeepAliveAsync(ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(KeepAliveMs, token).ConfigureAwait(false);
                    if (socket.State != WebSocketState.Open)
                        return;
                    await SendRawAsync(socket, new { type = "ping" }, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Keep alive stopped: " + ex.Message);
            }
        }

        private async Task<bool> ReadAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[8192];

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream(1024);
                WebSocketReceiveResult result;
                bool oversized = false;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return HandleClose(socket);
                    if (result.MessageType != WebSocketMessageType.Text)
                        break;
                    if (frame.Length + result.Count > MaxFrameBytes)
                    {
                        oversized = true;
                        break;
                    }
                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (oversized)
                    return true;
                if (result.MessageType != WebSocketMessageType.Text || frame.Length == 0)
                    continue;

                frame.Position = 0;
                try
                {
                    using JsonDocument document = await JsonDocument.ParseAsync(frame, default, token).ConfigureAwait(false);
                    Dispatch(document.RootElement, 0);
                }
                catch (JsonException)
                {
                }
            }

            return true;
        }

        private bool HandleClose(ClientWebSocket socket)
        {
            int code = socket.CloseStatus.HasValue ? (int)socket.CloseStatus.Value : 0;

            switch (code)
            {
                case 4004:
                    Emit(GameChatStrings.BridgeRoomMissing);
                    return true;
                case 4005:
                    Emit(GameChatStrings.BridgeRoomFull);
                    return true;
                case 4010:
                    Emit(GameChatStrings.BridgeKicked);
                    return false;
                case 4011:
                    Emit(GameChatStrings.BridgeKickCooldown);
                    return false;
                default:
                    return true;
            }
        }

        private void Dispatch(JsonElement root, int depth)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return;

            string type = GameChatBridgeTransport.ReadString(root, "type", 24);

            switch (type)
            {
                case "history":
                {
                    if (depth > 0 || !root.TryGetProperty("messages", out JsonElement history) || history.ValueKind != JsonValueKind.Array)
                        return;
                    int count = 0;
                    foreach (JsonElement item in history.EnumerateArray())
                    {
                        if (count++ >= MaxHistoryMessages)
                            break;
                        Dispatch(item, depth + 1);
                    }
                    return;
                }

                case "chat":
                {
                    string sender = GameChatBridgeText.CleanName(GameChatBridgeTransport.ReadString(root, "name", 40));
                    string body = GameChatBridgeTransport.ReadString(root, "text", MaxDisplayLength);
                    long senderId = GameChatBridgeTransport.ReadLong(root, "user_id");
                    double stamp = GameChatBridgeTransport.ReadDouble(root, "ts");

                    if (!MarkSeen(HashCode.Combine(senderId, stamp, body)))
                        return;

                    Raise(new GameChatBridgeMessage
                    {
                        Kind = "chat",
                        Sender = sender,
                        Text = body,
                        SenderId = senderId,
                        Verified = GameChatBridgeTransport.ReadBool(root, "verified"),
                    });
                    return;
                }

                case "system":
                case "error":
                    Raise(new GameChatBridgeMessage
                    {
                        Kind = "system",
                        Text = GameChatBridgeTransport.ReadString(root, "text", MaxDisplayLength),
                    });
                    return;

                case "votekick_init":
                {
                    string initiator = GameChatBridgeText.CleanName(GameChatBridgeTransport.ReadString(root, "initiator", 40));
                    string target = GameChatBridgeText.CleanName(GameChatBridgeTransport.ReadString(root, "target", 40));
                    string reason = GameChatBridgeTransport.ReadString(root, "reason", 200);
                    long votes = GameChatBridgeTransport.ReadLong(root, "votes");
                    long needed = GameChatBridgeTransport.ReadLong(root, "needed");

                    _votekickTarget = target;

                    string text = string.Format(GameChatStrings.BridgeVotekickStarted, initiator, target, votes, needed);
                    if (reason.Length > 0)
                        text += " " + string.Format(GameChatStrings.BridgeVotekickReason, reason);

                    Raise(new GameChatBridgeMessage { Kind = "system", Text = text });
                    return;
                }

                case "votekick_update":
                {
                    string target = GameChatBridgeText.CleanName(GameChatBridgeTransport.ReadString(root, "target", 40));
                    _votekickTarget = target;
                    Raise(new GameChatBridgeMessage
                    {
                        Kind = "system",
                        Text = string.Format(
                            GameChatStrings.BridgeVotekickProgress,
                            target,
                            GameChatBridgeTransport.ReadLong(root, "votes"),
                            GameChatBridgeTransport.ReadLong(root, "needed")),
                    });
                    return;
                }

                case "votekick_result":
                {
                    string target = GameChatBridgeText.CleanName(GameChatBridgeTransport.ReadString(root, "target", 40));
                    string outcome = GameChatBridgeTransport.ReadString(root, "result", 24);
                    _votekickTarget = "";
                    Raise(new GameChatBridgeMessage
                    {
                        Kind = "system",
                        Text = outcome == "kicked"
                            ? string.Format(GameChatStrings.BridgeVotekickPassed, target)
                            : string.Format(GameChatStrings.BridgeVotekickExpired, target),
                    });
                    return;
                }
            }
        }

        private void Raise(GameChatBridgeMessage message)
        {
            if (message.Text.Length == 0)
                return;
            OnMessage?.Invoke(this, message);
        }

        private bool MarkSeen(int key)
        {
            lock (_sync)
            {
                if (!_seen.Add(key))
                    return false;
                _seenOrder.Enqueue(key);
                while (_seenOrder.Count > MaxSeenMessages)
                    _seen.Remove(_seenOrder.Dequeue());
                return true;
            }
        }

        private bool TryTakeSendSlot()
        {
            long now = Environment.TickCount64;

            lock (_sync)
            {
                if (now - _lastSendMs < MinSendGapMs)
                    return false;
                while (_sendTimes.Count > 0 && now - _sendTimes.Peek() > SendWindowMs)
                    _sendTimes.Dequeue();
                if (_sendTimes.Count >= SendBurst)
                    return false;
                _sendTimes.Enqueue(now);
                _lastSendMs = now;
                return true;
            }
        }

        public Task<bool> SendMessageAsync(string text)
        {
            string clean = GameChatBridgeText.Clean(text, MaxSendLength);
            if (clean.Length == 0)
                return Task.FromResult(true);
            return SendPayloadAsync(new { type = "text", text = clean });
        }

        public Task<bool> SendVotekickAsync(string targetName, string reason, bool voteOnly)
        {
            string target = GameChatBridgeText.Clean(targetName, 40);
            if (target.Length == 0)
                return Task.FromResult(false);

            object payload = voteOnly
                ? new { type = "votekick_vote", target_name = target }
                : new { type = "votekick_init", target_name = target, reason = GameChatBridgeText.Clean(reason, 200) };

            return SendPayloadAsync(payload);
        }

        private async Task<bool> SendPayloadAsync(object payload)
        {
            ClientWebSocket? socket;
            CancellationToken token;

            lock (_sync)
            {
                socket = _socket;
                token = _cts.Token;
            }

            if (socket == null || socket.State != WebSocketState.Open)
            {
                Emit(GameChatStrings.BridgeNotConnected);
                return false;
            }

            if (!TryTakeSendSlot())
            {
                Emit(GameChatStrings.BridgeRateLimited);
                return false;
            }

            try
            {
                return await SendRawAsync(socket, payload, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Send failed: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> SendRawAsync(ClientWebSocket socket, object payload, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                    return false;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                return true;
            }
            catch (WebSocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static bool IsHexRoom(string value)
        {
            if (value.Length != 64)
                return false;
            foreach (char current in value)
            {
                if (!char.IsAsciiHexDigitLower(current))
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            CancellationTokenSource cts;
            lock (_sync)
            {
                _generation++;
                cts = _cts;
                _socket = null;
                _loop = null;
                _connected = false;
                _sendTimes.Clear();
                _seen.Clear();
                _seenOrder.Clear();
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            cts.Dispose();

            OnMessage = null;
            OnSystem = null;
            OnVerificationRequired = null;
            _sendLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
