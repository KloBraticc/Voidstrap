using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Pages
{
    internal sealed class DiscoverGameEntry : NotifyPropertyChangedViewModel
    {
        private string? _thumbnailUrl;

        private bool _isFavorite;

        public long UniverseId { get; init; }

        public long PlaceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string PlayerCount { get; init; } = string.Empty;

        public string LikePercent { get; init; } = string.Empty;

        public long Players { get; init; }

        public int Rating { get; init; } = -1;

        public Visibility LikeVisibility => LikePercent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public string? ThumbnailUrl
        {
            get => _thumbnailUrl;
            set => SetProperty(ref _thumbnailUrl, value);
        }

        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }
    }

    internal sealed partial class HomePageViewModel
    {
        private const int RecommendedLimit = 6;

        private const int FeaturedCandidateLimit = 10;

        private DiscoverGameEntry? _featuredGame;

        private string? _featuredVideoPath;

        private bool _discoverLoading;

        public ObservableCollection<DiscoverGameEntry> Recommended { get; } = new();

        public DiscoverGameEntry? FeaturedGame
        {
            get => _featuredGame;
            private set
            {
                if (SetProperty(ref _featuredGame, value))
                    OnPropertyChanged(nameof(FeaturedVisibility));
            }
        }

        public string? FeaturedVideoPath
        {
            get => _featuredVideoPath;
            private set => SetProperty(ref _featuredVideoPath, value);
        }

        public Visibility FeaturedVisibility => _featuredGame != null ? Visibility.Visible : Visibility.Collapsed;

        public Visibility RecommendedVisibility => Recommended.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private ICommand? _playDiscoverCommand;

        public ICommand PlayDiscoverCommand => _playDiscoverCommand ??= new RelayCommand<DiscoverGameEntry>(PlayDiscover);

        private ICommand? _openDiscoverCommand;

        public ICommand OpenDiscoverCommand => _openDiscoverCommand ??= new RelayCommand<DiscoverGameEntry>(OpenDiscover);

        private sealed record DiscoverCacheGame(long UniverseId, long PlaceId, string Name, string PlayerCount, string LikePercent, string? ThumbnailUrl);

        private sealed record DiscoverCache(DiscoverCacheGame? Featured, string? FeaturedVideoPath, List<DiscoverCacheGame> Recommended);

        private static DiscoverCache? _discoverCache;

        private static string DiscoverCachePath => Path.Combine(Paths.Cache, "HomeDiscover.json");

        private static DiscoverCacheGame ToCache(DiscoverGameEntry game) =>
            new(game.UniverseId, game.PlaceId, game.Name, game.PlayerCount, game.LikePercent, game.ThumbnailUrl);

        private static DiscoverGameEntry FromCache(DiscoverCacheGame game) =>
            new()
            {
                UniverseId = game.UniverseId,
                PlaceId = game.PlaceId,
                Name = game.Name,
                PlayerCount = game.PlayerCount,
                LikePercent = game.LikePercent,
                ThumbnailUrl = game.ThumbnailUrl,
            };

        private static DiscoverCache? ReadDiscoverCache()
        {
            const string LOG_IDENT = "HomePageViewModel::ReadDiscoverCache";
            try
            {
                if (File.Exists(DiscoverCachePath))
                    return JsonSerializer.Deserialize<DiscoverCache>(File.ReadAllText(DiscoverCachePath));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                App.Logger.WriteLine(LOG_IDENT, "The saved discover list could not be read: " + ex.Message);
            }
            return null;
        }

        private void RestoreDiscover()
        {
            if (_discoverCache == null)
                return;
            foreach (DiscoverCacheGame game in _discoverCache.Recommended)
                Recommended.Add(FromCache(game));
            if (_discoverCache.Featured != null)
            {
                _featuredGame = FromCache(_discoverCache.Featured);
                if (!string.IsNullOrEmpty(_discoverCache.FeaturedVideoPath) && File.Exists(_discoverCache.FeaturedVideoPath))
                    _featuredVideoPath = _discoverCache.FeaturedVideoPath;
            }
        }

        private static void SaveDiscover(DiscoverCache cache)
        {
            const string LOG_IDENT = "HomePageViewModel::SaveDiscover";
            _discoverCache = cache;
            try
            {
                Directory.CreateDirectory(Paths.Cache);
                File.WriteAllText(DiscoverCachePath, JsonSerializer.Serialize(cache));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                App.Logger.WriteLine(LOG_IDENT, "The discover list could not be saved: " + ex.Message);
            }
        }

        private async Task LoadDiscoverAsync()
        {
            const string LOG_IDENT = "HomePageViewModel::LoadDiscoverAsync";
            if (_discoverLoading)
                return;
            _discoverLoading = true;
            try
            {
                if (_discoverCache == null && Recommended.Count == 0)
                {
                    DiscoverCache? saved = await Task.Run(ReadDiscoverCache).ConfigureAwait(true);
                    if (saved != null && _discoverCache == null && Recommended.Count == 0)
                    {
                        _discoverCache = saved;
                        RestoreDiscover();
                        OnPropertyChanged(nameof(RecommendedVisibility));
                        OnPropertyChanged(nameof(FeaturedGame));
                        OnPropertyChanged(nameof(FeaturedVisibility));
                        OnPropertyChanged(nameof(FeaturedVideoPath));
                    }
                }
                string session = Guid.NewGuid().ToString();
                List<DiscoverGameEntry> topPlaying = new();
                List<DiscoverGameEntry> fallback = new();
                RobloxApiResponse sorts = await RobloxCookie
                    .SendGetWithCookieAsync("https://apis.roblox.com/explore-api/v1/get-sorts?sessionId=" + session, null, CancellationToken.None)
                    .ConfigureAwait(false);
                if (sorts.StatusCode == 200 && sorts.Body != null)
                    ReadExploreSorts(sorts.Body, topPlaying, fallback);

                List<DiscoverGameEntry> recommended = await ReadOmniRecommendationsAsync(session).ConfigureAwait(false);
                HashSet<long> played = Application.Current.Dispatcher.Invoke(() => _gameHistory.Select(entry => entry.UniverseId).ToHashSet());
                List<DiscoverGameEntry> picks = recommended.Concat(fallback)
                    .Where(game => !played.Contains(game.UniverseId))
                    .GroupBy(game => game.UniverseId)
                    .Select(group => group.First())
                    .Take(RecommendedLimit)
                    .ToList();
                List<DiscoverGameEntry> featuredCandidates = (topPlaying.Count > 0 ? topPlaying : fallback)
                    .Where(game => !game.Name.Contains("Brookhaven", StringComparison.OrdinalIgnoreCase))
                    .Take(FeaturedCandidateLimit)
                    .ToList();

                await FillDiscoverThumbnailsAsync(picks.Concat(featuredCandidates).ToList()).ConfigureAwait(false);

                if (picks.Count == 0 && featuredCandidates.Count == 0)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Recommended.Clear();
                    foreach (DiscoverGameEntry game in picks)
                        Recommended.Add(game);
                    OnPropertyChanged(nameof(RecommendedVisibility));
                    if (FeaturedGame == null && featuredCandidates.Count > 0)
                        FeaturedGame = featuredCandidates[0];
                });

                long cachedFeatured = _discoverCache?.Featured?.UniverseId ?? 0;
                foreach (DiscoverGameEntry candidate in featuredCandidates.OrderByDescending(game => game.UniverseId == cachedFeatured))
                {
                    string? video = await GameVideoStore.GetLocalVideoAsync(candidate.UniverseId, CancellationToken.None).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(video))
                        continue;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (FeaturedGame?.UniverseId != candidate.UniverseId)
                            FeaturedGame = candidate;
                        if (FeaturedVideoPath != video)
                            FeaturedVideoPath = video;
                    });
                    SaveDiscover(new DiscoverCache(ToCache(candidate), video, picks.Select(ToCache).ToList()));
                    return;
                }
                App.Logger.WriteLine(LOG_IDENT, "None of the top games has a trailer video");
                SaveDiscover(new DiscoverCache(featuredCandidates.Count > 0 ? ToCache(featuredCandidates[0]) : null, null, picks.Select(ToCache).ToList()));
            }
            finally
            {
                _discoverLoading = false;
            }
        }

        private static void ReadExploreSorts(string body, List<DiscoverGameEntry> topPlaying, List<DiscoverGameEntry> fallback)
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("sorts", out JsonElement sorts) || sorts.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement sort in sorts.EnumerateArray())
            {
                if (!sort.TryGetProperty("games", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
                    continue;
                string sortId = sort.TryGetProperty("sortId", out JsonElement id) ? id.GetString() ?? "" : "";
                List<DiscoverGameEntry> target = sortId.Contains("top-playing", StringComparison.OrdinalIgnoreCase) ? topPlaying : fallback;
                foreach (JsonElement game in games.EnumerateArray())
                {
                    DiscoverGameEntry? entry = ReadGame(game);
                    if (entry != null)
                        target.Add(entry);
                }
            }
        }

        internal static async Task<List<DiscoverGameEntry>> ReadOmniRecommendationsAsync(string session)
        {
            List<DiscoverGameEntry> result = new();
            string? cookie = RobloxCookie.Get();
            if (string.IsNullOrEmpty(cookie))
                return result;
            RobloxApiResponse response = await RobloxCookie
                .SendPostWithCookieAsync(
                    "https://apis.roblox.com/discovery-api/omni-recommendation",
                    "{\"pageType\":\"Home\",\"sessionId\":\"" + session + "\"}",
                    cookie,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (response.StatusCode != 200 || response.Body == null)
                return result;
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("contentMetadata", out JsonElement metadata)
                || !metadata.TryGetProperty("Game", out JsonElement gameData)
                || !root.TryGetProperty("sorts", out JsonElement sorts)
                || sorts.ValueKind != JsonValueKind.Array)
                return result;
            foreach (JsonElement sort in sorts.EnumerateArray())
            {
                string topic = sort.TryGetProperty("topic", out JsonElement t) ? t.GetString() ?? "" : "";
                if (topic.Contains("Continue", StringComparison.OrdinalIgnoreCase) || topic.Contains("Favorite", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!sort.TryGetProperty("recommendationList", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (!item.TryGetProperty("contentType", out JsonElement type) || type.GetString() != "Game")
                        continue;
                    if (!item.TryGetProperty("contentId", out JsonElement contentId))
                        continue;
                    if (gameData.TryGetProperty(contentId.ToString(), out JsonElement game) && ReadGame(game) is DiscoverGameEntry entry)
                        result.Add(entry);
                }
            }
            return result;
        }

        private static DiscoverGameEntry? ReadGame(JsonElement game)
        {
            long universeId = ReadLong(game, "universeId");
            long placeId = ReadLong(game, "rootPlaceId");
            string name = game.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            if (universeId <= 0 || placeId <= 0 || name.Length == 0)
                return null;
            long up = ReadLong(game, "totalUpVotes");
            long down = ReadLong(game, "totalDownVotes");
            return new DiscoverGameEntry
            {
                UniverseId = universeId,
                PlaceId = placeId,
                Name = name,
                PlayerCount = FormatCount(ReadLong(game, "playerCount")),
                Players = ReadLong(game, "playerCount"),
                Rating = up + down > 0 ? (int)(up * 100 / (up + down)) : -1,
                LikePercent = up + down > 0 ? (up * 100 / (up + down)) + "%" : string.Empty,
            };
        }

        private static long ReadLong(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value))
                return 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
        }

        internal static async Task<List<DiscoverGameEntry>> SearchGamesAsync(string query, string session, int maxPages, CancellationToken token)
        {
            List<DiscoverGameEntry> result = new();
            string pageToken = string.Empty;
            for (int page = 0; page < maxPages; page++)
            {
                RobloxApiResponse response = await RobloxCookie
                    .SendGetWithCookieAsync(
                        "https://apis.roblox.com/search-api/omni-search?searchQuery=" + Uri.EscapeDataString(query) + "&pageToken=" + Uri.EscapeDataString(pageToken) + "&sessionId=" + session + "&pageType=all",
                        null,
                        token
                    )
                    .ConfigureAwait(false);
                if (response.StatusCode != 200 || response.Body == null)
                    break;
                using JsonDocument doc = JsonDocument.Parse(response.Body);
                if (!doc.RootElement.TryGetProperty("searchResults", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
                    break;
                foreach (JsonElement group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("contents", out JsonElement contents) || contents.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement item in contents.EnumerateArray())
                    {
                        if (item.TryGetProperty("isSponsored", out JsonElement sponsored) && sponsored.ValueKind == JsonValueKind.True)
                            continue;
                        if (ReadGame(item) is DiscoverGameEntry entry)
                            result.Add(entry);
                    }
                }
                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() ?? string.Empty : string.Empty;
                if (pageToken.Length == 0)
                    break;
            }
            return result;
        }

        internal static async Task<DiscoverGameEntry?> LookupPlaceAsync(long placeId, CancellationToken token)
        {
            RobloxApiResponse universe = await RobloxCookie
                .SendGetWithCookieAsync("https://apis.roblox.com/universes/v1/places/" + placeId + "/universe", null, token)
                .ConfigureAwait(false);
            if (universe.StatusCode != 200 || universe.Body == null)
                return null;
            long universeId;
            using (JsonDocument doc = JsonDocument.Parse(universe.Body))
                universeId = ReadLong(doc.RootElement, "universeId");
            if (universeId <= 0)
                return null;
            RobloxApiResponse games = await RobloxCookie
                .SendGetWithCookieAsync("https://games.roblox.com/v1/games?universeIds=" + universeId, null, token)
                .ConfigureAwait(false);
            if (games.StatusCode != 200 || games.Body == null)
                return null;
            string name;
            long players;
            long rootPlaceId;
            using (JsonDocument doc = JsonDocument.Parse(games.Body))
            {
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    return null;
                name = data[0].TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                players = ReadLong(data[0], "playing");
                rootPlaceId = ReadLong(data[0], "rootPlaceId");
            }
            long up = 0;
            long down = 0;
            RobloxApiResponse votes = await RobloxCookie
                .SendGetWithCookieAsync("https://games.roblox.com/v1/games/votes?universeIds=" + universeId, null, token)
                .ConfigureAwait(false);
            if (votes.StatusCode == 200 && votes.Body != null)
            {
                using JsonDocument doc = JsonDocument.Parse(votes.Body);
                if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                {
                    up = ReadLong(data[0], "upVotes");
                    down = ReadLong(data[0], "downVotes");
                }
            }
            return new DiscoverGameEntry
            {
                UniverseId = universeId,
                PlaceId = rootPlaceId > 0 ? rootPlaceId : placeId,
                Name = name.Length > 0 ? name : "Place " + placeId,
                PlayerCount = FormatCount(players),
                Players = players,
                Rating = up + down > 0 ? (int)(up * 100 / (up + down)) : -1,
                LikePercent = up + down > 0 ? (up * 100 / (up + down)) + "%" : string.Empty,
            };
        }

        internal static async Task FillGameIconsAsync(List<DiscoverGameEntry> games, CancellationToken token)
        {
            Dictionary<long, List<DiscoverGameEntry>> byId = games.GroupBy(game => game.UniverseId).ToDictionary(group => group.Key, group => group.ToList());
            List<long> ids = byId.Keys.ToList();
            for (int i = 0; i < ids.Count; i += 50)
            {
                string chunk = string.Join(",", ids.Skip(i).Take(50));
                RobloxApiResponse response = await RobloxCookie
                    .SendGetWithCookieAsync("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + chunk + "&size=512x512&format=Png&isCircular=false", null, token)
                    .ConfigureAwait(false);
                if (response.StatusCode != 200 || response.Body == null)
                    continue;
                using JsonDocument doc = JsonDocument.Parse(response.Body);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement item in data.EnumerateArray())
                {
                    string url = item.TryGetProperty("imageUrl", out JsonElement u) ? u.GetString() ?? "" : "";
                    if (url.Length == 0 || !byId.TryGetValue(ReadLong(item, "targetId"), out List<DiscoverGameEntry>? targets))
                        continue;
                    foreach (DiscoverGameEntry target in targets)
                        target.ThumbnailUrl = url;
                }
            }
        }

        private static async Task FillDiscoverThumbnailsAsync(List<DiscoverGameEntry> games)
        {
            Dictionary<long, List<DiscoverGameEntry>> byId = games.GroupBy(game => game.UniverseId).ToDictionary(group => group.Key, group => group.ToList());
            List<long> ids = byId.Keys.ToList();
            for (int i = 0; i < ids.Count; i += 30)
            {
                string chunk = string.Join(",", ids.Skip(i).Take(30));
                RobloxApiResponse response = await RobloxCookie
                    .SendGetWithCookieAsync(
                        "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=" + chunk + "&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false",
                        null,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
                if (response.StatusCode != 200 || response.Body == null)
                    continue;
                using JsonDocument doc = JsonDocument.Parse(response.Body);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement item in data.EnumerateArray())
                {
                    long universeId = ReadLong(item, "universeId");
                    if (!byId.TryGetValue(universeId, out List<DiscoverGameEntry>? targets)
                        || !item.TryGetProperty("thumbnails", out JsonElement thumbs)
                        || thumbs.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement thumb in thumbs.EnumerateArray())
                    {
                        string url = thumb.TryGetProperty("imageUrl", out JsonElement u) ? u.GetString() ?? "" : "";
                        if (url.Length == 0)
                            continue;
                        foreach (DiscoverGameEntry target in targets)
                            target.ThumbnailUrl = url;
                        break;
                    }
                }
            }
        }

        private void PlayDiscover(DiscoverGameEntry? game)
        {
            if (game == null)
                return;
            LaunchPlace(game.PlaceId);
        }

        private static void OpenDiscover(DiscoverGameEntry? game)
        {
            if (game == null)
                return;
            Utilities.OpenWebLink("https://www.roblox.com/games/" + game.PlaceId);
        }
    }
}
