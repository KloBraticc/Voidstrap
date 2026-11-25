using System.Collections.Concurrent;

namespace Voidstrap.Integrations;

public readonly record struct GameVoteCounts(long Up, long Down)
{
    public double LikePercent => Up + Down > 0 ? Up * 100.0 / (Up + Down) : double.NaN;
}

public readonly record struct GameLiveStats(long Playing, long Visits, long Favorites, long? Price = null);

public enum GameVoteOutcome
{
    Ok,
    SignIn,
    PlayFirst,
    RateLimited,
    Failed,
}

public static class GameLiveData
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan PriceFreshFor = TimeSpan.FromMinutes(30);

    private static readonly ConcurrentDictionary<long, (GameVoteCounts Value, DateTime At)> Votes = new();

    private static readonly ConcurrentDictionary<long, (GameLiveStats Value, DateTime At)> Stats = new();

    private static readonly ConcurrentDictionary<long, (long? Value, DateTime At)> Prices = new();

    private static readonly ConcurrentDictionary<long, bool?> UserVotes = new();

    private static readonly ConcurrentDictionary<long, bool> CanVote = new();

    private static readonly ConcurrentDictionary<string, Task> InFlight = new();

    private static readonly SemaphoreSlim PriceFetchGate = new(1, 1);

    private static long _backoffUntilTicks;

    public static event EventHandler<long>? Changed;

    public static bool TryGetVotes(long universeId, out GameVoteCounts counts)
    {
        bool found = Votes.TryGetValue(universeId, out (GameVoteCounts Value, DateTime At) entry);
        counts = entry.Value;
        return found;
    }

    public static bool TryGetStats(long universeId, out GameLiveStats stats)
    {
        bool found = Stats.TryGetValue(universeId, out (GameLiveStats Value, DateTime At) entry);
        stats = entry.Value;
        return found;
    }

    public static string FormatRobuxAmount(long? price) =>
        price is long amount && amount > 0 ? amount.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public static string FormatRobux(long? price)
    {
        if (price is not long amount || amount <= 0)
            return string.Empty;
        string formatted = amount.ToString("N0", CultureInfo.CurrentCulture);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.Strings.Ui_Session_Robux_Amount, formatted);
        }
        catch (FormatException)
        {
            return "R$ " + formatted;
        }
    }

    internal static long? ReadPrice(JsonElement element, string propertyName = "price")
    {
        if (!element.TryGetProperty(propertyName, out JsonElement prop) || prop.ValueKind != JsonValueKind.Number)
            return null;
        return prop.TryGetInt64(out long value) && value > 0 ? value : null;
    }

    public static bool TryGetPrice(long universeId, out long? price)
    {
        if (universeId <= 0)
        {
            price = null;
            return false;
        }
        if (Prices.TryGetValue(universeId, out (long? Value, DateTime At) entry) && DateTime.UtcNow - entry.At < PriceFreshFor)
        {
            price = entry.Value;
            return true;
        }
        if (UniverseDetails.LoadFromCache(universeId)?.Data is { } cachedData)
        {
            price = cachedData.Price is > 0 ? cachedData.Price : null;
            return true;
        }
        price = null;
        return false;
    }

    public static void RememberPrice(long universeId, long? price)
    {
        if (universeId > 0)
            Prices[universeId] = (price is > 0 ? price : null, DateTime.UtcNow);
    }

    public static void NoteRateLimit() => Backoff(TimeSpan.FromSeconds(60));

    public static async Task<Dictionary<long, long?>> FetchPricesAsync(IEnumerable<long> universeIds, CancellationToken token)
    {
        const string LOG_IDENT = "GameLiveData::FetchPricesAsync";

        Dictionary<long, long?> result = new();
        List<long> missing = new();
        foreach (long id in universeIds.Where(static id => id > 0).Distinct())
        {
            if (TryGetPrice(id, out long? cached))
                result[id] = cached;
            else
                missing.Add(id);
        }
        if (missing.Count == 0 || InBackoff())
            return result;

        await PriceFetchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            List<long> remaining = new();
            foreach (long id in missing)
            {
                if (TryGetPrice(id, out long? cached))
                    result[id] = cached;
                else
                    remaining.Add(id);
            }
            if (remaining.Count == 0 || InBackoff())
                return result;

            for (int i = 0; i < remaining.Count; i += 100)
            {
                token.ThrowIfCancellationRequested();
                if (InBackoff())
                    break;
                if (i > 0)
                    await Task.Delay(350, token).ConfigureAwait(false);
                string chunk = string.Join(',', remaining.Skip(i).Take(100));
                try
                {
                    string json = await Utility.Http.GetString($"https://games.roblox.com/v1/games?universeIds={chunk}", token).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement item in data.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out JsonElement idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt64(out long id) || id <= 0)
                            continue;
                        long? price = ReadPrice(item);
                        RememberPrice(id, price);
                        result[id] = price;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (IsRateLimit(ex))
                        Backoff(TimeSpan.FromSeconds(60));
                    App.Logger.WriteLine(LOG_IDENT, ex.Message);
                    break;
                }
            }
        }
        finally
        {
            PriceFetchGate.Release();
        }
        return result;
    }

    public static bool? GetUserVote(long universeId) => UserVotes.TryGetValue(universeId, out bool? vote) ? vote : null;

    public static async Task<GameVoteCounts?> RefreshVotesAsync(long universeId, CancellationToken token)
    {
        if (universeId <= 0)
            return null;
        if (Votes.TryGetValue(universeId, out (GameVoteCounts Value, DateTime At) cached) && DateTime.UtcNow - cached.At < FreshFor)
            return cached.Value;
        if (!InBackoff())
            await Shared("v" + universeId, () => FetchVotesAsync(universeId)).WaitAsync(token).ConfigureAwait(false);
        return Votes.TryGetValue(universeId, out cached) ? cached.Value : null;
    }

    public static async Task<GameLiveStats?> RefreshStatsAsync(long universeId, CancellationToken token)
    {
        if (universeId <= 0)
            return null;
        if (Stats.TryGetValue(universeId, out (GameLiveStats Value, DateTime At) cached) && DateTime.UtcNow - cached.At < FreshFor)
            return cached.Value;
        if (!InBackoff())
            await Shared("s" + universeId, () => FetchStatsAsync(universeId)).WaitAsync(token).ConfigureAwait(false);
        return Stats.TryGetValue(universeId, out cached) ? cached.Value : null;
    }

    public static bool InBackoff() => DateTime.UtcNow.Ticks < Interlocked.Read(ref _backoffUntilTicks);

    public static async Task RefreshUserVoteAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "GameLiveData::RefreshUserVoteAsync";

        if (universeId <= 0 || string.IsNullOrEmpty(RobloxCookie.Get()))
            return;
        try
        {
            RobloxAuthedResult result = await RobloxAuthedClient
                .GetAsync($"https://games.roblox.com/v1/games/{universeId}/votes/user", token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                if (result.Status == RobloxAuthedStatus.RateLimited)
                    Backoff(result.RetryAfter ?? TimeSpan.FromSeconds(60));
                return;
            }
            using JsonDocument doc = JsonDocument.Parse(result.Body);
            JsonElement root = doc.RootElement;
            bool? vote = root.TryGetProperty("userVote", out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            CanVote[universeId] = !root.TryGetProperty("canVote", out JsonElement c) || c.ValueKind != JsonValueKind.False;
            if (GetUserVote(universeId) != vote || !UserVotes.ContainsKey(universeId))
            {
                UserVotes[universeId] = vote;
                Raise(universeId);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    public static async Task<GameVoteOutcome> SetVoteAsync(long universeId, bool? vote)
    {
        const string LOG_IDENT = "GameLiveData::SetVoteAsync";

        if (universeId <= 0)
            return GameVoteOutcome.Failed;
        if (string.IsNullOrEmpty(RobloxCookie.Get()))
            return GameVoteOutcome.SignIn;
        if (CanVote.TryGetValue(universeId, out bool allowed) && !allowed)
            return GameVoteOutcome.PlayFirst;

        bool? previous = GetUserVote(universeId);
        GameVoteCounts? before = TryGetVotes(universeId, out GameVoteCounts counts) ? counts : null;
        Apply(universeId, previous, vote);
        try
        {
            string body = vote is null ? "{\"vote\":null}" : vote.Value ? "{\"vote\":true}" : "{\"vote\":false}";
            RobloxAuthedResult result = await RobloxAuthedClient
                .PatchJsonAsync($"https://games.roblox.com/v1/games/{universeId}/user-votes", body, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Succeeded)
                return GameVoteOutcome.Ok;
            Restore(universeId, previous, before);
            if (result.Status == RobloxAuthedStatus.NotSignedIn)
                return GameVoteOutcome.SignIn;
            if (result.Status == RobloxAuthedStatus.RateLimited)
            {
                Backoff(result.RetryAfter ?? TimeSpan.FromSeconds(60));
                return GameVoteOutcome.RateLimited;
            }
            if (result.Status == RobloxAuthedStatus.NotAllowed && result.Body.Contains("play", StringComparison.OrdinalIgnoreCase))
            {
                CanVote[universeId] = false;
                return GameVoteOutcome.PlayFirst;
            }
            App.Logger.WriteLine(LOG_IDENT, "Vote rejected with status " + result.HttpStatus);
            return GameVoteOutcome.Failed;
        }
        catch (Exception ex)
        {
            Restore(universeId, previous, before);
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
            return GameVoteOutcome.Failed;
        }
    }

    private static void Apply(long universeId, bool? previous, bool? next)
    {
        UserVotes[universeId] = next;
        if (Votes.TryGetValue(universeId, out (GameVoteCounts Value, DateTime At) entry))
        {
            long up = entry.Value.Up + (next == true ? 1 : 0) - (previous == true ? 1 : 0);
            long down = entry.Value.Down + (next == false ? 1 : 0) - (previous == false ? 1 : 0);
            Votes[universeId] = (new GameVoteCounts(Math.Max(0, up), Math.Max(0, down)), entry.At);
        }
        Raise(universeId);
    }

    private static void Restore(long universeId, bool? previous, GameVoteCounts? before)
    {
        UserVotes[universeId] = previous;
        if (before is GameVoteCounts counts)
            Votes[universeId] = (counts, DateTime.UtcNow);
        Raise(universeId);
    }

    private static Task Shared(string key, Func<Task> start)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task existing = InFlight.GetOrAdd(key, gate.Task);
        if (!ReferenceEquals(existing, gate.Task))
            return existing;
        _ = RunShared(key, start, gate);
        return gate.Task;
    }

    private static async Task RunShared(string key, Func<Task> start, TaskCompletionSource gate)
    {
        try
        {
            await start().ConfigureAwait(false);
        }
        finally
        {
            InFlight.TryRemove(key, out _);
            gate.TrySetResult();
        }
    }

    private static async Task FetchVotesAsync(long universeId)
    {
        const string LOG_IDENT = "GameLiveData::FetchVotesAsync";

        try
        {
            string json = await Utility.Http.GetString($"https://games.roblox.com/v1/games/votes?universeIds={universeId}").ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return;
            JsonElement item = data[0];
            long up = item.TryGetProperty("upVotes", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0;
            long down = item.TryGetProperty("downVotes", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
            GameVoteCounts counts = new(up, down);
            bool changed = !Votes.TryGetValue(universeId, out (GameVoteCounts Value, DateTime At) old) || old.Value != counts;
            Votes[universeId] = (counts, DateTime.UtcNow);
            if (changed)
                Raise(universeId);
        }
        catch (Exception ex)
        {
            Backoff(IsRateLimit(ex) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15));
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private static async Task FetchStatsAsync(long universeId)
    {
        const string LOG_IDENT = "GameLiveData::FetchStatsAsync";

        try
        {
            string json = await Utility.Http.GetString($"https://games.roblox.com/v1/games?universeIds={universeId}").ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return;
            JsonElement g = data[0];
            long playing = g.TryGetProperty("playing", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
            long visits = g.TryGetProperty("visits", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            long favorites = g.TryGetProperty("favoritedCount", out JsonElement f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : 0;
            long? price = ReadPrice(g);
            RememberPrice(universeId, price);
            GameLiveStats stats = new(playing, visits, favorites, price);
            bool changed = !Stats.TryGetValue(universeId, out (GameLiveStats Value, DateTime At) old) || old.Value != stats;
            Stats[universeId] = (stats, DateTime.UtcNow);
            if (changed)
                Raise(universeId);
        }
        catch (Exception ex)
        {
            Backoff(IsRateLimit(ex) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15));
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private static bool IsRateLimit(Exception ex) =>
        ex is Utility.Http.RateLimitedException
        || ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
        || ex.Message.Contains("429", StringComparison.Ordinal);

    private static void Backoff(TimeSpan span)
    {
        long until = DateTime.UtcNow.Add(span).Ticks;
        long current = Interlocked.Read(ref _backoffUntilTicks);
        if (until > current)
            Interlocked.Exchange(ref _backoffUntilTicks, until);
    }

    private static void Raise(long universeId)
    {
        try
        {
            Changed?.Invoke(null, universeId);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("GameLiveData::Raise", ex.Message);
        }
    }
}
