using System;
using System.Collections.Generic;
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

public class LibraryViewModel : INotifyPropertyChanged
{
	private const int MaximumHistoryBytes = 16 * 1024 * 1024;

	private const int MaximumHistoryEntries = 100;

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

    private CancellationTokenSource? _gamePassLoadCts;

    private long _gamePassLoadVersion;

    public bool HasLoaded { get; private set; }

    public ObservableCollection<LibraryGameEntry> SidebarGames { get; } = new();

    public ObservableCollection<LibraryGameEntry> RecentGames { get; } = new();

    public ObservableCollection<LibraryGameEntry> WhatsNew { get; } = new();

    public ObservableCollection<LibraryEventEntry> Events { get; } = new();

    public ObservableCollection<LibraryGameEntry> AllGames { get; } = new();

    public ObservableCollection<GamePassEntry> GamePasses { get; } = new();

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

    public string AllGamesHeader => $"All Games ({AllGames.Count})";

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

    public async Task LoadAsync()
    {
        if (_loadInFlight)
        {
            _reloadRequested = true;
            return;
        }
        _loadInFlight = true;
        IsLoading = true;
        StatusText = "";
        try
        {
            try
            {
                await Voidstrap.Utility.WebsiteHistorySync.FetchAndApplyAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("LibraryViewModel::HistoryFetch", ex);
            }
            List<Models.Entities.ActivityData> rawHistory = await Task.Run(ReadHistoryFile);
            List<Models.Entities.ActivityData> unresolvedSessions = rawHistory
                .Where(s => s != null && s.UniverseId == 0 && s.PlaceId != 0)
                .ToList();
            if (unresolvedSessions.Count > 0)
            {
                await UniverseDetails.ResolvePlacesToUniversesAsync(unresolvedSessions.Select(s => s.PlaceId));
                foreach (Models.Entities.ActivityData session in unresolvedSessions)
                {
                    if (UniverseDetails.TryGetUniverseForPlace(session.PlaceId, out long resolvedUniverse))
                        session.UniverseId = resolvedUniverse;
                }
            }
            List<AppSettings.LibraryPin> pins = App.Settings.Prop.LibraryPins ?? new List<AppSettings.LibraryPin>();

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

            Integrations.PlayTimeStore.StoreData playStore = await Task.Run(Integrations.PlayTimeStore.Read);
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
            PublishGames(games);
            List<long> ids = games.Select(g => g.UniverseId).ToList();

            await Task.WhenAll(Chunk(ids, 50).Select(async chunk =>
            {
                try
                {
                    await UniverseDetails.FetchBulk(chunk);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("LibraryViewModel", "Details fetch failed: " + ex.Message);
                }
            }));

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
                game.IconUrl = details.Thumbnail?.ImageUrl;
                if (game.PlaceId == 0)
                    game.PlaceId = details.Data.RootPlaceId;
            }

            await Task.WhenAll(FetchLandscapeThumbnailsAsync(games), FetchVotesAsync(games), FetchEventsAsync(games));

            PrefetchUrls(games);
            PersistPinSnapshots(games, pins);
            PublishGames(games);
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("LibraryViewModel::LoadAsync", ex);
            StatusText = "Failed to load your library: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            _loadInFlight = false;
            if (_reloadRequested)
            {
                _reloadRequested = false;
                _ = LoadAsync();
            }
        }
    }

    private void PublishGames(List<LibraryGameEntry> games)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _masterGames.Clear();
            _masterGames.AddRange(games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));

            RecentGames.Clear();
            foreach (LibraryGameEntry game in games.Where(g => g.LastPlayed != null).OrderByDescending(g => g.LastPlayed).Take(12))
                RecentGames.Add(game);

            WhatsNew.Clear();
            DateTime cutoff = DateTime.Now.AddDays(-30);
            foreach (LibraryGameEntry game in games.Where(g => g.Updated != null && g.Updated.Value.ToLocalTime() >= cutoff).OrderByDescending(g => g.Updated).Take(12))
                WhatsNew.Add(game);

            AllGames.Clear();
            foreach (LibraryGameEntry game in _masterGames)
                AllGames.Add(game);

            RebuildSidebar();
            foreach (LibraryGameEntry game in games)
                game.RefreshAll();

            OnPropertyChanged(nameof(AllGamesHeader));
            OnPropertyChanged(nameof(SidebarCountText));
            OnPropertyChanged(nameof(TotalPlayTimeDisplay));
            OnPropertyChanged(nameof(WhatsNewVisibility));
            OnPropertyChanged(nameof(RecentVisibility));
            OnPropertyChanged(nameof(EmptyLibraryVisibility));

            if (_selectedGame != null)
            {
                LibraryGameEntry? match = _masterGames.FirstOrDefault(g => g.UniverseId == _selectedGame.UniverseId);
                SelectedGame = match;
            }
        });
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
            App.Settings.SaveDeferred();
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

    private static void PrefetchUrls(List<LibraryGameEntry> games)
    {
        try
        {
            List<string> icons = new List<string>();
            List<string> thumbs = new List<string>();
            foreach (LibraryGameEntry game in games)
            {
                if (!string.IsNullOrEmpty(game.IconUrl))
                    icons.Add(game.IconUrl);
                if (!string.IsNullOrEmpty(game.ThumbnailUrl))
                    thumbs.Add(game.ThumbnailUrl);
            }
            Voidstrap.Utility.DynamicRenderSystem.Prefetch(icons, 256);
            Voidstrap.Utility.DynamicRenderSystem.Prefetch(thumbs, 512);
        }
        catch
        {
        }
    }

    private async Task FetchLandscapeThumbnailsAsync(List<LibraryGameEntry> games)
    {
        List<long> ids = games.Select(g => g.UniverseId).ToList();
        Dictionary<long, LibraryGameEntry> byUniverse = new();
        foreach (LibraryGameEntry entry in games)
            byUniverse[entry.UniverseId] = entry;

        await Task.WhenAll(Chunk(ids, 50).Select(async chunk =>
        {
            try
            {
                string url = $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={chunk}&countPerUniverse=1&defaults=true&size=768x432&format=Png";
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url));
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
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Thumbnail fetch failed: " + ex.Message);
            }
        }));
    }

    private void OpenEvent(LibraryEventEntry entry)
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

    private async Task FetchEventsAsync(List<LibraryGameEntry> games)
    {
        System.Collections.Concurrent.ConcurrentBag<LibraryEventEntry> found = new();

        await Task.WhenAll(games.Select(async game =>
        {
            if (game.UniverseId <= 0)
                return;
            try
            {
                string url = "https://apis.roblox.com/virtual-events/v1/universes/" + game.UniverseId + "/virtual-events";
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url));
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
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Events fetch failed for " + game.UniverseId + ": " + ex.Message);
            }
        }));

        List<LibraryEventEntry> ordered = found
            .OrderBy(e => e.StartUtc ?? DateTime.MaxValue)
            .Take(24)
            .ToList();

        await ResolveEventThumbnailsAsync(ordered);

        Application.Current.Dispatcher.Invoke(() =>
        {
            Events.Clear();
            foreach (LibraryEventEntry entry in ordered)
                Events.Add(entry);
            OnPropertyChanged(nameof(EventsVisibility));
        });
    }

    private async Task ResolveEventThumbnailsAsync(List<LibraryEventEntry> events)
    {
        List<long> mediaIds = events.Where(e => e.MediaId > 0).Select(e => e.MediaId).Distinct().ToList();
        if (mediaIds.Count == 0)
            return;

        Dictionary<long, string> urls = new();
        await Task.WhenAll(Chunk(mediaIds, 50).Select(async chunk =>
        {
            try
            {
                string url = "https://thumbnails.roblox.com/v1/assets?assetIds=" + chunk + "&size=768x432&format=Png&isCircular=false";
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url));
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

    private async Task FetchVotesAsync(List<LibraryGameEntry> games)
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
                using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url));
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
            catch (Exception ex)
            {
                App.Logger.WriteLine("LibraryViewModel", "Votes fetch failed: " + ex.Message);
            }
        }));
    }

    private void RebuildSidebar()
    {
        string query = _sidebarSearch.Trim();
        SidebarGames.Clear();
        IEnumerable<LibraryGameEntry> filtered = _masterGames;
        if (query.Length > 0)
            filtered = filtered.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        foreach (LibraryGameEntry game in filtered.OrderByDescending(g => g.IsPinned).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            SidebarGames.Add(game);
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
        _ = LoadAsync();
    }

    private void LaunchGame(LibraryGameEntry? game)
    {
        if (game == null || game.PlaceId == 0)
            return;
        try
        {
            string uri = $"roblox://experiences/start?placeId={game.PlaceId}";
            string voidstrapPath = Paths.Process;
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
        List<AppSettings.LibraryPin> pins = App.Settings.Prop.LibraryPins ??= new List<AppSettings.LibraryPin>();
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
        App.Settings.SaveDeferred();
        RebuildSidebar();
    }

    private async Task RemoveGameAsync(LibraryGameEntry? game)
    {
        if (game == null)
            return;
        MessageBoxResult result = UI.Frontend.ShowMessageBox($"Remove {game.Name} from your library? Its recorded playtime will be deleted.", MessageBoxImage.Question, MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes)
            return;
        List<AppSettings.LibraryPin> pins = App.Settings.Prop.LibraryPins ??= new List<AppSettings.LibraryPin>();
        pins.RemoveAll(p => p.UniverseId == game.UniverseId);
        App.Settings.SaveDeferred();
        long universeId = game.UniverseId;
        await Task.Run(() =>
        {
            List<string> sessionKeys = RemoveFromHistoryFile(universeId);
            Integrations.PlayTimeStore.RemoveUniverse(universeId, sessionKeys);
        });
        await RemoveFromWebsiteAsync(universeId);
        if (_selectedGame?.UniverseId == universeId)
            SelectedGame = null;
        await LoadAsync();
    }

    private static async Task RemoveFromWebsiteAsync(long universeId)
    {
        if (!Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        try
        {
            string url = App.WebsiteBaseUrl + "/api/me/apphistory?universeId=" + universeId;
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, url);
            string? token = Voidstrap.Utility.WebsiteAuth.GetToken();
            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                App.Logger.WriteLine("LibraryViewModel", "Website history remove returned " + (int)response.StatusCode + " for " + universeId + ".");
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LibraryViewModel", "Website history remove failed: " + ex.Message);
        }
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
            List<AppSettings.LibraryPin> pins = App.Settings.Prop.LibraryPins ??= new List<AppSettings.LibraryPin>();
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
            App.Settings.Save();
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
			using (JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, url, ct)))
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
						using JsonDocument info = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, $"https://apis.roblox.com/game-passes/v1/game-passes/{pass.Id}/product-info", ct));
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
					using JsonDocument iconDoc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, iconUrl, ct));
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
