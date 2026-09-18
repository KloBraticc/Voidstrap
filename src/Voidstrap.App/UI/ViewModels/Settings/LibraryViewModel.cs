using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Models.Entities;
using Voidstrap.Models.Persistable;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class LibraryEventEntry
{
    public string Id { get; init; } = string.Empty;
    public long UniverseId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public DateTime? StartUtc { get; init; }
    public DateTime? EndUtc { get; init; }
    public long MediaId { get; init; }

    public string EventUrl => "https://www.roblox.com/events/" + Id;

    public string StartDisplay => StartUtc.HasValue
        ? StartUtc.Value.ToLocalTime().ToString("ddd, MMM d, h:mm tt")
        : string.Empty;

    public string SubtitleDisplay => string.IsNullOrWhiteSpace(Subtitle) ? GameName : Subtitle;
}

public class LibraryGameEntry : INotifyPropertyChanged
{
    private bool _isPinned;
    private string _likePercent = "";
    private long _playing;

    public long PlaceId { get; set; }

    public long UniverseId { get; set; }

    public string Name { get; set; } = "";

    public string CreatorName { get; set; } = "";

    public string Description { get; set; } = "";

    public string? IconUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    public long Visits { get; set; }

    public int MaxPlayers { get; set; }

    public string Genre { get; set; } = "";

    public DateTime? Created { get; set; }

    public DateTime? Updated { get; set; }

    public DateTime? LastPlayed { get; set; }

    public double PlayTimeMinutes { get; set; }

    public bool HasIcon => !string.IsNullOrEmpty(IconUrl);

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    public string CreatorDisplay => string.IsNullOrEmpty(CreatorName) ? "" : "by " + CreatorName;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned != value)
            {
                _isPinned = value;
                OnPropertyChanged(nameof(IsPinned));
                OnPropertyChanged(nameof(PinButtonText));
                OnPropertyChanged(nameof(PinIconVisibility));
            }
        }
    }

    public string PinButtonText => IsPinned ? "Unpin" : "Pin";

    public Visibility PinIconVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public string SidebarGroup => IsPinned ? "PINNED" : "ALL";

    public string LikePercent
    {
        get => _likePercent;
        set
        {
            if (_likePercent != value)
            {
                _likePercent = value;
                OnPropertyChanged(nameof(LikePercent));
            }
        }
    }

    public long Playing
    {
        get => _playing;
        set
        {
            if (_playing != value)
            {
                _playing = value;
                OnPropertyChanged(nameof(Playing));
                OnPropertyChanged(nameof(PlayingDisplay));
            }
        }
    }

    public string PlayingDisplay => Playing > 0 ? LibraryViewModel.FormatCount(Playing) : "0";

    public string VisitsDisplay => Visits > 0 ? LibraryViewModel.FormatCount(Visits) : "0";

    public string LastPlayedDisplay
    {
        get
        {
            if (LastPlayed == null || LastPlayed.Value == default)
                return "Never";
            DateTime value = LastPlayed.Value;
            if (value.Date == DateTime.Now.Date)
                return "Today";
            if (value.Date == DateTime.Now.Date.AddDays(-1))
                return "Yesterday";
            return value.ToString("MMM d, yyyy");
        }
    }

    public string PlayTimeDisplay
    {
        get
        {
            if (PlayTimeMinutes < 1)
                return "None recorded";
            long total = (long)Math.Round(PlayTimeMinutes);
            long hours = total / 60;
            long mins = total % 60;
            if (hours == 0)
                return $"{mins} min";
            if (mins == 0)
                return $"{hours} hr";
            return $"{hours} hr {mins} min";
        }
    }

    public string UpdatedAgoDisplay
    {
        get
        {
            if (Updated == null)
                return "";
            TimeSpan span = DateTime.Now - Updated.Value.ToLocalTime();
            if (span.TotalDays < 1)
                return "Updated today";
            if (span.TotalDays < 2)
                return "Updated yesterday";
            if (span.TotalDays < 30)
                return $"Updated {Math.Floor(span.TotalDays)} days ago";
            return "Updated " + Updated.Value.ToLocalTime().ToString("MMM d, yyyy");
        }
    }

    public string CreatedDisplay => Created?.ToLocalTime().ToString("MMM d, yyyy") ?? "Unknown";

    public string UpdatedDisplay => Updated?.ToLocalTime().ToString("MMM d, yyyy") ?? "Unknown";

    public void RefreshAll()
    {
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class GamePassEntry
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string PriceDisplay { get; set; } = "";

    public string? IconUrl { get; set; }

    public bool HasIcon => !string.IsNullOrEmpty(IconUrl);
}

