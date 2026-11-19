using System.Collections.Concurrent;

namespace Voidstrap.Integrations;

public static class RobloxGameFriends
{
    private const int MaxFriendsChecked = 200;

    private const int Concurrency = 4;

    private const int MaxActivity = 40;

    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(30);

    private static readonly TimeSpan ReportLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

    private static readonly ConcurrentDictionary<long, (DateTime At, GameFriendsReport Report)> Reports = new();

    private static readonly ConcurrentDictionary<long, Lazy<Task<GameFriendsReport>>> Pending = new();

    public static bool TryGetCached(long universeId, out GameFriendsReport? report)
    {
        report = null;
        if (!Reports.TryGetValue(universeId, out var entry) || DateTime.UtcNow - entry.At > ReportLifetime)
            return false;
        report = entry.Report;
        return true;
    }

    public static async Task<GameFriendsReport> GetAsync(long universeId, CancellationToken token)
    {
        if (TryGetCached(universeId, out GameFriendsReport? cached) && cached != null)
            return cached;
        Lazy<Task<GameFriendsReport>> pending = Pending.GetOrAdd(
            universeId,
            id => new Lazy<Task<GameFriendsReport>>(() => BuildAndStoreAsync(id))
        );
        return await pending.Value.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<GameFriendsReport> BuildAndStoreAsync(long universeId)
    {
        const string LOG_IDENT = "RobloxGameFriends::BuildAndStoreAsync";

        using CancellationTokenSource timeout = new(BuildTimeout);
        try
        {
            GameFriendsReport report = await BuildAsync(universeId, timeout.Token).ConfigureAwait(false);
            if (report.Status != GameFriendsStatus.Unavailable)
                Reports[universeId] = (DateTime.UtcNow, report);
            return report;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friends who play could not be loaded for universe " + universeId + ": " + ex.Message);
            return new GameFriendsReport { Status = GameFriendsStatus.Unavailable };
        }
        finally
        {
            Pending.TryRemove(universeId, out _);
        }
    }

    private static async Task<GameFriendsReport> BuildAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::BuildAsync";

        long userId = await RobloxBadges.GetSignedInUserIdAsync(token).ConfigureAwait(false);
        if (userId <= 0)
            return new GameFriendsReport { Status = GameFriendsStatus.NotSignedIn };
        List<long>? friendIds = await RobloxPresence.GetFriendIdsAsync(userId, token).ConfigureAwait(false);
        if (friendIds == null)
            return new GameFriendsReport { Status = GameFriendsStatus.Unavailable };
        if (friendIds.Count == 0)
            return new GameFriendsReport { Status = GameFriendsStatus.Ready };

        Task<List<FriendPresence>?> presenceTask = RobloxPresence.GetFriendPresencesAsync(userId, token);
        IReadOnlyList<RobloxBadges.Badge> badges;
        try
        {
            badges = await RobloxBadges.GetBadgeListAsync(universeId, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The badge list could not be loaded: " + ex.Message);
            badges = [];
        }

        List<long> checkedIds = friendIds.Take(MaxFriendsChecked).ToList();
        Dictionary<long, IReadOnlyDictionary<long, DateTime>> awardedByFriend = new();
        if (badges.Count > 0)
        {
            using SemaphoreSlim gate = new(Concurrency, Concurrency);
            object sync = new();
            await Task.WhenAll(
                    checkedIds.Select(async friendId =>
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            IReadOnlyDictionary<long, DateTime>? awarded = await RobloxBadges
                                .GetAwardedForUserAsync(friendId, universeId, badges, token)
                                .ConfigureAwait(false);
                            if (awarded is { Count: > 0 })
                            {
                                lock (sync)
                                    awardedByFriend[friendId] = awarded;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Badges of one friend could not be checked: " + ex.Message);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    })
                )
                .ConfigureAwait(false);
        }

        HashSet<long> playing = new();
        try
        {
            List<FriendPresence>? presences = await presenceTask.ConfigureAwait(false);
            foreach (FriendPresence presence in presences ?? [])
            {
                if (presence.InGame && presence.UniverseId == universeId)
                    playing.Add(presence.UserId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friend presence could not be loaded: " + ex.Message);
        }

        DateTime now = DateTime.UtcNow;
        Dictionary<long, string> badgeNames = new();
        foreach (RobloxBadges.Badge badge in badges)
            badgeNames[badge.Id] = badge.Name;
        List<GameFriend> friends = awardedByFriend
            .Keys.Union(playing)
            .Select(id =>
            {
                awardedByFriend.TryGetValue(id, out IReadOnlyDictionary<long, DateTime>? awarded);
                return new GameFriend
                {
                    UserId = id,
                    InGame = playing.Contains(id),
                    Earned = awarded?.Count ?? 0,
                    Total = badges.Count,
                    LastAwardUtc = awarded is { Count: > 0 } ? awarded.Values.Max() : DateTime.MinValue,
                };
            })
            .ToList();

        List<GameFriendActivity> activity = new();
        foreach ((long friendId, IReadOnlyDictionary<long, DateTime> awarded) in awardedByFriend)
        {
            DateTime first = DateTime.MaxValue;
            foreach ((long badgeId, DateTime when) in awarded)
            {
                if (when == DateTime.MinValue)
                    continue;
                if (when < first)
                    first = when;
                if (now - when > ActivityWindow)
                    continue;
                activity.Add(
                    new GameFriendActivity
                    {
                        UserId = friendId,
                        WhenUtc = when,
                        BadgeId = badgeId,
                        BadgeName = badgeNames.GetValueOrDefault(badgeId, ""),
                    }
                );
            }
            if (first != DateTime.MaxValue && now - first <= ActivityWindow)
                activity.Add(new GameFriendActivity { UserId = friendId, WhenUtc = first.AddTicks(-1), FirstPlay = true });
        }
        activity = activity.OrderByDescending(entry => entry.WhenUtc).Take(MaxActivity).ToList();

        List<long> shownIds = friends.Select(friend => friend.UserId).Distinct().ToList();
        if (shownIds.Count > 0)
        {
            List<ServerFriend> profiles = await RobloxPresence.GetProfilesAsync(shownIds, token).ConfigureAwait(false);
            Dictionary<long, ServerFriend> byId = new();
            foreach (ServerFriend profile in profiles)
                byId.TryAdd(profile.UserId, profile);
            foreach (GameFriend friend in friends)
                ApplyProfile(byId, friend.UserId, name => friend.Name = name, url => friend.HeadshotUrl = url);
            foreach (GameFriendActivity entry in activity)
                ApplyProfile(byId, entry.UserId, name => entry.Name = name, url => entry.HeadshotUrl = url);
        }

        List<long> iconIds = activity.Where(entry => !entry.FirstPlay).Select(entry => entry.BadgeId).Distinct().ToList();
        if (iconIds.Count > 0)
        {
            try
            {
                IReadOnlyDictionary<long, string> icons = await RobloxBadges.GetIconsAsync(iconIds, token).ConfigureAwait(false);
                foreach (GameFriendActivity entry in activity)
                    entry.BadgeIconUrl = icons.GetValueOrDefault(entry.BadgeId, "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Badge icons could not be loaded: " + ex.Message);
            }
        }

        List<GameFriend> recent = friends
            .Where(friend => friend.InGame || now - friend.LastAwardUtc <= RecentWindow)
            .OrderByDescending(friend => friend.InGame)
            .ThenByDescending(friend => friend.LastAwardUtc)
            .ToList();
        List<GameFriend> previous = friends.Except(recent).OrderByDescending(friend => friend.LastAwardUtc).ToList();
        App.Logger.WriteLine(
            LOG_IDENT,
            $"Checked {checkedIds.Count} of {friendIds.Count} friends for universe {universeId}: {recent.Count} recent, {previous.Count} previous, {activity.Count} activity entries"
        );
        return new GameFriendsReport
        {
            Status = GameFriendsStatus.Ready,
            Recent = recent,
            Previous = previous,
            Activity = activity,
        };
    }

    private static void ApplyProfile(Dictionary<long, ServerFriend> profiles, long userId, Action<string> setName, Action<string> setHeadshot)
    {
        if (!profiles.TryGetValue(userId, out ServerFriend? profile))
        {
            setName(userId.ToString(CultureInfo.InvariantCulture));
            return;
        }
        setName(profile.Label);
        setHeadshot(profile.HeadshotUrl);
    }
}
