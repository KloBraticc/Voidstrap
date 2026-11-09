namespace Voidstrap.Integrations;

public enum FriendPresenceKind
{
    Offline = 0,
    Online = 1,
    InGame = 2,
    InStudio = 3,
}

public sealed class FriendPresenceInfo
{
    public FriendPresenceKind Kind { get; init; }

    public string LastLocation { get; init; } = string.Empty;

    public long PlaceId { get; init; }

    public string GameId { get; init; } = string.Empty;
}

public sealed class FriendRequestInfo
{
    public long UserId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public DateTimeOffset? SentUtc { get; init; }
}

public static class RobloxFriends
{
    private const string FriendsApi = "https://friends.roblox.com/v1/";
    private const int PresenceBatch = 100;
    private const int TagBatch = 50;

    private static string? Cookie() => RobloxCookie.Get();

    private static async Task<RobloxChatStatus> PostAsync(string url, object? body, CancellationToken token)
    {
        string? cookie = Cookie();
        if (string.IsNullOrEmpty(cookie))
            return RobloxChatStatus.NotSignedIn;
        (RobloxChatStatus status, JsonDocument? document) = await RobloxChat
            .SendAsync(HttpMethod.Post, url, JsonSerializer.Serialize(body ?? new { }), cookie, token)
            .ConfigureAwait(false);
        document?.Dispose();
        return status;
    }

    public static Task<RobloxChatStatus> AcceptAsync(long userId, CancellationToken token) =>
        PostAsync(FriendsApi + "users/" + userId.ToString(CultureInfo.InvariantCulture) + "/accept-friend-request", null, token);

    public static Task<RobloxChatStatus> DeclineAsync(long userId, CancellationToken token) =>
        PostAsync(FriendsApi + "users/" + userId.ToString(CultureInfo.InvariantCulture) + "/decline-friend-request", null, token);

    public static Task<RobloxChatStatus> DeclineAllAsync(CancellationToken token) =>
        PostAsync(FriendsApi + "user/friend-requests/decline-all", null, token);

    public static Task<RobloxChatStatus> UnfriendAsync(long userId, CancellationToken token) =>
        PostAsync(FriendsApi + "users/" + userId.ToString(CultureInfo.InvariantCulture) + "/unfriend", null, token);

    public static Task<RobloxChatStatus> SendRequestAsync(long userId, CancellationToken token) =>
        PostAsync(
            FriendsApi + "users/" + userId.ToString(CultureInfo.InvariantCulture) + "/request-friendship",
            new { friendshipOriginSourceType = "UserProfile" },
            token
        );

    public static Task<RobloxChatStatus> SetNicknameAsync(long userId, string nickname, CancellationToken token) =>
        PostAsync("https://contacts.roblox.com/v1/user/tag", new { targetUserId = userId, userTag = nickname.Trim() }, token);

    public static async Task<int> GetRequestCountAsync(CancellationToken token)
    {
        string? cookie = Cookie();
        if (string.IsNullOrEmpty(cookie))
            return 0;
        (_, JsonDocument? document) = await RobloxChat
            .SendAsync(HttpMethod.Get, FriendsApi + "user/friend-requests/count", null, cookie, token)
            .ConfigureAwait(false);
        using (document)
            return document == null ? 0 : (int)RobloxChat.ReadLong(document.RootElement, "count");
    }

