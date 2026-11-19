using System.Collections.Concurrent;

namespace Voidstrap.Integrations;

public static class RobloxGameFriends
{
    private const int MaxFriendsChecked = 200;

    private const int Concurrency = 8;

    private const int MaxActivity = 40;

    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(350);

    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(30);

    private static readonly TimeSpan ReportLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

    private static readonly ConcurrentDictionary<long, (DateTime At, GameFriendsReport Report)> Reports = new();

    private static readonly ConcurrentDictionary<long, GameFriendsReport> Live = new();

    private static readonly ConcurrentDictionary<long, Lazy<Task>> Pending = new();

    public static event Action<long, GameFriendsReport>? ReportUpdated;

    public static bool TryGetCurrent(long universeId, out GameFriendsReport? report)
    {
        if (Reports.TryGetValue(universeId, out var entry) && DateTime.UtcNow - entry.At <= ReportLifetime)
        {
            report = entry.Report;
            return true;
        }
        return Live.TryGetValue(universeId, out report);
    }

    public static void Start(long universeId)
    {
        if (universeId <= 0)
            return;
        if (Reports.TryGetValue(universeId, out var entry) && DateTime.UtcNow - entry.At <= ReportLifetime)
            return;
        _ = Pending.GetOrAdd(universeId, id => new Lazy<Task>(() => BuildAndStoreAsync(id))).Value;
    }

    private static void Publish(long universeId, GameFriendsReport report)
    {
        if (report.Complete)
        {
            Live.TryRemove(universeId, out _);
            if (report.Status != GameFriendsStatus.Unavailable)
                Reports[universeId] = (DateTime.UtcNow, report);
        }
        else
        {
            Live[universeId] = report;
        }
        ReportUpdated?.Invoke(universeId, report);
    }

    private static async Task BuildAndStoreAsync(long universeId)
    {
        const string LOG_IDENT = "RobloxGameFriends::BuildAndStoreAsync";

        await Task.Yield();
        using CancellationTokenSource timeout = new(BuildTimeout);
        GameFriendsReport report;
        try
        {
            report = await BuildAsync(universeId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friends who play could not be loaded for universe " + universeId + ": " + ex.Message);
            report = new GameFriendsReport { Status = GameFriendsStatus.Unavailable, Complete = true };
        }
        finally
        {
            Pending.TryRemove(universeId, out _);
        }
        Publish(universeId, report);
    }

    private static async Task<GameFriendsReport> BuildAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::BuildAsync";

        long userId = await RobloxBadges.GetSignedInUserIdAsync(token).ConfigureAwait(false);
        if (userId <= 0)
            return new GameFriendsReport { Status = GameFriendsStatus.NotSignedIn, Complete = true };
        Task<IReadOnlyList<RobloxBadges.Badge>> badgesTask = LoadBadgesAsync(universeId, token);
        Task<HashSet<long>> playingTask = LoadPlayingAsync(userId, universeId, token);
        List<long>? friendIds = await RobloxPresence.GetFriendIdsAsync(userId, token).ConfigureAwait(false);
        if (friendIds == null)
            return new GameFriendsReport { Status = GameFriendsStatus.Unavailable, Complete = true };
        if (friendIds.Count == 0)
            return new GameFriendsReport { Status = GameFriendsStatus.Ready, Complete = true };

        Snapshot state = new();
        state.Playing = await playingTask.ConfigureAwait(false);
        await EnrichAsync(state, token).ConfigureAwait(false);
        Publish(universeId, Compose(state, false));

        state.Badges = await badgesTask.ConfigureAwait(false);
        List<long> checkedIds = friendIds
            .Take(MaxFriendsChecked)
            .OrderByDescending(id => state.Playing.Contains(id))
            .ToList();
        if (state.Badges.Count > 0)
        {
            ConcurrentDictionary<long, IReadOnlyDictionary<long, DateTime>> found = new();
            int changed = 0;
            using SemaphoreSlim gate = new(Concurrency, Concurrency);
            Task work = Task.WhenAll(
                checkedIds.Select(async friendId =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        IReadOnlyDictionary<long, DateTime>? awarded = await RobloxBadges
                            .GetAwardedForUserAsync(friendId, universeId, state.Badges, token)
                            .ConfigureAwait(false);
                        if (awarded is { Count: > 0 })
                        {
                            found[friendId] = awarded;
                            Interlocked.Exchange(ref changed, 1);
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
            );
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(PublishInterval, token)).ConfigureAwait(false);
                if (work.IsCompleted || Interlocked.Exchange(ref changed, 0) == 0)
                    continue;
                state.Awarded = new Dictionary<long, IReadOnlyDictionary<long, DateTime>>(found);
                await EnrichAsync(state, token).ConfigureAwait(false);
                Publish(universeId, Compose(state, false));
            }
            await work.ConfigureAwait(false);
            state.Awarded = new Dictionary<long, IReadOnlyDictionary<long, DateTime>>(found);
        }

        await EnrichAsync(state, token).ConfigureAwait(false);
        GameFriendsReport report = Compose(state, true);
        App.Logger.WriteLine(
            LOG_IDENT,
            $"Checked {checkedIds.Count} of {friendIds.Count} friends for universe {universeId}: {report.Recent.Count} recent, {report.Previous.Count} previous, {report.Activity.Count} activity entries"
        );
        return report;
    }

