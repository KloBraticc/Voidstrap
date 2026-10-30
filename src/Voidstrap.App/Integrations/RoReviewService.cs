using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Resources;

namespace Voidstrap.Integrations;

public static class RoReviewService
{
    private const string ApiBase = "https://hermivore.cat";
    private const int MaxResponseBytes = 512 * 1024;

    private static readonly HttpClient Client = Voidstrap.Utility.VpnHttpClient.Create(
        TimeSpan.FromSeconds(12),
        handler =>
        {
            handler.UseCookies = false;
            handler.AllowAutoRedirect = false;
        }
    );

    private static readonly HttpClient RobloxClient = Voidstrap.Utility.VpnHttpClient.Create(
        TimeSpan.FromSeconds(12),
        handler =>
        {
            handler.UseCookies = false;
            handler.AllowAutoRedirect = false;
        }
    );

    private static readonly ConcurrentDictionary<long, string> _avatarCache = new();
    private static readonly ConcurrentDictionary<long, long> _fetchRequestGeneration = new();

    private static string? _sessionToken;
    private static long _sessionUserId;
    private static string? _sessionUsername;
    private static string _robloxCsrf = string.Empty;
    private static long _sessionGeneration;
    private static long _identityGeneration;
    private static DateTimeOffset _sessionValidatedAt;
    private static readonly object _authLockObj = new();
    private static Task<bool>? _currentAuthTask;
    private static CancellationTokenSource? _currentAuthCancellation;
    private static MutationContext? _currentAuthContext;

    private sealed record SessionSnapshot(string Token, long UserId, string? Username, long Generation);

    public sealed class MutationContext
    {
        internal readonly byte[]? _cookieFingerprint;
        internal readonly long _identityGeneration;
        internal readonly long _userId;

        internal MutationContext(byte[]? cookieFingerprint, long identityGeneration, long userId)
        {
            _cookieFingerprint = cookieFingerprint;
            _identityGeneration = identityGeneration;
            _userId = userId;
        }
    }

    public static bool IsAuthenticated
    {
        get
        {
            lock (_authLockObj)
                return !string.IsNullOrEmpty(_sessionToken);
        }
    }

    public static long SessionUserId
    {
        get
        {
            lock (_authLockObj)
                return _sessionUserId;
        }
    }

    public static string? SessionUsername
    {
        get
        {
            lock (_authLockObj)
                return _sessionUsername;
        }
    }

    public static long SessionGeneration
    {
        get
        {
            lock (_authLockObj)
                return _sessionGeneration;
        }
    }
    public static event Action? AuthenticationChanged;
    public static event Action<long, ReviewData>? ReviewsUpdated;

    public sealed class ReviewData
    {
        public int UpCount { get; init; }
        public int DownCount { get; init; }
        public int ReviewCount { get; init; }
        public string? UserVote { get; init; }
        public long SessionUserId { get; init; }
        public string? SessionUsername { get; init; }
        public long SessionGeneration { get; init; }
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

