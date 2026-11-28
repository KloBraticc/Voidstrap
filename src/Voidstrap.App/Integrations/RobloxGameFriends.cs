using System.Collections.Concurrent;

namespace Voidstrap.Integrations;

public static class RobloxGameFriends
{
    private const int MaxFriendsChecked = 200;

    private const int Concurrency = 6;

    private const int ProbeBadges = 100;

    private const int MaxActivity = 40;

    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(30);

    private static readonly TimeSpan ReportLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PresenceLifetime = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<long, (DateTime At, GameFriendsReport Report)> Reports = new();

    private static readonly ConcurrentDictionary<long, GameFriendsReport> Live = new();

    private static readonly ConcurrentDictionary<long, Lazy<Task>> Pending = new();

    private static readonly SemaphoreSlim PresenceGate = new(1, 1);

    private static (long UserId, long FetchedAt, List<FriendPresence> Friends)? _presence;

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
        _ = Pending.GetOrAdd(universeId, id => new Lazy<Task>(() => Task.Run(() => BuildAndStoreAsync(id)))).Value;
    }

    internal static async Task<(GameFriendsStatus Status, IReadOnlyList<GameFriend> Friends)> GetPlayingAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::GetPlayingAsync";

        try
        {
            if (string.IsNullOrEmpty(RobloxCookie.Get()))
                return (GameFriendsStatus.NotSignedIn, []);

            long userId = await RobloxBadges.GetSignedInUserIdAsync(token).ConfigureAwait(false);
            if (userId <= 0)
                return (GameFriendsStatus.Unavailable, []);

            HashSet<long>? playing = await LoadPlayingAsync(userId, universeId, token).ConfigureAwait(false);
            if (playing == null)
                return (GameFriendsStatus.Unavailable, []);

            List<ServerFriend> resolved = await RobloxPresence.GetProfilesAsync(playing.ToList(), token).ConfigureAwait(false);
            Dictionary<long, ServerFriend> profiles = resolved.ToDictionary(profile => profile.UserId);

            if (await RobloxBadges.GetSignedInUserIdAsync(token).ConfigureAwait(false) != userId)
                return (GameFriendsStatus.Unavailable, []);

            List<GameFriend> friends = new(playing.Count);
            foreach (long id in playing)
            {
                GameFriend friend = new() { UserId = id, InGame = true };
                ApplyProfile(profiles, id, name => friend.Name = name, url => friend.HeadshotUrl = url);
                friends.Add(friend);
            }
            return (GameFriendsStatus.Ready, friends.OrderBy(friend => friend.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(friend => friend.UserId).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friends currently playing could not be loaded: " + ex.Message);
            return (GameFriendsStatus.Unavailable, []);
        }
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

        using CancellationTokenSource timeout = new(BuildTimeout);
        GameFriendsReport report;
        try
        {
            report = await BuildAsync(universeId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friends who play took too long to load for universe " + universeId);
            report = new GameFriendsReport { Status = GameFriendsStatus.Unavailable, Complete = true };
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friends who play could not be loaded for universe " + universeId + ": " + ex.Message);
            report = new GameFriendsReport { Status = GameFriendsStatus.Unavailable, Complete = true };
        }
        try
        {
            Publish(universeId, report);
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
            return new GameFriendsReport { Status = GameFriendsStatus.NotSignedIn, Complete = true };
        List<long>? friendIds = await RobloxPresence.GetFriendIdsAsync(userId, token).ConfigureAwait(false);
        if (friendIds == null)
            return new GameFriendsReport { Status = GameFriendsStatus.Unavailable, Complete = true };
        if (friendIds.Count == 0)
            return new GameFriendsReport { Status = GameFriendsStatus.Ready, Complete = true };

        Task<IReadOnlyList<RobloxBadges.Badge>> badgesTask = LoadBadgesAsync(universeId, token);
        Task<HashSet<long>?> playingTask = LoadPlayingAsync(userId, universeId, token);
        await Task.WhenAll(badgesTask, playingTask).ConfigureAwait(false);
        Snapshot state = new()
        {
            Badges = await badgesTask.ConfigureAwait(false),
            Playing = await playingTask.ConfigureAwait(false) ?? [],
        };

        List<long> checkedIds = friendIds.OrderByDescending(state.Playing.Contains).Take(MaxFriendsChecked).ToList();
        int skipped = checkedIds.Count;
        if (state.Badges.Count > 0)
        {
            List<RobloxBadges.Badge> probe = state.Badges.OrderByDescending(badge => badge.AwardedCount).Take(ProbeBadges).ToList();
            bool probeComplete = probe.Count == state.Badges.Count;
            ConcurrentDictionary<long, IReadOnlyDictionary<long, DateTime>> found = new();
            skipped = await CheckFriendsAsync(universeId, state, checkedIds, probe, probeComplete, found, token).ConfigureAwait(false);
            if (skipped == 0 && !probeComplete && !found.IsEmpty)
            {
                HashSet<long> probed = probe.Select(badge => badge.Id).ToHashSet();
                List<RobloxBadges.Badge> rest = state.Badges.Where(badge => !probed.Contains(badge.Id)).ToList();
                await CheckFriendsAsync(universeId, state, found.Keys.ToList(), rest, false, found, token).ConfigureAwait(false);
            }
            state.Awarded = new Dictionary<long, IReadOnlyDictionary<long, DateTime>>(found);
        }

        await EnrichAsync(state, token).ConfigureAwait(false);
        GameFriendsReport report = Compose(state, true);
        App.Logger.WriteLine(
            LOG_IDENT,
            $"Checked {checkedIds.Count - skipped} of {friendIds.Count} friends for universe {universeId}: {report.Recent.Count} recent, {report.Previous.Count} previous, {report.Activity.Count} activity entries"
        );
        return report;
    }

    private static async Task<int> CheckFriendsAsync(
        long universeId,
        Snapshot state,
        IReadOnlyList<long> friendIds,
        IReadOnlyList<RobloxBadges.Badge> badges,
        bool complete,
        ConcurrentDictionary<long, IReadOnlyDictionary<long, DateTime>> found,
        CancellationToken token
    )
    {
        const string LOG_IDENT = "RobloxGameFriends::CheckFriendsAsync";

        int changed = 0;
        int rateLimited = 0;
        int skipped = 0;
        using SemaphoreSlim gate = new(Concurrency, Concurrency);
        RobloxBadges.BeginFriendScan();
        try
        {
            Task work = Task.WhenAll(
                friendIds.Select(async friendId =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref rateLimited) != 0)
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }
                        await RobloxBadges.WaitForOwnProgressAsync(token).ConfigureAwait(false);
                        IReadOnlyDictionary<long, DateTime>? awarded = await RobloxBadges
                            .GetAwardedForUserAsync(friendId, universeId, badges, complete, token)
                            .ConfigureAwait(false);
                        if (awarded is not { Count: > 0 })
                            return;
                        found.AddOrUpdate(
                            friendId,
                            awarded,
                            (_, existing) => existing.Concat(awarded).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value)
                        );
                        Interlocked.Exchange(ref changed, 1);
                    }
                    catch (RobloxBadges.RateLimitedException)
                    {
                        Interlocked.Increment(ref skipped);
                        Interlocked.Exchange(ref rateLimited, 1);
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
            try
            {
                while (!work.IsCompleted && !token.IsCancellationRequested)
                {
                    await Task.WhenAny(work, Task.Delay(PublishInterval, token)).ConfigureAwait(false);
                    if (work.IsCompleted || token.IsCancellationRequested || Interlocked.Exchange(ref changed, 0) == 0)
                        continue;
                    state.Awarded = new Dictionary<long, IReadOnlyDictionary<long, DateTime>>(found);
                    await EnrichAsync(state, token).ConfigureAwait(false);
                    Publish(universeId, Compose(state, false));
                }
            }
            finally
            {
                await work.ConfigureAwait(false);
            }
            if (skipped > 0)
                App.Logger.WriteLine(LOG_IDENT, $"Roblox kept rate limiting badge requests, {skipped} of {friendIds.Count} friends were not checked for universe {universeId}");
            return skipped;
        }
        finally
        {
            RobloxBadges.EndFriendScan();
        }
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

    private static async Task<HashSet<long>?> LoadPlayingAsync(long userId, long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::LoadPlayingAsync";

        await PresenceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_presence is { } cached && cached.UserId == userId && Environment.TickCount64 - cached.FetchedAt < PresenceLifetime.TotalMilliseconds)
                return PlayingIn(cached.Friends, universeId);

            List<FriendPresence>? presences = await RobloxPresence.GetFriendPresencesAsync(userId, token, requireComplete: true).ConfigureAwait(false);
            if (presences == null || await RobloxBadges.GetSignedInUserIdAsync(token).ConfigureAwait(false) != userId)
                return null;

            _presence = (userId, Environment.TickCount64, presences);
            return PlayingIn(presences, universeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            App.Logger.WriteLine(LOG_IDENT, "Friend presence could not be loaded: " + ex.Message);
            return null;
        }
        finally
        {
            PresenceGate.Release();
        }
    }

    private static HashSet<long> PlayingIn(List<FriendPresence> presences, long universeId) =>
        presences.Where(presence => presence.InGame && presence.UniverseId == universeId).Select(presence => presence.UserId).ToHashSet();

    private static Task EnrichAsync(Snapshot state, CancellationToken token) =>
        Task.WhenAll(EnrichProfilesAsync(state, token), EnrichIconsAsync(state, token));

    private static async Task EnrichIconsAsync(Snapshot state, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameFriends::EnrichIconsAsync";

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

    private static async Task EnrichProfilesAsync(Snapshot state, CancellationToken token)
    {
        List<long> missing = state.Awarded.Keys.Union(state.Playing).Where(id => state.ProfilesTried.Add(id)).ToList();
        foreach (ServerFriend profile in await RobloxPresence.GetProfilesAsync(missing, token).ConfigureAwait(false))
            state.Profiles.TryAdd(profile.UserId, profile);
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