    public static async Task<RobloxChatResult<List<FriendRequestInfo>>> GetRequestsAsync(CancellationToken token)
    {
        string? cookie = Cookie();
        if (string.IsNullOrEmpty(cookie))
            return RobloxChatResult<List<FriendRequestInfo>>.Fail(RobloxChatStatus.NotSignedIn);
        List<FriendRequestInfo> requests = new();
        string? cursor = null;
        for (int page = 0; page < 10; page++)
        {
            string url = FriendsApi + "my/friends/requests?limit=50&sortOrder=Desc" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : string.Empty);
            (RobloxChatStatus status, JsonDocument? document) = await RobloxChat.SendAsync(HttpMethod.Get, url, null, cookie, token).ConfigureAwait(false);
            using (document)
            {
                if (document == null)
                    return page == 0 ? RobloxChatResult<List<FriendRequestInfo>>.Fail(status) : Ready(requests);
                if (RobloxChat.TryGetArray(document.RootElement, out JsonElement items, "data"))
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        long id = RobloxChat.ReadLong(item, "id");
                        if (id <= 0)
                            continue;
                        DateTimeOffset? sent = item.TryGetProperty("friendRequest", out JsonElement request) ? RobloxChat.ReadTime(request, "sentAt") : null;
                        requests.Add(
                            new FriendRequestInfo
                            {
                                UserId = id,
                                Name = RobloxChat.ReadString(item, "name"),
                                DisplayName = RobloxChat.ReadString(item, "displayName"),
                                SentUtc = sent,
                            }
                        );
                    }
                }
                cursor = RobloxChat.ReadCursor(document.RootElement);
            }
            if (string.IsNullOrEmpty(cursor))
                break;
        }
        List<long> missing = requests.Where(static request => request.Name.Length == 0).Select(static request => request.UserId).ToList();
        if (missing.Count > 0)
        {
            await RobloxChat.ResolveUsersAsync(missing, token).ConfigureAwait(false);
            requests = requests
                .Select(request =>
                    request.Name.Length > 0 || RobloxChat.GetCachedUser(request.UserId) is not RobloxChatUser user
                        ? request
                        : new FriendRequestInfo { UserId = request.UserId, Name = user.Name, DisplayName = user.DisplayName, SentUtc = request.SentUtc }
                )
                .ToList();
        }
        return Ready(requests);
    }

    public static async Task<Dictionary<long, FriendPresenceInfo>> GetPresenceAsync(IReadOnlyList<long> userIds, CancellationToken token)
    {
        Dictionary<long, FriendPresenceInfo> result = new();
        string? cookie = Cookie();
        for (int i = 0; i < userIds.Count; i += PresenceBatch)
        {
            string body = JsonSerializer.Serialize(new { userIds = userIds.Skip(i).Take(PresenceBatch) });
            (_, JsonDocument? document) = await RobloxChat
                .SendAsync(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", body, cookie, token)
                .ConfigureAwait(false);
            using (document)
            {
                if (document == null || !RobloxChat.TryGetArray(document.RootElement, out JsonElement items, "userPresences"))
                    continue;
                foreach (JsonElement item in items.EnumerateArray())
                {
                    long id = RobloxChat.ReadLong(item, "userId");
                    if (id <= 0)
                        continue;
                    long type = RobloxChat.ReadLong(item, "userPresenceType");
                    result[id] = new FriendPresenceInfo
                    {
                        Kind = type is >= 0 and <= 3 ? (FriendPresenceKind)type : FriendPresenceKind.Offline,
                        LastLocation = RobloxChat.ReadString(item, "lastLocation"),
                        PlaceId = RobloxChat.ReadLong(item, "placeId", "rootPlaceId"),
                        GameId = RobloxChat.ReadString(item, "gameId"),
                    };
                }
            }
        }
        return result;
    }

    public static async Task<Dictionary<long, DateTimeOffset>> GetLastOnlineAsync(IReadOnlyList<long> userIds, CancellationToken token)
    {
        Dictionary<long, DateTimeOffset> result = new();
        string? cookie = Cookie();
        for (int i = 0; i < userIds.Count; i += PresenceBatch)
        {
            string body = JsonSerializer.Serialize(new { userIds = userIds.Skip(i).Take(PresenceBatch) });
            (_, JsonDocument? document) = await RobloxChat
                .SendAsync(HttpMethod.Post, "https://presence.roblox.com/v1/presence/last-online", body, cookie, token)
                .ConfigureAwait(false);
            using (document)
            {
                if (document == null || !RobloxChat.TryGetArray(document.RootElement, out JsonElement items, "lastOnlineTimestamps"))
                    continue;
                foreach (JsonElement item in items.EnumerateArray())
                {
                    long id = RobloxChat.ReadLong(item, "userId");
                    if (id > 0 && RobloxChat.ReadTime(item, "lastOnline") is DateTimeOffset when)
                        result[id] = when;
                }
            }
        }
        return result;
    }

    public static async Task<Dictionary<long, string>> GetNicknamesAsync(IReadOnlyList<long> userIds, CancellationToken token)
    {
        Dictionary<long, string> result = new();
        string? cookie = Cookie();
        if (string.IsNullOrEmpty(cookie))
            return result;
        for (int i = 0; i < userIds.Count; i += TagBatch)
        {
            string body = JsonSerializer.Serialize(new { targetUserIds = userIds.Skip(i).Take(TagBatch) });
            (_, JsonDocument? document) = await RobloxChat
                .SendAsync(HttpMethod.Post, "https://contacts.roblox.com/v1/user/get-tags", body, cookie, token)
                .ConfigureAwait(false);
            using (document)
            {
                if (document == null || !RobloxChat.TryGetArray(document.RootElement, out JsonElement items, "data"))
                    continue;
                foreach (JsonElement item in items.EnumerateArray())
                {
                    long id = RobloxChat.ReadLong(item, "targetUserId");
                    string tag = RobloxChat.ReadString(item, "targetUserTag");
                    if (id > 0 && tag.Length > 0)
                        result[id] = tag;
                }
            }
        }
        return result;
    }

    public static async Task<long> FindUserIdAsync(string username, CancellationToken token)
    {
        string name = username.Trim().TrimStart('@');
        if (name.Length is < 3 or > 20)
            return 0;
        string body = JsonSerializer.Serialize(new { usernames = new[] { name }, excludeBannedUsers = true });
        (_, JsonDocument? document) = await RobloxChat
            .SendAsync(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users", body, null, token)
            .ConfigureAwait(false);
        using (document)
        {
            if (document == null || !RobloxChat.TryGetArray(document.RootElement, out JsonElement items, "data"))
                return 0;
            foreach (JsonElement item in items.EnumerateArray())
                return RobloxChat.ReadLong(item, "id");
        }
        return 0;
    }

    private static RobloxChatResult<List<FriendRequestInfo>> Ready(List<FriendRequestInfo> value) =>
        new() { Status = RobloxChatStatus.Ready, Value = value };
}