        public string Id
        {
            get => _id;
            set
            {
                _id = value;
                OnPropertyChanged(nameof(Id));
            }
        }
        public long AuthorId
        {
            get => _authorId;
            set
            {
                _authorId = value;
                OnPropertyChanged(nameof(AuthorId));
            }
        }
        public string AuthorName
        {
            get => _authorName;
            set
            {
                _authorName = value;
                OnPropertyChanged(nameof(AuthorName));
            }
        }
        public string AvatarUrl
        {
            get => _avatarUrl;
            set
            {
                _avatarUrl = value;
                OnPropertyChanged(nameof(AvatarUrl));
            }
        }
        public string Content
        {
            get => _content;
            set
            {
                _content = value;
                OnPropertyChanged(nameof(Content));
            }
        }
        public string CreatedAt
        {
            get => _createdAt;
            set
            {
                _createdAt = value;
                OnPropertyChanged(nameof(CreatedAt));
            }
        }
        public int UpCount
        {
            get => _upCount;
            set
            {
                _upCount = value;
                OnPropertyChanged(nameof(UpCount));
            }
        }
        public int DownCount
        {
            get => _downCount;
            set
            {
                _downCount = value;
                OnPropertyChanged(nameof(DownCount));
            }
        }
        public string? UserVote
        {
            get => _userVote;
            set
            {
                _userVote = value;
                OnPropertyChanged(nameof(UserVote));
                OnPropertyChanged(nameof(IsUpVoted));
                OnPropertyChanged(nameof(IsDownVoted));
                OnPropertyChanged(nameof(UpAppearance));
                OnPropertyChanged(nameof(DownAppearance));
            }
        }
        public string RatingText
        {
            get => _ratingText;
            set
            {
                _ratingText = value;
                OnPropertyChanged(nameof(RatingText));
            }
        }
        public bool CanDelete
        {
            get => _canDelete;
            set
            {
                _canDelete = value;
                OnPropertyChanged(nameof(CanDelete));
            }
        }
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
                RatingText = Strings.Ui_Session_NoRatings;
            }
            else
            {
                int pct = (int)Math.Round((double)UpCount / total * 100);
                string voteWord = total == 1 ? Strings.Ui_Session_Vote : Strings.Ui_Session_Votes;
                RatingText = string.Format(Locale.CurrentCulture, "{0}% {1} ({2} {3})", pct, Strings.Ui_Session_Positive, total, voteWord);
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

    private static SessionSnapshot? CaptureSession()
    {
        lock (_authLockObj)
        {
            if (string.IsNullOrEmpty(_sessionToken))
                return null;
            return new SessionSnapshot(_sessionToken, _sessionUserId, _sessionUsername, _sessionGeneration);
        }
    }

    private static bool IsCurrentSession(SessionSnapshot session)
    {
        lock (_authLockObj)
        {
            return _sessionGeneration == session.Generation
                && _sessionUserId == session.UserId
                && string.Equals(_sessionToken, session.Token, StringComparison.Ordinal);
        }
    }

    public static MutationContext CaptureMutationContext()
    {
        byte[]? cookieFingerprint = null;
        try
        {
            cookieFingerprint = CreateCookieFingerprint(RobloxCookie.Get());
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", "Could not capture Roblox account identity: " + ex.Message);
        }

        lock (_authLockObj)
        {
            long userId = string.IsNullOrEmpty(_sessionToken) ? 0 : _sessionUserId;
            return new MutationContext(cookieFingerprint, _identityGeneration, userId);
        }
    }

    private static byte[]? CreateCookieFingerprint(string? cookie) =>
        string.IsNullOrEmpty(cookie) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(cookie));

    private static bool IsMutationContextCurrent(MutationContext context, SessionSnapshot? session = null)
    {
        byte[]? capturedFingerprint = context._cookieFingerprint;
        if (capturedFingerprint == null)
            return false;

        string? cookie = RobloxCookie.Get();
        byte[]? fingerprint = CreateCookieFingerprint(cookie);
        if (fingerprint == null || !CryptographicOperations.FixedTimeEquals(capturedFingerprint, fingerprint))
            return false;

        lock (_authLockObj)
        {
            if (_identityGeneration != context._identityGeneration)
                return false;
            if (context._userId > 0 && (string.IsNullOrEmpty(_sessionToken) || _sessionUserId != context._userId))
                return false;
            if (
                session != null
                && (
                    _sessionGeneration != session.Generation
                    || _sessionUserId != session.UserId
                    || !string.Equals(_sessionToken, session.Token, StringComparison.Ordinal)
                )
            )
                return false;
            return true;
        }
    }

    private static bool IsSameMutationIdentity(MutationContext left, MutationContext right)
    {
        byte[]? leftFingerprint = left._cookieFingerprint;
        byte[]? rightFingerprint = right._cookieFingerprint;
        if (
            left._identityGeneration != right._identityGeneration
            || leftFingerprint == null
            || rightFingerprint == null
            || (left._userId > 0 && right._userId > 0 && left._userId != right._userId)
        )
            return false;
        return CryptographicOperations.FixedTimeEquals(leftFingerprint, rightFingerprint);
    }

    private static bool IsSessionGenerationCurrent(long generation)
    {
        lock (_authLockObj)
            return _sessionGeneration == generation;
    }

    private static long CurrentSessionGeneration
    {
        get
        {
            lock (_authLockObj)
                return _sessionGeneration;
        }
    }