internal sealed class LibraryCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> source)
    {
        Items.Clear();
        foreach (T item in source)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public class LibraryViewModel : INotifyPropertyChanged
{
	private const int MaximumHistoryBytes = 16 * 1024 * 1024;

	private const int MaximumHistoryEntries = 100;

    private const int GamePageSize = 30;

    private const long MaximumSnapshotBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions SnapshotOptions = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };

    private static string SnapshotPath => Integrations.LibraryStore.SnapshotPath;

    private static readonly HttpClient _http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(20));

    private readonly string _historyFilePath = Paths.ServerHistory;

    private readonly Dictionary<long, List<GamePassEntry>> _gamePassCache = new();

    private readonly List<LibraryGameEntry> _masterGames = new();

    private LibraryGameEntry? _selectedGame;

    private string _sidebarSearch = "";

    private string _statusText = "";

    private string _gamePassStatus = "";

    private string _addGameText = "";

    private bool _isLoading;

    private bool _gamePassesLoading;

    private bool _loadInFlight;

    private bool _reloadRequested;

    private bool _reloadForced;

    private bool _loadingMoreGames;

    private CancellationTokenSource? _gamePassLoadCts;

    private CancellationTokenSource? _loadCts;

    private long _gamePassLoadVersion;

    public bool HasLoaded { get; private set; }

    public ObservableCollection<LibraryGameEntry> SidebarGames { get; } = new LibraryCollection<LibraryGameEntry>();

    public ObservableCollection<LibraryGameEntry> RecentGames { get; } = new LibraryCollection<LibraryGameEntry>();

    public ObservableCollection<LibraryGameEntry> WhatsNew { get; } = new LibraryCollection<LibraryGameEntry>();

    public ObservableCollection<LibraryEventEntry> Events { get; } = new LibraryCollection<LibraryEventEntry>();

    public ObservableCollection<LibraryGameEntry> AllGames { get; } = new LibraryCollection<LibraryGameEntry>();

    public ObservableCollection<GamePassEntry> GamePasses { get; } = new LibraryCollection<GamePassEntry>();

    public ICommand SelectGameCommand { get; }

    public ICommand OpenEventCommand { get; }

    public ICommand GoHomeCommand { get; }

    public ICommand LaunchCommand { get; }

    public ICommand TogglePinCommand { get; }

    public ICommand AddGameCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand RemoveGameCommand { get; }

    public ICommand OpenGamePassCommand { get; }

    public LibraryViewModel()
    {
        SelectGameCommand = new RelayCommand<LibraryGameEntry>(SelectGame);
        OpenEventCommand = new RelayCommand<LibraryEventEntry>(OpenEvent);
        GoHomeCommand = new RelayCommand(GoHome);
        LaunchCommand = new RelayCommand<LibraryGameEntry>(LaunchGame);
        TogglePinCommand = new RelayCommand<LibraryGameEntry>(TogglePin);
        AddGameCommand = new AsyncRelayCommand(AddGameFromTextAsync);
        RefreshCommand = new RelayCommand(Refresh);
        RemoveGameCommand = new AsyncRelayCommand<LibraryGameEntry>(RemoveGameAsync);
        OpenGamePassCommand = new RelayCommand<GamePassEntry>(OpenGamePass);
    }

    private void OpenGamePass(GamePassEntry? pass)
    {
        if (pass == null || pass.Id == 0)
            return;
        try
        {
            Utilities.ShellExecute($"https://www.roblox.com/game-pass/{pass.Id}");
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("LibraryViewModel::OpenGamePass", ex);
        }
    }

    public LibraryGameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (_selectedGame != value)
            {
                _selectedGame = value;
                OnPropertyChanged(nameof(SelectedGame));
                OnPropertyChanged(nameof(DashboardVisibility));
                OnPropertyChanged(nameof(DetailVisibility));
				CancellationTokenSource? previous = _gamePassLoadCts;
				_gamePassLoadCts = null;
				previous?.Cancel();
				long version = Interlocked.Increment(ref _gamePassLoadVersion);
				GamePasses.Clear();
				GamePassStatus = "";
				GamePassesLoading = false;
                if (value != null)
                {
					value.RefreshAll();
					CancellationTokenSource current = new();
					_gamePassLoadCts = current;
					GamePassesLoading = true;
					_ = LoadGamePassesAsync(value.UniverseId, version, current);
                }
            }
        }
    }

    public Visibility DashboardVisibility => SelectedGame == null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DetailVisibility => SelectedGame == null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WhatsNewVisibility => WhatsNew.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EventsVisibility => Events.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RecentVisibility => RecentGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyLibraryVisibility => AllGames.Count == 0 && !IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public string AllGamesHeader => $"All Games ({_masterGames.Count})";

    public string SidebarCountText => $"ALL ({_masterGames.Count})";

    public string TotalPlayTimeDisplay
    {
        get
        {
            double total = _masterGames.Sum(g => g.PlayTimeMinutes);
            if (total < 1)
                return "Total playtime: none yet";
            long rounded = (long)Math.Round(total);
            long hours = rounded / 60;
            long mins = rounded % 60;
            if (hours == 0)
                return $"Total playtime: {mins} min";
            if (mins == 0)
                return $"Total playtime: {hours} hr";
            return $"Total playtime: {hours} hr {mins} min";
        }
    }

    public string SidebarSearch
    {
        get => _sidebarSearch;
        set
        {
            if (_sidebarSearch != value)
            {
                _sidebarSearch = value;
                OnPropertyChanged(nameof(SidebarSearch));
                RebuildSidebar();
            }
        }
    }

    public string AddGameText
    {
        get => _addGameText;
        set
        {
            if (_addGameText != value)
            {
                _addGameText = value;
                OnPropertyChanged(nameof(AddGameText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }
    }

    public Visibility StatusVisibility => string.IsNullOrEmpty(StatusText) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(EmptyLibraryVisibility));
            }
        }
    }

    public bool GamePassesLoading
    {
        get => _gamePassesLoading;
        set
        {
            if (_gamePassesLoading != value)
            {
                _gamePassesLoading = value;
                OnPropertyChanged(nameof(GamePassesLoading));
            }
        }
    }

    public string GamePassStatus
    {
        get => _gamePassStatus;
        set
        {
            if (_gamePassStatus != value)
            {
                _gamePassStatus = value;
                OnPropertyChanged(nameof(GamePassStatus));
                OnPropertyChanged(nameof(GamePassStatusVisibility));
            }
        }
    }

    public Visibility GamePassStatusVisibility => string.IsNullOrEmpty(GamePassStatus) ? Visibility.Collapsed : Visibility.Visible;

    public static string FormatCount(long value)
    {
        if (value >= 1_000_000_000)
            return $"{value / 1_000_000_000.0:0.#}B";
        if (value >= 1_000_000)
            return $"{value / 1_000_000.0:0.#}M";
        if (value >= 1_000)
            return $"{value / 1_000.0:0.#}K";
        return value.ToString();
    }

    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (_loadInFlight)
        {
            _reloadRequested = true;
            _reloadForced |= forceRefresh;
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        CancellationTokenSource cancellation = new();
        _loadCts = cancellation;
        CancellationToken token = cancellation.Token;
        _loadInFlight = true;
        HasLoaded = true;
        IsLoading = true;
        StatusText = "";
        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            List<AppSettings.LibraryPin> pins = Integrations.LibraryStore.Pins;
            (List<LibraryGameEntry> shownGames, bool snapshotFresh) = await Task.Run(() => BuildLocalGames(pins), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (shownGames.Count > 0)
            {
                await UpdateUiAsync(() =>
                {
                    shownGames = MergeWithShown(shownGames);
                    PublishGamesCore(shownGames);
                    IsLoading = false;
                }).ConfigureAwait(false);
                App.Logger.WriteLine("LibraryViewModel", "Cached library shown in " + elapsed.ElapsedMilliseconds + " ms");
            }

            bool refreshShown = forceRefresh || !snapshotFresh || shownGames.Exists(static game => !IsEnriched(game));
            if (refreshShown)
                await EnrichAsync(shownGames, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            List<LibraryGameEntry> games = await BuildHistoryGamesAsync(pins, token).ConfigureAwait(false);
            HashSet<long> shownIds = shownGames.Select(static game => game.UniverseId).ToHashSet();
            List<LibraryGameEntry> added = games.Where(game => !shownIds.Contains(game.UniverseId)).ToList();
            await EnrichAsync(added, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            List<LibraryGameEntry> published = games;
            await UpdateUiAsync(() =>
            {
                published = MergeWithShown(games);
                PersistPinSnapshots(published, pins);
                PublishGamesCore(published);
                IsLoading = false;
            }).ConfigureAwait(false);
            bool usedNetwork = refreshShown || added.Count > 0;
            App.Logger.WriteLine("LibraryViewModel", "Library details shown in " + elapsed.ElapsedMilliseconds + " ms, network refresh " + (usedNetwork ? "used" : "skipped"));

            if (usedNetwork)
                SaveSnapshot(published);
            _ = FetchEventsInBackgroundAsync(published, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HasLoaded = false;
            App.Logger.WriteException("LibraryViewModel::LoadAsync", ex);
            await UpdateUiAsync(() => StatusText = "Failed to load your library: " + ex.Message).ConfigureAwait(false);
        }
        finally
        {
            await UpdateUiAsync(() =>
            {
                if (ReferenceEquals(_loadCts, cancellation))
                    IsLoading = false;
            }).ConfigureAwait(false);
            _loadInFlight = false;
            if (_reloadRequested)
            {
                bool force = _reloadForced;
                _reloadRequested = false;
                _reloadForced = false;
                _ = UpdateUiAsync(() => _ = LoadAsync(force));
            }
        }
    }

    private (List<LibraryGameEntry> Games, bool Fresh) BuildLocalGames(List<AppSettings.LibraryPin> pins)
    {
        List<LibraryGameEntry> games = BuildEntries(ReadHistoryFile(), pins, Integrations.PlayTimeStore.Read());
        bool fresh = false;
        try
        {
            string path = SnapshotPath;
            if (File.Exists(path))
            {
                fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < SnapshotFreshness;
                Dictionary<long, LibraryGameEntry> stored = new();
                foreach (LibraryGameEntry entry in JsonFile.Deserialize<List<LibraryGameEntry>>(path, SnapshotOptions, MaximumSnapshotBytes))
                {
                    if (entry != null && entry.UniverseId > 0)
                        stored.TryAdd(entry.UniverseId, entry);
                }
                foreach (LibraryGameEntry game in games)
                {
                    if (stored.TryGetValue(game.UniverseId, out LibraryGameEntry? snapshot))
                        ApplyMetadata(game, snapshot);
                }
            }
        }
        catch (Exception ex)
        {
            fresh = false;
            App.Logger.WriteLine("LibraryViewModel", "Library snapshot could not be read: " + ex.Message);
        }
        ApplyCachedDetails(games);
        return (games, fresh);
    }

    private async Task<List<LibraryGameEntry>> BuildHistoryGamesAsync(List<AppSettings.LibraryPin> pins, CancellationToken token)
    {
        List<Models.Entities.ActivityData> history = await Task.Run(ReadHistoryFile, token).ConfigureAwait(false);
        List<Models.Entities.ActivityData> unresolved = history.Where(static session => session.UniverseId == 0).ToList();
        if (unresolved.Count > 0)
        {
            await UniverseDetails.ResolvePlacesToUniversesAsync(unresolved.Select(static session => session.PlaceId), token).ConfigureAwait(false);
            foreach (Models.Entities.ActivityData session in unresolved)
            {
                if (UniverseDetails.TryGetUniverseForPlace(session.PlaceId, out long resolvedUniverse))
                    session.UniverseId = resolvedUniverse;
            }
        }
        Integrations.PlayTimeStore.StoreData playStore = await Task.Run(Integrations.PlayTimeStore.Read, token).ConfigureAwait(false);
        List<LibraryGameEntry> games = BuildEntries(history, pins, playStore);
        ApplyCachedDetails(games);
        return games;
    }

    private static async Task EnrichAsync(List<LibraryGameEntry> games, CancellationToken token)
    {
        if (games.Count == 0)
            return;
        await Task.WhenAll(
            FetchUniverseDetailsAsync(games, token),
            FetchLandscapeThumbnailsAsync(games, token),
            FetchVotesAsync(games, token)).ConfigureAwait(false);
        ApplyCachedDetails(games);
    }

    private List<LibraryGameEntry> MergeWithShown(List<LibraryGameEntry> games)
    {
        Dictionary<long, LibraryGameEntry> shown = new();
        foreach (LibraryGameEntry game in _masterGames)
            shown.TryAdd(game.UniverseId, game);
        List<LibraryGameEntry> merged = new(games.Count);
        foreach (LibraryGameEntry game in games)
        {
            if (!shown.TryGetValue(game.UniverseId, out LibraryGameEntry? existing) || ReferenceEquals(existing, game))
            {
                merged.Add(game);
                continue;
            }
            ApplyMetadata(existing, game);
            if (game.PlaceId != 0)
                existing.PlaceId = game.PlaceId;
            existing.LastPlayed = game.LastPlayed;
            existing.PlayTimeMinutes = game.PlayTimeMinutes;
            existing.IsPinned = game.IsPinned;
            existing.RefreshAll();
            merged.Add(existing);
        }
        return merged;
    }

    private static void ApplyMetadata(LibraryGameEntry target, LibraryGameEntry source)
    {
        if (!IsPlaceholderName(source) && IsPlaceholderName(target))
            target.Name = source.Name;
        if (!string.IsNullOrWhiteSpace(source.CreatorName))
            target.CreatorName = source.CreatorName;
        if (!string.IsNullOrWhiteSpace(source.Description))
            target.Description = source.Description;
        if (!string.IsNullOrWhiteSpace(source.IconUrl))
            target.IconUrl = source.IconUrl;
        if (!string.IsNullOrWhiteSpace(source.ThumbnailUrl))
            target.ThumbnailUrl = source.ThumbnailUrl;
        if (source.Visits > 0)
            target.Visits = source.Visits;
        if (source.MaxPlayers > 0)
            target.MaxPlayers = source.MaxPlayers;
        if (!string.IsNullOrWhiteSpace(source.Genre))
            target.Genre = source.Genre;
        if (source.Created != null)
            target.Created = source.Created;
        if (source.Updated != null)
            target.Updated = source.Updated;
        if (!string.IsNullOrEmpty(source.LikePercent))
            target.LikePercent = source.LikePercent;
        if (source.Playing > 0)
            target.Playing = source.Playing;
        if (target.PlaceId == 0)
            target.PlaceId = source.PlaceId;
    }

    private static bool IsPlaceholderName(LibraryGameEntry game)
    {
        return string.IsNullOrWhiteSpace(game.Name) || game.Name == "Unknown game" || game.Name == "Place " + game.PlaceId;
    }

    private static bool IsEnriched(LibraryGameEntry game)
    {
        return !IsPlaceholderName(game) && !string.IsNullOrEmpty(game.IconUrl) && !string.IsNullOrEmpty(game.ThumbnailUrl);
    }

    private static void SaveSnapshot(List<LibraryGameEntry> games)
    {
        try
        {
            Directory.CreateDirectory(Paths.Library);
            JsonFile.SerializeAtomic(SnapshotPath, games, SnapshotOptions, false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "Library snapshot could not be saved: " + ex.Message);
        }
    }

    private static async Task FetchUniverseDetailsAsync(List<LibraryGameEntry> games, CancellationToken token)
    {
        List<long> ids = games
            .Where(static game => game.UniverseId > 0)
            .Select(static game => game.UniverseId)
            .Distinct()
            .ToList();
        await Task.WhenAll(Chunk(ids, 50).Select(async chunk =>
        {
            try
            {
                await UniverseDetails.FetchBulk(chunk, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Details fetch failed: " + ex.Message);
            }
        })).ConfigureAwait(false);
    }

    private async Task FetchEventsInBackgroundAsync(List<LibraryGameEntry> games, CancellationToken token)
    {
        try
        {
            List<LibraryGameEntry> prioritized = games
                .OrderByDescending(static game => game.IsPinned)
                .ThenByDescending(static game => game.LastPlayed)
                .Take(48)
                .ToList();
            await FetchEventsAsync(prioritized, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "Event loading stopped: " + ex.Message);
        }
    }

    private static void ApplyCachedDetails(List<LibraryGameEntry> games)
    {
        foreach (LibraryGameEntry game in games)
        {
            UniverseDetails? details = UniverseDetails.LoadFromCache(game.UniverseId);
            if (details?.Data == null)
            {
                if (string.IsNullOrEmpty(game.Name))
                    game.Name = $"Place {game.PlaceId}";
                continue;
            }
            game.Name = details.Data.Name ?? $"Place {game.PlaceId}";
            game.CreatorName = details.Data.Creator?.Name ?? "";
            game.Description = details.Data.Description ?? "";
            game.Playing = details.Data.Playing;
            game.Visits = details.Data.Visits;
            game.MaxPlayers = details.Data.MaxPlayers;
            game.Genre = details.Data.Genre ?? "";
            game.Created = details.Data.Created;
            game.Updated = details.Data.Updated;
            game.IconUrl = details.Thumbnail?.ImageUrl ?? game.IconUrl;
            if (game.PlaceId == 0)
                game.PlaceId = details.Data.RootPlaceId;
        }
    }

    private static Task UpdateUiAsync(Action action)
    {
        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }

    private static List<LibraryGameEntry> BuildEntries(
        List<Models.Entities.ActivityData> rawHistory,
        List<AppSettings.LibraryPin> pins,
        Integrations.PlayTimeStore.StoreData playStore)
    {

        Dictionary<long, LibraryGameEntry> byUniverse = new();
        foreach (Models.Entities.ActivityData session in rawHistory.OrderByDescending(x => x.TimeJoined))
        {
            if (session.UniverseId == 0)
                continue;
            if (!byUniverse.TryGetValue(session.UniverseId, out LibraryGameEntry? entry))
            {
                entry = new LibraryGameEntry
                {
                    UniverseId = session.UniverseId,
                    PlaceId = session.PlaceId,
                    LastPlayed = session.TimeJoined
                };
                byUniverse[session.UniverseId] = entry;
            }
            if (session.TimeLeft.HasValue && session.TimeLeft.Value > session.TimeJoined)
            {
                double minutes = (session.TimeLeft.Value - session.TimeJoined).TotalMinutes;
                if (minutes > 0 && minutes < 1440)
                    entry.PlayTimeMinutes += minutes;
            }
        }

        foreach (AppSettings.LibraryPin pin in pins)
        {
            if (pin.UniverseId == 0)
                continue;
            if (!byUniverse.TryGetValue(pin.UniverseId, out LibraryGameEntry? entry))
            {
                entry = new LibraryGameEntry
                {
                    UniverseId = pin.UniverseId,
                    PlaceId = pin.PlaceId,
                    Name = pin.Name ?? ""
                };
                byUniverse[pin.UniverseId] = entry;
            }
            ApplyPinSnapshot(entry, pin);
            entry.IsPinned = true;
            if (entry.PlaceId == 0)
                entry.PlaceId = pin.PlaceId;
            if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(pin.Name))
                entry.Name = pin.Name;
        }

        foreach (KeyValuePair<long, Integrations.PlayTimeStore.UniverseTime> stored in playStore.Universes)
        {
            if (!byUniverse.TryGetValue(stored.Key, out LibraryGameEntry? entry))
            {
                entry = new LibraryGameEntry
                {
                    UniverseId = stored.Key
                };
                byUniverse[stored.Key] = entry;
            }
            if (stored.Value.Minutes > entry.PlayTimeMinutes)
                entry.PlayTimeMinutes = stored.Value.Minutes;
            if (stored.Value.LastPlayed != null && (entry.LastPlayed == null || stored.Value.LastPlayed > entry.LastPlayed))
                entry.LastPlayed = stored.Value.LastPlayed;
        }

        List<LibraryGameEntry> games = byUniverse.Values.ToList();
        foreach (LibraryGameEntry game in games)
        {
            if (string.IsNullOrWhiteSpace(game.Name))
                game.Name = game.PlaceId > 0 ? $"Place {game.PlaceId}" : "Unknown game";
        }
        return games;
    }


    private void PublishGamesCore(List<LibraryGameEntry> games)
    {
        _masterGames.Clear();
        _masterGames.AddRange(games.OrderBy(static game => game.Name, StringComparer.OrdinalIgnoreCase));

        ReplaceIfChanged(RecentGames, games
            .Where(static game => game.LastPlayed != null)
            .OrderByDescending(static game => game.LastPlayed)
            .Take(12));

        DateTime cutoff = DateTime.Now.AddDays(-30);
        ReplaceIfChanged(WhatsNew, games
            .Where(game => game.Updated != null && game.Updated.Value.ToLocalTime() >= cutoff)
            .OrderByDescending(static game => game.Updated)
            .Take(12));

        ReplaceIfChanged(AllGames, _masterGames.Take(Math.Max(GamePageSize, AllGames.Count)));

        RebuildSidebar(false);

        OnPropertyChanged(nameof(AllGamesHeader));
        OnPropertyChanged(nameof(SidebarCountText));
        OnPropertyChanged(nameof(TotalPlayTimeDisplay));
        OnPropertyChanged(nameof(WhatsNewVisibility));
        OnPropertyChanged(nameof(RecentVisibility));
        OnPropertyChanged(nameof(EmptyLibraryVisibility));

        if (_selectedGame != null)
        {
            LibraryGameEntry? match = _masterGames.FirstOrDefault(game => game.UniverseId == _selectedGame.UniverseId);
            SelectedGame = match;
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> source)
    {
        if (collection is LibraryCollection<T> optimized)
        {
            optimized.ReplaceWith(source);
            return;
        }

        collection.Clear();
        foreach (T item in source)
            collection.Add(item);
    }

    private static void ReplaceIfChanged<T>(ObservableCollection<T> collection, IEnumerable<T> source) where T : class
    {
        List<T> items = source.ToList();
        if (items.Count == collection.Count)
        {
            bool unchanged = true;
            for (int index = 0; index < items.Count && unchanged; index++)
                unchanged = ReferenceEquals(items[index], collection[index]);
            if (unchanged)
                return;
        }
        ReplaceCollection(collection, items);
    }

    public void LoadMoreGames()
    {
        if (_loadingMoreGames || AllGames.Count >= _masterGames.Count)
            return;

        _loadingMoreGames = true;
        try
        {
            int end = Math.Min(AllGames.Count + GamePageSize, _masterGames.Count);
            for (int index = AllGames.Count; index < end; index++)
                AllGames.Add(_masterGames[index]);
        }
        finally
        {
            _loadingMoreGames = false;
        }
    }

    private static void ApplyPinSnapshot(LibraryGameEntry entry, AppSettings.LibraryPin pin)
    {
        if (!string.IsNullOrWhiteSpace(pin.Name))
            entry.Name = pin.Name;
        if (!string.IsNullOrWhiteSpace(pin.CreatorName))
            entry.CreatorName = pin.CreatorName;
        if (!string.IsNullOrWhiteSpace(pin.Description))
            entry.Description = pin.Description;
        if (!string.IsNullOrWhiteSpace(pin.IconUrl))
            entry.IconUrl = pin.IconUrl;
        if (!string.IsNullOrWhiteSpace(pin.ThumbnailUrl))
            entry.ThumbnailUrl = pin.ThumbnailUrl;
        if (pin.Visits > 0)
            entry.Visits = pin.Visits;
        if (pin.MaxPlayers > 0)
            entry.MaxPlayers = pin.MaxPlayers;
        if (!string.IsNullOrWhiteSpace(pin.Genre))
            entry.Genre = pin.Genre;
        entry.Created ??= pin.Created;
        entry.Updated ??= pin.Updated;
    }

    private static void PersistPinSnapshots(List<LibraryGameEntry> games, List<AppSettings.LibraryPin> pins)
    {
        bool changed = false;
        foreach (AppSettings.LibraryPin pin in pins)
        {
            LibraryGameEntry? game = games.FirstOrDefault(candidate => candidate.UniverseId == pin.UniverseId);
            if (game == null)
                continue;
            changed |= UpdatePinSnapshot(pin, game);
        }
        if (changed)
            Integrations.LibraryStore.Save();
    }

    private static bool UpdatePinSnapshot(AppSettings.LibraryPin pin, LibraryGameEntry game)
    {
        string iconUrl = game.IconUrl ?? "";
        string thumbnailUrl = game.ThumbnailUrl ?? "";
        bool changed = pin.PlaceId != game.PlaceId ||
            pin.Name != game.Name ||
            pin.CreatorName != game.CreatorName ||
            pin.Description != game.Description ||
            pin.IconUrl != iconUrl ||
            pin.ThumbnailUrl != thumbnailUrl ||
            pin.Visits != game.Visits ||
            pin.MaxPlayers != game.MaxPlayers ||
            pin.Genre != game.Genre ||
            pin.Created != game.Created ||
            pin.Updated != game.Updated;
        pin.PlaceId = game.PlaceId;
        pin.Name = game.Name;
        pin.CreatorName = game.CreatorName;
        pin.Description = game.Description;
        pin.IconUrl = iconUrl;
        pin.ThumbnailUrl = thumbnailUrl;
        pin.Visits = game.Visits;
        pin.MaxPlayers = game.MaxPlayers;
        pin.Genre = game.Genre;
        pin.Created = game.Created;
        pin.Updated = game.Updated;
        return changed;
    }

    private List<Models.Entities.ActivityData> ReadHistoryFile()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
                return new List<Models.Entities.ActivityData>();
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};
			return JsonFile.Deserialize<List<Models.Entities.ActivityData>>(_historyFilePath, options, MaximumHistoryBytes)
				.Where(static entry => entry is not null && entry.PlaceId > 0)
				.OrderByDescending(static entry => entry.TimeJoined)
				.Take(MaximumHistoryEntries)
				.ToList();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "History read failed: " + ex.Message);
            return new List<Models.Entities.ActivityData>();
        }
    }

    private static IEnumerable<string> Chunk(List<long> ids, int size)
    {
        for (int i = 0; i < ids.Count; i += size)
            yield return string.Join(',', ids.Skip(i).Take(size));
    }

    private static async Task FetchLandscapeThumbnailsAsync(List<LibraryGameEntry> games, CancellationToken token = default)
    {
        List<long> ids = games.Select(g => g.UniverseId).ToList();
        Dictionary<long, LibraryGameEntry> byUniverse = new();
        foreach (LibraryGameEntry entry in games)
            byUniverse[entry.UniverseId] = entry;

        await Task.WhenAll(Chunk(ids, 50).Select(async chunk =>
        {
            try
            {
                string url = $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={chunk}&countPerUniverse=1&defaults=true&size=768x432&format=Jpeg";
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url, token: token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    return;
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("universeId", out JsonElement idProp))
                        continue;
                    long universeId = idProp.GetInt64();
                    if (!item.TryGetProperty("thumbnails", out JsonElement thumbs) || thumbs.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement thumb in thumbs.EnumerateArray())
                    {
                        if (thumb.TryGetProperty("imageUrl", out JsonElement urlProp) && urlProp.ValueKind == JsonValueKind.String)
                        {
                            string? imageUrl = urlProp.GetString();
                            if (byUniverse.TryGetValue(universeId, out LibraryGameEntry? game) && !string.IsNullOrEmpty(imageUrl))
                                game.ThumbnailUrl = imageUrl;
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Thumbnail fetch failed: " + ex.Message);
            }
        }));
    }

    private void OpenEvent(LibraryEventEntry? entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Id))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(entry.EventUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "Could not open event: " + ex.Message);
        }
    }

    private async Task FetchEventsAsync(List<LibraryGameEntry> games, CancellationToken token = default)
    {
        System.Collections.Concurrent.ConcurrentBag<LibraryEventEntry> found = new();
        using SemaphoreSlim gate = new SemaphoreSlim(6, 6);

        await Task.WhenAll(games.Select(async game =>
        {
            if (game.UniverseId <= 0)
                return;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string url = "https://apis.roblox.com/virtual-events/v1/universes/" + game.UniverseId + "/virtual-events";
                string? body = await Voidstrap.Utility.GitHubCache.GetStringAsync(url, TimeSpan.FromMinutes(20), token: token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(body))
                    return;
                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    return;

                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    if (ReadEventText(item, "eventStatus") != "active")
                        continue;
                    if (ReadEventText(item, "eventVisibility") != "public")
                        continue;

                    DateTime? start = null;
                    DateTime? end = null;
                    if (item.TryGetProperty("eventTime", out JsonElement time) && time.ValueKind == JsonValueKind.Object)
                    {
                        start = ReadEventDate(time, "startUtc");
                        end = ReadEventDate(time, "endUtc");
                    }

                    if (end.HasValue && end.Value < DateTime.UtcNow)
                        continue;

                    long mediaId = 0;
                    if (item.TryGetProperty("thumbnails", out JsonElement thumbs) && thumbs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement thumb in thumbs.EnumerateArray())
                        {
                            if (thumb.ValueKind == JsonValueKind.Object
                                && thumb.TryGetProperty("mediaId", out JsonElement mid)
                                && mid.ValueKind == JsonValueKind.Number
                                && mid.TryGetInt64(out long mv))
                            {
                                mediaId = mv;
                                break;
                            }
                        }
                    }

                    string title = ReadEventText(item, "displayTitle");
                    if (string.IsNullOrWhiteSpace(title))
                        title = ReadEventText(item, "title");
                    string subtitle = ReadEventText(item, "displaySubtitle");
                    if (string.IsNullOrWhiteSpace(subtitle))
                        subtitle = ReadEventText(item, "subtitle");

                    found.Add(new LibraryEventEntry
                    {
                        Id = ReadEventText(item, "id"),
                        UniverseId = game.UniverseId,
                        Title = title,
                        Subtitle = subtitle,
                        GameName = game.Name ?? string.Empty,
                        StartUtc = start,
                        EndUtc = end,
                        MediaId = mediaId,
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Events fetch failed for " + game.UniverseId + ": " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        List<LibraryEventEntry> ordered = found
            .OrderBy(e => e.StartUtc ?? DateTime.MaxValue)
            .Take(24)
            .ToList();

        await ResolveEventThumbnailsAsync(ordered, token).ConfigureAwait(false);

        Action publish = () =>
        {
            if (token.IsCancellationRequested)
                return;
            ReplaceCollection(Events, ordered);
            OnPropertyChanged(nameof(EventsVisibility));
        };
        await Application.Current.Dispatcher.InvokeAsync(publish);
    }

    private static async Task ResolveEventThumbnailsAsync(List<LibraryEventEntry> events, CancellationToken token = default)
    {
        List<long> mediaIds = events.Where(e => e.MediaId > 0).Select(e => e.MediaId).Distinct().ToList();
        if (mediaIds.Count == 0)
            return;

        Dictionary<long, string> urls = new();
        await Task.WhenAll(Chunk(mediaIds, 50).Select(async chunk =>
        {
            try
            {
                string url = "https://thumbnails.roblox.com/v1/assets?assetIds=" + chunk + "&size=768x432&format=Jpeg&isCircular=false";
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url, token: token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    return;
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!item.TryGetProperty("targetId", out JsonElement idProp) || !idProp.TryGetInt64(out long targetId))
                        continue;
                    if (item.TryGetProperty("imageUrl", out JsonElement urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    {
                        string? image = urlProp.GetString();
                        if (!string.IsNullOrEmpty(image))
                        {
                            lock (urls)
                                urls[targetId] = image;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Event thumbnail fetch failed: " + ex.Message);
            }
        }));

        foreach (LibraryEventEntry entry in events)
        {
            if (entry.MediaId > 0 && urls.TryGetValue(entry.MediaId, out string? image))
                entry.ThumbnailUrl = image;
        }
    }

    private static string ReadEventText(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static DateTime? ReadEventDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;
        return DateTime.TryParse(
            value.GetString(),
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
            ? parsed
            : null;
    }

    private static async Task FetchVotesAsync(List<LibraryGameEntry> games, CancellationToken token = default)
    {
        List<long> ids = games.Select(g => g.UniverseId).ToList();
        Dictionary<long, LibraryGameEntry> byUniverse = new();
        foreach (LibraryGameEntry entry in games)
            byUniverse[entry.UniverseId] = entry;

        await Task.WhenAll(Chunk(ids, 50).Select(async chunk =>
        {
            try
            {
                string url = $"https://games.roblox.com/v1/games/votes?universeIds={chunk}";
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url, token: token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    return;
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out JsonElement idProp))
                        continue;
                    long universeId = idProp.GetInt64();
                    long up = item.TryGetProperty("upVotes", out JsonElement upProp) ? upProp.GetInt64() : 0;
                    long down = item.TryGetProperty("downVotes", out JsonElement downProp) ? downProp.GetInt64() : 0;
                    if (byUniverse.TryGetValue(universeId, out LibraryGameEntry? game) && up + down > 0)
                        game.LikePercent = $"{Math.Round(up * 100.0 / (up + down))}%";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Votes fetch failed: " + ex.Message);
            }
        }));
    }

    private void RebuildSidebar(bool force = true)
    {
        string query = _sidebarSearch.Trim();
        IEnumerable<LibraryGameEntry> filtered = _masterGames;
        if (query.Length > 0)
            filtered = filtered.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        IEnumerable<LibraryGameEntry> ordered = filtered.OrderByDescending(static game => game.IsPinned).ThenBy(static game => game.Name, StringComparer.OrdinalIgnoreCase);
        if (force)
            ReplaceCollection(SidebarGames, ordered);
        else
            ReplaceIfChanged(SidebarGames, ordered);
    }

    private void SelectGame(LibraryGameEntry? game)
    {
        if (game == null)
            return;
        SelectedGame = game;
        if (!string.IsNullOrEmpty(game.ThumbnailUrl))
            Voidstrap.Utility.DynamicRenderSystem.Prefetch(game.ThumbnailUrl, 1024);
    }

    private void GoHome()
    {
        SelectedGame = null;
    }

    private void Refresh()
    {
        _ = LoadAsync(true);
    }

    private void LaunchGame(LibraryGameEntry? game)
    {
        if (game == null || game.PlaceId == 0)
            return;
        try
        {
            string uri = $"roblox://experiences/start?placeId={game.PlaceId}";
            string voidstrapPath = Paths.LaunchExecutable;
            Process.Start(new ProcessStartInfo
            {
                FileName = voidstrapPath,
                Arguments = $"-player \"{uri}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(voidstrapPath) ?? ""
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("LibraryViewModel::LaunchGame", ex);
        }
    }

    private void TogglePin(LibraryGameEntry? game)
    {
        if (game == null)
            return;
        List<AppSettings.LibraryPin> pins = Integrations.LibraryStore.Pins;
        AppSettings.LibraryPin? existing = pins.FirstOrDefault(p => p.UniverseId == game.UniverseId);
        if (existing != null)
        {
            pins.Remove(existing);
            game.IsPinned = false;
        }
        else
        {
            pins.Add(new AppSettings.LibraryPin
            {
                PlaceId = game.PlaceId,
                UniverseId = game.UniverseId,
                Name = game.Name ?? ""
            });
            game.IsPinned = true;
        }
        Integrations.LibraryStore.Save();
        RebuildSidebar();
    }

    private async Task RemoveGameAsync(LibraryGameEntry? game)
    {
        if (game == null)
            return;
        MessageBoxResult result = UI.Frontend.ShowMessageBox($"Remove {game.Name} from your library? Its recorded playtime will be deleted.", MessageBoxImage.Question, MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes)
            return;
        List<AppSettings.LibraryPin> pins = Integrations.LibraryStore.Pins;
        pins.RemoveAll(p => p.UniverseId == game.UniverseId);
        Integrations.LibraryStore.Save();
        long universeId = game.UniverseId;
        await Task.Run(() =>
        {
            List<string> sessionKeys = RemoveFromHistoryFile(universeId);
            Integrations.PlayTimeStore.RemoveUniverse(universeId, sessionKeys);
        });
        if (_selectedGame?.UniverseId == universeId)
            SelectedGame = null;
        await LoadAsync();
    }

    private List<string> RemoveFromHistoryFile(long universeId)
    {
        List<string> removedKeys = new();
        try
        {
            List<Models.Entities.ActivityData> history = ReadHistoryFile();
            List<Models.Entities.ActivityData> kept = new();
            foreach (Models.Entities.ActivityData session in history)
            {
				if (session.UniverseId == universeId)
					removedKeys.Add($"{session.PlaceId}_{session.JobId}");
                else
                    kept.Add(session);
            }
			if (removedKeys.Count > 0)
				JsonFile.SerializeAtomic(_historyFilePath, kept, JsonOptions.Indented);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "History remove failed: " + ex.Message);
        }
        return removedKeys;
    }

    private async Task AddGameFromTextAsync()
    {
        string text = AddGameText.Trim();
        if (text.Length == 0)
            return;
        long placeId = ParsePlaceId(text);
        if (placeId <= 0)
        {
            StatusText = "Enter a valid PlaceId or Roblox game link.";
            return;
        }
        StatusText = "";
        try
        {
            string url = $"https://apis.roblox.com/universes/v1/places/{placeId}/universe";
            using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url));
            if (!doc.RootElement.TryGetProperty("universeId", out JsonElement idProp) || idProp.ValueKind != JsonValueKind.Number)
            {
                StatusText = "Could not find a game with that PlaceId.";
                return;
            }
            long universeId = idProp.GetInt64();
            List<AppSettings.LibraryPin> pins = Integrations.LibraryStore.Pins;
            AppSettings.LibraryPin? pin = pins.FirstOrDefault(p => p.UniverseId == universeId);
            if (pin == null)
            {
                pin = new AppSettings.LibraryPin
                {
                    PlaceId = placeId,
                    UniverseId = universeId,
                    Name = $"Place {placeId}"
                };
                pins.Add(pin);
            }
            else
            {
                pin.PlaceId = placeId;
                if (string.IsNullOrWhiteSpace(pin.Name))
                    pin.Name = $"Place {placeId}";
            }
            Integrations.LibraryStore.Save();
            AddGameText = "";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "Add game failed: " + ex.Message);
            StatusText = "Could not find a game with that PlaceId.";
        }
    }

    private static long ParsePlaceId(string text)
    {
        if (long.TryParse(text, out long direct))
            return direct;
        int index = text.IndexOf("/games/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            string rest = text.Substring(index + 7);
            string digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            if (long.TryParse(digits, out long fromUrl))
                return fromUrl;
        }
        return 0;
    }

    private async Task LoadGamePassesAsync(long universeId, long version, CancellationTokenSource cancellation)
    {
		CancellationToken ct = cancellation.Token;
        try
        {
			if (_gamePassCache.TryGetValue(universeId, out List<GamePassEntry>? cached))
			{
				ApplyGamePasses(universeId, version, cached);
				return;
			}
			ct.ThrowIfCancellationRequested();
            List<GamePassEntry> passes = new();
            List<long> forSaleIds = new();
            string url = $"https://apis.roblox.com/game-passes/v1/universes/{universeId}/game-passes?limit=100&sortOrder=1";
			using (JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url, token: ct)))
            {
                if (doc.RootElement.TryGetProperty("gamePasses", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in data.EnumerateArray())
                    {
                        long id = item.TryGetProperty("id", out JsonElement idProp) ? idProp.GetInt64() : 0;
                        if (id == 0)
                            continue;
                        string name = item.TryGetProperty("displayName", out JsonElement dispProp) && dispProp.ValueKind == JsonValueKind.String ? dispProp.GetString() ?? "" : "";
                        if (name.Length == 0 && item.TryGetProperty("name", out JsonElement nameProp) && nameProp.ValueKind == JsonValueKind.String)
                            name = nameProp.GetString() ?? "";
                        bool forSale = item.TryGetProperty("isForSale", out JsonElement saleProp) && saleProp.ValueKind == JsonValueKind.True;
                        if (forSale)
                            forSaleIds.Add(id);
                        passes.Add(new GamePassEntry
                        {
                            Id = id,
                            Name = name,
                            PriceDisplay = forSale ? "For sale" : "Off sale"
                        });
                    }
                }
            }
            foreach (GamePassEntry[] batch in passes.Where(p => forSaleIds.Contains(p.Id)).Take(40).Chunk(8))
            {
				ct.ThrowIfCancellationRequested();
                await Task.WhenAll(batch.Select(async pass =>
                {
                    try
                    {
						using JsonDocument info = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, $"https://apis.roblox.com/game-passes/v1/game-passes/{pass.Id}/product-info", token: ct));
                        if (info.RootElement.TryGetProperty("PriceInRobux", out JsonElement priceProp) && priceProp.ValueKind == JsonValueKind.Number)
                            pass.PriceDisplay = $"R$ {priceProp.GetInt32():N0}";
                    }
					catch (OperationCanceledException) when (ct.IsCancellationRequested)
					{
						throw;
					}
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine("LibraryViewModel", "Gamepass price failed: " + ex.Message);
                    }
                }));
            }
            if (passes.Count > 0)
            {
                try
                {
                    string iconUrl = $"https://thumbnails.roblox.com/v1/game-passes?gamePassIds={string.Join(',', passes.Select(p => p.Id).Take(100))}&size=150x150&format=Png";
					using JsonDocument iconDoc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, iconUrl, token: ct));
                    if (iconDoc.RootElement.TryGetProperty("data", out JsonElement iconData) && iconData.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in iconData.EnumerateArray())
                        {
                            long targetId = item.TryGetProperty("targetId", out JsonElement idProp) ? idProp.GetInt64() : 0;
                            string? image = item.TryGetProperty("imageUrl", out JsonElement urlProp) && urlProp.ValueKind == JsonValueKind.String ? urlProp.GetString() : null;
                            GamePassEntry? pass = passes.FirstOrDefault(p => p.Id == targetId);
                            if (pass != null)
                                pass.IconUrl = image;
                        }
                    }
                }
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
                catch (Exception ex)
                {
                    App.Logger.WriteLine("LibraryViewModel", "Gamepass icons failed: " + ex.Message);
                }
            }
			ct.ThrowIfCancellationRequested();
            _gamePassCache[universeId] = passes;
			ApplyGamePasses(universeId, version, passes);
        }
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "Gamepass fetch failed: " + ex.Message);
			if (IsCurrentGamePassLoad(universeId, version))
                GamePassStatus = "Could not load this game's store right now.";
        }
        finally
        {
			if (ReferenceEquals(_gamePassLoadCts, cancellation))
			{
				_gamePassLoadCts = null;
				GamePassesLoading = false;
			}
			cancellation.Dispose();
        }
    }

	private bool IsCurrentGamePassLoad(long universeId, long version)
	{
		return _selectedGame?.UniverseId == universeId && Interlocked.Read(ref _gamePassLoadVersion) == version;
	}

    private void ApplyGamePasses(long universeId, long version, List<GamePassEntry> passes)
    {
		if (!IsCurrentGamePassLoad(universeId, version))
            return;
        GamePasses.Clear();
        foreach (GamePassEntry pass in passes)
            GamePasses.Add(pass);
        GamePassStatus = passes.Count == 0 ? "This game does not sell any gamepasses." : "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
