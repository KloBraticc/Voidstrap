using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionGameBrowser : Window
{
    private const int MaxRecentGames = 24;
    private const int MaxFavorites = 48;
    private const int MaxSearchResults = 20;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const long MaxHistoryBytes = 8 * 1024 * 1024;
    private static readonly string SearchSessionId = Guid.NewGuid().ToString();
    private readonly ActivityWatcher _activity;
    private readonly ServerMatchmaker? _matchmaker;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource? _searchCts;
    private bool _continueLoaded;
    private bool _favoritesLoaded;
    private List<GameBrowserTile> _continue = new();
    private List<GameBrowserTile> _favorites = new();
    private bool _closed;
    // The search tab is checked in the XAML, which raises Checked before the rest of the window exists
    private bool _ready;
    private bool _launching;
    private int _searchGeneration;
    private string _mode = "search";

    public ObservableCollection<GameBrowserTile> Games { get; } = new();

    public SessionGameBrowser(ActivityWatcher activity, ServerMatchmaker? matchmaker = null)
    {
        _activity = activity;
        _matchmaker = matchmaker;
        InitializeComponent();
        _ready = true;
        DataContext = this;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _searchTimer.Tick += OnSearchTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await LoadContinueAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "Recent games could not be shown: " + ex.Message);
        }
    }

    // The search tab shows the games you played last until something is typed
    private async Task LoadContinueAsync()
    {
        if (_closed)
            return;
        SectionText.Text = "Continue";
        if (_continueLoaded)
        {
            ReplaceGames(_continue);
            SetBusy(false, Games.Count == 0 ? Strings.ContextMenu_GameBrowser_NoContinue : string.Empty);
            return;
        }
        string mode = _mode;
        SetBusy(true, Strings.ContextMenu_GameBrowser_Loading);
        try
        {
            List<ActivityData> recent = new();
            if (_activity.InGame && _activity.Data.PlaceId > 0)
                recent.Add(_activity.Data);
            lock (_activity.History)
                recent.AddRange(_activity.History);
            recent.AddRange(await Task.Run(LoadStoredHistory, _lifetimeToken));
            List<ActivityData> games = recent
                .Where(game => game.PlaceId > 0)
                .OrderByDescending(game => game.TimeJoined)
                .GroupBy(game => game.PlaceId)
                .Select(group => group.First())
                .Take(MaxRecentGames)
                .ToList();
            await UniverseDetails.FetchForEntriesAsync(games, _lifetimeToken);
            _continue = games.Select(game => new GameBrowserTile(game)).ToList();
            _continueLoaded = true;
            if (_closed || _mode != mode || SearchBox.Text.Trim().Length >= 2)
                return;
            ReplaceGames(_continue);
            StatusText.Text = Games.Count == 0 ? Strings.ContextMenu_GameBrowser_NoContinue : string.Empty;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "Recent games could not be loaded: " + ex.Message);
            StatusText.Text = Strings.ContextMenu_GameBrowser_Failed;
        }
        finally
        {
            if (!_closed && _mode == mode)
            {
                SetBusy(false, StatusText.Text);
                UpdateCount();
            }
        }
    }

    private readonly CancellationTokenSource _lifetime = new();
    private CancellationToken _lifetimeToken => _lifetime.Token;

    private static List<ActivityData> LoadStoredHistory()
    {
        try
        {
            if (!File.Exists(Paths.ServerHistory))
                return new List<ActivityData>();
            FileInfo file = new(Paths.ServerHistory);
            if (file.Length <= 0 || file.Length > MaxHistoryBytes)
                return new List<ActivityData>();
            return JsonFile.Deserialize<List<ActivityData>>(Paths.ServerHistory, JsonOptions.Tolerant, MaxHistoryBytes)
                .Where(HistoryPersister.IsWithinDesktopRetention)
                .OrderByDescending(game => game.TimeJoined)
                .Take(100)
                .ToList();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "Saved game history could not be loaded: " + ex.Message);
            return new List<ActivityData>();
        }
    }

    private async Task LoadFavoritesAsync()
    {
        if (_closed)
            return;
        SectionText.Text = "Favorites";
        if (_favoritesLoaded)
        {
            ReplaceGames(_favorites);
            SetBusy(false, Games.Count == 0 ? Strings.ContextMenu_GameBrowser_NoFavorites : string.Empty);
            return;
        }
        string mode = _mode;
        SetBusy(true, Strings.ContextMenu_GameBrowser_Loading);
        try
        {
            List<ActivityData> favorites = (App.Settings.Prop.FavoriteGamePlaceIds ?? new List<long>())
                .Where(placeId => placeId > 0)
                .Distinct()
                .Take(MaxFavorites)
                .Select(placeId => new ActivityData { PlaceId = placeId })
                .ToList();
            await UniverseDetails.FetchForEntriesAsync(favorites, _lifetimeToken);
            _favorites = favorites.Select(game => new GameBrowserTile(game)).ToList();
            _favoritesLoaded = true;
            if (_closed || _mode != mode)
                return;
            ReplaceGames(_favorites);
            StatusText.Text = Games.Count == 0 ? Strings.ContextMenu_GameBrowser_NoFavorites : string.Empty;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "Favorite games could not be loaded: " + ex.Message);
            StatusText.Text = Strings.ContextMenu_GameBrowser_Failed;
        }
        finally
        {
            if (!_closed && _mode == mode)
            {
                SetBusy(false, StatusText.Text);
                UpdateCount();
            }
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_closed || !_ready)
            return;
        _mode = ReferenceEquals(sender, FavoritesTab) ? "favorites" : "search";
        _searchTimer.Stop();
        CancelSearch();
        if (_mode == "favorites")
            _ = LoadFavoritesAsync();
        else if (SearchBox.Text.Trim().Length >= 2)
            StartSearch();
        else
            _ = LoadContinueAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_closed || !_ready)
            return;
        _searchTimer.Stop();
        CancelSearch();
        // Typing always searches, even from the favorites tab
        if (_mode != "search")
        {
            _mode = "search";
            FavoritesTab.Checked -= Tab_Checked;
            SearchTab.Checked -= Tab_Checked;
            SearchTab.IsChecked = true;
            FavoritesTab.Checked += Tab_Checked;
            SearchTab.Checked += Tab_Checked;
        }
        if (SearchBox.Text.Trim().Length < 2)
        {
            _ = LoadContinueAsync();
            return;
        }
        SectionText.Text = "Results";
        _searchTimer.Start();
    }

    private void StartSearch() => _searchTimer.Start();

    private async void OnSearchTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        if (_closed)
            return;
        try
        {
            await SearchAsync(SearchBox.Text.Trim());
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "Game search could not be shown: " + ex.Message);
        }
    }

    private async Task SearchAsync(string query)
    {
        int generation = ++_searchGeneration;
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        request.CancelAfter(TimeSpan.FromSeconds(12));
        _searchCts = request;
        SetBusy(true, Strings.ContextMenu_GameBrowser_Loading);
        try
        {
            List<ActivityData> results = long.TryParse(query, out long placeId) && placeId > 0
                ? [new ActivityData { PlaceId = placeId }]
                : await SearchGamesAsync(query, request.Token);
            if (_closed || generation != _searchGeneration)
                return;
            await UniverseDetails.FetchForEntriesAsync(results, request.Token);
            if (_closed || generation != _searchGeneration)
                return;
            ReplaceGames(results.Select(game => new GameBrowserTile(game)));
            StatusText.Text = Games.Count == 0
                ? string.Format(Locale.CurrentCulture, Strings.ContextMenu_GameBrowser_NoResults, query)
                : string.Empty;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!_closed && generation == _searchGeneration)
                StatusText.Text = Strings.ContextMenu_GameBrowser_Failed;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "Game search failed: " + ex.Message);
            if (!_closed && generation == _searchGeneration)
                StatusText.Text = Strings.ContextMenu_GameBrowser_Failed;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, request))
                _searchCts = null;
            if (!_closed && generation == _searchGeneration)
            {
                SetBusy(false, StatusText.Text);
                UpdateCount();
            }
        }
    }

    private static async Task<List<ActivityData>> SearchGamesAsync(string query, CancellationToken token)
    {
        string url = "https://apis.roblox.com/search-api/omni-search?searchQuery=" + Uri.EscapeDataString(query)
            + "&pageToken=&sessionId=" + SearchSessionId + "&pageType=all";
        using HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        string body = await Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token);
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("searchResults", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
            return new List<ActivityData>();
        List<ActivityData> results = new();
        HashSet<long> seen = new();
        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("contents", out JsonElement contents) || contents.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement item in contents.EnumerateArray())
            {
                if (results.Count >= MaxSearchResults)
                    return results;
                if (item.TryGetProperty("isSponsored", out JsonElement sponsored) && sponsored.ValueKind == JsonValueKind.True)
                    continue;
                if (!item.TryGetProperty("rootPlaceId", out JsonElement place) || !place.TryGetInt64(out long id) || id <= 0 || !seen.Add(id))
                    continue;
                ActivityData activity = new() { PlaceId = id };
                if (item.TryGetProperty("universeId", out JsonElement universe) && universe.TryGetInt64(out long universeId))
                    activity.UniverseId = universeId;
                results.Add(activity);
            }
        }
        return results;
    }

    private async void GameTile_Click(object sender, RoutedEventArgs e)
    {
        if (_launching || _closed || sender is not Button { Tag: GameBrowserTile tile } || tile.PlaceId <= 0)
            return;
        if (_matchmaker != null && _activity.InGame)
        {
            // Roblox is already running, so switch through the matchmaker handoff instead of starting a second client
            _launching = true;
            SetBusy(true, Strings.ContextMenu_GameBrowser_Loading);
            try
            {
                if (await Task.Run(() => _matchmaker.LaunchPlaceAsync(tile.PlaceId, null, CancellationToken.None)))
                    return;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionGameBrowser", "The selected game could not be opened: " + ex.Message);
            }
            finally
            {
                _launching = false;
                if (!_closed)
                    SetBusy(false, string.Empty);
            }
            if (!_closed)
                StatusText.Text = Strings.ContextMenu_GameBrowser_Failed;
            return;
        }
        try
        {
            string executable = Paths.LaunchExecutable;
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
            };
            startInfo.ArgumentList.Add("-player");
            startInfo.ArgumentList.Add($"roblox://experiences/start?placeId={tile.PlaceId}");
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionGameBrowser", "The selected game could not be opened: " + ex.Message);
            StatusText.Text = Strings.ContextMenu_GameBrowser_Failed;
        }
    }

    private void ReplaceGames(IEnumerable<GameBrowserTile> games)
    {
        Games.Clear();
        foreach (GameBrowserTile game in games)
            Games.Add(game);
    }

    private void SetBusy(bool busy, string status)
    {
        LoadingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private void UpdateCount()
    {
    }

    private void CancelSearch()
    {
        _searchGeneration++;
        _searchCts?.Cancel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        SearchBox.TextChanged -= SearchBox_TextChanged;
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTick;
        // The token sources are only cancelled, never disposed, because loads that are still running read them
        _searchCts?.Cancel();
        _searchCts = null;
        _lifetime.Cancel();
        Games.Clear();
        DataContext = null;
    }
}

