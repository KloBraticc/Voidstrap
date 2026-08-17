using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Integrations.GameChat
{
    public sealed class GameChatVoidstrapProfile
    {
        public long RobloxId;
        public bool Exists;
        public string Username = "";
        public string DisplayName = "";
        public string About = "";
        public string Status = "";
        public string BannerUrl = "";
        public string GradientCss = "";
        public string AvatarUrl = "";
        public string AvatarBorderCss = "";
        public string EquippedBorderJson = "";
        public int Friends;
        public int Followers;
        public bool IsFriend;
        public bool RequestSent;
        public bool Self;
        public List<GameChatBadge> Badges = [];
    }

    public sealed class GameChatBorder
    {
        public string AvatarBorderCss = "";
        public string EquippedBorderJson = "";
    }

    public sealed class GameChatIdentity
    {
        public long AccountId;
        public long RobloxId;
        public string AvatarBorderCss = "";
        public string EquippedBorderJson = "";
        public IReadOnlyList<GameChatBadge> Badges = [];
    }

    public sealed class GameChatWornItem
    {
        public long Id;
        public string Name = "";
        public ImageSource? Image;
    }

    public static class GameChatRoblox
    {
        private const string Tag = "GameChatRoblox";
        public const long LocalAccountIdBase = 1000000000000;

        private static readonly HttpClient _authClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(12), handler =>
        {
            handler.UseCookies = false;
            handler.AllowAutoRedirect = false;
        });

        private static readonly ConcurrentDictionary<long, Task<ImageSource?>> _headshotCache = new();
        private static readonly ConcurrentDictionary<long, Task<GameChatBorder>> _borderCache = new();
        private static readonly ConcurrentDictionary<long, IdentityCacheEntry> _identityCache = new();
        private static readonly SemaphoreSlim _identityRequestGate = new(1, 1);
        private static long _lastIdentityRequestTicks;

        private const int MaxProfileCacheEntries = 256;
        private const long IdentitySuccessMilliseconds = 120000;
        private const long IdentityFailureMilliseconds = 15000;

        private sealed class IdentityCacheEntry
        {
            public required Task<GameChatIdentity?> Task;
            public long Created;
        }

        private const int MaxJsonBytes = 4194304;

        private const int MaxImageBytes = 4000000;

        private static HashSet<long> _blockedIds = [];
        private static DateTime _blockedFetchedUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim _blockedLock = new(1, 1);

        public static HashSet<long> BlockedSnapshot => _blockedIds;

        public static void InvalidateAccountState()
        {
            _borderCache.Clear();
            _identityCache.Clear();
            _blockedIds = [];
            _blockedFetchedUtc = DateTime.MinValue;
        }

        public static Task<GameChatBorder> GetBorderAsync(long robloxId)
        {
            if (robloxId <= 0)
                return Task.FromResult(new GameChatBorder());
            Task<GameChatBorder> task = _borderCache.GetOrAdd(robloxId, FetchBorderAsync);
            TrimCache(_borderCache);
            return task;
        }

        private static async Task<GameChatBorder> FetchBorderAsync(long robloxId)
        {
            try
            {
                GameChatIdentity? identity = await GetChatIdentityAsync(robloxId).ConfigureAwait(false);
                if (identity == null)
                    return new GameChatBorder();
                return new GameChatBorder
                {
                    AvatarBorderCss = identity.AvatarBorderCss,
                    EquippedBorderJson = identity.EquippedBorderJson,
                };
            }
            catch
            {
                return new GameChatBorder();
            }
        }

        public static async Task<GameChatIdentity?> GetChatIdentityAsync(long accountId, CancellationToken token = default)
        {
            if (accountId <= 0)
                return null;
            while (true)
            {
                long now = Environment.TickCount64;
                if (_identityCache.TryGetValue(accountId, out IdentityCacheEntry? existing))
                {
                    long lifetime = existing.Task.IsCompletedSuccessfully && existing.Task.Result != null
                        ? IdentitySuccessMilliseconds
                        : IdentityFailureMilliseconds;
                    if (now - existing.Created < lifetime)
                        return await existing.Task.WaitAsync(token).ConfigureAwait(false);
                    _identityCache.TryRemove(new KeyValuePair<long, IdentityCacheEntry>(accountId, existing));
                }
                var entry = new IdentityCacheEntry
                {
                    Created = now,
                    Task = FetchChatIdentityWithTimeoutAsync(accountId),
                };
                if (_identityCache.TryAdd(accountId, entry))
                {
                    TrimIdentityCache();
                    return await entry.Task.WaitAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static async Task<GameChatIdentity?> FetchChatIdentityWithTimeoutAsync(long accountId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await _identityRequestGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    long delay = 1050 - (Environment.TickCount64 - Volatile.Read(ref _lastIdentityRequestTicks));
                    if (delay > 0)
                        await Task.Delay((int)delay, timeout.Token).ConfigureAwait(false);
                    Volatile.Write(ref _lastIdentityRequestTicks, Environment.TickCount64);
                    GameChatVoidstrapProfile? profile = await GetVoidstrapProfileAsync(accountId, timeout.Token).ConfigureAwait(false);
                    if (profile == null || !profile.Exists)
                        return null;
                    return new GameChatIdentity
                    {
                        AccountId = accountId,
                        RobloxId = profile.RobloxId,
                        AvatarBorderCss = profile.AvatarBorderCss,
                        EquippedBorderJson = profile.EquippedBorderJson,
                        Badges = CopyBadges(profile.Badges),
                    };
                }
                finally
                {
                    _identityRequestGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static List<GameChatBadge> CopyBadges(List<GameChatBadge> badges)
        {
            if (badges.Count == 0)
                return [];
            var copy = new List<GameChatBadge>(Math.Min(6, badges.Count));
            foreach (GameChatBadge badge in badges)
            {
                if (copy.Count >= 6)
                    break;
                copy.Add(new GameChatBadge { Id = badge.Id, Name = badge.Name, Image = badge.Image });
            }
            return copy;
        }

        private static void TrimIdentityCache()
        {
            if (_identityCache.Count <= MaxProfileCacheEntries)
                return;
            foreach (long key in _identityCache.Keys)
            {
                if (_identityCache.Count <= MaxProfileCacheEntries)
                    break;
                _identityCache.TryRemove(key, out _);
            }
        }

        public static Task<ImageSource?> GetHeadshotAsync(long userId)
        {
            if (userId <= 0)
                return Task.FromResult<ImageSource?>(null);
            Task<ImageSource?> task = _headshotCache.GetOrAdd(userId, FetchHeadshotAsync);
            TrimCache(_headshotCache);
            return task;
        }

        public static async Task<ImageSource?> GetChatHeadshotAsync(long accountId, CancellationToken token = default)
        {
            if (accountId <= 0)
                return null;
            if (accountId < LocalAccountIdBase)
                return await GetHeadshotAsync(accountId).WaitAsync(token).ConfigureAwait(false);
            GameChatIdentity? identity = await GetChatIdentityAsync(accountId, token).ConfigureAwait(false);
            return identity?.RobloxId > 0
                ? await GetHeadshotAsync(identity.RobloxId).WaitAsync(token).ConfigureAwait(false)
                : null;
        }

        private static async Task<ImageSource?> FetchHeadshotAsync(long userId)
        {
            try
            {
                string url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + userId + "&size=48x48&format=Png&isCircular=true";
                using JsonDocument? doc = await GetJsonAsync(url).ConfigureAwait(false);
                if (doc == null)
                    return null;
                string? imageUrl = null;
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("imageUrl", out var iu) && iu.ValueKind == JsonValueKind.String)
                            imageUrl = iu.GetString();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(imageUrl))
                    return null;
                return await DownloadImageAsync(imageUrl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Headshot fetch failed: " + ex.Message);
                return null;
            }
        }

        private static async Task<ImageSource?> DownloadImageAsync(string imageUrl, CancellationToken token = default)
        {
            try
            {
                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                    return null;
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                byte[]? bytes = await ReadBoundedAsync(response.Content, MaxImageBytes, token).ConfigureAwait(false);
                if (bytes == null)
                    return null;
                ImageSource? bmp = Utility.SafeImaging.FromBytes(bytes, 110);
                if (bmp == null)
                    return null;
                if (bmp.CanFreeze)
                    bmp.Freeze();
                return bmp;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<List<GameChatWornItem>> GetWearingAsync(long userId, CancellationToken token = default)
        {
            var list = new List<GameChatWornItem>();
            if (userId <= 0)
                return list;
            try
            {
                using JsonDocument? doc = await GetJsonAsync("https://avatar.roblox.com/v1/users/" + userId + "/avatar", token).ConfigureAwait(false);
                if (doc == null)
                    return list;
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        long id = a.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
                        string name = a.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? (nEl.GetString() ?? "") : "";
                        if (id > 0 && list.Count < 100)
                            list.Add(new GameChatWornItem { Id = id, Name = name });
                    }
                }
                if (list.Count == 0)
                    return list;

                var sb = new StringBuilder();
                foreach (var w in list)
                {
                    if (sb.Length > 0)
                        sb.Append(',');
                    sb.Append(w.Id);
                }
                var urlById = new Dictionary<long, string>();
                using JsonDocument? thumbDoc = await GetJsonAsync("https://thumbnails.roblox.com/v1/assets?assetIds=" + sb + "&size=110x110&format=Png&isCircular=false", token).ConfigureAwait(false);
                if (thumbDoc == null)
                    return list;
                if (thumbDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        long tid = item.TryGetProperty("targetId", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                        string? iu = item.TryGetProperty("imageUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                        if (tid > 0 && !string.IsNullOrEmpty(iu))
                            urlById[tid] = iu;
                    }
                }
                var downloads = new List<Task>(4);
                foreach (var w in list)
                {
                    token.ThrowIfCancellationRequested();
                    if (urlById.TryGetValue(w.Id, out string? imageUrl))
                    {
                        downloads.Add(FillWornImageAsync(w, imageUrl, token));
                        if (downloads.Count == 4)
                        {
                            await Task.WhenAll(downloads).ConfigureAwait(false);
                            downloads.Clear();
                        }
                    }
                }
                if (downloads.Count > 0)
                    await Task.WhenAll(downloads).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                list.Clear();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Wearing fetch failed: " + ex.Message);
            }
            return list;
        }

        private static async Task FillWornImageAsync(GameChatWornItem item, string imageUrl, CancellationToken token)
        {
            item.Image = await DownloadImageAsync(imageUrl, token).ConfigureAwait(false);
        }

        public static async Task<GameChatVoidstrapProfile?> GetVoidstrapProfileAsync(long robloxId, CancellationToken token = default)
        {
            if (robloxId <= 0)
                return null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + "/api/users/" + robloxId);
                string? authToken = Utility.WebsiteAuth.GetToken();
                if (!string.IsNullOrEmpty(authToken))
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + authToken);
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                byte[]? payload = await ReadBoundedAsync(response.Content, MaxJsonBytes, token).ConfigureAwait(false);
                if (payload == null)
                    return null;
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                var profile = new GameChatVoidstrapProfile { RobloxId = robloxId < LocalAccountIdBase ? robloxId : 0 };

                if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                    return profile;

                profile.Exists = true;
                profile.RobloxId = Long(user, "robloxId", profile.RobloxId);
                profile.Username = Str(user, "username");
                profile.DisplayName = Str(user, "displayName");
                profile.About = Str(user, "about");
                profile.Status = Str(user, "status");
                profile.BannerUrl = Str(user, "banner");
                profile.GradientCss = Str(user, "gradient");
                profile.AvatarUrl = Str(user, "avatar");
                profile.AvatarBorderCss = Str(user, "avatarBorder");
                if (user.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var badge in badges.EnumerateArray())
                    {
                        if (profile.Badges.Count >= 6)
                            break;
                        string name = Str(badge, "name");
                        string id = Str(badge, "id");
                        string image = BadgeImage(badge);
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(image) && image.Length <= 256 * 1024)
                            profile.Badges.Add(new GameChatBadge { Id = id, Name = name, Image = image });
                    }
                }
                if (user.TryGetProperty("equippedBorder", out var eqb) && eqb.ValueKind == JsonValueKind.Object)
                    profile.EquippedBorderJson = eqb.GetRawText();

                if (root.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
                {
                    profile.Friends = Int(counts, "friends");
                    profile.Followers = Int(counts, "followers");
                }
                if (root.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
                    profile.Self = true;
                if (root.TryGetProperty("rel", out var rel) && rel.ValueKind == JsonValueKind.Object)
                {
                    profile.IsFriend = rel.TryGetProperty("isFriend", out var isf) && isf.ValueKind == JsonValueKind.True;
                    profile.RequestSent = rel.TryGetProperty("requestSent", out var rs) && rs.ValueKind == JsonValueKind.True;
                }
                return profile;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, "Voidstrap profile fetch failed: " + ex.Message);
                return null;
            }
        }

        public static async Task<(bool Ok, string? Error)> AddVoidstrapFriendAsync(long robloxId, CancellationToken token = default)
        {
            if (robloxId <= 0)
                return (false, "Invalid user.");
            string? authToken = Utility.WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(authToken))
                return (false, "Sign in with /login first to add friends.");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, App.WebsiteBaseUrl + "/api/users/" + robloxId + "/friend");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + authToken);
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await App.HttpClient.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (true, null);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return (false, "Sign in with /login first to add friends.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return (false, "Too many requests, try again later.");
                return (false, "Could not send friend request.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (false, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static int Int(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        }

        private static long Long(JsonElement obj, string name, long fallback)
        {
            if (!obj.TryGetProperty(name, out JsonElement value))
                return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
                return number;
            return fallback;
        }

        private static string BadgeImage(JsonElement badge)
        {
            string[] names = ["image", "imageUrl", "icon", "url"];
            foreach (string name in names)
            {
                string value = Str(badge, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return "";
        }

        public static async Task<bool> IsBlockedAsync(long userId)
        {
            if (userId <= 0)
                return false;
            var set = await GetBlockedIdsAsync().ConfigureAwait(false);
            return set.Contains(userId);
        }

        public static async Task<HashSet<long>> GetBlockedIdsAsync()
        {
            if ((DateTime.UtcNow - _blockedFetchedUtc).TotalMinutes < 5)
                return _blockedIds;

            await _blockedLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if ((DateTime.UtcNow - _blockedFetchedUtc).TotalMinutes < 5)
                    return _blockedIds;

                string? cookie = RobloxCookie.Get();
                if (string.IsNullOrEmpty(cookie))
                {
                    _blockedFetchedUtc = DateTime.UtcNow;
                    return _blockedIds;
                }

                var result = new HashSet<long>();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://accountsettings.roblox.com/v1/users/get-detailed-blocked-users");
                    request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                    using var response = await _authClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        byte[]? payload = await ReadBoundedAsync(response.Content, MaxJsonBytes).ConfigureAwait(false);
                        if (payload == null)
                            return _blockedIds;
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.TryGetProperty("blockedUsers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in arr.EnumerateArray())
                            {
                                if (u.TryGetProperty("userId", out var idEl) && idEl.TryGetInt64(out var id))
                                    result.Add(id);
                            }
                        }
                        _blockedIds = result;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(Tag, "Block list fetch failed: " + ex.Message);
                }
                _blockedFetchedUtc = DateTime.UtcNow;
                return _blockedIds;
            }
            finally
            {
                _blockedLock.Release();
            }
        }

        private static string Str(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }

        private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken token = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            byte[]? payload = await ReadBoundedAsync(response.Content, MaxJsonBytes, token).ConfigureAwait(false);
            return payload == null ? null : JsonDocument.Parse(payload);
        }

        private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken token = default)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maxBytes))
                return null;
            await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream output = new(content.Headers.ContentLength is long length ? (int)length : 81920);
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > maxBytes)
                    return null;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }

        private static void TrimCache<T>(ConcurrentDictionary<long, Task<T>> cache)
        {
            if (cache.Count <= MaxProfileCacheEntries)
                return;
            foreach (long key in cache.Keys)
            {
                if (cache.Count <= MaxProfileCacheEntries)
                    break;
                cache.TryRemove(key, out _);
            }
        }
    }
}
