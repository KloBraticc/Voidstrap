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
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource? _searchCts;
    private bool _continueLoaded;
    private bool _favoritesLoaded;
    private bool _closed;
    private int _searchGeneration;
    private string _mode = "continue";

    public ObservableCollection<GameBrowserTile> Games { get; } = new();

    public SessionGameBrowser(ActivityWatcher activity)
    {
        _activity = activity;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _searchTimer.Tick += OnSearchTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await LoadContinueAsync();
    }

    private async Task LoadContinueAsync()
    {
        if (_continueLoaded || _closed)
            return;
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
            if (_closed || _mode != mode)
                return;
            ReplaceGames(games.Select(game => new GameBrowserTile(game)));
            _continueLoaded = true;
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
        if (_favoritesLoaded || _closed)
            return;
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
            if (_closed || _mode != mode)
                return;
            ReplaceGames(favorites.Select(game => new GameBrowserTile(game)));
            _favoritesLoaded = true;
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

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode } || _closed)
            return;
        _mode = mode;
        bool search = mode == "search";
        SearchBox.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        CancelSearch();
        if (mode == "continue")
            _ = LoadContinueAsync();
        else if (mode == "favorites")
            _ = LoadFavoritesAsync();
        else if (SearchBox.Text.Trim().Length >= 2)
            StartSearch();
        else
        {
            Games.Clear();
            SetBusy(false, Strings.ContextMenu_GameBrowser_SearchPrompt);
            UpdateCount();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_closed || _mode != "search")
            return;
        _searchTimer.Stop();
        CancelSearch();
        if (SearchBox.Text.Trim().Length < 2)
        {
            Games.Clear();
            SetBusy(false, Strings.ContextMenu_GameBrowser_SearchPrompt);
            UpdateCount();
            return;
        }
        _searchTimer.Start();
    }

    private void StartSearch() => _searchTimer.Start();

    private async void OnSearchTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        await SearchAsync(SearchBox.Text.Trim());
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

    private void GameTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameBrowserTile tile })
            return;
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
            Process.Start(startInfo);
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

    private void UpdateCount() => CountText.Text = Games.Count.ToString(Locale.CurrentCulture);

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
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Games.Clear();
        DataContext = null;
        GC.SuppressFinalize(this);
    }
}

public sealed class GameBrowserTile
{
    private readonly ActivityData _activity;

    public long PlaceId => _activity.PlaceId;
    public string Name => _activity.UniverseDetails?.Data?.Name ?? _activity.GameName;
    public string ThumbnailUrl => _activity.UniverseDetails?.Thumbnail?.ImageUrl ?? string.Empty;
    public string PlayingText => _activity.UniverseDetails?.Data?.Playing is long playing
        ? string.Format(Locale.CurrentCulture, Strings.ContextMenu_GameBrowser_Playing, playing)
        : string.Empty;

    public GameBrowserTile(ActivityData activity)
    {
        _activity = activity;
    }
}
