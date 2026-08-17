using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.GameChat
{
    public class GameChatBadge
    {
        public string Id = "";
        public string Name = "";
        public string Image = "";
    }

    public sealed class GameChatBadgeUpdate
    {
        public long UserId;
        public IReadOnlyList<GameChatBadge> Badges = [];
    }

    public class GameChatMessage
    {
        public long Id;
        public long SenderId;
        public string Type = "";
        public string Sender = "";
        public string Target = "";
        public string Text = "";
        public bool IsTo;
        public bool Verified;
        public List<GameChatBadge> Badges = [];
        public bool IsBroadcast;
        public string? Color;
        public JsonElement Scores;
        public bool HasScores;
    }

    public class GameChatRejection
    {
        public string Reason = "";
        public string Target = "";
    }

    public enum GameChatBugResult
    {
        Ok,
        RateLimited,
        NotConnected,
        Failed
    }

    public class GameChatClient : IDisposable
    {
        private const string Tag = "GameChatClient";
        private static string BaseUrl => App.WebsiteBaseUrl + "/api/chat";

        private CancellationTokenSource _cts = new();
        private string? _token;
        private long _since;
        private readonly HashSet<long> _seen = [];
        private readonly Queue<long> _seenOrder = new();
        private readonly Lock _sync = new();
        private readonly Lock _resetLock = new();
        private readonly SemaphoreSlim _socketSendLock = new(1, 1);
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private ClientWebSocket? _socket;
        private List<GameChatBadge> _ownBadges = [];
        private readonly ConcurrentDictionary<long, byte> _badgeLookups = new();
        private readonly Dictionary<long, (IReadOnlyList<GameChatBadge> Badges, long Expires)> _knownBadges = [];
        private int _connectionGeneration;
        private Task? _receiveTask;
        private bool _disposed;

        private const int MinPollMs = 5000;
        private const int MaxPollMs = 60000;
        private const int HiddenPollMs = 60000;
        private const int MaxIncomingMessageBytes = 1024 * 1024;
        private const int MaxHttpResponseBytes = 1024 * 1024;
        private const int MaxBadgeImageCharacters = 256 * 1024;
        private const int MaxBadgeUsers = 128;
        private const long BadgeCacheMilliseconds = 120000;

        public string ChannelId { get; set; } = "global";
        public string Name { get; private set; } = "";
        public long OwnAccountId { get; private set; }
        public long OwnRobloxId { get; set; }
        public bool Verified { get; private set; }
        public bool Connected => _token != null;
        public bool SlowPoll { get; set; }

        public event EventHandler<string>? OnSystemMessage;
        public event EventHandler<GameChatMessage>? OnMessage;
        public event EventHandler<GameChatRejection>? OnRejected;
        public event EventHandler<GameChatBadgeUpdate>? OnBadgesUpdated;

        private void EmitSystem(string text)
        {
            OnSystemMessage?.Invoke(this, text);
        }

        private static StringContent JsonBody(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        private async Task ConnectAsync(CancellationToken ct, bool announce = true)
        {
            if (_disposed)
                return;
            int generation = Volatile.Read(ref _connectionGeneration);
            int maxRetries = 12;
            int delayMilliseconds = 5000;

            for (int i = 1; i <= maxRetries; i++)
            {
                if (ct.IsCancellationRequested)
                    return;

                try
                {
                    _token = null;
                    if (announce)
                        EmitSystem(string.Format(GameChatStrings.ConnectingToServer, ChannelId));

                    using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                    string? accountToken = Utility.WebsiteAuth.GetToken();
                    if (!string.IsNullOrEmpty(accountToken))
                        request.Headers.Add("Authorization", "Bearer " + accountToken);
                    request.Content = JsonBody(new { action = "join", channelId = ChannelId });
                    using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        _token = null;
                        string banMsg = GameChatStrings.BannedFromChat;
                        try
                        {
                            using var banDoc = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxHttpResponseBytes, ct).ConfigureAwait(false));
                            if (banDoc.RootElement.TryGetProperty("error", out var be) && be.ValueKind == JsonValueKind.String)
                                banMsg = be.GetString() ?? banMsg;
                        }
                        catch
                        {
                        }
                        EmitSystem(banMsg);
                        return;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)response.StatusCode);

                    using var doc = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxHttpResponseBytes, ct).ConfigureAwait(false));
                    var root = doc.RootElement;
                    string? token = root.GetProperty("token").GetString();
                    if (ct.IsCancellationRequested || generation != Volatile.Read(ref _connectionGeneration))
                        return;
                    _token = token;
                    Name = root.GetProperty("name").GetString() ?? "";
                    OwnAccountId = root.TryGetProperty("robloxId", out var rid) && rid.ValueKind == JsonValueKind.Number ? rid.GetInt64() : 0;
                    if (OwnAccountId > 0 && OwnAccountId < GameChatRoblox.LocalAccountIdBase)
                        OwnRobloxId = OwnAccountId;
                    Verified = root.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
                    _ownBadges = ReadBadges(root);
                    if (OwnAccountId > 0)
                        StoreKnownBadges(OwnAccountId, _ownBadges);
                    SaveOwnIdentity();
                    if (OwnAccountId > 0)
                        _ = ResolveOwnIdentityAsync(OwnAccountId, generation, ct);
                    _since = root.TryGetProperty("serial", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    if (ct.IsCancellationRequested || generation != Volatile.Read(ref _connectionGeneration))
                    {
                        _token = null;
                        return;
                    }
                    lock (_sync)
                    {
                        _seen.Clear();
                        _seenOrder.Clear();
                    }

                    if (announce)
                        EmitSystem(GameChatStrings.ConnectedSuccessfully);

                    _receiveTask = ReceiveLoopAsync(ct);
                    return;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    if (i == maxRetries)
                    {
                        EmitSystem(string.Format(GameChatStrings.ConnectionFailed, ex.Message));
                    }
                    else
                    {
                        try
                        {
                            await Task.Delay(delayMilliseconds, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        public Task RestartAsync(bool announce = true)
        {
            if (_disposed)
                return Task.CompletedTask;
            return Task.Run(() => RestartCoreAsync(announce));
        }

        private async Task RestartCoreAsync(bool announce)
        {
            if (_disposed)
                return;
			(int generation, CancellationToken token) = ResetConnection();
            try
            {
                await _connectionLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_disposed || generation != Volatile.Read(ref _connectionGeneration))
                        return;
                    await ConnectAsync(token, announce).ConfigureAwait(false);
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Stop()
        {
            if (_disposed)
                return;
			ResetConnection();
		}

        private (int Generation, CancellationToken Token) ResetConnection()
		{
            lock (_resetLock)
            {
                if (_disposed)
                    return (Volatile.Read(ref _connectionGeneration), new CancellationToken(true));
                CancellationTokenSource old;
				CancellationToken token;
				int generation;
                lock (_sync)
                {
					generation = Interlocked.Increment(ref _connectionGeneration);
                    old = _cts;
                    _cts = new CancellationTokenSource();
					token = _cts.Token;
                    _socket = null;
                    _receiveTask = null;
                }
                try
                {
                    old.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                old.Dispose();
            _token = null;
            _ownBadges = [];
            OwnAccountId = 0;
            OwnRobloxId = 0;
				return (generation, token);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            int retryDelay = MinPollMs;
            try
            {
                while (!ct.IsCancellationRequested && _token != null)
                {
                    ClientWebSocket? connectedSocket = null;
                    try
                    {
                        using var socket = new ClientWebSocket();
                        socket.Options.SetRequestHeader("x-chat-token", _token);
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                        string socketUrl = BaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase) + "/socket";
                        await socket.ConnectAsync(new Uri(socketUrl), ct);
                        connectedSocket = socket;
                        lock (_sync)
                            _socket = socket;
                        retryDelay = MinPollMs;
                        await ReadSocketAsync(socket, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(Tag, "Live chat connection error: " + ex.Message);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_socket, connectedSocket))
                                _socket = null;
                        }
                    }
                    if (ct.IsCancellationRequested || _token == null)
                        return;
                    await PollOnceAsync(ct);
                    await Task.Delay(SlowPoll ? HiddenPollMs : retryDelay, ct);
                    retryDelay = Math.Min(MaxPollMs, retryDelay * 2);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                EmitSystem(string.Format(GameChatStrings.ReceiveError, ex.Message));
            }
        }

        private async Task ReadSocketAsync(ClientWebSocket socket, CancellationToken ct)
        {
            byte[] buffer = new byte[8192];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (result.MessageType != WebSocketMessageType.Text)
                        break;
                    if (message.Length + result.Count > MaxIncomingMessageBytes)
                        throw new InvalidDataException("Chat message exceeded the size limit");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text || message.Length == 0)
                    continue;
                message.Position = 0;
                using var doc = await JsonDocument.ParseAsync(message, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var eventName) && eventName.GetString() == "rejected")
                {
                    OnRejected?.Invoke(this, new GameChatRejection
                    {
                        Reason = ReadString(root, "reason", 64),
                        Target = ReadString(root, "target", 64),
                    });
                    continue;
                }
                DispatchMessage(root);
            }
        }

        private async Task<bool> SendSocketAsync(object payload, CancellationToken ct)
        {
            ClientWebSocket? socket;
            lock (_sync)
                socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
                return false;
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _socketSendLock.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open)
                    return false;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
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
                _socketSendLock.Release();
            }
        }

        private async Task PollOnceAsync(CancellationToken ct)
        {
            string? token = _token;
            if (token == null)
                return;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/poll?since=" + _since);
                request.Headers.Add("x-chat-token", token);
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _ = RestartAsync(false);
                    return;
                }
                if (!response.IsSuccessStatusCode)
                    return;
                using var doc = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxHttpResponseBytes, ct).ConfigureAwait(false));
                var root = doc.RootElement;
                if (root.TryGetProperty("serial", out var serial) && serial.ValueKind == JsonValueKind.Number)
                    _since = Math.Max(_since, serial.GetInt64());
                if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var item in messages.EnumerateArray())
                    DispatchMessage(item);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Chat recovery error: " + ex.Message);
            }
        }

        private void DispatchMessage(JsonElement m)
        {
            long id = m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
            if (id <= 0)
                return;
            _since = Math.Max(_since, id);
            if (!MarkSeen(id))
                return;

            var msg = new GameChatMessage
            {
                Id = id,
                SenderId = m.TryGetProperty("senderId", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt64() : 0,
                Type = ReadString(m, "type", 24),
                Sender = ReadString(m, "sender", 64),
                Target = ReadString(m, "target", 64),
                Text = ReadString(m, "text", 4000),
                IsTo = m.TryGetProperty("isTo", out var it) && it.ValueKind == JsonValueKind.True,
                Verified = m.TryGetProperty("verified", out var vf) && vf.ValueKind == JsonValueKind.True,
                IsBroadcast = m.TryGetProperty("isBroadcast", out var bc) && bc.ValueKind == JsonValueKind.True,
                Color = m.TryGetProperty("color", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString() : null,
                Badges = ReadBadges(m),
            };
            if (msg.Badges.Count == 0 && msg.SenderId > 0 && msg.SenderId == OwnAccountId)
                msg.Badges.AddRange(_ownBadges);
            if (msg.SenderId > 0)
            {
                if (msg.Badges.Count > 0)
                    StoreKnownBadges(msg.SenderId, msg.Badges);
                else if (TryGetKnownBadges(msg.SenderId, out IReadOnlyList<GameChatBadge>? known))
                    msg.Badges.AddRange(known);
            }
            if (msg.Type == "whisper" && msg.Sender == Name)
                msg.IsTo = true;
            if (m.TryGetProperty("attributeScores", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                msg.Scores = sc.Clone();
                msg.HasScores = true;
            }
            OnMessage?.Invoke(this, msg);
            if (msg.SenderId > 0)
                QueueBadgeLookup(msg.SenderId);
        }

        private async Task ResolveOwnIdentityAsync(long accountId, int generation, CancellationToken token)
        {
            try
            {
                GameChatIdentity? identity = await GameChatRoblox.GetChatIdentityAsync(accountId, token).ConfigureAwait(false);
                if (_disposed || token.IsCancellationRequested || generation != Volatile.Read(ref _connectionGeneration) || identity == null)
                    return;
                OwnRobloxId = identity.RobloxId;
                SaveOwnIdentity();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void SaveOwnIdentity()
        {
            bool accountVerified = Verified && OwnRobloxId > 0;
            if (App.Settings.Prop.GameChatVerified == accountVerified && App.Settings.Prop.GameChatRobloxUserId == (accountVerified ? OwnRobloxId : 0))
                return;
            App.Settings.Prop.GameChatVerified = accountVerified;
            App.Settings.Prop.GameChatRobloxUserId = accountVerified ? OwnRobloxId : 0;
            App.Settings.SaveDeferred();
        }

        private void QueueBadgeLookup(long userId)
        {
            if (_disposed || userId <= 0 || _badgeLookups.Count >= MaxBadgeUsers || !_badgeLookups.TryAdd(userId, 0))
                return;
            CancellationToken token;
            int generation = Volatile.Read(ref _connectionGeneration);
            lock (_sync)
                token = _cts.Token;
            _ = ResolveBadgesAsync(userId, generation, token);
        }

        private async Task ResolveBadgesAsync(long userId, int generation, CancellationToken token)
        {
            try
            {
                GameChatIdentity? identity = await GameChatRoblox.GetChatIdentityAsync(userId, token).ConfigureAwait(false);
                if (_disposed || token.IsCancellationRequested || generation != Volatile.Read(ref _connectionGeneration) || identity == null)
                    return;
                IReadOnlyList<GameChatBadge> badges = identity.Badges;
                StoreKnownBadges(userId, badges);
                OnBadgesUpdated?.Invoke(this, new GameChatBadgeUpdate { UserId = userId, Badges = badges });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Badge lookup failed: " + ex.Message);
            }
            finally
            {
                _badgeLookups.TryRemove(userId, out _);
            }
        }

        private bool TryGetKnownBadges(long userId, out IReadOnlyList<GameChatBadge> badges)
        {
            lock (_sync)
            {
                if (_knownBadges.TryGetValue(userId, out var entry) && entry.Expires > Environment.TickCount64)
                {
                    badges = entry.Badges;
                    return true;
                }
                _knownBadges.Remove(userId);
            }
            badges = [];
            return false;
        }

        private void StoreKnownBadges(long userId, IReadOnlyList<GameChatBadge> badges)
        {
            if (userId <= 0)
                return;
            lock (_sync)
            {
                if (_knownBadges.Count >= MaxBadgeUsers && !_knownBadges.ContainsKey(userId))
                {
                    long oldestId = 0;
                    long oldestExpiry = long.MaxValue;
                    foreach (var pair in _knownBadges)
                    {
                        if (pair.Value.Expires >= oldestExpiry)
                            continue;
                        oldestId = pair.Key;
                        oldestExpiry = pair.Value.Expires;
                    }
                    if (oldestId > 0)
                        _knownBadges.Remove(oldestId);
                }
                _knownBadges[userId] = (CopyBadges(badges), Environment.TickCount64 + BadgeCacheMilliseconds);
            }
        }

        private static List<GameChatBadge> CopyBadges(IReadOnlyList<GameChatBadge> badges)
        {
            if (badges.Count == 0)
                return [];
            var copy = new List<GameChatBadge>(Math.Min(6, badges.Count));
            for (int i = 0; i < badges.Count && copy.Count < 6; i++)
            {
                GameChatBadge badge = badges[i];
                if (!string.IsNullOrWhiteSpace(badge.Name) && !string.IsNullOrWhiteSpace(badge.Image))
                    copy.Add(new GameChatBadge { Id = badge.Id, Name = badge.Name, Image = badge.Image });
            }
            return copy;
        }

        private static List<GameChatBadge> ReadBadges(JsonElement container)
        {
            var result = new List<GameChatBadge>();
            if (!container.TryGetProperty("badges", out var badges) || badges.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var badge in badges.EnumerateArray())
            {
                if (result.Count >= 6)
                    break;
                string name = ReadString(badge, "name", 32);
                string id = ReadString(badge, "id", 32);
                string image = ReadBadgeImage(badge);
                if (image.Length > MaxBadgeImageCharacters)
                    continue;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(image))
                    result.Add(new GameChatBadge { Id = id, Name = name, Image = image });
            }
            return result;
        }

        private static string ReadBadgeImage(JsonElement badge)
        {
            string[] names = ["image", "imageUrl", "icon", "url"];
            foreach (string name in names)
            {
                if (badge.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
            return "";
        }

        private bool MarkSeen(long id)
        {
            lock (_sync)
            {
                if (!_seen.Add(id))
                    return false;
                _seenOrder.Enqueue(id);
                while (_seenOrder.Count > 2000)
                    _seen.Remove(_seenOrder.Dequeue());
                return true;
            }
        }

        private static string ReadString(JsonElement element, string name, int maxLength)
        {
            string value = element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? ""
                : "";
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private async Task<bool> HandleSendResponse(HttpResponseMessage response, string localEchoType, string target, string text, CancellationToken token)
        {
            string body = await Utility.Http.ReadStringBoundedAsync(response.Content, MaxHttpResponseBytes, token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "rejected")
            {
                var rejection = new GameChatRejection
                {
                    Reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    Target = root.TryGetProperty("target", out var tg) ? tg.GetString() ?? "" : "",
                };
                OnRejected?.Invoke(this, rejection);
                return false;
            }

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                EmitSystem(GameChatStrings.NotConnected);
                return false;
            }

            long id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
            string echoText = root.TryGetProperty("text", out var txEl) && txEl.ValueKind == JsonValueKind.String ? (txEl.GetString() ?? text) : text;
            var msg = new GameChatMessage
            {
                Id = id,
                SenderId = OwnAccountId > 0 ? OwnAccountId : OwnRobloxId,
                Type = localEchoType,
                Sender = Name,
                Target = target,
                Text = echoText,
                IsTo = localEchoType == "whisper",
                Verified = Verified,
            };
            msg.Badges.AddRange(_ownBadges);
            if (root.TryGetProperty("attributeScores", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                msg.Scores = sc.Clone();
                msg.HasScores = true;
            }
            if (id > 0)
                MarkSeen(id);
            _since = Math.Max(_since, id);
            OnMessage?.Invoke(this, msg);
            return true;
        }

        public async Task SendMessageAsync(string text)
        {
            if (_token == null)
            {
                EmitSystem(GameChatStrings.NotConnected);
                return;
            }

            try
            {
                if (await SendSocketAsync(new { action = "message", text }, _cts.Token))
                    return;
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Add("x-chat-token", _token);
                request.Content = JsonBody(new { action = "message", text });
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    EmitSystem(GameChatStrings.NotConnected);
                    return;
                }
                await HandleSendResponse(response, "message", "", text, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                EmitSystem(GameChatStrings.SendTimedOut);
            }
            catch (Exception ex)
            {
                EmitSystem(string.Format(GameChatStrings.SendError, ex.Message));
            }
        }

        public async Task SendWhisperAsync(string target, string text)
        {
            if (_token == null)
            {
                EmitSystem(GameChatStrings.NotConnected);
                return;
            }

            try
            {
                if (await SendSocketAsync(new { action = "whisper", target, text }, _cts.Token))
                    return;
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Add("x-chat-token", _token);
                request.Content = JsonBody(new { action = "whisper", target, text });
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    EmitSystem(GameChatStrings.NotConnected);
                    return;
                }
                await HandleSendResponse(response, "whisper", target, text, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                EmitSystem(GameChatStrings.SendTimedOut);
            }
            catch (Exception ex)
            {
                EmitSystem(string.Format(GameChatStrings.SendError, ex.Message));
            }
        }

        public async Task SendEchoAsync(string text)
        {
            if (_token == null)
            {
                EmitSystem(GameChatStrings.NotConnected);
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Add("x-chat-token", _token);
                request.Content = JsonBody(new { action = "echo", text });
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    EmitSystem(GameChatStrings.NotConnected);
                    return;
                }
                string body = await Utility.Http.ReadStringBoundedAsync(response.Content, MaxHttpResponseBytes, _cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var status) && status.GetString() == "rejected")
                {
                    string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                    string messageText = reason switch
                    {
                        "moderation" => GameChatStrings.MessageRejectedModeration,
                        "queue_full" => GameChatStrings.MessageRejectedQueueFull,
                        "api_error" => GameChatStrings.MessageRejectedApiError,
                        _ => GameChatStrings.MessageRejectedUnknown,
                    };
                    EmitSystem(messageText);
                    return;
                }

                if (root.TryGetProperty("text", out var echoed))
                {
                    EmitSystem(string.Format(GameChatStrings.EchoResponse, echoed.GetString() ?? ""));
                }
            }
            catch (TaskCanceledException)
            {
                EmitSystem(GameChatStrings.RequestTimedOut);
            }
            catch (Exception ex)
            {
                EmitSystem(string.Format(GameChatStrings.ConnectionError, ex.Message));
            }
        }

        public async Task<GameChatBugResult> SendBugAsync(string text)
        {
            if (_token == null)
                return GameChatBugResult.NotConnected;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Add("x-chat-token", _token);
                request.Content = JsonBody(new { action = "bug", text });
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    return GameChatBugResult.RateLimited;
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return GameChatBugResult.NotConnected;
                if (!response.IsSuccessStatusCode)
                    return GameChatBugResult.Failed;
                return GameChatBugResult.Ok;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Bug report failed: " + ex.Message);
                return GameChatBugResult.Failed;
            }
        }

        public async Task<GameChatBugResult> SendReportAsync(string target, long targetId, string text)
        {
            if (_token == null)
                return GameChatBugResult.NotConnected;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
                request.Headers.Add("x-chat-token", _token);
                request.Content = JsonBody(new { action = "report", target, targetId, text });
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    return GameChatBugResult.RateLimited;
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return GameChatBugResult.NotConnected;
                if (!response.IsSuccessStatusCode)
                    return GameChatBugResult.Failed;
                return GameChatBugResult.Ok;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Report failed: " + ex.Message);
                return GameChatBugResult.Failed;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            lock (_resetLock)
            {
                CancellationTokenSource cts;
                lock (_sync)
                {
                    cts = _cts;
                    _token = null;
                    _socket = null;
                    _receiveTask = null;
                    _seen.Clear();
                    _seenOrder.Clear();
                    _knownBadges.Clear();
                }
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                cts.Dispose();
            }
            OnSystemMessage = null;
            OnMessage = null;
            OnRejected = null;
            OnBadgesUpdated = null;
            GC.SuppressFinalize(this);
        }
    }
}
