using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json;
using Voidstrap.Models.Entities;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.Integrations;

public static class RoReviewCommunityFeed
{
    private sealed class FeedCache
    {
        public Dictionary<long, long> Checked { get; set; } = new();
        public Dictionary<long, CommunityGameInfo> Games { get; set; } = new();
        public List<CommunityReview> Reviews { get; set; } = new();
    }

    private const int MaxPlaces = 20000;
    private const int MaxReviews = 40000;
    private const int ReviewConcurrency = 24;
    private const int SearchConcurrency = 4;
    private const int SearchPages = 8;
    private const long RecheckSeconds = 6 * 60 * 60;
    private static readonly HttpClient HttpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(20));
    private static readonly JsonSerializerOptions CacheOptions = new() { IgnoreReadOnlyProperties = true };
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static readonly object CacheLock = new();
    private static FeedCache? _cache;

    private static readonly string[] SearchSeeds =
    {
        "simulator", "obby", "tycoon", "horror", "roleplay", "rpg", "anime", "fighting", "shooter", "survival",
        "racing", "story", "tower defense", "sword", "fps", "pvp", "battle", "escape", "hangout", "life",
        "city", "school", "pet", "zombie", "war", "military", "parkour", "puzzle", "music", "dance",
        "sports", "soccer", "basketball", "football", "fishing", "farming", "cooking", "restaurant", "hospital", "police",
        "prison", "heist", "detective", "mystery", "adventure", "dungeon", "dragon", "magic", "pirate", "ninja",
        "samurai", "space", "mining", "clicker", "idle", "merge", "rng", "gacha", "trading", "fashion",
        "horror story", "anime battlegrounds", "simulator 2", "brainrot", "steal a", "grow a", "99 nights", "doors", "hide and seek", "tag", "bedwars", "murder", "sniper", "car", "driving", "train", "plane", "boat", "airport", "island", "ocean", "survive", "disaster", "natural disaster", "build", "sandbox", "creative", "art", "draw", "guess", "quiz", "trivia", "word", "spelling", "math", "chess", "card", "casino", "slots", "obstacle", "tower", "climb", "jump", "run", "speed", "race", "kart", "bike", "skate", "ski", "snow", "christmas", "halloween", "easter", "summer", "beach", "camp", "forest", "jungle", "desert", "castle", "kingdom", "medieval", "viking", "knight", "wizard", "witch", "vampire", "werewolf", "alien", "robot", "mech", "superhero", "villain", "spy", "agent", "cops", "robbers", "jail", "bank", "mall", "shop", "store", "cafe", "bakery", "pizza", "burger", "cat", "dog", "horse", "dinosaur", "shark", "monster", "ghost", "backrooms", "piggy", "rainbow", "doll", "baby", "family", "house", "home", "mansion", "hotel", "office", "work", "job", "factory", "army", "tank", "gun", "boxing", "wrestling", "karate", "kung fu", "hockey", "baseball", "tennis", "golf", "volleyball", "bowling", "pool", "swim", "fish", "hunt", "zoo", "farm", "garden", "plant", "tree", "fruit", "anime fighting", "one piece", "dragon ball", "naruto", "demon", "slayer", "hero", "academy", "jojo", "titan", "ghoul", "bleach",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
    };

    private static string CachePath => Path.Combine(Paths.Cache, "CommunityFeed.json");

    public static List<(CommunityGameInfo Game, List<CommunityReview> Reviews)> LoadCached()
    {
        lock (CacheLock)
        {
            FeedCache cache = GetCacheLocked();
            cache.Reviews.RemoveAll(static r => r == null || string.IsNullOrEmpty(r.Content));
            return cache.Reviews
                .GroupBy(static r => r.PlaceId)
                .Select(g => (cache.Games.TryGetValue(g.Key, out CommunityGameInfo? info) ? info : new CommunityGameInfo { PlaceId = g.Key, Name = g.First().GameName }, g.OrderByDescending(static r => r.UnixTime).ToList()))
                .ToList();
        }
    }

    public static async Task RunAsync(Action<CommunityGameInfo, IReadOnlyList<CommunityReview>> onReviews, Action<int, int> onProgress, CancellationToken token)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::RunAsync";
        try
        {
            await RunLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        List<Task> work = new();
        try
        {
            ConcurrentDictionary<long, CommunityGameInfo> places = new();
            AddLibraryPlaces(places);
            Task explore = AddExplorePlacesAsync(places, token);
            Task search = AddSearchPlacesAsync(places, token);
            HashSet<long> queued = new();
            int done = 0;
            using SemaphoreSlim gate = new(ReviewConcurrency, ReviewConcurrency);
            bool sourcesDone = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (KeyValuePair<long, CommunityGameInfo> place in places.ToArray())
                {
                    if (queued.Count >= MaxPlaces || !queued.Add(place.Key))
                        continue;
                    if (!NeedsCheck(place.Key))
                    {
                        Interlocked.Increment(ref done);
                        continue;
                    }
                    work.Add(FetchPlaceCountedAsync(place.Key, place.Value, gate, onReviews, () => Interlocked.Increment(ref done), token));
                }
                onProgress(Volatile.Read(ref done), queued.Count);
                if (sourcesDone)
                    break;
                sourcesDone = explore.IsCompleted && search.IsCompleted;
                await Task.Delay(150, token).ConfigureAwait(false);
            }
            while (work.Any(static t => !t.IsCompleted))
            {
                onProgress(Volatile.Read(ref done), queued.Count);
                await Task.WhenAny(Task.WhenAll(work), Task.Delay(1000, token)).ConfigureAwait(false);
            }
            onProgress(Volatile.Read(ref done), queued.Count);
            App.Logger.WriteLine(LOG_IDENT, "Community feed scanned " + queued.Count + " places");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        finally
        {
            try
            {
                await Task.WhenAll(work).ConfigureAwait(false);
            }
            catch { }
            Save();
            RunLock.Release();
        }
    }

    private static async Task FetchPlaceCountedAsync(long placeId, CommunityGameInfo source, SemaphoreSlim gate, Action<CommunityGameInfo, IReadOnlyList<CommunityReview>> onReviews, Action onDone, CancellationToken token)
    {
        try
        {
            await FetchPlaceAsync(placeId, source, gate, onReviews, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            onDone();
        }
    }

    private static async Task FetchPlaceAsync(long placeId, CommunityGameInfo source, SemaphoreSlim gate, Action<CommunityGameInfo, IReadOnlyList<CommunityReview>> onReviews, CancellationToken token)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::FetchPlaceAsync";
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RoReviewService.ReviewData? data = null;
            for (int attempt = 0; attempt < 4 && data == null; attempt++)
            {
                await RoReviewService.WaitForCooldownAsync(token).ConfigureAwait(false);
                data = await RoReviewService.FetchReviewsAsync(placeId, token).ConfigureAwait(false);
                if (data == null && RoReviewService.CooldownRemaining == TimeSpan.Zero)
                    break;
            }
            if (data == null)
                return;
            List<CommunityReview> added = new();
            CommunityGameInfo info;
            lock (CacheLock)
            {
                FeedCache cache = GetCacheLocked();
                cache.Checked[placeId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                info = Merge(cache.Games.TryGetValue(placeId, out CommunityGameInfo? known) ? known : null, source);
                cache.Games[placeId] = info;
                cache.Reviews.RemoveAll(r => r.PlaceId == placeId);
                foreach (RoReviewService.ReviewEntry entry in data.Reviews)
                {
                    if (string.IsNullOrWhiteSpace(entry.Content))
                        continue;
                    CommunityReview review = new()
                    {
                        Id = entry.Id,
                        PlaceId = placeId,
                        GameName = info.Name,
                        AuthorName = entry.AuthorName,
                        AuthorId = entry.AuthorId,
                        AvatarUrl = entry.AvatarUrl,
                        Content = entry.Content,
                        CreatedAt = entry.CreatedAt,
                        UnixTime = entry.UnixTime,
                        UpCount = entry.UpCount,
                        DownCount = entry.DownCount,
                    };
                    cache.Reviews.Add(review);
                    added.Add(review);
                }
                if (cache.Reviews.Count > MaxReviews)
                    cache.Reviews = cache.Reviews.OrderByDescending(static r => r.UnixTime).Take(MaxReviews).ToList();
            }
            if (added.Count == 0)
                return;
            if (info.IconUrl.Length == 0 || info.ThumbnailUrl.Length == 0 || info.CreatorName.Length == 0 || info.Name.Length == 0)
            {
                CommunityGameInfo enriched = await EnrichAsync(info, token).ConfigureAwait(false);
                lock (CacheLock)
                    GetCacheLocked().Games[placeId] = enriched;
                info = enriched;
            }
            onReviews(info, added);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static CommunityGameInfo Merge(CommunityGameInfo? known, CommunityGameInfo source)
    {
        known ??= new CommunityGameInfo();
        return new CommunityGameInfo
        {
            PlaceId = source.PlaceId,
            UniverseId = source.UniverseId > 0 ? source.UniverseId : known.UniverseId,
            Name = source.Name.Length > 0 ? source.Name : known.Name,
            IconUrl = source.IconUrl.Length > 0 ? source.IconUrl : known.IconUrl,
            ThumbnailUrl = source.ThumbnailUrl.Length > 0 ? source.ThumbnailUrl : known.ThumbnailUrl,
            CreatorName = source.CreatorName.Length > 0 ? source.CreatorName : known.CreatorName,
        };
    }

    private static async Task<CommunityGameInfo> EnrichAsync(CommunityGameInfo info, CancellationToken token)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::EnrichAsync";
        CommunityGameInfo result = Merge(null, info);
        try
        {
            if (result.UniverseId <= 0)
            {
                using JsonDocument universe = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, "https://apis.roblox.com/universes/v1/places/" + result.PlaceId + "/universe", token: token).ConfigureAwait(false));
                if (universe.RootElement.TryGetProperty("universeId", out JsonElement id) && id.TryGetInt64(out long universeId))
                    result.UniverseId = universeId;
            }
            if (result.UniverseId <= 0)
                return result;
            string ids = result.UniverseId.ToString();
            Task<string> details = Http.GetStringBoundedAsync(HttpClient, "https://games.roblox.com/v1/games?universeIds=" + ids, token: token);
            Task<string> icons = Http.GetStringBoundedAsync(HttpClient, "https://thumbnails.roblox.com/v1/games/icons?universeIds=" + ids + "&size=256x256&format=Png&isCircular=false", token: token);
            Task<string> thumbs = Http.GetStringBoundedAsync(HttpClient, "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=" + ids + "&countPerUniverse=1&defaults=true&size=768x432&format=Jpeg&isCircular=false", token: token);
            await Task.WhenAll(details, icons, thumbs).ConfigureAwait(false);
            using (JsonDocument doc = JsonDocument.Parse(details.Result))
                foreach (JsonElement game in Data(doc))
                {
                    if (result.Name.Length == 0 && game.TryGetProperty("name", out JsonElement name))
                        result.Name = name.GetString() ?? string.Empty;
                    if (game.TryGetProperty("creator", out JsonElement creator) && creator.TryGetProperty("name", out JsonElement creatorName))
                        result.CreatorName = creatorName.GetString() ?? string.Empty;
                }
            using (JsonDocument doc = JsonDocument.Parse(icons.Result))
                foreach (JsonElement icon in Data(doc))
                    if (icon.TryGetProperty("imageUrl", out JsonElement url) && url.GetString() is { Length: > 0 } iconUrl && iconUrl.StartsWith("https://", StringComparison.Ordinal))
                        result.IconUrl = iconUrl;
            using (JsonDocument doc = JsonDocument.Parse(thumbs.Result))
                foreach (JsonElement item in Data(doc))
                    if (item.TryGetProperty("thumbnails", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement thumb in list.EnumerateArray())
                            if (thumb.TryGetProperty("imageUrl", out JsonElement url) && url.GetString() is { Length: > 0 } thumbUrl && thumbUrl.StartsWith("https://", StringComparison.Ordinal))
                            {
                                result.ThumbnailUrl = thumbUrl;
                                break;
                            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        return result;
    }

    private static IEnumerable<JsonElement> Data(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in data.EnumerateArray())
                yield return item;
    }

    private static bool NeedsCheck(long placeId)
    {
        lock (CacheLock)
            return !GetCacheLocked().Checked.TryGetValue(placeId, out long at) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at > RecheckSeconds;
    }

    private static void AddLibraryPlaces(ConcurrentDictionary<long, CommunityGameInfo> places)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::AddLibraryPlaces";
        try
        {
            string path = LibraryStore.SnapshotPath;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return;
            foreach (LibraryGameEntry entry in JsonFile.Deserialize<List<LibraryGameEntry>>(path, CacheOptions) ?? new())
                if (entry.PlaceId > 0)
                    places.TryAdd(entry.PlaceId, new CommunityGameInfo { PlaceId = entry.PlaceId, UniverseId = entry.UniverseId, Name = entry.Name ?? string.Empty, IconUrl = entry.IconUrl ?? string.Empty, ThumbnailUrl = entry.ThumbnailUrl ?? string.Empty, CreatorName = entry.CreatorName ?? string.Empty });
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private static async Task AddExplorePlacesAsync(ConcurrentDictionary<long, CommunityGameInfo> places, CancellationToken token)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::AddExplorePlacesAsync";
        try
        {
            string url = "https://apis.roblox.com/explore-api/v1/get-sorts?sessionId=" + Guid.NewGuid() + "&device=computer&country=all";
            using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("sorts", out JsonElement sorts) || sorts.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement sort in sorts.EnumerateArray())
            {
                if (!sort.TryGetProperty("games", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
                    continue;
                AddGames(places, games);
            }
            await Task.WhenAll(sorts.EnumerateArray()
                .Select(static sort => sort.TryGetProperty("sortId", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty)
                .Where(static id => id.Length > 0)
                .ToList()
                .Select(id => PageSortAsync(places, id, token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private static void AddGames(ConcurrentDictionary<long, CommunityGameInfo> places, JsonElement games)
    {
        foreach (JsonElement game in games.EnumerateArray())
            if (game.TryGetProperty("rootPlaceId", out JsonElement id) && id.TryGetInt64(out long placeId) && placeId > 0)
                places.TryAdd(placeId, new CommunityGameInfo
                {
                    PlaceId = placeId,
                    UniverseId = game.TryGetProperty("universeId", out JsonElement universe) && universe.TryGetInt64(out long universeId) ? universeId : 0,
                    Name = game.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                });
    }

    private static async Task PageSortAsync(ConcurrentDictionary<long, CommunityGameInfo> places, string sortId, CancellationToken token)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::PageSortAsync";
        string session = Guid.NewGuid().ToString();
        string pageToken = string.Empty;
        try
        {
            for (int page = 0; page < 10 && places.Count < MaxPlaces; page++)
            {
                string url = "https://apis.roblox.com/explore-api/v1/get-sort-content?sessionId=" + session + "&sortId=" + Uri.EscapeDataString(sortId) + "&pageToken=" + Uri.EscapeDataString(pageToken) + "&device=computer&country=all";
                string? body = await GetWithBackoffAsync(url, token).ConfigureAwait(false);
                if (body == null)
                    break;
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
                    AddGames(places, games);
                pageToken = document.RootElement.TryGetProperty("nextPageToken", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() ?? string.Empty : string.Empty;
                if (pageToken.Length == 0)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private static async Task AddSearchPlacesAsync(ConcurrentDictionary<long, CommunityGameInfo> places, CancellationToken token)
    {
        string session = Guid.NewGuid().ToString();
        using SemaphoreSlim gate = new(SearchConcurrency, SearchConcurrency);
        await Task.WhenAll(SearchSeeds.Select(seed => SearchSeedAsync(seed, session, places, gate, token))).ConfigureAwait(false);
    }

    private static async Task SearchSeedAsync(string seed, string session, ConcurrentDictionary<long, CommunityGameInfo> places, SemaphoreSlim gate, CancellationToken token)
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::SearchSeedAsync";
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (places.Count >= MaxPlaces)
                return;
            string pageToken = string.Empty;
            for (int page = 0; page < SearchPages && places.Count < MaxPlaces; page++)
            {
                string url = "https://apis.roblox.com/search-api/omni-search?searchQuery=" + Uri.EscapeDataString(seed) + "&pageToken=" + Uri.EscapeDataString(pageToken) + "&sessionId=" + session + "&pageType=all";
                string? body = await GetWithBackoffAsync(url, token).ConfigureAwait(false);
                if (body == null)
                    break;
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("searchResults", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement group in groups.EnumerateArray())
                        if (group.TryGetProperty("contents", out JsonElement contents) && contents.ValueKind == JsonValueKind.Array)
                            AddGames(places, contents);
                pageToken = document.RootElement.TryGetProperty("nextPageToken", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() ?? string.Empty : string.Empty;
                if (pageToken.Length == 0)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> GetWithBackoffAsync(string url, CancellationToken token)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, token).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                return null;
            return await Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, token).ConfigureAwait(false);
        }
        return null;
    }

    private static FeedCache GetCacheLocked()
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::GetCacheLocked";
        if (_cache != null)
            return _cache;
        try
        {
            if (File.Exists(CachePath) && new FileInfo(CachePath).Length > 0)
                _cache = JsonFile.Deserialize<FeedCache>(CachePath, CacheOptions, 64L * 1024 * 1024);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        _cache ??= new FeedCache();
        _cache.Reviews ??= new();
        _cache.Games ??= new();
        _cache.Checked ??= new();
        return _cache;
    }

    private static void Save()
    {
        const string LOG_IDENT = "RoReviewCommunityFeed::Save";
        try
        {
            lock (CacheLock)
            {
                if (_cache == null)
                    return;
                Directory.CreateDirectory(Paths.Cache);
                JsonFile.SerializeAtomic(CachePath, _cache, CacheOptions, false);
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }
}