public sealed class GameBrowserTile
{
    private readonly ActivityData _activity;

    public long PlaceId => _activity.PlaceId;
    public string Name => _activity.UniverseDetails?.Data?.Name ?? _activity.GameName;
    private BitmapImage? _thumbnail;
    private bool _thumbnailResolved;

    public string ThumbnailUrl => _activity.UniverseDetails?.Thumbnail?.ImageUrl ?? string.Empty;

    // Decoded at tile size, the full thumbnail is several times larger than the 148 pixel tile
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnailResolved)
                return _thumbnail;
            _thumbnailResolved = true;
            if (!Uri.TryCreate(ThumbnailUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return null;
            try
            {
                BitmapImage image = new();
                image.BeginInit();
                image.UriSource = uri;
                image.DecodePixelWidth = 288;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                _thumbnail = image;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionGameBrowser", "A game thumbnail could not be loaded: " + ex.Message);
            }
            return _thumbnail;
        }
    }
    public string PlayingText => _activity.UniverseDetails?.Data?.Playing is long playing ? Compact(playing) + " playing" : string.Empty;

    // 52, 1.1K, 23K, 1.2M
    private static string Compact(long value)
    {
        System.Globalization.CultureInfo culture = Locale.CurrentCulture;
        if (value >= 1_000_000)
            return (value / 1_000_000d).ToString(value >= 10_000_000 ? "0" : "0.#", culture) + "M";
        if (value >= 1_000)
            return (value / 1_000d).ToString(value >= 10_000 ? "0" : "0.#", culture) + "K";
        return value.ToString(culture);
    }

    public GameBrowserTile(ActivityData activity)
    {
        _activity = activity;
    }
}
