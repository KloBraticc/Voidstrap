using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class LibraryStorefrontViewModel : INotifyPropertyChanged
{
    private const int FeaturedCount = 10;

    private const int TabGameCount = 15;

    private const int GenreCount = 8;

    private const int ScreenshotCount = 5;

    private static readonly HttpClient HttpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(20));

    private readonly List<StoreSort> _sorts = new();

    private StoreGameEntry? _featuredGame;

    private StoreSort? _selectedSort;

    private StoreGameEntry? _previewGame;

    private string? _selectedGenre;

    private bool _loaded;

    private bool _loading;

    public ObservableCollection<StoreGameEntry> Featured { get; } = new();

    public ObservableCollection<StoreSort> Sorts { get; } = new();

    public ObservableCollection<StoreGameEntry> TabGames { get; } = new();

    public ObservableCollection<StoreGameEntry> GenreTiles { get; } = new();

    public ICommand NextFeaturedCommand { get; }

    public ICommand PreviousFeaturedCommand { get; }

    public ICommand SelectFeaturedCommand { get; }

    public ICommand SelectSortCommand { get; }

    public ICommand SelectGenreCommand { get; }

    public ICommand ClearGenreCommand { get; }

    public LibraryStorefrontViewModel()
    {
        NextFeaturedCommand = new RelayCommand(NextFeatured);
        PreviousFeaturedCommand = new RelayCommand(() => MoveFeatured(-1));
        SelectFeaturedCommand = new RelayCommand<StoreGameEntry>(game => FeaturedGame = game);
        SelectSortCommand = new RelayCommand<StoreSort>(sort => SelectedSort = sort);
        SelectGenreCommand = new RelayCommand<StoreGameEntry>(game => SelectGenre(game?.GenreName));
        ClearGenreCommand = new RelayCommand(() => SelectGenre(null));
    }

    public Visibility StoreVisibility => Featured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public StoreGameEntry? FeaturedGame
    {
        get => _featuredGame;
        set
        {
            if (_featuredGame == value)
                return;
            if (_featuredGame != null)
                _featuredGame.IsCurrent = false;
            _featuredGame = value;
            if (_featuredGame != null)
                _featuredGame.IsCurrent = true;
            OnPropertyChanged(nameof(FeaturedGame));
        }
    }

    public StoreGameEntry? PreviewGame
    {
        get => _previewGame;
        set
        {
            if (_previewGame == value)
                return;
            _previewGame = value;
            OnPropertyChanged(nameof(PreviewGame));
        }
    }

    public StoreSort? SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (_selectedSort == value || value == null)
                return;
            if (_selectedSort != null)
                _selectedSort.IsCurrent = false;
            _selectedSort = value;
            _selectedSort.IsCurrent = true;
            OnPropertyChanged(nameof(SelectedSort));
            RebuildTabGames();
        }
    }

    public string? SelectedGenre => _selectedGenre;

    public Visibility GenreFilterVisibility => _selectedGenre == null ? Visibility.Collapsed : Visibility.Visible;

    public void NextFeatured() => MoveFeatured(1);

    private void MoveFeatured(int step)
    {
        if (Featured.Count == 0)
            return;
        int index = _featuredGame == null ? 0 : Featured.IndexOf(_featuredGame);
        FeaturedGame = Featured[(index + step + Featured.Count) % Featured.Count];
    }

    private void SelectGenre(string? genre)
    {
        _selectedGenre = string.IsNullOrEmpty(genre) ? null : genre;
        OnPropertyChanged(nameof(SelectedGenre));
        OnPropertyChanged(nameof(GenreFilterVisibility));
        RebuildTabGames();
    }

    private void RebuildTabGames()
    {
        IEnumerable<StoreGameEntry> games = _selectedGenre == null
            ? _selectedSort?.Games ?? Enumerable.Empty<StoreGameEntry>()
            : _sorts.SelectMany(static sort => sort.Games).Where(game => game.GenreName == _selectedGenre).GroupBy(static game => game.UniverseId).Select(static group => group.First());
        TabGames.Clear();
        foreach (StoreGameEntry game in games.Take(TabGameCount))
            TabGames.Add(game);
        PreviewGame = TabGames.FirstOrDefault();
    }

    public async Task LoadAsync(CancellationToken token = default, bool force = false)
    {
        const string LOG_IDENT = "LibraryStorefrontViewModel::LoadAsync";
        if (_loading || (_loaded && !force))
            return;
        _loading = true;
        try
        {
            List<StoreSort> sorts = await FetchSortsAsync(token).ConfigureAwait(false);
            if (sorts.Count == 0)
                return;
            List<StoreGameEntry> visible = sorts.SelectMany(static sort => sort.Games.Take(TabGameCount)).GroupBy(static game => game.UniverseId).Select(static group => group.First()).ToList();
            await FetchMediaAsync(visible, token).ConfigureAwait(false);
            Dictionary<long, StoreGameEntry> shared = visible.ToDictionary(static game => game.UniverseId);
            foreach (StoreSort sort in sorts)
                sort.Games = sort.Games.Take(TabGameCount).Select(game => shared[game.UniverseId]).ToList();
            await Application.Current.Dispatcher.InvokeAsync(() => Apply(sorts));
            _loaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(List<StoreSort> sorts)
    {
        _selectedSort = null;
        _featuredGame = null;
        _sorts.Clear();
        _sorts.AddRange(sorts);
        Sorts.Clear();
        foreach (StoreSort sort in sorts)
            Sorts.Add(sort);
        Featured.Clear();
        foreach (StoreGameEntry game in sorts[0].Games.Where(static game => game.Screenshots.Count > 0).Take(FeaturedCount))
            Featured.Add(game);
        FeaturedGame = Featured.FirstOrDefault();
        GenreTiles.Clear();
        foreach (StoreGameEntry game in sorts.SelectMany(static sort => sort.Games).Where(static game => !string.IsNullOrEmpty(game.GenreName) && game.HasThumbnail).GroupBy(static game => game.GenreName).Select(static group => group.First()).Take(GenreCount))
            GenreTiles.Add(game);
        SelectedSort = sorts[0];
        OnPropertyChanged(nameof(StoreVisibility));
    }

    private static async Task<List<StoreSort>> FetchSortsAsync(CancellationToken token)
    {
        string url = $"https://apis.roblox.com/explore-api/v1/get-sorts?sessionId={Guid.NewGuid()}&device=computer&country=all";
        using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
        List<StoreSort> sorts = new();
        foreach (JsonElement sort in document.RootElement.GetProperty("sorts").EnumerateArray())
        {
            if (!sort.TryGetProperty("games", out JsonElement games) || games.ValueKind != JsonValueKind.Array || games.GetArrayLength() == 0)
                continue;
            StoreSort entry = new()
            {
                Id = sort.GetProperty("sortId").GetString() ?? "",
                Title = sort.GetProperty("sortDisplayName").GetString() ?? "",
            };
            foreach (JsonElement game in games.EnumerateArray())
                entry.Games.Add(ReadGame(game));
            sorts.Add(entry);
        }
        return sorts;
    }

    private static StoreGameEntry ReadGame(JsonElement game)
    {
        long up = game.TryGetProperty("totalUpVotes", out JsonElement upVotes) ? upVotes.GetInt64() : 0;
        long down = game.TryGetProperty("totalDownVotes", out JsonElement downVotes) ? downVotes.GetInt64() : 0;
        string genre = game.TryGetProperty("genreL1", out JsonElement genreL1) ? genreL1.GetString() ?? "" : "";
        return new StoreGameEntry
        {
            UniverseId = game.GetProperty("universeId").GetInt64(),
            PlaceId = game.GetProperty("rootPlaceId").GetInt64(),
            Name = game.GetProperty("name").GetString() ?? "",
            Playing = game.TryGetProperty("playerCount", out JsonElement players) ? players.GetInt64() : 0,
            LikePercent = up + down > 0 ? Math.Round(up * 100.0 / (up + down)) + "%" : "",
            GenreName = genre,
            Genre = genre,
        };
    }

    private static async Task FetchMediaAsync(List<StoreGameEntry> games, CancellationToken token)
    {
        Dictionary<long, StoreGameEntry> byUniverse = games.ToDictionary(static game => game.UniverseId);
        foreach (StoreGameEntry[] chunk in games.Chunk(50))
        {
            string ids = string.Join(',', chunk.Select(static game => game.UniverseId));
            Dictionary<long, List<string>> screenshots = await FetchScreenshotsAsync(ids, token).ConfigureAwait(false);
            foreach (KeyValuePair<long, List<string>> pair in screenshots)
            {
                if (pair.Value.Count == 0)
                    continue;
                byUniverse[pair.Key].Screenshots = pair.Value;
                byUniverse[pair.Key].ThumbnailUrl = pair.Value[0];
            }
            string iconUrl = $"https://thumbnails.roblox.com/v1/games/icons?universeIds={ids}&size=256x256&format=Png&isCircular=false";
            using JsonDocument icons = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, iconUrl, token: token).ConfigureAwait(false));
            foreach (JsonElement icon in icons.RootElement.GetProperty("data").EnumerateArray())
                if (byUniverse.TryGetValue(icon.GetProperty("targetId").GetInt64(), out StoreGameEntry? game))
                    game.IconUrl = icon.GetProperty("imageUrl").GetString();
        }
    }

    public static async Task<List<string>> FetchScreenshotsAsync(long universeId, CancellationToken token)
    {
        Dictionary<long, List<string>> result = await FetchScreenshotsAsync(universeId.ToString(), token).ConfigureAwait(false);
        return result.TryGetValue(universeId, out List<string>? urls) ? urls : new List<string>();
    }

    private static async Task<Dictionary<long, List<string>>> FetchScreenshotsAsync(string ids, CancellationToken token)
    {
        string url = $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={ids}&countPerUniverse={ScreenshotCount}&defaults=true&size=768x432&format=Jpeg&isCircular=false";
        using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
        Dictionary<long, List<string>> result = new();
        foreach (JsonElement item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            List<string> urls = new();
            foreach (JsonElement thumbnail in item.GetProperty("thumbnails").EnumerateArray())
                if (thumbnail.TryGetProperty("imageUrl", out JsonElement image) && image.GetString() is { Length: > 0 } imageUrl)
                    urls.Add(imageUrl);
            result[item.GetProperty("universeId").GetInt64()] = urls;
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
