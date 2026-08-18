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
using System.Windows.Media;
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

    internal class WebsiteBadgeEntry
    {
        public string Name { get; init; } = "";
        public ImageSource? Image { get; init; }
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

        public string PriceDisplay
        {
            get
            {
                if (Price is null or <= 0)
                    return "Free";
                return $"R$ {Price:N0}";
            }
        }

        public int? Price { get; init; }

        public string CatalogUrl => App.WebsiteBaseUrl + "/pages/item.html?id=" + Id;
    }

    internal class UnderratedGameEntry
    {
        public long UniverseId { get; init; }
        public long PlaceId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? BannerUrl { get; init; }
        public bool HasBanner => !string.IsNullOrEmpty(BannerUrl);
        public string LikePercent { get; init; } = string.Empty;
        public string PlayerCount { get; init; } = string.Empty;
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

            string savedKey = "";
            try
            {
                var perGame = App.Settings.Prop.PerGamePreferredDatacenters;
                if (perGame != null && perGame.TryGetValue(data.PlaceId, out var key))
                    savedKey = key ?? "";
            }
            catch { }

            _selectedDatacenter = _datacenterOptions.FirstOrDefault(o =>
                string.Equals(o.Key, savedKey, StringComparison.OrdinalIgnoreCase))
                ?? _datacenterOptions.FirstOrDefault();
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
                if (_selectedDatacenter == value) return;
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

    internal class HomePageViewModel : NotifyPropertyChangedViewModel
    {
        private static readonly string NewCatalogApiUrl = App.WebsiteBaseUrl + "/api/catalog/new";
        private static readonly string WebsiteProfileApiUrl = App.WebsiteBaseUrl + "/api/me";
        private readonly string _historyFilePath = Paths.ServerHistory;
        private const int MaxHistoryEntries = 50;

        private readonly ObservableCollection<HistoryGameEntry> _gameHistory = new();
        private readonly ObservableCollection<CatalogItemEntry> _newCatalogItems = new();
        private GenericTriState _loadState = GenericTriState.Unknown;
        private GenericTriState _catalogLoadState = GenericTriState.Unknown;
        private string _error = string.Empty;
        private string _catalogError = string.Empty;
        private bool _hasWebsiteProfile;
        private string _websiteDisplayName = string.Empty;
        private string _websiteUsername = string.Empty;
        private string? _websiteAvatarUrl;
        private string? _websiteBannerUrl;
        private string? _websiteGradientCss;
        private string? _websiteGradientKey;
        private string? _websiteAvatarBorder;
        private string? _websiteStatus;
        private string? _websiteAbout;
        private Brush? _websiteBannerBrush;
        private Brush? _websiteBorderBrush;
        private int _websiteLevel;
        private int _websiteLevelInto;
        private int _websiteLevelNeeded;
        private int _websiteLevelPercent;
        private bool _websiteLevelMax;
        private bool _hasWebsiteLevel;
        private int _websiteFriends;
        private int _websiteFollowers;
        private int _websiteFollowing;
        private int _websiteLikes;
        private int _websiteDislikes;
        private CancellationTokenSource? _profileLoadCts;

        public ObservableCollection<DatacenterOption> DatacenterOptions { get; } = new();

        public ObservableCollection<HistoryGameEntry> GameHistory => _gameHistory;

        public ObservableCollection<CatalogItemEntry> NewCatalogItems => _newCatalogItems;

        private readonly ObservableCollection<UnderratedGameEntry> _underratedGames = new();

        public ObservableCollection<UnderratedGameEntry> UnderratedGames => _underratedGames;

        public bool HasUnderratedGames => _underratedGames.Count > 0;

        private string _underratedSubtitle = "Voted by the Voidstrap community";
        public string UnderratedSubtitle
        {
            get => _underratedSubtitle;
            private set { _underratedSubtitle = value; OnPropertyChanged(nameof(UnderratedSubtitle)); }
        }

        private static string ResetCountdown(long resetsAtMs)
        {
            if (resetsAtMs <= 0)
                return "";
            var reset = DateTimeOffset.FromUnixTimeMilliseconds(resetsAtMs);
            var span = reset - DateTimeOffset.UtcNow;
            if (span.TotalMinutes <= 0)
                return " · resets soon";
            if (span.TotalDays >= 1)
                return $" · resets in {(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1)
                return $" · resets in {(int)span.TotalHours}h {span.Minutes}m";
            return $" · resets in {span.Minutes}m";
        }

        public bool HasCatalogItems => _newCatalogItems.Count > 0;

        public bool IsCatalogLoading => _catalogLoadState == GenericTriState.Unknown;

        public bool IsCatalogEmpty => _catalogLoadState != GenericTriState.Unknown && _newCatalogItems.Count == 0;

        public bool HasWebsiteProfile
        {
            get => _hasWebsiteProfile;
            set
            {
                _hasWebsiteProfile = value;
                OnPropertyChanged(nameof(HasWebsiteProfile));
                OnPropertyChanged(nameof(IsSignedOut));
            }
        }

        public bool IsSignedOut => !_hasWebsiteProfile;

        private bool _isSigningIn;

        public bool IsSigningIn
        {
            get => _isSigningIn;
            set
            {
                if (_isSigningIn == value)
                    return;

                _isSigningIn = value;

                if (Application.Current?.Dispatcher is not null && !Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsSigningIn)));
                else
                    OnPropertyChanged(nameof(IsSigningIn));
            }
        }

        public bool GameChatEnabled
        {
            get => App.Settings.Prop.GameChatEnabled;
            set
            {
                if (App.Settings.Prop.GameChatEnabled == value)
                    return;
                App.Settings.Prop.GameChatEnabled = value;
                App.Settings.SaveDeferred();
                OnPropertyChanged(nameof(GameChatEnabled));
            }
        }

        public string WebsiteDisplayName
        {
            get => _websiteDisplayName;
            set { if (_websiteDisplayName == value) return; _websiteDisplayName = value; OnPropertyChanged(nameof(WebsiteDisplayName)); }
        }

        public string WebsiteUsername
        {
            get => _websiteUsername;
            set { if (_websiteUsername == value) return; _websiteUsername = value; OnPropertyChanged(nameof(WebsiteUsername)); }
        }

        public ObservableCollection<WebsiteBadgeEntry> WebsiteBadges { get; } = new ObservableCollection<WebsiteBadgeEntry>();

        private string _lastVisualSig = "\0";
        private System.Threading.CancellationTokenSource _autoRefreshCts;

        public string? WebsiteAvatarUrl
        {
            get => _websiteAvatarUrl;
            set { if (_websiteAvatarUrl == value) return; _websiteAvatarUrl = value; OnPropertyChanged(nameof(WebsiteAvatarUrl)); OnPropertyChanged(nameof(HasWebsiteAvatar)); }
        }

        public string? WebsiteBannerUrl
        {
            get => _websiteBannerUrl;
            set { if (_websiteBannerUrl == value) return; _websiteBannerUrl = value; OnPropertyChanged(nameof(WebsiteBannerUrl)); OnPropertyChanged(nameof(HasWebsiteBanner)); }
        }

        public string? WebsiteGradientCss
        {
            get => _websiteGradientCss;
            set { _websiteGradientCss = value; OnPropertyChanged(nameof(WebsiteGradientCss)); }
        }

        public string? WebsiteGradientKey
        {
            get => _websiteGradientKey;
            set { _websiteGradientKey = value; OnPropertyChanged(nameof(WebsiteGradientKey)); }
        }

        public string? WebsiteAvatarBorder
        {
            get => _websiteAvatarBorder;
            set { _websiteAvatarBorder = value; OnPropertyChanged(nameof(WebsiteAvatarBorder)); OnPropertyChanged(nameof(HasWebsiteBorder)); }
        }

        public string? WebsiteStatus
        {
            get => _websiteStatus;
            set { if (_websiteStatus == value) return; _websiteStatus = value; OnPropertyChanged(nameof(WebsiteStatus)); OnPropertyChanged(nameof(HasWebsiteStatus)); }
        }

        public string? WebsiteAbout
        {
            get => _websiteAbout;
            set { if (_websiteAbout == value) return; _websiteAbout = value; OnPropertyChanged(nameof(WebsiteAbout)); OnPropertyChanged(nameof(HasWebsiteAbout)); }
        }

        public Brush? WebsiteBannerBrush
        {
            get => _websiteBannerBrush;
            set { _websiteBannerBrush = value; OnPropertyChanged(nameof(WebsiteBannerBrush)); OnPropertyChanged(nameof(HasWebsiteBannerBrush)); }
        }

        public Brush? WebsiteBorderBrush
        {
            get => _websiteBorderBrush;
            set { _websiteBorderBrush = value; OnPropertyChanged(nameof(WebsiteBorderBrush)); OnPropertyChanged(nameof(HasWebsiteBorderBrush)); }
        }

        public bool HasWebsiteAvatar => !string.IsNullOrEmpty(WebsiteAvatarUrl);
        public bool HasWebsiteBanner => !string.IsNullOrEmpty(WebsiteBannerUrl) || GradientWebsite.HasGradient(WebsiteGradientKey, WebsiteGradientCss);
        public bool HasWebsiteBannerBrush => WebsiteBannerBrush is not null;
        public bool HasWebsiteBorder => GradientProfileBorder.HasBorder(WebsiteAvatarBorder);
        public bool HasWebsiteBorderBrush => WebsiteBorderBrush is not null;
        public bool HasWebsiteStatus => !string.IsNullOrEmpty(WebsiteStatus);
        public bool HasWebsiteAbout => !string.IsNullOrEmpty(WebsiteAbout);

        public int WebsiteFriends
        {
            get => _websiteFriends;
            set { _websiteFriends = value; OnPropertyChanged(nameof(WebsiteFriends)); OnPropertyChanged(nameof(WebsiteFriendsDisplay)); }
        }

        public int WebsiteFollowers
        {
            get => _websiteFollowers;
            set { _websiteFollowers = value; OnPropertyChanged(nameof(WebsiteFollowers)); OnPropertyChanged(nameof(WebsiteFollowersDisplay)); }
        }

        public int WebsiteFollowing
        {
            get => _websiteFollowing;
            set { _websiteFollowing = value; OnPropertyChanged(nameof(WebsiteFollowing)); OnPropertyChanged(nameof(WebsiteFollowingDisplay)); }
        }

        public int WebsiteLikes
        {
            get => _websiteLikes;
            set { _websiteLikes = value; OnPropertyChanged(nameof(WebsiteLikes)); OnPropertyChanged(nameof(WebsiteLikesDisplay)); }
        }

        public int WebsiteDislikes
        {
            get => _websiteDislikes;
            set { _websiteDislikes = value; OnPropertyChanged(nameof(WebsiteDislikes)); OnPropertyChanged(nameof(WebsiteDislikesDisplay)); }
        }

        public int WebsiteLevel
        {
            get => _websiteLevel;
            set { _websiteLevel = value; OnPropertyChanged(nameof(WebsiteLevel)); OnPropertyChanged(nameof(WebsiteLevelDisplay)); }
        }

        public int WebsiteLevelPercent
        {
            get => _websiteLevelPercent;
            set { _websiteLevelPercent = value; OnPropertyChanged(nameof(WebsiteLevelPercent)); }
        }

        public bool HasWebsiteLevel
        {
            get => _hasWebsiteLevel;
            set { _hasWebsiteLevel = value; OnPropertyChanged(nameof(HasWebsiteLevel)); }
        }

        public int WebsiteLevelInto
        {
            get => _websiteLevelInto;
            set { _websiteLevelInto = value; OnPropertyChanged(nameof(WebsiteLevelInto)); OnPropertyChanged(nameof(WebsiteLevelXpDisplay)); }
        }

        public int WebsiteLevelNeeded
        {
            get => _websiteLevelNeeded;
            set { _websiteLevelNeeded = value; OnPropertyChanged(nameof(WebsiteLevelNeeded)); OnPropertyChanged(nameof(WebsiteLevelXpDisplay)); }
        }

        public bool WebsiteLevelMax
        {
            get => _websiteLevelMax;
            set { _websiteLevelMax = value; OnPropertyChanged(nameof(WebsiteLevelMax)); OnPropertyChanged(nameof(WebsiteLevelXpDisplay)); }
        }

        public string WebsiteLevelDisplay => WebsiteLevel.ToString("N0");

        public string WebsiteLevelXpDisplay => WebsiteLevelMax
            ? "Max level"
            : WebsiteLevelInto.ToString("N0") + " / " + WebsiteLevelNeeded.ToString("N0") + " XP";

        public string WebsiteFriendsDisplay => WebsiteFriends.ToString("N0");
        public string WebsiteFollowersDisplay => WebsiteFollowers.ToString("N0");
        public string WebsiteFollowingDisplay => WebsiteFollowing.ToString("N0");
        public string WebsiteLikesDisplay => WebsiteLikes.ToString("N0");
        public string WebsiteDislikesDisplay => WebsiteDislikes.ToString("N0");

        public string CatalogError
        {
            get => _catalogError;
            private set { _catalogError = value; OnPropertyChanged(nameof(CatalogError)); }
        }

        public bool IsEmpty => _gameHistory.Count == 0;

        public bool IsFilteredEmpty => _gameHistory.Count > 0 && FilteredHistory.Cast<object>().Count() == 0;

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
                        return entry.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || entry.CreatorName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || entry.PlaceId.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
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

        public ICommand RefreshCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand LaunchCommand { get; }
        public ICommand CopyLinkCommand { get; }
        public ICommand OpenCatalogItemCommand { get; }
        public ICommand LaunchUnderratedCommand { get; }
        public ICommand SignInCommand { get; }
        public ICommand SignOutCommand { get; }
        public ICommand AddAccountCommand { get; }
        public ICommand SwitchAccountCommand { get; }
        public ICommand RemoveAccountCommand { get; }
        public HomePageViewModel()
        {
            RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true));
            ClearCommand = new RelayCommand(ClearHistory);
            LaunchCommand = new RelayCommand<HistoryGameEntry>(LaunchGame);
            CopyLinkCommand = new RelayCommand<HistoryGameEntry>(CopyDeeplink);
            OpenCatalogItemCommand = new RelayCommand<CatalogItemEntry>(OpenCatalogItem);
            LaunchUnderratedCommand = new RelayCommand<UnderratedGameEntry>(LaunchUnderrated);
            SignInCommand = new RelayCommand(SignIn);
            SignOutCommand = new RelayCommand(SignOut);
            AddAccountCommand = new RelayCommand(SignIn);
            SwitchAccountCommand = new RelayCommand<Voidstrap.Utility.AuthAccount>(SwitchAccount);
            RemoveAccountCommand = new RelayCommand<Voidstrap.Utility.AuthAccount>(RemoveAccount);
            RefreshAccounts();
        }

        private void OnWebsiteAuthChanged()
        {
            try
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        RefreshAccounts();
                        CancelProfileLoad();
                        ClearWebsiteProfile();
                        if (Voidstrap.Utility.WebsiteAuth.IsSignedIn())
                        {
                            _ = LoadAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("HomePageViewModel::AuthChanged", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::AuthChanged", ex);
            }
        }

        public void StartAutoRefresh()
        {
            if (_autoRefreshCts != null)
                return;

            Voidstrap.Utility.WebsiteAuth.Changed -= OnWebsiteAuthChanged;
            Voidstrap.Utility.WebsiteAuth.Changed += OnWebsiteAuthChanged;
            _autoRefreshCts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = _autoRefreshCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (token.IsCancellationRequested)
                        break;
                    if (!Voidstrap.Utility.WebsiteAuth.IsSignedIn())
                        continue;
                    try
                    {
                        await LoadWebsiteProfileAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }, token);
        }

        public void StopAutoRefresh()
        {
            try
            {
                Voidstrap.Utility.WebsiteAuth.Changed -= OnWebsiteAuthChanged;
            }
            catch
            {
            }
            try
            {
                _autoRefreshCts?.Cancel();
                _autoRefreshCts?.Dispose();
            }
            catch
            {
            }
            _autoRefreshCts = null;
            CancelProfileLoad();
        }

        private static readonly object _signInGate = new object();
        private static System.Threading.CancellationTokenSource _signInCts;
        private static DateTime _signInLastClick = DateTime.MinValue;

        private void SignIn()
        {
            System.Threading.CancellationTokenSource cts;

            lock (_signInGate)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _signInLastClick).TotalMilliseconds < 900)
                {
                    App.Logger.WriteLine("HomePageViewModel::SignIn", "Ignoring repeat click within the debounce window");
                    return;
                }
                _signInLastClick = now;

                if (_signInCts is not null)
                {
                    App.Logger.WriteLine("HomePageViewModel::SignIn", "Cancelling the previous sign in attempt, starting a new one");
                    try { _signInCts.Cancel(); } catch (ObjectDisposedException) { }
                }

                cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                _signInCts = cts;
            }

            IsSigningIn = true;
            _ = SignInAsync(cts);
        }

        private async Task SignInAsync(System.Threading.CancellationTokenSource timeoutCts)
        {
            try
            {
                byte[] sessionBytes = new byte[32];
                System.Security.Cryptography.RandomNumberGenerator.Fill(sessionBytes);
                string sessionId = Convert.ToHexString(sessionBytes).ToLowerInvariant();

                string signInUrl = App.WebsiteBaseUrl + "/pages/app-signin.html#session=" + sessionId;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = signInUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("HomePageViewModel::SignIn", ex);
                    Frontend.ShowMessageBox("Could not open the sign in page in your browser. Please try again.", System.Windows.MessageBoxImage.Warning);
                    return;
                }

                string pollUrl = App.WebsiteBaseUrl + "/api/app/auth/poll";
                string vsToken = null;
                int pollCount = 0;
                while (!timeoutCts.IsCancellationRequested)
                {
                    if (pollCount > 0)
                    {
                        int waitMs = pollCount <= 12 ? 350 : (pollCount <= 30 ? 1000 : 2000);
                        try
                        {
                            await Task.Delay(waitMs, timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                    pollCount++;
                    try
                    {
                        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, pollUrl);
                        req.Headers.TryAddWithoutValidation("x-app-session", sessionId);
                        using var resp = await App.HttpClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            continue;
                        }
                        string json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(resp.Content, 262144, timeoutCts.Token).ConfigureAwait(false);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ready", out var readyEl) && readyEl.ValueKind == System.Text.Json.JsonValueKind.True
                            && root.TryGetProperty("vs_token", out var tokEl) && tokEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            vsToken = tokEl.GetString();
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine("HomePageViewModel::SignIn", "Poll error, retrying: " + ex.Message);
                    }
                }

                if (string.IsNullOrWhiteSpace(vsToken))
                {
                    App.Logger.WriteLine("HomePageViewModel::SignIn", "Sign in timed out or was cancelled");
                    return;
                }

                bool superseded;
                lock (_signInGate)
                {
                    superseded = !ReferenceEquals(_signInCts, timeoutCts);
                }

                if (superseded || timeoutCts.IsCancellationRequested)
                {
                    App.Logger.WriteLine("HomePageViewModel::SignIn", "Discarding a result from a superseded sign in attempt");
                    return;
                }

                Voidstrap.Utility.WebsiteAuth.Save(vsToken.Trim());
                await Application.Current.Dispatcher.InvokeAsync(() => LoadWebsiteProfileAsync());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::SignIn", ex);
            }
            finally
            {
                bool current;
                lock (_signInGate)
                {
                    current = ReferenceEquals(_signInCts, timeoutCts);
                    if (current)
                        _signInCts = null;
                }

                if (current)
                    IsSigningIn = false;

                timeoutCts.Dispose();
            }
        }

        private void SignOut()
        {
            try
            {
                Voidstrap.Utility.WebsiteAuth.Clear();
                RefreshAccounts();
                CancelProfileLoad();
                ClearWebsiteProfile();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::SignOut", ex);
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
                    DatacenterOptions.Clear();
                    foreach (var o in built)
                        DatacenterOptions.Add(o);
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::LoadDatacenterOptions", ex);
            }
        }

        private DateTime _lastNetworkLoadUtc = DateTime.MinValue;

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

            PrefetchVisibleThumbnails();

            bool fresh = !force
                && _lastNetworkLoadUtc != DateTime.MinValue
                && DateTime.UtcNow - _lastNetworkLoadUtc < NetworkLoadFreshWindow;

            Task profileTask = SafeAsync(LoadWebsiteProfileAsync, "Profile");
            Task datacenterTask = SafeAsync(LoadDatacenterOptionsAsync, "Datacenters");

            if (fresh)
            {
                await Task.WhenAll(profileTask, datacenterTask).ConfigureAwait(true);
                return;
            }

            CatalogError = string.Empty;
            _catalogLoadState = GenericTriState.Unknown;
            NotifyCatalogChanged();

            Task catalogTask = SafeAsync(FetchNewCatalogItemsAsync, "Catalog");
            Task underratedTask = SafeAsync(FetchUnderratedGamesAsync, "Underrated");
            Task enrichTask = SafeAsync(() => EnrichHistoryAsync(entries), "Enrich");

            await Task.WhenAll(profileTask, datacenterTask, catalogTask, underratedTask, enrichTask).ConfigureAwait(true);
            _lastNetworkLoadUtc = DateTime.UtcNow;
            PrefetchVisibleThumbnails();
        }

        private static async Task SafeAsync(Func<Task> work, string stage)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("HomePageViewModel::Load", stage + " stage failed: " + ex.Message);
            }
        }

        private async Task<List<ActivityData>> RenderHistoryFromDiskAsync()
        {
            var entries = await Task.Run(() => ReadFromFile());

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _gameHistory.Clear();
                foreach (var entry in entries)
                    _gameHistory.Add(new HistoryGameEntry(entry, DatacenterOptions));

                NotifyCollectionsChanged();
            });

            return entries.ToList();
        }

        private async Task EnrichHistoryAsync(List<ActivityData> entries)
        {
            try
            {
                await Voidstrap.Utility.WebsiteHistorySync.FetchAndApplyAsync();
                entries = await RenderHistoryFromDiskAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::HistoryFetch", ex);
            }

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
                List<string> cardUrls = new List<string>();
                foreach (var entry in _gameHistory)
                {
                    string? url = entry.ThumbnailUrl;
                    if (!string.IsNullOrEmpty(url))
                        historyUrls.Add(url);
                }
                foreach (var item in _newCatalogItems)
                {
                    string? url = item.ImageUrl;
                    if (!string.IsNullOrEmpty(url))
                        cardUrls.Add(url);
                }
                foreach (var entry in _underratedGames)
                {
                    string? url = entry.BannerUrl;
                    if (!string.IsNullOrEmpty(url))
                        cardUrls.Add(url);
                }
                Voidstrap.Utility.DynamicRenderSystem.Prefetch(historyUrls, 512);
                Voidstrap.Utility.DynamicRenderSystem.Prefetch(cardUrls, 256);
            }
            catch
            {
            }
        }

        private static string FormatPlayerCount(long n)
        {
            if (n >= 1000000)
                return (n / 1000000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
            if (n >= 1000)
                return (n / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K";
            return n.ToString();
        }

        private async Task FetchUnderratedGamesAsync()
        {
            try
            {
                string body = await Voidstrap.Utility.Http.GetString(App.WebsiteBaseUrl + "/api/gamevote/top");
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                long resetsAt = doc.RootElement.TryGetProperty("resetsAt", out var raEl) && raEl.ValueKind == System.Text.Json.JsonValueKind.Number ? raEl.GetInt64() : 0;
                var list = new List<UnderratedGameEntry>();
                if (doc.RootElement.TryGetProperty("games", out var games) && games.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var g in games.EnumerateArray())
                    {
                        long universeId = g.TryGetProperty("universeId", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Number ? u.GetInt64() : 0;
                        long placeId = g.TryGetProperty("rootPlaceId", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt64() : 0;
                        if (universeId <= 0 || placeId <= 0)
                            continue;
                        string name = g.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? (n.GetString() ?? "") : "";
                        string banner = g.TryGetProperty("banner", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String ? (b.GetString() ?? "") : "";
                        if (string.IsNullOrEmpty(banner) && g.TryGetProperty("thumb", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                            banner = t.GetString() ?? "";
                        long playing = g.TryGetProperty("playing", out var pl) && pl.ValueKind == System.Text.Json.JsonValueKind.Number ? pl.GetInt64() : 0;
                        int up = g.TryGetProperty("up", out var upEl) && upEl.ValueKind == System.Text.Json.JsonValueKind.Number ? upEl.GetInt32() : 0;
                        int down = g.TryGetProperty("down", out var dnEl) && dnEl.ValueKind == System.Text.Json.JsonValueKind.Number ? dnEl.GetInt32() : 0;
                        int total = up + down;
                        int pct = total > 0 ? (int)Math.Round(up * 100.0 / total) : 100;
                        list.Add(new UnderratedGameEntry
                        {
                            UniverseId = universeId,
                            PlaceId = placeId,
                            Name = name,
                            BannerUrl = string.IsNullOrEmpty(banner) ? null : banner,
                            LikePercent = pct + "%",
                            PlayerCount = FormatPlayerCount(playing),
                        });
                    }
                }
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _underratedGames.Clear();
                    foreach (var entry in list)
                        _underratedGames.Add(entry);
                    UnderratedSubtitle = "Voted by the Voidstrap community" + ResetCountdown(resetsAt);
                    OnPropertyChanged(nameof(UnderratedGames));
                    OnPropertyChanged(nameof(HasUnderratedGames));
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::FetchUnderratedGames", ex);
            }
        }

        private async Task FetchNewCatalogItemsAsync()
        {
            try
            {
                var response = await Voidstrap.Utility.GitHubCache.GetJsonAsync<VoidstrapCatalogNewResponse>(NewCatalogApiUrl, TimeSpan.FromMinutes(10));
                if (response == null)
                {
                    _catalogLoadState = GenericTriState.Failed;
                    Application.Current.Dispatcher.Invoke(NotifyCatalogChanged);
                    return;
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _newCatalogItems.Clear();
                    foreach (var item in response.Items)
                    {
                        _newCatalogItems.Add(new CatalogItemEntry
                        {
                            Id = item.Id,
                            Name = item.Name,
                            Creator = item.Creator,
                            TypeName = item.TypeName,
                            Limited = item.Limited,
                            Price = item.Price,
                            ImageUrl = string.IsNullOrEmpty(item.Image) ? null : item.Image
                        });
                    }

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

        private async Task LoadWebsiteProfileAsync()
        {
            CancellationTokenSource loadCts = new CancellationTokenSource();
            CancellationTokenSource? previous = Interlocked.Exchange(ref _profileLoadCts, loadCts);
            previous?.Cancel();
            string activeAccount = Voidstrap.Utility.WebsiteAuth.GetActiveId() ?? "";
            try
            {
                string? websiteToken = GetWebsiteToken();
                if (string.IsNullOrEmpty(websiteToken))
                {
                    await Application.Current.Dispatcher.InvokeAsync(ClearWebsiteProfile);
                    return;
                }

                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, WebsiteProfileApiUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", websiteToken);
                using var response = await App.HttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, loadCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Voidstrap.Utility.WebsiteAuth.Clear();
                        await Application.Current.Dispatcher.InvokeAsync(ClearWebsiteProfile);
                    }
                    return;
                }

                var json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, loadCts.Token).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("user", out var userProp) || userProp.ValueKind == System.Text.Json.JsonValueKind.Null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(ClearWebsiteProfile);
                    return;
                }

                string displayName = "";
                string username = "";
                string? userId = null;
                string? avatar = null;
                string? banner = null;
                string? gradient = null;
                string? gradientKey = null;
                string? avatarBorder = null;
                string? status = null;
                string? about = null;
                var badges = new List<WebsiteBadgeEntry>();
                int levelValue = 0;
                int levelInto = 0;
                int levelNeeded = 0;
                int levelPercent = 0;
                bool levelMax = false;
                bool levelKnown = false;

                if (userProp.TryGetProperty("progress", out var progressProp)
                    && progressProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (progressProp.TryGetProperty("level", out var lvEl) && lvEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        levelValue = lvEl.GetInt32();
                        levelKnown = true;
                    }
                    if (progressProp.TryGetProperty("into", out var intoEl) && intoEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        levelInto = intoEl.GetInt32();
                    if (progressProp.TryGetProperty("needed", out var needEl) && needEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        levelNeeded = needEl.GetInt32();
                    if (progressProp.TryGetProperty("percent", out var pctEl) && pctEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        levelPercent = Math.Max(0, Math.Min(100, pctEl.GetInt32()));
                    if (progressProp.TryGetProperty("max", out var maxEl)
                        && (maxEl.ValueKind == System.Text.Json.JsonValueKind.True || maxEl.ValueKind == System.Text.Json.JsonValueKind.False))
                        levelMax = maxEl.GetBoolean();
                }

                if (userProp.TryGetProperty("id", out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    userId = idProp.GetString();
                if (userProp.TryGetProperty("displayName", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String)
                    displayName = dn.GetString() ?? "";
                if (userProp.TryGetProperty("username", out var un) && un.ValueKind == System.Text.Json.JsonValueKind.String)
                    username = un.GetString() ?? "";
                if (userProp.TryGetProperty("avatar", out var av) && av.ValueKind == System.Text.Json.JsonValueKind.String)
                    avatar = Voidstrap.Utility.WebsiteUrl.Absolute(av.GetString());
                if (userProp.TryGetProperty("banner", out var ba) && ba.ValueKind == System.Text.Json.JsonValueKind.String)
                    banner = ba.GetString();
                if (userProp.TryGetProperty("gradient", out var gr) && gr.ValueKind == System.Text.Json.JsonValueKind.String)
                    gradient = gr.GetString();
                if (userProp.TryGetProperty("gradientKey", out var gk) && gk.ValueKind == System.Text.Json.JsonValueKind.String)
                    gradientKey = gk.GetString();
                if (userProp.TryGetProperty("avatarBorder", out var ab) && ab.ValueKind == System.Text.Json.JsonValueKind.String)
                    avatarBorder = ab.GetString();

                Voidstrap.Utility.BorderRender borderVisual = null;
                string borderRaw = "";
                if (userProp.TryGetProperty("equippedBorder", out var eqb) && eqb.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    borderRaw = eqb.GetRawText();
                    borderVisual = Voidstrap.Utility.WebsiteBorderRenderer.Build(eqb, 58.0, 96.0);
                }
                if (userProp.TryGetProperty("status", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String)
                    status = st.GetString();
                if (userProp.TryGetProperty("about", out var ab2) && ab2.ValueKind == System.Text.Json.JsonValueKind.String)
                    about = ab2.GetString();
                if (userProp.TryGetProperty("badges", out var badgeArray) && badgeArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var badge in badgeArray.EnumerateArray())
                    {
                        if (badges.Count >= 12)
                            break;
                        string name = badge.TryGetProperty("name", out var badgeName) && badgeName.ValueKind == JsonValueKind.String ? badgeName.GetString() ?? "" : "";
                        string image = badge.TryGetProperty("image", out var badgeImage) && badgeImage.ValueKind == JsonValueKind.String ? badgeImage.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        var source = DecodeBadgeImage(image);
                        if (source == null)
                            continue;
                        badges.Add(new WebsiteBadgeEntry { Name = name, Image = source });
                    }
                }

                avatar = ResolveWebsiteUrl(avatar);
                banner = ResolveWebsiteUrl(banner);

                App.Logger.WriteLine("HomePageViewModel", "Website profile: displayName=" + displayName + " username=" + username + " userId=" + (userId ?? "(null)") + " banner=" + (banner ?? "(null)") + " gradient=" + (gradient ?? "(null)") + " avatarBorder=" + (avatarBorder ?? "(null)"));

                int friends = WebsiteFriends;
                int followers = WebsiteFollowers;
                int following = WebsiteFollowing;
                int likes = WebsiteLikes;
                int dislikes = WebsiteDislikes;

                if (!string.IsNullOrEmpty(userId))
                {
                    try
                    {
                        using var statsReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, App.WebsiteBaseUrl + "/api/users/" + userId);
                        statsReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", websiteToken);
                        using var statsResp = await App.HttpClient.SendAsync(statsReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, loadCts.Token).ConfigureAwait(false);
                        if (statsResp.IsSuccessStatusCode)
                        {
                            var statsJson = await Voidstrap.Utility.Http.ReadStringBoundedAsync(statsResp.Content, 2 * 1024 * 1024, loadCts.Token).ConfigureAwait(false);
                            using var statsDoc = System.Text.Json.JsonDocument.Parse(statsJson);
                            var statsRoot = statsDoc.RootElement;
                            if (statsRoot.TryGetProperty("counts", out var counts))
                            {
                                if (counts.TryGetProperty("friends", out var fr) && fr.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    friends = fr.GetInt32();
                                if (counts.TryGetProperty("followers", out var fw) && fw.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    followers = fw.GetInt32();
                                if (counts.TryGetProperty("following", out var fl) && fl.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    following = fl.GetInt32();
                            }
                            if (statsRoot.TryGetProperty("user", out var statsUser) && statsUser.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (statsUser.TryGetProperty("status", out var stEl) && stEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    string s = stEl.GetString();
                                    if (!string.IsNullOrEmpty(s))
                                        status = s;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                    }

                    try
                    {
                        using var likesReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, App.WebsiteBaseUrl + "/api/likes?targets=profile:" + userId);
                        using var likesResp = await App.HttpClient.SendAsync(likesReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, loadCts.Token).ConfigureAwait(false);
                        if (likesResp.IsSuccessStatusCode)
                        {
                            var likesJson = await Voidstrap.Utility.Http.ReadStringBoundedAsync(likesResp.Content, 2 * 1024 * 1024, loadCts.Token).ConfigureAwait(false);
                            using var likesDoc = System.Text.Json.JsonDocument.Parse(likesJson);
                            if (likesDoc.RootElement.TryGetProperty("likes", out var likesMap))
                            {
                                string key = "profile:" + userId;
                                if (likesMap.TryGetProperty(key, out var entry))
                                {
                                    if (entry.TryGetProperty("up", out var up) && up.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        likes = up.GetInt32();
                                    if (entry.TryGetProperty("down", out var dnCount) && dnCount.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        dislikes = dnCount.GetInt32();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                    }
                }

                string visualSig = (avatar ?? "") + "|" + (banner ?? "") + "|" + (gradient ?? "") + "|" + (avatarBorder ?? "") + "|" + borderRaw;
                bool visualsChanged = visualSig != _lastVisualSig;
                Brush bannerBrush = null;
                Brush borderBrush = null;
                if (visualsChanged)
                {
                    bannerBrush = await Voidstrap.Utility.GradientWebsite.CreateBannerBrushAsync(banner, gradient, loadCts.Token).ConfigureAwait(false);
                    borderBrush = Voidstrap.Utility.GradientProfileBorder.ParseBorder(avatarBorder);
                }

                if (loadCts.IsCancellationRequested || activeAccount != (Voidstrap.Utility.WebsiteAuth.GetActiveId() ?? ""))
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (loadCts.IsCancellationRequested || activeAccount != (Voidstrap.Utility.WebsiteAuth.GetActiveId() ?? ""))
                        return;
                    WebsiteDisplayName = displayName;
                    WebsiteUsername = username;
                    WebsiteBadges.Clear();
                    foreach (var badge in badges)
                        WebsiteBadges.Add(badge);
                    WebsiteStatus = status;
                    WebsiteAbout = about;
                    WebsiteLevel = levelValue;
                    WebsiteLevelInto = levelInto;
                    WebsiteLevelNeeded = levelNeeded;
                    WebsiteLevelPercent = levelPercent;
                    WebsiteLevelMax = levelMax;
                    HasWebsiteLevel = levelKnown;
                    WebsiteFriends = friends;
                    WebsiteFollowers = followers;
                    WebsiteFollowing = following;
                    WebsiteLikes = likes;
                    WebsiteDislikes = dislikes;
                    if (visualsChanged)
                    {
                        _lastVisualSig = visualSig;
                        WebsiteAvatarUrl = avatar;
                        WebsiteBannerUrl = banner;
                        WebsiteGradientCss = gradient;
                        WebsiteGradientKey = gradientKey;
                        WebsiteAvatarBorder = avatarBorder;
                        ApplyBorderVisual(borderVisual);
                        WebsiteBannerBrush = bannerBrush;
                        WebsiteBorderBrush = borderBrush;
                    }
                    HasWebsiteProfile = true;
                });

                try
                {
                    if (!string.IsNullOrEmpty(userId))
                    {
                        Voidstrap.Utility.WebsiteAuth.AddOrUpdateAccount(websiteToken, userId, string.IsNullOrEmpty(displayName) ? username : displayName, avatar ?? "");
                    }
                }
                catch { }
                await Application.Current.Dispatcher.InvokeAsync(RefreshAccounts);
            }
            catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::LoadWebsiteProfile", ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref _profileLoadCts, null, loadCts);
                loadCts.Dispose();
            }
        }

        private void CancelProfileLoad()
        {
            CancellationTokenSource? cancellation = Interlocked.Exchange(ref _profileLoadCts, null);
            cancellation?.Cancel();
        }

        private static string? ResolveWebsiteUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("http", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return value;
            return App.WebsiteBaseUrl.TrimEnd('/') + "/" + value.TrimStart('/');
        }

        private static ImageSource? DecodeBadgeImage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return Voidstrap.Utility.AppImage.LoadSync(ResolveWebsiteUrl(value) ?? "", 38);
            int comma = value.IndexOf(',');
            if (comma <= 0)
                return null;
            try
            {
                return Voidstrap.Utility.SafeImaging.FromBytes(Convert.FromBase64String(value.Substring(comma + 1)), 38);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static Uri? BuildBadgeUri(string value)
        {
            string? resolved = ResolveWebsiteUrl(value);
            return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ? uri : null;
        }

        private void ClearWebsiteProfile()
        {
            HasWebsiteProfile = false;
            WebsiteDisplayName = "";
            WebsiteUsername = "";
            WebsiteBadges.Clear();
            WebsiteAvatarUrl = "";
            WebsiteBannerUrl = "";
            WebsiteGradientCss = "";
            WebsiteGradientKey = "";
            WebsiteAvatarBorder = "";
            WebsiteStatus = "";
            WebsiteAbout = "";
            WebsiteBannerBrush = null;
            WebsiteBorderBrush = null;
            WebsiteBorderImage = null;
            HasWebsiteLevel = false;
            WebsiteLevel = 0;
            WebsiteLevelInto = 0;
            WebsiteLevelNeeded = 0;
            WebsiteLevelPercent = 0;
            WebsiteLevelMax = false;
            WebsiteFriends = 0;
            WebsiteFollowers = 0;
            WebsiteFollowing = 0;
            WebsiteLikes = 0;
            WebsiteDislikes = 0;
            _lastVisualSig = "\0";
        }

        public System.Collections.ObjectModel.ObservableCollection<Voidstrap.Utility.AuthAccount> Accounts { get; } = new System.Collections.ObjectModel.ObservableCollection<Voidstrap.Utility.AuthAccount>();

        public bool HasMultipleAccounts => Accounts.Count > 1;

        private void RefreshAccounts()
        {
            try
            {
                Accounts.Clear();
                foreach (var acc in Voidstrap.Utility.WebsiteAuth.GetAccounts())
                {
                    Accounts.Add(acc);
                }
                OnPropertyChanged(nameof(Accounts));
                OnPropertyChanged(nameof(HasMultipleAccounts));
            }
            catch { }
        }

        private void SwitchAccount(Voidstrap.Utility.AuthAccount account)
        {
            try
            {
                if (account == null || string.IsNullOrEmpty(account.Id))
                    return;
                if (Voidstrap.Utility.WebsiteAuth.SetActive(account.Id))
                {
                    CancelProfileLoad();
                    ClearWebsiteProfile();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::SwitchAccount", ex);
            }
        }

        private void RemoveAccount(Voidstrap.Utility.AuthAccount account)
        {
            try
            {
                if (account == null || string.IsNullOrEmpty(account.Id))
                    return;
                Voidstrap.Utility.WebsiteAuth.RemoveAccount(account.Id);
                RefreshAccounts();
                CancelProfileLoad();
                ClearWebsiteProfile();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::RemoveAccount", ex);
            }
        }

        private System.Windows.Media.ImageSource _websiteBorderImage;
        public System.Windows.Media.ImageSource WebsiteBorderImage
        {
            get => _websiteBorderImage;
            set { _websiteBorderImage = value; OnPropertyChanged(nameof(WebsiteBorderImage)); OnPropertyChanged(nameof(HasWebsiteBorderImage)); }
        }
        public bool HasWebsiteBorderImage => _websiteBorderImage != null;

        private double _websiteBorderWidth;
        public double WebsiteBorderWidth { get => _websiteBorderWidth; set { _websiteBorderWidth = value; OnPropertyChanged(nameof(WebsiteBorderWidth)); } }

        private double _websiteBorderHeight;
        public double WebsiteBorderHeight { get => _websiteBorderHeight; set { _websiteBorderHeight = value; OnPropertyChanged(nameof(WebsiteBorderHeight)); } }

        private System.Windows.Thickness _websiteBorderMargin;
        public System.Windows.Thickness WebsiteBorderMargin { get => _websiteBorderMargin; set { _websiteBorderMargin = value; OnPropertyChanged(nameof(WebsiteBorderMargin)); } }

        private int _websiteBorderZ;
        public int WebsiteBorderZ { get => _websiteBorderZ; set { _websiteBorderZ = value; OnPropertyChanged(nameof(WebsiteBorderZ)); } }

        private void ApplyBorderVisual(Voidstrap.Utility.BorderRender visual)
        {
            if (visual == null)
            {
                WebsiteBorderImage = null;
                return;
            }
            WebsiteBorderWidth = visual.Width;
            WebsiteBorderHeight = visual.Height;
            WebsiteBorderMargin = visual.Margin;
            WebsiteBorderZ = visual.ZIndex;
            WebsiteBorderImage = visual.Image;
        }


        private static string? GetWebsiteToken() => Voidstrap.Utility.WebsiteAuth.GetToken();

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
                App.Logger.WriteException("HomePageViewModel::LaunchGame", ex);
            }
        }

        private void LaunchUnderrated(UnderratedGameEntry? entry)
        {
            if (entry is null || entry.PlaceId == 0) return;
            try
            {
                string uri = $"roblox://experiences/start?placeId={entry.PlaceId}";
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
                App.Logger.WriteException("HomePageViewModel::LaunchUnderrated", ex);
            }
        }

        private void CopyDeeplink(HistoryGameEntry? entry)
        {
            if (entry is null) return;
            try
            {
                Clipboard.SetText($"https://www.roblox.com/games/{entry.PlaceId}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::CopyDeeplink", ex);
            }
        }

        private static void OpenCatalogItem(CatalogItemEntry? entry)
        {
            if (entry is null || entry.Id == 0) return;
            try
            {
                Utilities.ShellExecute(entry.CatalogUrl);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomePageViewModel::OpenCatalogItem", ex);
            }
        }
    }
}