    private static async Task<SessionSnapshot?> ValidateSessionAsync(CancellationToken ct, bool force = false)
    {
        SessionSnapshot? snapshot = CaptureSession();
        if (snapshot == null)
            return null;

        lock (_authLockObj)
        {
            if (_sessionGeneration != snapshot.Generation || !string.Equals(_sessionToken, snapshot.Token, StringComparison.Ordinal))
                return null;
            if (!force && DateTimeOffset.UtcNow - _sessionValidatedAt < TimeSpan.FromSeconds(15))
                return snapshot;
        }

        string? cookie = RobloxCookie.Get();
        if (string.IsNullOrEmpty(cookie))
        {
            ClearSessionIfCurrent(snapshot);
            return null;
        }

        RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, ct).ConfigureAwait(false);
        if (account == null)
            return null;
        if (account.UserId != snapshot.UserId)
        {
            ClearSessionIfCurrent(snapshot);
            return null;
        }

        lock (_authLockObj)
        {
            if (_sessionGeneration != snapshot.Generation || !string.Equals(_sessionToken, snapshot.Token, StringComparison.Ordinal))
                return null;
            _sessionValidatedAt = DateTimeOffset.UtcNow;
            return snapshot;
        }
    }

    private static bool ClearSessionIfCurrent(SessionSnapshot session, bool cancelPendingAuthentication = true)
    {
        CancellationTokenSource? cancellation = null;
        lock (_authLockObj)
        {
            if (_sessionGeneration != session.Generation || !string.Equals(_sessionToken, session.Token, StringComparison.Ordinal))
                return false;

            _sessionGeneration++;
            _identityGeneration++;
            _sessionToken = null;
            _sessionUserId = 0;
            _sessionUsername = null;
            _robloxCsrf = string.Empty;
            _sessionValidatedAt = default;

            if (cancelPendingAuthentication)
            {
                cancellation = _currentAuthCancellation;
                _currentAuthCancellation = null;
                _currentAuthTask = null;
                _currentAuthContext = null;
            }
        }

        CancelAuthentication(cancellation);
        AuthenticationChanged?.Invoke();
        return true;
    }

    private static void CancelAuthentication(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public static void InvalidateSession()
    {
        CancellationTokenSource? cancellation;
        lock (_authLockObj)
        {
            _sessionGeneration++;
            _identityGeneration++;
            _sessionToken = null;
            _sessionUserId = 0;
            _sessionUsername = null;
            _robloxCsrf = string.Empty;
            _sessionValidatedAt = default;
            cancellation = _currentAuthCancellation;
            _currentAuthCancellation = null;
            _currentAuthTask = null;
            _currentAuthContext = null;
        }

        CancelAuthentication(cancellation);
        AuthenticationChanged?.Invoke();
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
                return Strings.Ui_Session_JustNow;
            if (diff.TotalMinutes < 60 && diff.TotalMinutes >= 1)
                return string.Format(Locale.CurrentCulture, Strings.Ui_Session_MinutesAgo, (int)diff.TotalMinutes);
            if (diff.TotalHours < 24 && diff.TotalHours >= 1)
                return string.Format(Locale.CurrentCulture, Strings.Ui_Session_HoursAgo, (int)diff.TotalHours);
            return dt.ToLocalTime().ToString("d", Locale.CurrentCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp
            )
        )
            return FormatTimestamp(timestamp.ToUnixTimeSeconds());
        return value;
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
            string url =
                "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds="
                + string.Join(",", idsToFetch)
                + "&size=150x150&format=Png&isCircular=true";
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage res = await RobloxClient
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
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
        catch { }
    }

    public static async Task<ReviewData?> FetchReviewsAsync(long placeId, CancellationToken ct)
    {
        if (placeId <= 0)
            return null;

        long fetchGeneration = _fetchRequestGeneration.AddOrUpdate(placeId, 1, (_, current) => current + 1);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            SessionSnapshot? session = await ValidateSessionAsync(timeout.Token).ConfigureAwait(false);
            long sessionGeneration = session?.Generation ?? CurrentSessionGeneration;
            string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "?game=true";
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            if (session != null)
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.Token);

            using HttpResponseMessage res = await Client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            string json;
            if (
                !res.IsSuccessStatusCode
                && session != null
                && (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            )
            {
                ClearSessionIfCurrent(session);
                session = null;
                sessionGeneration = CurrentSessionGeneration;
                using HttpRequestMessage publicReq = new(HttpMethod.Get, url);
                publicReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
                using HttpResponseMessage publicRes = await Client
                    .SendAsync(publicReq, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!publicRes.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(
                        "RoReviewService",
                        "Review fetch returned "
                            + (int)publicRes.StatusCode
                            + " for place "
                            + placeId.ToString(CultureInfo.InvariantCulture)
                    );
                    return null;
                }
                json = await Voidstrap
                    .Utility.Http.ReadStringBoundedAsync(publicRes.Content, MaxResponseBytes, timeout.Token)
                    .ConfigureAwait(false);
            }
            else if (!res.IsSuccessStatusCode)
            {
                App.Logger.WriteLine(
                    "RoReviewService",
                    "Review fetch returned " + (int)res.StatusCode + " for place " + placeId.ToString(CultureInfo.InvariantCulture)
                );
                return null;
            }
            else
            {
                json = await Voidstrap
                    .Utility.Http.ReadStringBoundedAsync(res.Content, MaxResponseBytes, timeout.Token)
                    .ConfigureAwait(false);
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            long sessionUserId = session?.UserId ?? 0;
            string? sessionUsername = session?.Username;
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
            if (root.TryGetProperty("profile_rating", out JsonElement rating) && rating.ValueKind == JsonValueKind.Object)
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
            if (sessionUserId > 0)
            {
                if (upVoters.Contains(sessionUserId))
                    userVote = "up";
                else if (downVoters.Contains(sessionUserId))
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
                        if (
                            string.IsNullOrEmpty(authorName)
                            && author.TryGetProperty("roblox_username", out JsonElement un)
                            && un.ValueKind == JsonValueKind.String
                        )
                            authorName = un.GetString() ?? string.Empty;
                    }

                    if (string.IsNullOrEmpty(authorName) && authorId > 0)
                        authorName = authorId.ToString(CultureInfo.InvariantCulture);

                    string content =
                        r.TryGetProperty("content", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                            ? cEl.GetString() ?? string.Empty
                            : string.Empty;
                    if (content.Length > 0)
                        content = WebUtility.HtmlDecode(content).Trim();

                    long unixSec = 0;
                    if (r.TryGetProperty("time", out JsonElement timeEl))
                    {
                        if (timeEl.ValueKind == JsonValueKind.Number && timeEl.TryGetInt64(out long parsedNum))
                            unixSec = parsedNum;
                        else if (
                            timeEl.ValueKind == JsonValueKind.String
                            && long.TryParse(timeEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedStr)
                        )
                            unixSec = parsedStr;
                    }
                    if (unixSec > 1_000_000_000_000)
                        unixSec /= 1000;

                    string createdAt = FormatTimestamp(unixSec);
                    if (
                        string.IsNullOrEmpty(createdAt)
                        && r.TryGetProperty("created_at", out JsonElement caEl)
                        && caEl.ValueKind == JsonValueKind.String
                    )
                        createdAt = FormatTimestamp(caEl.GetString());

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
                    if (
                        sessionUserId > 0
                        && r.TryGetProperty("rating", out JsonElement ratingObj)
                        && ratingObj.ValueKind == JsonValueKind.Object
                    )
                    {
                        if (ratingObj.TryGetProperty("up", out JsonElement rUp) && rUp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement v in rUp.EnumerateArray())
                            {
                                if (v.TryGetInt64(out long uid) && uid == sessionUserId)
                                {
                                    reviewUserVote = "up";
                                    break;
                                }
                            }
                        }
                        if (
                            reviewUserVote == null
                            && ratingObj.TryGetProperty("down", out JsonElement rDown)
                            && rDown.ValueKind == JsonValueKind.Array
                        )
                        {
                            foreach (JsonElement v in rDown.EnumerateArray())
                            {
                                if (v.TryGetInt64(out long uid) && uid == sessionUserId)
                                {
                                    reviewUserVote = "down";
                                    break;
                                }
                            }
                        }
                    }

                    bool canDelete =
                        (sessionUserId > 0 && authorId == sessionUserId)
                        || (
                            !string.IsNullOrEmpty(sessionUsername)
                            && string.Equals(authorName, sessionUsername, StringComparison.OrdinalIgnoreCase)
                        );

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
                            CanDelete = canDelete,
                        };
                        entry.RefreshRating();
                        reviews.Add(entry);
                    }
                }
            }

            reviews.Sort((a, b) => b.UnixTime.CompareTo(a.UnixTime));
            await PopulateAvatarsAsync(reviews, timeout.Token).ConfigureAwait(false);

            if (
                !IsSessionGenerationCurrent(sessionGeneration)
                || (session != null && !IsCurrentSession(session))
                || !_fetchRequestGeneration.TryGetValue(placeId, out long currentFetchGeneration)
                || currentFetchGeneration != fetchGeneration
            )
                return null;

            ReviewData data = new()
            {
                UpCount = upCount,
                DownCount = downCount,
                ReviewCount =
                    reviews.Count > 0
                        ? reviews.Count
                        : (
                            root.TryGetProperty("reviews", out JsonElement ra) && ra.ValueKind == JsonValueKind.Array
                                ? ra.GetArrayLength()
                                : 0
                        ),
                UserVote = userVote,
                SessionUserId = sessionUserId,
                SessionUsername = sessionUsername,
                SessionGeneration = sessionGeneration,
                Reviews = reviews,
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

    public static Task<bool> VoteAsync(long placeId, string vote, MutationContext context, CancellationToken ct)
    {
        if (placeId <= 0 || !IsValidVote(vote))
            return Task.FromResult(false);
        string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "/rate?game=true";
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["vote"] = (vote ?? string.Empty).ToLowerInvariant() });
        return SendMutationAsync(HttpMethod.Post, url, body, "Vote", context, ct);
    }

    public static Task<bool> RateReviewAsync(long placeId, string reviewId, string vote, MutationContext context, CancellationToken ct)
    {
        if (
            placeId <= 0
            || string.IsNullOrWhiteSpace(reviewId)
            || reviewId.StartsWith("temp_", StringComparison.Ordinal)
            || !IsValidVote(vote)
        )
            return Task.FromResult(false);
        string url =
            ApiBase
            + "/api/roblox/reviews/"
            + placeId.ToString(CultureInfo.InvariantCulture)
            + "/"
            + Uri.EscapeDataString(reviewId)
            + "/rate?game=true";
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["vote"] = (vote ?? string.Empty).ToLowerInvariant() });
        return SendMutationAsync(HttpMethod.Post, url, body, "RateReview", context, ct);
    }

    public static Task<bool> DeleteReviewAsync(long placeId, string reviewId, MutationContext context, CancellationToken ct)
    {
        if (placeId <= 0 || string.IsNullOrWhiteSpace(reviewId) || reviewId.StartsWith("temp_", StringComparison.Ordinal))
            return Task.FromResult(false);
        string url =
            ApiBase
            + "/api/roblox/reviews/"
            + placeId.ToString(CultureInfo.InvariantCulture)
            + "/"
            + Uri.EscapeDataString(reviewId)
            + "?game=true";
        return SendMutationAsync(HttpMethod.Delete, url, null, "DeleteReview", context, ct);
    }

    public static Task<bool> SubmitReviewAsync(long placeId, string content, MutationContext context, CancellationToken ct)
    {
        string normalizedContent = content?.Trim() ?? string.Empty;
        if (placeId <= 0 || normalizedContent.Length == 0 || normalizedContent.Length > 500)
            return Task.FromResult(false);
        string url = ApiBase + "/api/roblox/reviews/" + placeId.ToString(CultureInfo.InvariantCulture) + "?game=true";
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = normalizedContent });
        return SendMutationAsync(HttpMethod.Post, url, body, "SubmitReview", context, ct);
    }

    private static bool IsValidVote(string vote) =>
        string.Equals(vote, "up", StringComparison.OrdinalIgnoreCase) || string.Equals(vote, "down", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> SendMutationAsync(
        HttpMethod method,
        string url,
        string? body,
        string operation,
        MutationContext context,
        CancellationToken ct
    )
    {
        try
        {
            if (!IsMutationContextCurrent(context))
                return false;
            SessionSnapshot? session = await ValidateSessionAsync(ct, true).ConfigureAwait(false);
            if (session == null || !IsCurrentSession(session) || !IsMutationContextCurrent(context, session))
                return false;

            using HttpRequestMessage req = new(method, url);
            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.Token);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            if (!IsCurrentSession(session) || !IsMutationContextCurrent(context, session))
                return false;

            using HttpResponseMessage res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                App.Logger.WriteLine("RoReviewService", operation + " returned " + (int)res.StatusCode);
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    ClearSessionIfCurrent(session);
                return false;
            }

            return IsCurrentSession(session) && IsMutationContextCurrent(context, session);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewService", operation + " failed: " + ex.Message);
            return false;
        }
    }

    public static Task<bool> AuthenticateAsync(MutationContext? context = null)
    {
        MutationContext expectedContext = context ?? CaptureMutationContext();
        if (!IsMutationContextCurrent(expectedContext))
            return Task.FromResult(false);

        Task<bool> authenticationTask;
        lock (_authLockObj)
        {
            if (_currentAuthTask != null && !_currentAuthTask.IsCompleted)
            {
                authenticationTask = _currentAuthTask;
                if (_currentAuthContext != null && IsSameMutationIdentity(expectedContext, _currentAuthContext))
                    return CompleteAuthenticationAsync(authenticationTask, expectedContext);
                return AuthenticateAfterCurrentAsync(authenticationTask, expectedContext);
            }

            CancellationTokenSource cancellation = new();
            long generation = _sessionGeneration;
            _currentAuthCancellation = cancellation;
            _currentAuthContext = expectedContext;
            _currentAuthTask = Task.Run(() => RunAuthenticationAsync(generation, expectedContext, cancellation));
            authenticationTask = _currentAuthTask;
        }

        return CompleteAuthenticationAsync(authenticationTask, expectedContext);
    }

    private static async Task<bool> CompleteAuthenticationAsync(Task<bool> authenticationTask, MutationContext context)
    {
        if (!await authenticationTask.ConfigureAwait(false) || !IsMutationContextCurrent(context))
            return false;
        SessionSnapshot? session = CaptureSession();
        return session != null && IsCurrentSession(session) && IsMutationContextCurrent(context, session);
    }

    private static async Task<bool> AuthenticateAfterCurrentAsync(Task<bool> authenticationTask, MutationContext context)
    {
        await authenticationTask.ConfigureAwait(false);
        if (!IsMutationContextCurrent(context))
            return false;
        return await AuthenticateAsync(context).ConfigureAwait(false);
    }

    private static async Task<bool> RunAuthenticationAsync(long generation, MutationContext context, CancellationTokenSource cancellation)
    {
        try
        {
            CancellationToken ct = cancellation.Token;
            if (!IsMutationContextCurrent(context))
                return false;
            SessionSnapshot? session = CaptureSession();
            if (session != null)
            {
                if (session.Generation != generation || !IsMutationContextCurrent(context, session))
                    return false;

                string? cookie = RobloxCookie.Get();
                if (string.IsNullOrEmpty(cookie))
                {
                    ClearSessionIfCurrent(session, false);
                    return false;
                }

                RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, ct).ConfigureAwait(false);
                if (account == null || !IsCurrentSession(session) || !IsMutationContextCurrent(context, session))
                    return false;
                if (account.UserId == session.UserId)
                {
                    lock (_authLockObj)
                    {
                        if (
                            _sessionGeneration != session.Generation
                            || !string.Equals(_sessionToken, session.Token, StringComparison.Ordinal)
                        )
                            return false;
                        _sessionValidatedAt = DateTimeOffset.UtcNow;
                    }
                    return true;
                }

                if (!ClearSessionIfCurrent(session, false))
                    return false;
                return false;
            }

            if (!IsSessionGenerationCurrent(generation) || !IsMutationContextCurrent(context))
                return false;
            return await DoAuthenticateAsync(generation, context, ct).ConfigureAwait(false);
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
            lock (_authLockObj)
            {
                if (ReferenceEquals(_currentAuthCancellation, cancellation))
                {
                    _currentAuthCancellation = null;
                    _currentAuthTask = null;
                    _currentAuthContext = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private static async Task<bool> DoAuthenticateAsync(long generation, MutationContext context, CancellationToken ct)
    {
        try
        {
            if (!IsMutationContextCurrent(context))
                return false;
            string? cookie = RobloxCookie.Get();
            if (string.IsNullOrEmpty(cookie))
            {
                App.Logger.WriteLine("RoReviewService", "No Roblox cookie available for auth");
                return false;
            }

            RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, ct).ConfigureAwait(false);
            if (
                account == null
                || account.UserId <= 0
                || !IsSessionGenerationCurrent(generation)
                || !IsMutationContextCurrent(context)
                || (context._userId > 0 && account.UserId != context._userId)
            )
            {
                App.Logger.WriteLine("RoReviewService", "Could not resolve Roblox account");
                return false;
            }

            using HttpRequestMessage challengeReq = new(HttpMethod.Post, ApiBase + "/api/roblox/verify/challenge");
            challengeReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            using HttpResponseMessage challengeRes = await Client
                .SendAsync(challengeReq, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!challengeRes.IsSuccessStatusCode || !IsSessionGenerationCurrent(generation) || !IsMutationContextCurrent(context))
            {
                App.Logger.WriteLine("RoReviewService", "Verify challenge returned " + (int)challengeRes.StatusCode);
                return false;
            }

            string challengeJson = await Voidstrap
                .Utility.Http.ReadStringBoundedAsync(challengeRes.Content, MaxResponseBytes, ct)
                .ConfigureAwait(false);
            using JsonDocument challengeDoc = JsonDocument.Parse(challengeJson);
            JsonElement cRoot = challengeDoc.RootElement;
            if (cRoot.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.True)
            {
                string reason =
                    cRoot.TryGetProperty("reason", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString() ?? "unknown"
                        : "unknown";
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
            string sessionId =
                cRoot.TryGetProperty("session_id", out JsonElement sidEl) && sidEl.ValueKind == JsonValueKind.String
                    ? sidEl.GetString() ?? string.Empty
                    : string.Empty;
            if (botId <= 0 || sessionId.Length == 0 || !IsSessionGenerationCurrent(generation) || !IsMutationContextCurrent(context))
            {
                App.Logger.WriteLine("RoReviewService", "Challenge did not return bot id or session id");
                return false;
            }

            if (!IsMutationContextCurrent(context))
                return false;
            bool friended = await SendFriendRequestAsync(botId, cookie, generation, ct).ConfigureAwait(false);
            if (!friended)
            {
                App.Logger.WriteLine("RoReviewService", "Could not send friend request to verification bot");
                return false;
            }

            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                if (!IsSessionGenerationCurrent(generation) || !IsMutationContextCurrent(context))
                    return false;

                using HttpRequestMessage statusReq = new(
                    HttpMethod.Get,
                    ApiBase + "/api/roblox/verify/status/" + Uri.EscapeDataString(sessionId)
                );
                statusReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
                using HttpResponseMessage statusRes = await Client
                    .SendAsync(statusReq, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!IsSessionGenerationCurrent(generation) || !IsMutationContextCurrent(context))
                    return false;
                if (!statusRes.IsSuccessStatusCode)
                    continue;

                string statusJson = await Voidstrap
                    .Utility.Http.ReadStringBoundedAsync(statusRes.Content, MaxResponseBytes, ct)
                    .ConfigureAwait(false);
                using JsonDocument statusDoc = JsonDocument.Parse(statusJson);
                JsonElement sRoot = statusDoc.RootElement;
                string status =
                    sRoot.TryGetProperty("status", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.String
                        ? stEl.GetString() ?? string.Empty
                        : string.Empty;
                if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    string? token =
                        sRoot.TryGetProperty("session_token", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                            ? tEl.GetString()
                            : null;
                    long userId = 0;
                    if (sRoot.TryGetProperty("user_id", out JsonElement uidEl))
                    {
                        if (uidEl.ValueKind == JsonValueKind.Number)
                            uidEl.TryGetInt64(out userId);
                        else if (uidEl.ValueKind == JsonValueKind.String)
                            long.TryParse(uidEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);
                    }
                    if (
                        userId <= 0
                        || userId != account.UserId
                        || string.IsNullOrWhiteSpace(token)
                        || (context._userId > 0 && userId != context._userId)
                        || !IsMutationContextCurrent(context)
                    )
                        return false;

                    string? username =
                        sRoot.TryGetProperty("username", out JsonElement unEl) && unEl.ValueKind == JsonValueKind.String
                            ? unEl.GetString()
                            : null;
                    long authenticatedUserId = userId;
                    string authenticatedUsername = string.IsNullOrWhiteSpace(username) ? account.Username : username;
                    lock (_authLockObj)
                    {
                        if (
                            _sessionGeneration != generation
                            || _identityGeneration != context._identityGeneration
                            || ct.IsCancellationRequested
                        )
                            return false;
                        _sessionToken = token;
                        _sessionUserId = authenticatedUserId;
                        _sessionUsername = authenticatedUsername;
                        _sessionValidatedAt = DateTimeOffset.UtcNow;
                        _sessionGeneration++;
                    }

                    App.Logger.WriteLine("RoReviewService", "Authenticated with the signed in account");
                    AuthenticationChanged?.Invoke();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string uUrl =
                                "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds="
                                + authenticatedUserId.ToString(CultureInfo.InvariantCulture)
                                + "&size=150x150&format=Png&isCircular=true";
                            using HttpRequestMessage uReq = new(HttpMethod.Get, uUrl);
                            uReq.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
                            using HttpResponseMessage uRes = await RobloxClient
                                .SendAsync(uReq, HttpCompletionOption.ResponseHeadersRead)
                                .ConfigureAwait(false);
                            if (!uRes.IsSuccessStatusCode)
                                return;
                            string uJson = await Voidstrap
                                .Utility.Http.ReadStringBoundedAsync(uRes.Content, MaxResponseBytes, CancellationToken.None)
                                .ConfigureAwait(false);
                            using JsonDocument uDoc = JsonDocument.Parse(uJson);
                            if (
                                uDoc.RootElement.TryGetProperty("data", out JsonElement dArr)
                                && dArr.ValueKind == JsonValueKind.Array
                                && dArr.GetArrayLength() > 0
                                && dArr[0].TryGetProperty("imageUrl", out JsonElement img)
                                && img.ValueKind == JsonValueKind.String
                            )
                            {
                                string avatar = img.GetString() ?? string.Empty;
                                if (avatar.Length > 0)
                                    _avatarCache[authenticatedUserId] = avatar;
                            }
                        }
                        catch { }
                    });

                    return true;
                }

                if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
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

    private static async Task<bool> SendFriendRequestAsync(long botId, string cookie, long generation, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string csrf;
            lock (_authLockObj)
            {
                if (_sessionGeneration != generation || ct.IsCancellationRequested)
                    return false;
                csrf = _robloxCsrf;
            }

            if (!string.Equals(RobloxCookie.Get(), cookie, StringComparison.Ordinal) || !IsSessionGenerationCurrent(generation))
                return false;

            using HttpRequestMessage req = new(
                HttpMethod.Post,
                "https://friends.roblox.com/v1/users/" + botId.ToString(CultureInfo.InvariantCulture) + "/request-friendship"
            );
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            if (csrf.Length > 0)
                req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);

            using HttpResponseMessage res = await RobloxClient
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!IsSessionGenerationCurrent(generation))
                return false;
            if (
                res.StatusCode == HttpStatusCode.Forbidden
                && res.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values)
                && values.FirstOrDefault() is { Length: > 0 } fresh
                && !string.Equals(fresh, csrf, StringComparison.Ordinal)
            )
            {
                lock (_authLockObj)
                {
                    if (_sessionGeneration != generation)
                        return false;
                    _robloxCsrf = fresh;
                }
                continue;
            }
            if (res.IsSuccessStatusCode)
                return true;
            if (res.StatusCode == HttpStatusCode.BadRequest)
            {
                string errBody = await Voidstrap
                    .Utility.Http.ReadStringBoundedAsync(res.Content, MaxResponseBytes, ct)
                    .ConfigureAwait(false);
                if (errBody.Contains("already", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            App.Logger.WriteLine("RoReviewService", "Friend request returned " + (int)res.StatusCode);
            return false;
        }
        return false;
    }
}
