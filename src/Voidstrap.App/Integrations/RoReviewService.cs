using System;
using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<long, string> _avatarCache = new();

    private static string? _sessionToken;
    private static long _sessionUserId;
    private static string? _sessionUsername;
    private static string _robloxCsrf = string.Empty;
    private static readonly object _authLockObj = new();
    private static Task<bool>? _currentAuthTask;

    public static bool IsAuthenticated => !string.IsNullOrEmpty(_sessionToken);
    public static long SessionUserId => _sessionUserId;
    public static string? SessionUsername => _sessionUsername;
    public static event Action? AuthenticationChanged;
    public static event Action<long, ReviewData>? ReviewsUpdated;

    public sealed class ReviewData
    {
        public int UpCount { get; init; }
        public int DownCount { get; init; }
        public int ReviewCount { get; init; }
        public string? UserVote { get; init; }
        public List<ReviewEntry> Reviews { get; init; } = new();
    }

    public sealed class ReviewEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private string _id = string.Empty;
        private long _authorId;
        private string _authorName = string.Empty;
        private string _avatarUrl = string.Empty;
        private string _content = string.Empty;
        private string _createdAt = string.Empty;
        private int _upCount;
        private int _downCount;
        private string? _userVote;
        private string _ratingText = string.Empty;
        private bool _canDelete;

        public string Id { get => _id; set { _id = value; OnPropertyChanged(nameof(Id)); } }
        public long AuthorId { get => _authorId; set { _authorId = value; OnPropertyChanged(nameof(AuthorId)); } }
        public string AuthorName { get => _authorName; set { _authorName = value; OnPropertyChanged(nameof(AuthorName)); } }
        public string AvatarUrl { get => _avatarUrl; set { _avatarUrl = value; OnPropertyChanged(nameof(AvatarUrl)); } }
        public string Content { get => _content; set { _content = value; OnPropertyChanged(nameof(Content)); } }
        public string CreatedAt { get => _createdAt; set { _createdAt = value; OnPropertyChanged(nameof(CreatedAt)); } }
        public int UpCount { get => _upCount; set { _upCount = value; OnPropertyChanged(nameof(UpCount)); } }
        public int DownCount { get => _downCount; set { _downCount = value; OnPropertyChanged(nameof(DownCount)); } }
        public string? UserVote { get => _userVote; set { _userVote = value; OnPropertyChanged(nameof(UserVote)); OnPropertyChanged(nameof(IsUpVoted)); OnPropertyChanged(nameof(IsDownVoted)); OnPropertyChanged(nameof(UpAppearance)); OnPropertyChanged(nameof(DownAppearance)); } }
        public string RatingText { get => _ratingText; set { _ratingText = value; OnPropertyChanged(nameof(RatingText)); } }
        public bool CanDelete { get => _canDelete; set { _canDelete = value; OnPropertyChanged(nameof(CanDelete)); } }
        public long UnixTime { get; set; }

        public bool IsUpVoted => string.Equals(UserVote, "up", StringComparison.OrdinalIgnoreCase);
        public bool IsDownVoted => string.Equals(UserVote, "down", StringComparison.OrdinalIgnoreCase);

        public Wpf.Ui.Common.ControlAppearance UpAppearance =>
            IsUpVoted ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Transparent;

        public Wpf.Ui.Common.ControlAppearance DownAppearance =>
            IsDownVoted ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Transparent;

        public void RefreshRating()
        {
            int total = UpCount + DownCount;
            if (total == 0)
            {
                RatingText = "No ratings yet";
            }
            else
            {
                int pct = (int)Math.Round((double)UpCount / total * 100);
                RatingText = pct.ToString(CultureInfo.InvariantCulture) + "% positive (" + total.ToString(CultureInfo.InvariantCulture) + " " + (total == 1 ? "vote" : "votes") + ")";
            }
            OnPropertyChanged(nameof(RatingText));
            OnPropertyChanged(nameof(UpCount));
            OnPropertyChanged(nameof(DownCount));
            OnPropertyChanged(nameof(IsUpVoted));
            OnPropertyChanged(nameof(IsDownVoted));
            OnPropertyChanged(nameof(UpAppearance));
            OnPropertyChanged(nameof(DownAppearance));
        }

        private void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
    }

    public static string GetCachedAvatar(long userId)
    {
        return _avatarCache.TryGetValue(userId, out string? u) ? u : string.Empty;
    }

    private static string FormatTimestamp(long seconds)
    {
        if (seconds <= 0)
            return string.Empty;
        try
        {
            if (seconds > 1_000_000_000_000)
                seconds /= 1000;
            DateTimeOffset dt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            TimeSpan diff = DateTimeOffset.UtcNow - dt;
            if (diff.TotalSeconds < 60 && diff.TotalSeconds >= 0)
                return "just now";
            if (diff.TotalMinutes < 60 && diff.TotalMinutes >= 1)
                return ((int)diff.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m ago";
            if (diff.TotalHours < 24 && diff.TotalHours >= 1)
                return ((int)diff.TotalHours).ToString(CultureInfo.InvariantCulture) + "h ago";
            return dt.ToLocalTime().ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task PopulateAvatarsAsync(List<ReviewEntry> reviews, CancellationToken ct)
    {
        List<long> missing = new();
        foreach (ReviewEntry r in reviews)
        {
            if (r.AuthorId <= 0)
                continue;
            if (_avatarCache.TryGetValue(r.AuthorId, out string? cached) && cached.Length > 0)
            {
                r.AvatarUrl = cached;
            }
            else
            {
                missing.Add(r.AuthorId);
            }
        }
        List<long> idsToFetch = missing.Distinct().Take(100).ToList();
        if (idsToFetch.Count == 0)
            return;
        try
        {
            string url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(",", idsToFetch) + "&size=150x150&format=Png&isCircular=true";
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage res = await RobloxClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return;
            string json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(res.Content, MaxResponseBytes, ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out JsonElement dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in dataArr.EnumerateArray())
                {
                    if (item.TryGetProperty("targetId", out JsonElement tid) && item.TryGetProperty("imageUrl", out JsonElement img))
                    {
                        if (tid.TryGetInt64(out long id) && img.ValueKind == JsonValueKind.String)
                        {
                            string u = img.GetString() ?? string.Empty;
                            if (u.Length > 0)
                                _avatarCache[id] = u;
                        }
                    }
                }
                foreach (ReviewEntry r in reviews)
                {
                    if (string.IsNullOrEmpty(r.AvatarUrl) && _avatarCache.TryGetValue(r.AuthorId, out string? imgUrl))
                        r.AvatarUrl = imgUrl;
                }
            }
        }
        catch
        {
        }
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
            if (root.TryGetProperty("profile_score", out JsonElement scoreEl) && scoreEl.ValueKind == JsonValueKind.Object)
            {
                if (scoreEl.TryGetProperty("up", out JsonElement upNum) && upNum.TryGetInt32(out int u))
                    upCount = u;
                if (scoreEl.TryGetProperty("down", out JsonElement downNum) && downNum.TryGetInt32(out int d))
                    downCount = d;
            }
            if (root.TryGetProperty("profile_rating", out JsonElement rating))
            {
                if (rating.TryGetProperty("up", out JsonElement up) && up.ValueKind == JsonValueKind.Array)
                {
                    if (upCount == 0)
                        upCount = up.GetArrayLength();
                    foreach (JsonElement v in up.EnumerateArray())
                    {
                        if (v.TryGetInt64(out long id))
                            upVoters.Add(id);
                    }
                }
                if (rating.TryGetProperty("down", out JsonElement down) && down.ValueKind == JsonValueKind.Array)
                {
                    if (downCount == 0)
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
                    string id = string.Empty;
                    if (r.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                        id = idEl.GetString() ?? string.Empty;
                    else if (r.TryGetProperty("_id", out JsonElement legacyIdEl) && legacyIdEl.ValueKind == JsonValueKind.String)
                        id = legacyIdEl.GetString() ?? string.Empty;

                    long authorId = 0;
                    string authorName = string.Empty;

                    if (r.TryGetProperty("from", out JsonElement fromEl) && fromEl.ValueKind == JsonValueKind.Object)
                    {
                        if (fromEl.TryGetProperty("id", out JsonElement fid))
                        {
                            if (fid.ValueKind == JsonValueKind.Number)
                                fid.TryGetInt64(out authorId);
                            else if (fid.ValueKind == JsonValueKind.String)
                                long.TryParse(fid.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out authorId);
                        }
                        if (fromEl.TryGetProperty("name", out JsonElement fname) && fname.ValueKind == JsonValueKind.String)
                            authorName = fname.GetString() ?? string.Empty;
                    }

                    if (authorId == 0 && r.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object)
                    {
                        if (author.TryGetProperty("roblox_id", out JsonElement rid))
                        {
                            if (rid.ValueKind == JsonValueKind.Number)
                                rid.TryGetInt64(out authorId);
                            else if (rid.ValueKind == JsonValueKind.String)
                                long.TryParse(rid.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out authorId);
                        }
                        if (string.IsNullOrEmpty(authorName) && author.TryGetProperty("roblox_username", out JsonElement un))
                            authorName = un.GetString() ?? string.Empty;
                    }

                    if (string.IsNullOrEmpty(authorName) && authorId > 0)
                        authorName = authorId.ToString(CultureInfo.InvariantCulture);

                    string content = r.TryGetProperty("content", out JsonElement cEl) ? (cEl.GetString() ?? string.Empty) : string.Empty;
                    if (content.Length > 0)
                        content = WebUtility.HtmlDecode(content).Trim();

                    long unixSec = 0;
                    if (r.TryGetProperty("time", out JsonElement timeEl))
                    {
                        if (timeEl.ValueKind == JsonValueKind.Number && timeEl.TryGetInt64(out long parsedNum))
                            unixSec = parsedNum;
                        else if (timeEl.ValueKind == JsonValueKind.String && long.TryParse(timeEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedStr))
                            unixSec = parsedStr;
                    }
                    if (unixSec > 1_000_000_000_000)
                        unixSec /= 1000;

                    string createdAt = FormatTimestamp(unixSec);
                    if (string.IsNullOrEmpty(createdAt) && r.TryGetProperty("created_at", out JsonElement caEl))
                    {
                        createdAt = caEl.GetString() ?? string.Empty;
                    }

                    int reviewUp = 0;
                    int reviewDown = 0;
                    if (r.TryGetProperty("score", out JsonElement scoreObj) && scoreObj.ValueKind == JsonValueKind.Object)
                    {
                        if (scoreObj.TryGetProperty("up", out JsonElement uEl) && uEl.TryGetInt32(out int u))
                            reviewUp = u;
                        if (scoreObj.TryGetProperty("down", out JsonElement dEl) && dEl.TryGetInt32(out int d))
                            reviewDown = d;
                    }

                    string? reviewUserVote = null;
                    if (_sessionUserId > 0 && r.TryGetProperty("rating", out JsonElement ratingObj) && ratingObj.ValueKind == JsonValueKind.Object)
                    {
                        if (ratingObj.TryGetProperty("up", out JsonElement rUp) && rUp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement v in rUp.EnumerateArray())
                            {
                                if (v.TryGetInt64(out long uid) && uid == _sessionUserId)
                                {
                                    reviewUserVote = "up";
                                    break;
                                }
                            }
                        }
                        if (reviewUserVote == null && ratingObj.TryGetProperty("down", out JsonElement rDown) && rDown.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement v in rDown.EnumerateArray())
                            {
                                if (v.TryGetInt64(out long uid) && uid == _sessionUserId)
                                {
                                    reviewUserVote = "down";
                                    break;
                                }
                            }
                        }
                    }

                    bool canDelete = (_sessionUserId > 0 && authorId == _sessionUserId)
                        || (!string.IsNullOrEmpty(_sessionUsername) && string.Equals(authorName, _sessionUsername, StringComparison.OrdinalIgnoreCase));

                    if (content.Length > 0)
                    {
                        ReviewEntry entry = new()
                        {
                            Id = id,
                            AuthorId = authorId,
                            AuthorName = authorName,
                            Content = content,
                            CreatedAt = createdAt,
                            UnixTime = unixSec,
                            UpCount = reviewUp,
                            DownCount = reviewDown,
                            UserVote = reviewUserVote,
                            CanDelete = canDelete
                        };
                        entry.RefreshRating();
                        reviews.Add(entry);
                    }
                }
            }

            reviews.Sort((a, b) => b.UnixTime.CompareTo(a.UnixTime));

            await PopulateAvatarsAsync(reviews, timeout.Token).ConfigureAwait(false);

            ReviewData data = new()
            {
                UpCount = upCount,
                DownCount = downCount,
                ReviewCount = reviews.Count > 0 ? reviews.Count : (root.TryGetProperty("reviews", out JsonElement ra) && ra.ValueKind == JsonValueKind.Array ? ra.GetArrayLength() : 0),
                UserVote = userVote,
                Reviews = reviews
            };
            ReviewsUpdated?.Invoke(placeId, data);
            return data;
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

    public static async Task<bool> RateReviewAsync(long placeId, string reviewId, string vote, CancellationToken ct)
    {
        if (!IsAuthenticated || placeId <= 0 || string.IsNullOrWhiteSpace(reviewId))
            return false;
        try
        {
            string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(reviewId) + "/rate?game=true";
            string body = "{\"vote\":\"" + vote + "\"}";
            using HttpRequestMessage req = new(HttpMethod.Post, url);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _sessionToken);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                App.Logger.WriteLine("RoReviewService", "RateReview returned " + (int)res.StatusCode);
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    ClearSession();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "RateReview failed: " + ex.Message);
            return false;
        }
    }

    public static async Task<bool> DeleteReviewAsync(long placeId, string reviewId, CancellationToken ct)
    {
        if (!IsAuthenticated || placeId <= 0 || string.IsNullOrWhiteSpace(reviewId))
            return false;
        try
        {
            string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(reviewId) + "?game=true";
            using HttpRequestMessage req = new(HttpMethod.Delete, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _sessionToken);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                App.Logger.WriteLine("RoReviewService", "DeleteReview returned " + (int)res.StatusCode);
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    ClearSession();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "DeleteReview failed: " + ex.Message);
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

    public static Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated)
            return Task.FromResult(true);

        lock (_authLockObj)
        {
            if (IsAuthenticated)
                return Task.FromResult(true);

            if (_currentAuthTask != null && !_currentAuthTask.IsCompleted)
                return _currentAuthTask;

            _currentAuthTask = Task.Run(() => DoAuthenticateAsync(ct), ct);
            return _currentAuthTask;
        }
    }

    private static async Task<bool> DoAuthenticateAsync(CancellationToken ct)
    {
        try
        {
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
                App.Logger.WriteLine("RoReviewService", "Challenge did not return bot id or session id");
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
                        AuthenticationChanged?.Invoke();

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string uUrl = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + _sessionUserId.ToString(CultureInfo.InvariantCulture) + "&size=150x150&format=Png&isCircular=true";
                                using HttpRequestMessage uReq = new(HttpMethod.Get, uUrl);
                                uReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
                                using HttpResponseMessage uRes = await RobloxClient.SendAsync(uReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                                if (uRes.IsSuccessStatusCode)
                                {
                                    string uJson = await Voidstrap.Utility.Http.ReadStringBoundedAsync(uRes.Content, MaxResponseBytes, ct).ConfigureAwait(false);
                                    using JsonDocument uDoc = JsonDocument.Parse(uJson);
                                    if (uDoc.RootElement.TryGetProperty("data", out JsonElement dArr) && dArr.ValueKind == JsonValueKind.Array && dArr.GetArrayLength() > 0)
                                    {
                                        if (dArr[0].TryGetProperty("imageUrl", out JsonElement img) && img.ValueKind == JsonValueKind.String)
                                        {
                                            string u = img.GetString() ?? string.Empty;
                                            if (u.Length > 0)
                                                _avatarCache[_sessionUserId] = u;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }, ct);

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
            if (res.StatusCode == HttpStatusCode.BadRequest)
            {
                string errBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (errBody.Contains("already", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
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
        AuthenticationChanged?.Invoke();
    }
}
