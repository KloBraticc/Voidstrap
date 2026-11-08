using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public long UniverseId { get; init; }

        public long PlaceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string PlayerCount { get; init; } = string.Empty;

        public string LikePercent { get; init; } = string.Empty;

        public Visibility LikeVisibility => LikePercent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public string? ThumbnailUrl
        {
            get => _thumbnailUrl;
            set => SetProperty(ref _thumbnailUrl, value);
        }
    }

    internal sealed partial class HomePageViewModel
    {
        private const int RecommendedLimit = 12;

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

        private async Task LoadDiscoverAsync()
        {
            const string LOG_IDENT = "HomePageViewModel::LoadDiscoverAsync";
            if (_discoverLoading)
                return;
            _discoverLoading = true;
            try
            {
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
                    .Take(FeaturedCandidateLimit)
                    .ToList();

                await FillDiscoverThumbnailsAsync(picks.Concat(featuredCandidates).ToList()).ConfigureAwait(false);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Recommended.Clear();
                    foreach (DiscoverGameEntry game in picks)
                        Recommended.Add(game);
                    OnPropertyChanged(nameof(RecommendedVisibility));
                    if (FeaturedGame == null && featuredCandidates.Count > 0)
                        FeaturedGame = featuredCandidates[0];
                });

                foreach (DiscoverGameEntry candidate in featuredCandidates)
                {
                    string? video = await GameVideoStore.GetLocalVideoAsync(candidate.UniverseId, CancellationToken.None).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(video))
                        continue;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        FeaturedGame = candidate;
                        FeaturedVideoPath = video;
                    });
                    return;
                }
                App.Logger.WriteLine(LOG_IDENT, "None of the top games has a trailer video");
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

        private static async Task<List<DiscoverGameEntry>> ReadOmniRecommendationsAsync(string session)
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
                LikePercent = up + down > 0 ? (up * 100 / (up + down)) + "%" : string.Empty,
            };
        }

        private static long ReadLong(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value))
                return 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
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
