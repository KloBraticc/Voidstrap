using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public static class RoReviewService
{
    private const string ApiBase = "https://hermivore.cat";
    private const int MaxResponseBytes = 512 * 1024;

    private static readonly HttpClient Client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(12), handler =>
    {
        handler.UseCookies = false;
        handler.AllowAutoRedirect = false;
    });

    private static readonly HttpClient RobloxClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(12), handler =>
    {
        handler.UseCookies = false;
        handler.AllowAutoRedirect = false;
    });

    private static string? _sessionToken;
    private static long _sessionUserId;
    private static string? _sessionUsername;
    private static string _robloxCsrf = string.Empty;
    private static readonly SemaphoreSlim _authLock = new(1, 1);
    private static bool _authInProgress;

    public static bool IsAuthenticated => !string.IsNullOrEmpty(_sessionToken);
    public static long SessionUserId => _sessionUserId;

    public sealed class ReviewData
    {
        public int UpCount { get; init; }
        public int DownCount { get; init; }
        public int ReviewCount { get; init; }
        public string? UserVote { get; init; }
        public List<ReviewEntry> Reviews { get; init; } = new();
    }

    public sealed class ReviewEntry
    {
        public string Id { get; init; } = string.Empty;
        public long AuthorId { get; init; }
        public string AuthorName { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public string CreatedAt { get; init; } = string.Empty;
    }

    public static async Task<ReviewData?> FetchReviewsAsync(long placeId, CancellationToken ct)
    {
        if (placeId <= 0)
            return null;
        try
        {
            string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "?game=true";
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            if (!string.IsNullOrEmpty(_sessionToken))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _sessionToken);
            using HttpResponseMessage res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return null;
            using JsonDocument doc = JsonDocument.Parse(
                await Voidstrap.Utility.Http.ReadStringBoundedAsync(res.Content, MaxResponseBytes, timeout.Token).ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            int upCount = 0;
            int downCount = 0;
            List<long> upVoters = new();
            List<long> downVoters = new();
            if (root.TryGetProperty("profile_rating", out JsonElement rating))
            {
                if (rating.TryGetProperty("up", out JsonElement up) && up.ValueKind == JsonValueKind.Array)
                {
                    upCount = up.GetArrayLength();
                    foreach (JsonElement v in up.EnumerateArray())
                    {
                        if (v.TryGetInt64(out long id))
                            upVoters.Add(id);
                    }
                }
                if (rating.TryGetProperty("down", out JsonElement down) && down.ValueKind == JsonValueKind.Array)
                {
                    downCount = down.GetArrayLength();
                    foreach (JsonElement v in down.EnumerateArray())
                    {
                        if (v.TryGetInt64(out long id))
                            downVoters.Add(id);
                    }
                }
            }
            string? userVote = null;
            if (_sessionUserId > 0)
            {
                if (upVoters.Contains(_sessionUserId))
                    userVote = "up";
                else if (downVoters.Contains(_sessionUserId))
                    userVote = "down";
            }
            List<ReviewEntry> reviews = new();
            if (root.TryGetProperty("reviews", out JsonElement reviewsArr) && reviewsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in reviewsArr.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object)
                        continue;
                    string id = r.TryGetProperty("_id", out JsonElement idEl) ? (idEl.GetString() ?? string.Empty) : string.Empty;
                    long authorId = 0;
                    if (r.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object)
                    {
                        if (author.TryGetProperty("roblox_id", out JsonElement rid))
                        {
                            if (rid.ValueKind == JsonValueKind.Number)
                                rid.TryGetInt64(out authorId);
                            else if (rid.ValueKind == JsonValueKind.String && long.TryParse(rid.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                                authorId = parsed;
                        }
                    }
                    string authorName = string.Empty;
                    if (r.TryGetProperty("author", out JsonElement auth2) && auth2.ValueKind == JsonValueKind.Object)
                    {
                        if (auth2.TryGetProperty("roblox_username", out JsonElement un))
                            authorName = un.GetString() ?? string.Empty;
                    }
                    string content = r.TryGetProperty("content", out JsonElement cEl) ? (cEl.GetString() ?? string.Empty) : string.Empty;
                    string createdAt = r.TryGetProperty("created_at", out JsonElement caEl) ? (caEl.GetString() ?? string.Empty) : string.Empty;
                    if (content.Length > 0)
                        reviews.Add(new ReviewEntry { Id = id, AuthorId = authorId, AuthorName = authorName, Content = content, CreatedAt = createdAt });
                }
            }
            return new ReviewData
            {
                UpCount = upCount,
                DownCount = downCount,
                ReviewCount = reviews.Count > 0 ? reviews.Count : (root.TryGetProperty("reviews", out JsonElement ra) && ra.ValueKind == JsonValueKind.Array ? ra.GetArrayLength() : 0),
                UserVote = userVote,
                Reviews = reviews
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "FetchReviews failed: " + ex.Message);
            return null;
        }
    }

    public static async Task<bool> VoteAsync(long placeId, string vote, CancellationToken ct)
    {
        if (!IsAuthenticated || placeId <= 0)
            return false;
        try
        {
            string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "/rate?game=true";
            string body = "{\"vote\":\"" + vote + "\"}";
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _sessionToken);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                App.Logger.WriteLine("RoReviewService", "Vote returned " + (int)res.StatusCode);
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    ClearSession();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "Vote failed: " + ex.Message);
            return false;
        }
    }

    public static async Task<bool> SubmitReviewAsync(long placeId, string content, CancellationToken ct)
    {
        if (!IsAuthenticated || placeId <= 0 || string.IsNullOrWhiteSpace(content))
            return false;
        try
        {
            string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "?game=true";
            string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = content.Trim() });
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _sessionToken);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                App.Logger.WriteLine("RoReviewService", "SubmitReview returned " + (int)res.StatusCode);
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    ClearSession();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "SubmitReview failed: " + ex.Message);
            return false;
        }
    }

    public static async Task<bool> AuthenticateAsync(CancellationToken ct)
    {
        if (IsAuthenticated)
            return true;
        if (!await _authLock.WaitAsync(0, ct).ConfigureAwait(false))
            return false;
        try
        {
            if (IsAuthenticated)
                return true;
            if (_authInProgress)
                return false;
            _authInProgress = true;
            string? cookie = RobloxCookie.Get();
            if (string.IsNullOrEmpty(cookie))
            {
                App.Logger.WriteLine("RoReviewService", "No Roblox cookie available for auth");
                return false;
            }
            RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, ct).ConfigureAwait(false);
            if (account == null || account.UserId <= 0)
            {
                App.Logger.WriteLine("RoReviewService", "Could not resolve Roblox account");
                return false;
            }
            using HttpRequestMessage challengeReq = new(HttpMethod.Post, ApiBase + "/api/roblox/verify/challenge");
            challengeReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage challengeRes = await Client.SendAsync(challengeReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!challengeRes.IsSuccessStatusCode)
            {
                App.Logger.WriteLine("RoReviewService", "Verify challenge returned " + (int)challengeRes.StatusCode);
                return false;
            }
            string challengeJson = await Voidstrap.Utility.Http.ReadStringBoundedAsync(challengeRes.Content, MaxResponseBytes, ct).ConfigureAwait(false);
            using JsonDocument challengeDoc = JsonDocument.Parse(challengeJson);
            JsonElement cRoot = challengeDoc.RootElement;
            if (cRoot.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.True)
            {
                string reason = cRoot.TryGetProperty("reason", out JsonElement rEl) ? (rEl.GetString() ?? "unknown") : "unknown";
                App.Logger.WriteLine("RoReviewService", "Challenge error: " + reason);
                return false;
            }
            long botId = 0;
            if (cRoot.TryGetProperty("bot_id", out JsonElement bidEl))
            {
                if (bidEl.ValueKind == JsonValueKind.Number)
                    bidEl.TryGetInt64(out botId);
                else if (bidEl.ValueKind == JsonValueKind.String)
                    long.TryParse(bidEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out botId);
            }
            string sessionId = cRoot.TryGetProperty("session_id", out JsonElement sidEl) ? (sidEl.GetString() ?? string.Empty) : string.Empty;
            if (botId <= 0 || sessionId.Length == 0)
            {
                App.Logger.WriteLine("RoReviewService", "Challenge did not return bot_id or session_id");
                return false;
            }
            bool friended = await SendFriendRequestAsync(botId, cookie, ct).ConfigureAwait(false);
            if (!friended)
            {
                App.Logger.WriteLine("RoReviewService", "Could not send friend request to verification bot");
                return false;
            }
            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                using HttpRequestMessage statusReq = new(HttpMethod.Get, ApiBase + "/api/roblox/verify/status/" + Uri.EscapeDataString(sessionId));
                statusReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
                using HttpResponseMessage statusRes = await Client.SendAsync(statusReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!statusRes.IsSuccessStatusCode)
                    continue;
                string statusJson = await Voidstrap.Utility.Http.ReadStringBoundedAsync(statusRes.Content, MaxResponseBytes, ct).ConfigureAwait(false);
                using JsonDocument statusDoc = JsonDocument.Parse(statusJson);
                JsonElement sRoot = statusDoc.RootElement;
                string status = sRoot.TryGetProperty("status", out JsonElement stEl) ? (stEl.GetString() ?? string.Empty) : string.Empty;
                if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    string? token = sRoot.TryGetProperty("session_token", out JsonElement tEl) ? tEl.GetString() : null;
                    long userId = 0;
                    if (sRoot.TryGetProperty("user_id", out JsonElement uidEl))
                    {
                        if (uidEl.ValueKind == JsonValueKind.Number)
                            uidEl.TryGetInt64(out userId);
                        else if (uidEl.ValueKind == JsonValueKind.String)
                            long.TryParse(uidEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);
                    }
                    string? username = sRoot.TryGetProperty("username", out JsonElement unEl) ? unEl.GetString() : null;
                    if (!string.IsNullOrEmpty(token))
                    {
                        _sessionToken = token;
                        _sessionUserId = userId > 0 ? userId : account.UserId;
                        _sessionUsername = !string.IsNullOrEmpty(username) ? username : account.Username;
                        App.Logger.WriteLine("RoReviewService", "Authenticated as " + _sessionUsername);
                        return true;
                    }
                }
                else if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine("RoReviewService", "Verification session expired");
                    return false;
                }
            }
            App.Logger.WriteLine("RoReviewService", "Verification polling timed out");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "Authentication failed: " + ex.Message);
            return false;
        }
        finally
        {
            _authInProgress = false;
            _authLock.Release();
        }
    }

    private static async Task<bool> SendFriendRequestAsync(long botId, string cookie, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpRequestMessage req = new(HttpMethod.Post, "https://friends.roblox.com/v1/users/" + botId.ToString(CultureInfo.InvariantCulture) + "/request-friendship");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            string csrf = _robloxCsrf;
            if (csrf.Length > 0)
                req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            using HttpResponseMessage res = await RobloxClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.Forbidden
                && res.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values)
                && values.FirstOrDefault() is { Length: > 0 } fresh
                && !string.Equals(fresh, csrf, StringComparison.Ordinal))
            {
                _robloxCsrf = fresh;
                continue;
            }
            if (res.IsSuccessStatusCode)
                return true;
            App.Logger.WriteLine("RoReviewService", "Friend request returned " + (int)res.StatusCode);
            return false;
        }
        return false;
    }

    private static void ClearSession()
    {
        _sessionToken = null;
        _sessionUserId = 0;
        _sessionUsername = null;
    }
}
