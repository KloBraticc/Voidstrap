using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class RobloxFavorites
{
    private const string LOG_IDENT = "RobloxFavorites";

    private const int PageSize = 100;

    private const int MaxPages = 5;

    private const long MaximumStoreBytes = 4L * 1024 * 1024;

    private static readonly TimeSpan ResponseCacheAge = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromHours(3);

    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private static readonly object Sync = new();

    private static StoreData? _store;

    public sealed class Favorite
    {
        public long UniverseId { get; set; }
        public long PlaceId { get; set; }
        public string Name { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public long Visits { get; set; }
        public DateTime? Updated { get; set; }
    }

    public sealed class StoreData
    {
        public long UserId { get; set; }
        public DateTime LastCheckedUtc { get; set; }
        public List<Favorite> Favorites { get; set; } = [];
        public Dictionary<long, DateTime> SeenUpdates { get; set; } = [];
    }

    private sealed class FavoritesPage
    {
        [JsonPropertyName("nextPageCursor")]
        public string? NextPageCursor { get; set; }

        [JsonPropertyName("data")]
        public List<FavoriteItem>? Data { get; set; }
    }

    private sealed class FavoriteItem
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("creator")]
        public NamedReference? Creator { get; set; }

        [JsonPropertyName("rootPlace")]
        public NamedReference? RootPlace { get; set; }

        [JsonPropertyName("placeVisits")]
        public long? PlaceVisits { get; set; }

        [JsonPropertyName("updated")]
        public DateTime? Updated { get; set; }
    }

    private sealed class NamedReference
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private static string StorePath => Path.Combine(Paths.Library, "Favorites.json");

    public static List<Favorite> ReadCached()
    {
        lock (Sync)
        {
            return Load().Favorites.ToList();
        }
    }

    public static void StartBackgroundCheck(CancellationToken token)
    {
        if (!App.Settings.Prop.FavoriteUpdateAlerts)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                DateTime lastChecked;
                lock (Sync)
                {
                    lastChecked = Load().LastCheckedUtc;
                }
                if (DateTime.UtcNow - lastChecked < BackgroundCheckInterval)
                    return;
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The favorite update check failed: " + ex.Message);
            }
        }, token);
    }

    public static async Task<List<Favorite>?> RefreshAsync(CancellationToken token)
    {
        long userId = await RobloxBadges.GetSignedInUserIdAsync(token).ConfigureAwait(false);
        if (userId <= 0)
            return null;

        await RefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            List<Favorite>? fetched = await FetchAsync(userId, token).ConfigureAwait(false);
            if (fetched == null)
                return null;

            List<Favorite> updated = [];
            lock (Sync)
            {
                StoreData store = Load();
                bool sameUser = store.UserId == userId;
                Dictionary<long, DateTime> seen = sameUser ? store.SeenUpdates : [];
                Dictionary<long, DateTime> nextSeen = [];
                foreach (Favorite favorite in fetched)
                {
                    if (favorite.Updated is not DateTime when)
                    {
                        if (seen.TryGetValue(favorite.UniverseId, out DateTime previous))
                            nextSeen[favorite.UniverseId] = previous;
                        continue;
                    }
                    if (seen.TryGetValue(favorite.UniverseId, out DateTime last) && when > last)
                        updated.Add(favorite);
                    nextSeen[favorite.UniverseId] = when;
                }
                store.UserId = userId;
                store.LastCheckedUtc = DateTime.UtcNow;
                store.Favorites = fetched;
                store.SeenUpdates = nextSeen;
                Save(store);
            }

            if (updated.Count > 0 && App.Settings.Prop.FavoriteUpdateAlerts)
            {
                foreach (Favorite favorite in updated)
                {
                    AppNotifications.RecordInfo(
                        "favorite-update:" + favorite.UniverseId + ":" + favorite.Updated!.Value.Ticks,
                        string.Format(Strings.Ui_Notifications_FavoriteUpdated_Title, favorite.Name),
                        Strings.Ui_Notifications_FavoriteUpdated_Text);
                }
                App.Logger.WriteLine(LOG_IDENT, updated.Count + " favorite game(s) were updated since the last check");
            }

            return fetched;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<List<Favorite>?> FetchAsync(long userId, CancellationToken token)
    {
        List<Favorite> favorites = [];
        HashSet<long> seen = [];
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            string url = "https://games.roblox.com/v2/users/" + userId + "/favorite/games?accessFilter=2&limit=" + PageSize + "&sortOrder=Desc"
                + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            FavoritesPage? response = await GitHubCache.GetJsonAsync<FavoritesPage>(url, ResponseCacheAge, token).ConfigureAwait(false);
            if (response?.Data == null)
                return page == 0 ? null : favorites;
            foreach (FavoriteItem item in response.Data)
            {
                if (item.Id <= 0 || !seen.Add(item.Id))
                    continue;
                favorites.Add(new Favorite
                {
                    UniverseId = item.Id,
                    PlaceId = item.RootPlace?.Id ?? 0,
                    Name = item.Name ?? "",
                    CreatorName = item.Creator?.Name ?? "",
                    Visits = item.PlaceVisits ?? 0,
                    Updated = item.Updated?.ToUniversalTime()
                });
            }
            cursor = response.NextPageCursor;
            if (string.IsNullOrEmpty(cursor))
                break;
        }
        return favorites;
    }

    private static StoreData Load()
    {
        if (_store != null)
            return _store;
        try
        {
            if (File.Exists(StorePath))
                _store = JsonFile.Deserialize<StoreData>(StorePath, JsonOptions.Tolerant, MaximumStoreBytes);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The favorites cache could not be read: " + ex.Message);
        }
        return _store ??= new StoreData();
    }

    private static void Save(StoreData store)
    {
        _store = store;
        try
        {
            JsonFile.SerializeAtomic(StorePath, store, JsonOptions.Indented, false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The favorites cache could not be saved: " + ex.Message);
        }
    }
}
