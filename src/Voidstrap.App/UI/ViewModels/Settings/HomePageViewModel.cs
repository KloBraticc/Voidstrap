using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Voidstrap.Integrations;
using Voidstrap.Models.APIs;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Pages
{
    internal class DatacenterOption
    {
        public string Key { get; init; } = "";
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }


    internal class HistoryGameEntry : NotifyPropertyChangedViewModel
    {
        private readonly ObservableCollection<DatacenterOption> _datacenterOptions;
        private DatacenterOption? _selectedDatacenter;
        private string _likePercent = "--";
        private string _playerCount = "--";

        public HistoryGameEntry(ActivityData data, ObservableCollection<DatacenterOption> datacenterOptions)
        {
            Data = data;
            _datacenterOptions = datacenterOptions;
            _selectedDatacenter = FindSavedDatacenter();
        }

        private DatacenterOption? FindSavedDatacenter()
        {
            string savedKey = "";
            try
            {
                var perGame = App.Settings.Prop.PerGamePreferredDatacenters;
                if (perGame != null && perGame.TryGetValue(Data.PlaceId, out var key))
                    savedKey = key ?? "";
            }
            catch { }

            return _datacenterOptions.FirstOrDefault(o =>
                string.Equals(o.Key, savedKey, StringComparison.OrdinalIgnoreCase))
                ?? _datacenterOptions.FirstOrDefault();
        }

        public void ResolveDatacenter()
        {
            DatacenterOption? resolved = FindSavedDatacenter();
            if (ReferenceEquals(_selectedDatacenter, resolved))
                return;
            _selectedDatacenter = resolved;
            OnPropertyChanged(nameof(SelectedDatacenter));
        }

        public ActivityData Data { get; }

        public long PlaceId => Data.PlaceId;

        public long UniverseId => Data.UniverseId;

        public DateTime TimeJoined => Data.TimeJoined;

        public string Name => Data.UniverseDetails?.Data?.Name ?? (Data.PlaceId != 0 ? $"Place {Data.PlaceId}" : "Unknown");

        public string CreatorName
        {
            get
            {
                string? creator = Data.UniverseDetails?.Data?.Creator?.Name;
                return string.IsNullOrEmpty(creator) ? "" : $"by {creator}";
            }
        }

        public string? ThumbnailUrl => Data.UniverseDetails?.Thumbnail?.ImageUrl;

        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

        public string LikePercent
        {
            get => _likePercent;
            set { _likePercent = value; OnPropertyChanged(nameof(LikePercent)); }
        }

        public string PlayerCount
        {
            get => _playerCount;
            set { _playerCount = value; OnPropertyChanged(nameof(PlayerCount)); }
        }

        public ObservableCollection<DatacenterOption> DatacenterOptions => _datacenterOptions;

        public bool ExcludedFromMatchmaker => Voidstrap.Integrations.ServerMatchmaker.IsExcluded(PlaceId);

        public bool MatchmakerEnabledForGame => !ExcludedFromMatchmaker;

        public string SkipMatchmakerLabel => ExcludedFromMatchmaker ? "Matchmaker skipped" : "Skip matchmaker";

        public Wpf.Ui.Common.SymbolRegular SkipMatchmakerIcon => ExcludedFromMatchmaker
            ? Wpf.Ui.Common.SymbolRegular.CheckmarkCircle24
            : Wpf.Ui.Common.SymbolRegular.ArrowRouting24;

        public string SkipMatchmakerTooltip => ExcludedFromMatchmaker
            ? "Voidstrap joins this game normally. Click to let it pick servers again."
            : "For games that put you in their own server when you pick a map or a village. Click so Voidstrap joins normally and never moves you.";

        public ICommand ToggleSkipMatchmakerCommand => new RelayCommand(ToggleSkipMatchmaker);

        private void ToggleSkipMatchmaker()
        {
            Voidstrap.Integrations.ServerMatchmaker.SetExcluded(PlaceId, !ExcludedFromMatchmaker);
            OnPropertyChanged(nameof(ExcludedFromMatchmaker));
            OnPropertyChanged(nameof(MatchmakerEnabledForGame));
            OnPropertyChanged(nameof(SkipMatchmakerLabel));
            OnPropertyChanged(nameof(SkipMatchmakerIcon));
            OnPropertyChanged(nameof(SkipMatchmakerTooltip));
        }

        public DatacenterOption? SelectedDatacenter
        {
            get => _selectedDatacenter;
            set
            {
                if (value == null || _selectedDatacenter == value) return;
                _selectedDatacenter = value;
                OnPropertyChanged(nameof(SelectedDatacenter));

                try
                {
                    var perGame = App.Settings.Prop.PerGamePreferredDatacenters ??= new Dictionary<long, string>();
                    string key = value?.Key ?? "";

                    if (string.IsNullOrEmpty(key))
                        perGame.Remove(PlaceId);
                    else
                        perGame[PlaceId] = key;

                    App.Settings.SaveDeferred();
                }
                catch { }
            }
        }

        public void RefreshDetails()
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(CreatorName));
            OnPropertyChanged(nameof(ThumbnailUrl));
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    internal class NewsCardEntry
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
        public DateTime Created { get; init; }

        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

        public Visibility TagVisibility => string.IsNullOrEmpty(Tag) ? Visibility.Collapsed : Visibility.Visible;

        public string CardTitle
        {
            get
            {
                string value = Title.Trim();
                if (value.Length <= 44)
                    return value;
                int cut = value.LastIndexOf(' ', 43);
                if (cut < 24)
                    cut = 43;
                return value[..cut].TrimEnd(' ', ',', ':', ';');
            }
        }

        public string DateLabel
        {
            get
            {
                if (Created == DateTime.MinValue)
                    return "roblox announcement";
                DateTime local = Created.ToLocalTime();
                double days = Math.Floor((DateTime.Now.Date - local.Date).TotalDays);
                if (days <= 0)
                    return "today";
                if (days == 1)
                    return "yesterday";
                if (days < 7)
                    return $"{days:0} days ago";
                return local.ToString("MMMM d, yyyy").ToLowerInvariant();
            }
        }
    }

    internal class CatalogItemEntry
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Creator { get; init; } = string.Empty;
        public string TypeName { get; init; } = string.Empty;
        public bool Limited { get; init; }
        public string? ImageUrl { get; init; }
        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

        public int? Price { get; init; }

        public string PriceDisplay
        {
            get
            {
                if (Price is null or <= 0)
                    return "Free";
                return $"R$ {Price:N0}";
            }
        }

        public string CatalogUrl => "https://www.roblox.com/catalog/" + Id;
    }

    internal class HomePageViewModel : NotifyPropertyChangedViewModel
    {
        private readonly string _historyFilePath = Paths.ServerHistory;
        private const int MaxHistoryEntries = 50;

        private readonly ObservableCollection<HistoryGameEntry> _gameHistory = new();
        private GenericTriState _loadState = GenericTriState.Unknown;
        private string _error = string.Empty;
        private readonly ObservableCollection<CatalogItemEntry> _newCatalogItems = new();
        private GenericTriState _catalogLoadState = GenericTriState.Unknown;
        private string _catalogError = string.Empty;
        private static List<CatalogItemEntry>? _cachedCatalog;
        private List<CatalogItemEntry>? _appliedCatalog;
        private readonly ObservableCollection<NewsCardEntry> _latestNews = new();
        private static List<NewsCardEntry>? _cachedNews;
        private List<NewsCardEntry>? _appliedNews;

        public ObservableCollection<DatacenterOption> DatacenterOptions { get; } = new();

        public ObservableCollection<HistoryGameEntry> GameHistory => _gameHistory;

        public bool IsEmpty => _gameHistory.Count == 0;

        public bool IsFilteredEmpty => _gameHistory.Count > 0 && !FilteredHistory.Cast<object>().Any();

        private ICollectionView? _filteredHistory;
        public ICollectionView FilteredHistory
        {
            get
            {
                if (_filteredHistory is null)
                {
                    _filteredHistory = CollectionViewSource.GetDefaultView(_gameHistory);
                    _filteredHistory.Filter = obj =>
                    {
                        if (string.IsNullOrWhiteSpace(_searchText)) return true;
                        if (obj is not HistoryGameEntry entry) return false;
                        string q = _searchText.Trim();
                        return entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || entry.CreatorName.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || entry.PlaceId.ToString().Contains(q, StringComparison.OrdinalIgnoreCase);
                    };
                }
                return _filteredHistory;
            }
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;
                _searchText = value ?? "";
                OnPropertyChanged(nameof(SearchText));
                try { FilteredHistory.Refresh(); } catch { }
                OnPropertyChanged(nameof(IsFilteredEmpty));
            }
        }

        public GenericTriState LoadState
        {
            get => _loadState;
            private set { _loadState = value; OnPropertyChanged(nameof(LoadState)); }
        }

        public string Error
        {
            get => _error;
            private set { _error = value; OnPropertyChanged(nameof(Error)); }
        }

        public ObservableCollection<CatalogItemEntry> NewCatalogItems => _newCatalogItems;

        public ObservableCollection<NewsCardEntry> LatestNews => _latestNews;

        public bool HasLatestNews => _latestNews.Count > 0;

        public string NewsHeaderCount
        {
            get
            {
                int recent = _latestNews.Count(entry => entry.Created != DateTime.MinValue && DateTime.UtcNow - entry.Created < TimeSpan.FromDays(7));
                return recent > 0 ? $"({recent} new)" : string.Empty;
            }
        }

        public bool HasCatalogItems => _newCatalogItems.Count > 0;

        public bool IsCatalogLoading => _catalogLoadState == GenericTriState.Unknown;

        public bool IsCatalogEmpty => _catalogLoadState != GenericTriState.Unknown && _newCatalogItems.Count == 0;

        public string CatalogError
        {
            get => _catalogError;
            private set { _catalogError = value; OnPropertyChanged(nameof(CatalogError)); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand LaunchCommand { get; }
        public ICommand CopyLinkCommand { get; }
        public ICommand OpenCatalogItemCommand { get; }
        public ICommand OpenNewsCardCommand { get; }
        public HomePageViewModel()
        {
            RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true));
            ClearCommand = new RelayCommand(ClearHistory);
            LaunchCommand = new RelayCommand<HistoryGameEntry>(LaunchGame);
            CopyLinkCommand = new RelayCommand<HistoryGameEntry>(CopyDeeplink);
            OpenCatalogItemCommand = new RelayCommand<CatalogItemEntry>(OpenCatalogItem);
            OpenNewsCardCommand = new RelayCommand<NewsCardEntry>(OpenNewsCard);
            OpenStudioProjectCommand = new RelayCommand<StudioProject>(p => { if (p != null) StudioProjects.Open(p); });
            LaunchStudioCommand = new RelayCommand(StudioProjects.LaunchStudio);
            RefreshStudioCommand = new AsyncRelayCommand(LoadStudioAsync);
        }

        private bool _isStudioTab;
        private bool _studioLoaded;
        private bool _isStudioLoading;
        private string _studioStatus = string.Empty;

        public ObservableCollection<StudioProject> StudioRecent { get; } = new();
        public ObservableCollection<StudioProject> StudioOwned { get; } = new();
        public ObservableCollection<StudioProject> StudioLocalFiles { get; } = new();
        public ObservableCollection<StudioProject> StudioTemplates { get; } = new();

        public ICommand OpenStudioProjectCommand { get; }
        public ICommand LaunchStudioCommand { get; }
        public ICommand RefreshStudioCommand { get; }

        private int _studioColumns = 5;

        public int StudioColumns
        {
            get => _studioColumns;
            set { if (_studioColumns == value) return; _studioColumns = value; OnPropertyChanged(nameof(StudioColumns)); }
        }

        public bool IsStudioTab
        {
            get => _isStudioTab;
            set
            {
                if (_isStudioTab == value) return;
                _isStudioTab = value;
                OnPropertyChanged(nameof(IsStudioTab));
                OnPropertyChanged(nameof(RobloxTabVisibility));
                OnPropertyChanged(nameof(StudioTabVisibility));
                if (value && !_studioLoaded)
                    _ = LoadStudioAsync();
            }
        }

        public Visibility RobloxTabVisibility => _isStudioTab ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StudioTabVisibility => _isStudioTab ? Visibility.Visible : Visibility.Collapsed;

        public bool IsStudioLoading
        {
            get => _isStudioLoading;
            private set { _isStudioLoading = value; OnPropertyChanged(nameof(IsStudioLoading)); }
        }

        public string StudioStatus
        {
            get => _studioStatus;
            private set { _studioStatus = value; OnPropertyChanged(nameof(StudioStatus)); }
        }

        public Visibility StudioRecentVisibility => StudioRecent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StudioOwnedVisibility => StudioOwned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StudioLocalFilesVisibility => StudioLocalFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StudioTemplatesVisibility => StudioTemplates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private async Task LoadStudioAsync()
        {
            if (IsStudioLoading) return;
            _studioLoaded = true;
            IsStudioLoading = true;
            StudioStatus = "Loading your Studio experiences...";
            try
            {
                var recentTask = StudioProjects.GetRecentAsync(CancellationToken.None);
                var ownedTask = StudioProjects.GetOwnedAsync(CancellationToken.None);
                var templatesTask = StudioProjects.GetTemplatesAsync(CancellationToken.None);
                var localTask = Task.Run(StudioProjects.GetLocalFiles);
                await Task.WhenAll(recentTask, ownedTask, templatesTask, localTask).ConfigureAwait(true);

                Fill(StudioRecent, recentTask.Result);
                Fill(StudioOwned, ownedTask.Result);
                Fill(StudioLocalFiles, localTask.Result);
                Fill(StudioTemplates, templatesTask.Result);

                StudioStatus = StudioRecent.Count == 0 && StudioOwned.Count == 0 && !RobloxCookie.Exists
                    ? "Sign in to Roblox to see your experiences here."
                    : string.Empty;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::LoadStudioAsync", ex);
                StudioStatus = "Could not load your Studio experiences.";
            }
            finally
            {
                IsStudioLoading = false;
                OnPropertyChanged(nameof(StudioRecentVisibility));
                OnPropertyChanged(nameof(StudioOwnedVisibility));
                OnPropertyChanged(nameof(StudioLocalFilesVisibility));
                OnPropertyChanged(nameof(StudioTemplatesVisibility));
            }

            static void Fill(ObservableCollection<StudioProject> target, List<StudioProject> source)
            {
                target.Clear();
                foreach (StudioProject p in source)
                    target.Add(p);
            }
        }

        private async Task LoadDatacenterOptionsAsync()
        {
            try
            {
                var built = await Task.Run(() =>
                {
                    var list = new List<DatacenterOption> { new DatacenterOption { Key = "", Display = "Preferred Server (Auto)" } };

                    var bestByKey = new Dictionary<string, LearnedServerEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in ServerFetchStore.AllEntries())
                    {
                        if (string.IsNullOrWhiteSpace(entry.City)) continue;
                        if (entry.Lat == 0 && entry.Lon == 0) continue;
                        string key = $"{entry.City}|{entry.Country}";
                        if (!bestByKey.TryGetValue(key, out var existing) || entry.SeenCount > existing.SeenCount)
                            bestByKey[key] = entry;
                    }

                    foreach (var e in bestByKey.Values
                        .OrderBy(x => x.Country, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.City, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(new DatacenterOption
                        {
                            Key = $"{e.City}|{e.Country}",
                            Display = $"{e.City}, {e.Country}"
                        });
                    }
                    return list;
                });

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (DatacenterOptions.Select(o => o.Key).SequenceEqual(built.Select(o => o.Key), StringComparer.OrdinalIgnoreCase))
                        return;
                    DatacenterOptions.Clear();
                    foreach (var o in built)
                        DatacenterOptions.Add(o);
                    foreach (var entry in _gameHistory)
                        entry.ResolveDatacenter();
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::LoadDatacenterOptions", ex);
            }
        }

        private static DateTime _lastNetworkLoadUtc = DateTime.MinValue;

        private static Dictionary<long, string>? _cachedLikes;

        private static readonly TimeSpan NetworkLoadFreshWindow = TimeSpan.FromSeconds(90.0);

        public Task LoadAsync() => LoadAsync(force: false);

        public async Task LoadAsync(bool force)
        {
            Error = string.Empty;

            List<ActivityData> entries;
            try
            {
                entries = await RenderHistoryFromDiskAsync();
                LoadState = GenericTriState.Successful;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::LoadAsync", ex);
                Error = $"Failed to load history: {ex.Message}";
                LoadState = GenericTriState.Failed;
                entries = new List<ActivityData>();
            }

            ApplyCachedHistoryDetails();
            ApplyCachedCatalog();
            ApplyCachedNews();
            PrefetchVisibleThumbnails();

            bool fresh = !force
                && _lastNetworkLoadUtc != DateTime.MinValue
                && DateTime.UtcNow - _lastNetworkLoadUtc < NetworkLoadFreshWindow;

            Task datacenterTask = SafeAsync(LoadDatacenterOptionsAsync, "Datacenters");

            if (fresh)
            {
                await datacenterTask.ConfigureAwait(true);
                return;
            }

            Task enrichTask = SafeAsync(() => EnrichHistoryAsync(entries), "Enrich");
            Task catalogTask = SafeAsync(FetchNewCatalogItemsAsync, "Catalog");
            Task newsTask = SafeAsync(FetchRobloxNewsAsync, "News");

            await Task.WhenAll(datacenterTask, enrichTask, catalogTask, newsTask).ConfigureAwait(true);
            _lastNetworkLoadUtc = DateTime.UtcNow;
            PrefetchVisibleThumbnails();
        }

        private void ApplyCachedHistoryDetails()
        {
            Dictionary<long, string>? likes = _cachedLikes;
            foreach (var entry in _gameHistory)
            {
                if (entry.Data.UniverseDetails == null)
                {
                    entry.Data.UniverseDetails = UniverseDetails.LoadFromCache(entry.UniverseId);
                    if (entry.Data.UniverseDetails != null)
                        entry.RefreshDetails();
                }
                if (likes != null && likes.TryGetValue(entry.UniverseId, out var like))
                    entry.LikePercent = like;
                long playing = entry.Data.UniverseDetails?.Data?.Playing ?? 0;
                if (playing > 0)
                    entry.PlayerCount = FormatCount(playing);
            }
        }

        private static async Task SafeAsync(Func<Task> work, string stage)
        {
            long started = Environment.TickCount64;
            try
            {
                await work().ConfigureAwait(false);
                App.Logger.WriteLine("HomePageViewModel::Load", stage + " stage finished in " + (Environment.TickCount64 - started) + " ms");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("HomePageViewModel::Load", stage + " stage failed: " + ex.Message);
            }
        }

        private string _renderedHistoryStamp = "";

        private List<ActivityData> _renderedHistory = new List<ActivityData>();

        private string HistoryStamp()
        {
            try
            {
                FileInfo info = new FileInfo(_historyFilePath);
                return info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing";
            }
            catch
            {
                return "";
            }
        }

        private async Task<List<ActivityData>> RenderHistoryFromDiskAsync()
        {
            string stamp = HistoryStamp();
            if (stamp.Length > 0 && stamp == _renderedHistoryStamp && _gameHistory.Count == _renderedHistory.Count)
                return _renderedHistory.ToList();
            var entries = await Task.Run(() => ReadFromFile());

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _gameHistory.Clear();
                foreach (var entry in entries)
                    _gameHistory.Add(new HistoryGameEntry(entry, DatacenterOptions));

                NotifyCollectionsChanged();
            });
            _renderedHistoryStamp = stamp;
            _renderedHistory = entries;

            return entries.ToList();
        }

        private async Task EnrichHistoryAsync(List<ActivityData> entries)
        {
            try
            {
                await UniverseDetails.FetchForEntriesAsync(entries);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var entry in _gameHistory)
                    {
                        if (entry.Data.UniverseDetails == null)
                            entry.Data.UniverseDetails = UniverseDetails.LoadFromCache(entry.UniverseId);
                        entry.RefreshDetails();
                    }

                    NotifyCollectionsChanged();
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::FetchForEntries", ex);
            }

            await FetchVotesAndPlayingAsync();
        }

        private void PrefetchVisibleThumbnails()
        {
            try
            {
                List<string> historyUrls = new List<string>();
                foreach (var entry in _gameHistory)
                {
                    if (historyUrls.Count >= 12)
                        break;
                    string? url = entry.ThumbnailUrl;
                    if (!string.IsNullOrEmpty(url))
                        historyUrls.Add(url);
                }
                Voidstrap.Utility.DynamicRenderSystem.Prefetch(historyUrls, 256);
            }
            catch
            {
            }
        }

        private void NotifyCollectionsChanged()
        {
            OnPropertyChanged(nameof(GameHistory));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsFilteredEmpty));
            try { FilteredHistory.Refresh(); } catch { }
        }

        private async Task FetchVotesAndPlayingAsync()
        {
            List<HistoryGameEntry> snapshot;
            try
            {
                snapshot = Application.Current.Dispatcher.Invoke(() => _gameHistory.ToList());
            }
            catch
            {
                return;
            }

            var ids = snapshot
                .Where(x => x.UniverseId != 0)
                .Select(x => x.UniverseId)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
                return;

            var likeById = new Dictionary<long, string>();

            try
            {
                string url = $"https://games.roblox.com/v1/games/votes?universeIds={string.Join(",", ids)}";
                string json = await Voidstrap.Utility.Http.GetString(url);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in data.EnumerateArray())
                    {
                        if (!el.TryGetProperty("id", out var idEl)) continue;
                        long id = idEl.GetInt64();
                        long up = el.TryGetProperty("upVotes", out var upEl) ? upEl.GetInt64() : 0;
                        long down = el.TryGetProperty("downVotes", out var downEl) ? downEl.GetInt64() : 0;
                        long total = up + down;
                        likeById[id] = total > 0 ? $"{Math.Round(up * 100.0 / total):0}%" : "--";
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::FetchVotes", ex);
            }

            if (likeById.Count > 0)
            {
                Dictionary<long, string> merged = _cachedLikes != null ? new Dictionary<long, string>(_cachedLikes) : new Dictionary<long, string>();
                foreach (var pair in likeById)
                    merged[pair.Key] = pair.Value;
                _cachedLikes = merged;
            }
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var entry in _gameHistory)
                    {
                        if (likeById.TryGetValue(entry.UniverseId, out var like))
                            entry.LikePercent = like;

                        long playing = entry.Data.UniverseDetails?.Data?.Playing ?? 0;
                        entry.PlayerCount = playing > 0 ? FormatCount(playing) : "--";
                    }
                });
            }
            catch { }
        }

        private static string FormatCount(long count)
        {
            if (count >= 1_000_000)
                return $"{count / 1_000_000.0:0.#}M";
            if (count >= 1_000)
                return $"{count / 1_000.0:0.#}K";
            return count.ToString();
        }

        private List<ActivityData> ReadFromFile()
        {
            try
            {
                if (!File.Exists(_historyFilePath))
                {
                    App.Logger.WriteLine("HomePageViewModel::ReadFromFile", "File does not exist");
                    return new List<ActivityData>();
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var data = JsonFile.Deserialize<List<ActivityData>>(_historyFilePath, options, 16777216);
                var entries = (data ?? new List<ActivityData>())
                    .Where(HistoryPersister.IsWithinDesktopRetention)
                    .OrderByDescending(x => x.TimeJoined)
                    .GroupBy(x => x.UniverseId != 0 ? x.UniverseId : -Math.Abs(x.PlaceId))
                    .Select(g => g.First())
                    .Take(MaxHistoryEntries)
                    .ToList();

                foreach (var entry in entries)
                    entry.ComputeDisplayTimes();

                return entries;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::ReadFromFile", ex);
                return new List<ActivityData>();
            }
        }

        private void ApplyCachedNews()
        {
            List<NewsCardEntry>? news = _cachedNews;
            if (news == null || ReferenceEquals(news, _appliedNews))
                return;
            _appliedNews = news;
            PublishNews(news);
        }

        private async Task FetchRobloxNewsAsync()
        {
            try
            {
                List<RobloxNews.NewsPost> source = await RobloxNews.GetLatestAsync(2, CancellationToken.None).ConfigureAwait(false);
                List<NewsCardEntry> items = source.Select(post => new NewsCardEntry
                {
                    Id = post.Id,
                    Title = post.Title,
                    Summary = post.Summary,
                    Url = post.Url,
                    Tag = post.Tag,
                    ImageUrl = string.IsNullOrEmpty(post.ImageUrl) ? null : RobloxNews.Thumbnail(post.ImageUrl, 760),
                    Created = post.Created
                }).ToList();
                if (items.Count == 0)
                    return;
                _cachedNews = items;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _appliedNews = items;
                    PublishNews(items);
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::FetchRobloxNews", ex);
            }
        }

        private void PublishNews(List<NewsCardEntry> items)
        {
            _latestNews.Clear();
            foreach (NewsCardEntry item in items)
                _latestNews.Add(item);
            OnPropertyChanged(nameof(LatestNews));
            OnPropertyChanged(nameof(HasLatestNews));
            OnPropertyChanged(nameof(NewsHeaderCount));
        }

        private static void OpenNewsCard(NewsCardEntry? entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Url))
                return;
            try
            {
                Utilities.ShellExecute(entry.Url);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::OpenNewsCard", ex);
            }
        }

        private void ApplyCachedCatalog()
        {
            List<CatalogItemEntry>? catalog = _cachedCatalog;
            if (catalog == null || ReferenceEquals(catalog, _appliedCatalog))
                return;
            _appliedCatalog = catalog;
            _newCatalogItems.Clear();
            foreach (CatalogItemEntry item in catalog)
                _newCatalogItems.Add(item);
            _catalogLoadState = GenericTriState.Successful;
            NotifyCatalogChanged();
        }

        private async Task FetchNewCatalogItemsAsync()
        {
            try
            {
                List<RobloxCatalog.NewItem> source = await RobloxCatalog.GetNewestAsync(CancellationToken.None).ConfigureAwait(false);
                List<CatalogItemEntry> items = source.Select(item => new CatalogItemEntry
                {
                    Id = item.Id,
                    Name = item.Name,
                    Creator = item.Creator,
                    TypeName = item.TypeName,
                    Limited = item.Limited,
                    Price = item.Price,
                    ImageUrl = string.IsNullOrEmpty(item.Image) ? null : item.Image
                }).ToList();
                _cachedCatalog = items;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _appliedCatalog = items;
                    _newCatalogItems.Clear();
                    foreach (CatalogItemEntry item in items)
                        _newCatalogItems.Add(item);
                    _catalogLoadState = GenericTriState.Successful;
                    NotifyCatalogChanged();
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::FetchNewCatalogItems", ex);
                CatalogError = string.Empty;
                _catalogLoadState = GenericTriState.Failed;
                Application.Current.Dispatcher.Invoke(NotifyCatalogChanged);
            }
        }

        private void NotifyCatalogChanged()
        {
            OnPropertyChanged(nameof(NewCatalogItems));
            OnPropertyChanged(nameof(HasCatalogItems));
            OnPropertyChanged(nameof(IsCatalogLoading));
            OnPropertyChanged(nameof(IsCatalogEmpty));
        }

        private static void OpenCatalogItem(CatalogItemEntry? entry)
        {
            if (entry == null)
                return;
            try
            {
                Utilities.ShellExecute(entry.CatalogUrl);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::OpenCatalogItem", ex);
            }
        }

        private void ClearHistory()
        {
            try
            {
                if (File.Exists(_historyFilePath))
                    File.Delete(_historyFilePath);

                _gameHistory.Clear();
                NotifyCollectionsChanged();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::ClearHistory", ex);
            }
        }

        private void LaunchGame(HistoryGameEntry? entry)
        {
            if (entry is null || entry.PlaceId == 0) return;
            try
            {
                string uri = $"roblox://experiences/start?placeId={entry.PlaceId}";

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
                App.Logger.WriteException("HomePageViewModel::LaunchGame", ex);
            }
        }

        private void CopyDeeplink(HistoryGameEntry? entry)
        {
            if (entry is null) return;
            try
            {
                Voidstrap.Utility.ClipboardService.SetText($"https://www.roblox.com/games/{entry.PlaceId}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::CopyDeeplink", ex);
            }
        }
    }
}