    private static async Task<IReadOnlyList<RobloxBadges.Badge>> LoadBadgesAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::LoadBadgesAsync";

        try
        {
            return await RobloxBadges.GetBadgeListAsync(universeId, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The badge list could not be loaded: " + ex.Message);
            return [];
        }
    }

    private static async Task<HashSet<long>> LoadPlayingAsync(long userId, long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::LoadPlayingAsync";

        HashSet<long> playing = new();
        try
        {
            List<FriendPresence>? presences = await RobloxPresence.GetFriendPresencesAsync(userId, token).ConfigureAwait(false);
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
        return playing;
    }

    private static async Task EnrichAsync(Snapshot state, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::EnrichAsync";

        List<long> missingProfiles = state.Awarded.Keys.Union(state.Playing).Where(id => state.ProfilesTried.Add(id)).ToList();
        if (missingProfiles.Count > 0)
        {
            List<ServerFriend> profiles = await RobloxPresence.GetProfilesAsync(missingProfiles, token).ConfigureAwait(false);
            foreach (ServerFriend profile in profiles)
                state.Profiles.TryAdd(profile.UserId, profile);
        }

        DateTime now = DateTime.UtcNow;
        List<long> missingIcons = state
            .Awarded.Values.SelectMany(awarded => awarded)
            .Where(pair => pair.Value != DateTime.MinValue && now - pair.Value <= ActivityWindow)
            .Select(pair => pair.Key)
            .Where(id => state.IconsTried.Add(id))
            .ToList();
        if (missingIcons.Count == 0)
            return;
        try
        {
            IReadOnlyDictionary<long, string> icons = await RobloxBadges.GetIconsAsync(missingIcons, token).ConfigureAwait(false);
            foreach ((long id, string url) in icons)
                state.Icons[id] = url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.WriteLine(LOG_IDENT, "Badge icons could not be loaded: " + ex.Message);
        }
    }

    private static GameFriendsReport Compose(Snapshot state, bool complete)
    {
        DateTime now = DateTime.UtcNow;
        Dictionary<long, string> badgeNames = new();
        foreach (RobloxBadges.Badge badge in state.Badges)
            badgeNames[badge.Id] = badge.Name;

        List<GameFriend> friends = state
            .Awarded.Keys.Union(state.Playing)
            .Select(id =>
            {
                state.Awarded.TryGetValue(id, out IReadOnlyDictionary<long, DateTime>? awarded);
                GameFriend friend = new()
                {
                    UserId = id,
                    InGame = state.Playing.Contains(id),
                    Earned = awarded?.Count ?? 0,
                    Total = state.Badges.Count,
                    LastAwardUtc = awarded is { Count: > 0 } ? awarded.Values.Max() : DateTime.MinValue,
                };
                ApplyProfile(state.Profiles, id, name => friend.Name = name, url => friend.HeadshotUrl = url);
                return friend;
            })
            .ToList();

        List<GameFriendActivity> activity = new();
        foreach ((long friendId, IReadOnlyDictionary<long, DateTime> awarded) in state.Awarded)
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
                        BadgeIconUrl = state.Icons.GetValueOrDefault(badgeId, ""),
                    }
                );
            }
            if (first != DateTime.MaxValue && now - first <= ActivityWindow)
                activity.Add(new GameFriendActivity { UserId = friendId, WhenUtc = first.AddTicks(-1), FirstPlay = true });
        }
        activity = activity.OrderByDescending(entry => entry.WhenUtc).Take(MaxActivity).ToList();
        foreach (GameFriendActivity entry in activity)
            ApplyProfile(state.Profiles, entry.UserId, name => entry.Name = name, url => entry.HeadshotUrl = url);

        List<GameFriend> recent = friends
            .Where(friend => friend.InGame || now - friend.LastAwardUtc <= RecentWindow)
            .OrderByDescending(friend => friend.InGame)
            .ThenByDescending(friend => friend.LastAwardUtc)
            .ToList();
        List<GameFriend> previous = friends.Except(recent).OrderByDescending(friend => friend.LastAwardUtc).ToList();
        return new GameFriendsReport
        {
            Status = GameFriendsStatus.Ready,
            Complete = complete,
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

    private sealed class Snapshot
    {
        public HashSet<long> Playing { get; set; } = [];

        public IReadOnlyList<RobloxBadges.Badge> Badges { get; set; } = [];

        public Dictionary<long, IReadOnlyDictionary<long, DateTime>> Awarded { get; set; } = new();

        public Dictionary<long, ServerFriend> Profiles { get; } = new();

        public HashSet<long> ProfilesTried { get; } = [];

        public Dictionary<long, string> Icons { get; } = new();

        public HashSet<long> IconsTried { get; } = [];
    }
}
