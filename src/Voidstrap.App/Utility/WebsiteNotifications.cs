using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IO.Ably;
using IO.Ably.Realtime;

namespace Voidstrap.Utility
{
    public sealed class WebsiteNotification
    {
        public string Id { get; set; } = "";
        public long Timestamp { get; set; }
        public bool Read { get; set; }
        public string Type { get; set; } = "";
        public string FromId { get; set; } = "";
        public string FromName { get; set; } = "";
        public string FromAvatar { get; set; } = "";
        public string Image { get; set; } = "";
        public string Target { get; set; } = "";
        public string Text { get; set; } = "";
        public string EquippedBorderJson { get; set; } = "";
    }

    public static class WebsiteNotifications
    {
        private const int MaxPayloadLength = 2_000_000;

        public static event Action<int>? UnreadChanged;

        public static async Task<(bool Ok, List<WebsiteNotification> Items, int Unread, string? Error)> GetAsync(CancellationToken cancellationToken = default)
        {
            List<WebsiteNotification> items = new List<WebsiteNotification>();
            string activeAccount = WebsiteAuth.GetActiveId() ?? "";
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token))
                    return (false, items, 0, "You are not signed in.");

                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + "/api/notifications");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return (false, items, 0, "Your session expired. Sign in again.");
                if (!response.IsSuccessStatusCode)
                    return (false, items, 0, "Could not load notifications, HTTP " + (int)response.StatusCode + ".");
                if (response.Content.Headers.ContentLength is long length && length > MaxPayloadLength)
                    return (false, items, 0, "The notification response was too large.");

                string json = await Http.ReadStringBoundedAsync(response.Content, MaxPayloadLength, cancellationToken).ConfigureAwait(false);

                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                JsonElement root = document.RootElement;
                int unread = Number(root, "unread");
                if (root.TryGetProperty("items", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement value in array.EnumerateArray())
                    {
                        if (items.Count >= 100 || value.ValueKind != JsonValueKind.Object)
                            break;
                        string id = String(value, "id");
                        if (string.IsNullOrEmpty(id))
                            continue;
                        WebsiteNotification notification = new WebsiteNotification
                        {
                            Id = id,
                            Timestamp = Long(value, "ts"),
                            Read = Boolean(value, "read"),
                            Type = String(value, "type"),
                            FromId = String(value, "fromId"),
                            FromName = String(value, "fromName"),
                            FromAvatar = String(value, "fromAvatar"),
                            Image = String(value, "image"),
                            Target = String(value, "target"),
                            Text = String(value, "text")
                        };
                        if (value.TryGetProperty("fromEquippedBorder", out JsonElement border) && border.ValueKind == JsonValueKind.Object)
                            notification.EquippedBorderJson = border.GetRawText();
                        items.Add(notification);
                    }
                }

                unread = Math.Clamp(unread, 0, 100);
                PublishUnread(unread, activeAccount);
                return (true, items, unread, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (false, items, 0, null);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteNotifications::Get", ex);
                return (false, items, 0, "Network error while loading notifications.");
            }
        }

        public static Task<(bool Ok, int? Unread, string? Error)> MarkOneAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
                return Task.FromResult<(bool, int?, string?)>((false, null, "Invalid notification."));
            return PostAsync("/api/notifications/read-one", JsonSerializer.Serialize(new { id }), cancellationToken);
        }

        public static Task<(bool Ok, int? Unread, string? Error)> MarkAllAsync(CancellationToken cancellationToken = default)
        {
            return PostAsync("/api/notifications/read", null, cancellationToken);
        }

        public static Task<(bool Ok, int? Unread, string? Error)> ClearAsync(CancellationToken cancellationToken = default)
        {
            return PostAsync("/api/notifications/clear", null, cancellationToken);
        }

        public static Task<(bool Ok, int? Unread, string? Error)> RespondToFriendRequestAsync(string userId, bool accept, CancellationToken cancellationToken = default)
        {
            if (!IsNumericId(userId))
                return Task.FromResult<(bool, int?, string?)>((false, null, "Invalid user."));
            return PostAsync("/api/users/" + userId + (accept ? "/friend" : "/unfriend"), "{}", cancellationToken);
        }

        public static void PublishUnread(int unread)
        {
            PublishUnread(Math.Clamp(unread, 0, 100), WebsiteAuth.GetActiveId() ?? "");
        }

        private static async Task<(bool Ok, int? Unread, string? Error)> PostAsync(string path, string? body, CancellationToken cancellationToken)
        {
            string activeAccount = WebsiteAuth.GetActiveId() ?? "";
            try
            {
                string? token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token))
                    return (false, null, "You are not signed in.");
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, App.WebsiteBaseUrl + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                string json = await Http.ReadStringBoundedAsync(response.Content, 65536, cancellationToken).ConfigureAwait(false);
                int? unread = null;
                string? error = null;
                if (json.Length <= 65536 && !string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                        JsonElement root = document.RootElement;
                        if (root.TryGetProperty("unread", out JsonElement unreadElement) && unreadElement.TryGetInt32(out int value))
                            unread = Math.Clamp(value, 0, 100);
                        error = String(root, "error");
                    }
                    catch
                    {
                    }
                }
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        return (false, null, "Your session expired. Sign in again.");
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        return (false, null, "Too many requests, slow down.");
                    return (false, null, string.IsNullOrWhiteSpace(error) ? "The action failed, HTTP " + (int)response.StatusCode + "." : error);
                }
                if (unread.HasValue)
                    PublishUnread(unread.Value, activeAccount);
                return (true, unread, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (false, null, null);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteNotifications::Post", ex);
                return (false, null, "Network error.");
            }
        }

        private static void PublishUnread(int unread, string activeAccount)
        {
            if (activeAccount == (WebsiteAuth.GetActiveId() ?? ""))
                UnreadChanged?.Invoke(unread);
        }

        private static bool IsNumericId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 20)
                return false;
            foreach (char character in value)
            {
                if (character < '0' || character > '9')
                    return false;
            }
            return true;
        }

        private static string String(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property))
                return "";
            if (property.ValueKind == JsonValueKind.String)
                return property.GetString() ?? "";
            if (property.ValueKind == JsonValueKind.Number)
                return property.GetRawText();
            return "";
        }

        private static bool Boolean(JsonElement value, string name)
        {
            return value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.True;
        }

        private static int Number(JsonElement value, string name)
        {
            return value.TryGetProperty(name, out JsonElement property) && property.TryGetInt32(out int result) ? result : 0;
        }

        private static long Long(JsonElement value, string name)
        {
            return value.TryGetProperty(name, out JsonElement property) && property.TryGetInt64(out long result) ? result : 0;
        }
    }

    public sealed class WebsiteNotificationRealtime : IDisposable
    {
        private const int MaxPayloadLength = 2_000_000;
        private AblyRealtime? _client;
        private IRealtimeChannel? _channel;
        private bool _disposed;

        public void Start()
        {
            Stop();
            if (_disposed)
                return;
            string? token = WebsiteAuth.GetToken();
            string account = WebsiteAuth.GetActiveId() ?? "";
            if (string.IsNullOrEmpty(token) || !IsChannelSegment(account))
                return;
            AblyRealtime? client = null;
            try
            {
                ClientOptions options = new ClientOptions
                {
                    AuthUrl = new Uri(App.WebsiteBaseUrl + "/api/notifications/realtime-token"),
                    AuthMethod = HttpMethod.Get,
                    AuthHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                    UseTokenAuth = true,
                    AutoConnect = true,
                    EchoMessages = true,
                    LogLevel = LogLevel.Error
                };
                client = new AblyRealtime(options);
                IRealtimeChannel channel = client.Channels.Get("voidstrap:notifications:user:" + account);
                channel.Subscribe("message", OnMessage);
                _client = client;
                _channel = channel;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                App.Logger.WriteException("WebsiteNotificationRealtime::Start", ex);
            }
        }

        private void OnMessage(IO.Ably.Message message)
        {
            try
            {
                string payload = message.Data is string text ? text : Newtonsoft.Json.JsonConvert.SerializeObject(message.Data);
                if (payload.Length > MaxPayloadLength)
                    return;
                using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.TryGetProperty("unread", out JsonElement value) && value.TryGetInt32(out int unread))
                    WebsiteNotifications.PublishUnread(unread);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteNotificationRealtime::Message", ex);
            }
        }

        private void Stop()
        {
            IRealtimeChannel? channel = _channel;
            AblyRealtime? client = _client;
            _channel = null;
            _client = null;
            if (channel != null)
            {
                try
                {
                    channel.Unsubscribe("message", OnMessage);
                }
                catch
                {
                }
            }
            if (client == null)
                return;
            try
            {
                client.Close();
            }
            catch
            {
            }
            client.Dispose();
        }

        private static bool IsChannelSegment(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
                return false;
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-')
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
