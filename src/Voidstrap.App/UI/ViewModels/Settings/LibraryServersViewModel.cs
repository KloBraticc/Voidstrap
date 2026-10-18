using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.ViewModels.Settings;

public class LibraryServersViewModel : NotifyPropertyChangedViewModel
{
    private const string LogIdent = "LibraryServers";

    private const int AvatarAttemptLimit = 3;

    private const int AvatarCardsPerRound = 12;

    private const int MaxAvatarTokens = 6;

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan AvatarRetryDelay = TimeSpan.FromSeconds(2.5);

    private readonly List<ServerCard> _publicAll = new();

    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<MatchmakerCandidate> _incoming = new();

    private readonly List<ServerCard> _avatarQueue = new();

    private readonly HashSet<ServerCard> _avatarQueued = new();

    private CancellationTokenSource? _publicCts;

    private CancellationTokenSource? _privateCts;

    private HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

    private LibraryGameEntry? _game;

    private long _placeId;

    private int _publicGeneration;

    private int _privateGeneration;

    private int _renderPending;

    private bool _viewActive;

    private bool _publicLoading;

    private bool _privateLoading;

    private bool _avatarsBusy;

    private bool _joining;

    private bool _isServersTabSelected;

    private bool _isPrivateServersTabSelected;

    private bool _publicLoaded;

    private bool _privateLoaded;

    private string _publicStatus = "";

    private string _privateStatus = "";

    private string _countText = "";

    private string _selectedRegion = "";

    private int _sortIndex;

    private bool _hideFull;

    private bool _updatingRegions;

    public LibraryServersViewModel()
    {
        JoinCommand = new AsyncRelayCommand<ServerCard>(JoinAsync);
        RefreshCommand = new AsyncRelayCommand(LoadPublicAsync);
        RefreshPrivateCommand = new AsyncRelayCommand(RefreshPrivateAsync);
    }

    public ObservableCollection<ServerCard> Servers { get; } = new();

    public ObservableCollection<ServerCard> PrivateServers { get; } = new();

    public ObservableCollection<string> Regions { get; } = new();

    public ICommand JoinCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand RefreshPrivateCommand { get; }

    public bool PublicLoading
    {
        get => _publicLoading;
        private set
        {
            if (SetProperty(ref _publicLoading, value))
                OnPropertyChanged(nameof(PublicStatusVisibility));
        }
    }

    public string PublicStatus
    {
        get => _publicStatus;
        private set
        {
            if (SetProperty(ref _publicStatus, value))
                OnPropertyChanged(nameof(PublicStatusVisibility));
        }
    }

    public Visibility PublicStatusVisibility => string.IsNullOrEmpty(_publicStatus) && !_publicLoading ? Visibility.Collapsed : Visibility.Visible;

    public bool PrivateLoading
    {
        get => _privateLoading;
        private set
        {
            if (SetProperty(ref _privateLoading, value))
                OnPropertyChanged(nameof(PrivateStatusVisibility));
        }
    }

    public string PrivateStatus
    {
        get => _privateStatus;
        private set
        {
            if (SetProperty(ref _privateStatus, value))
                OnPropertyChanged(nameof(PrivateStatusVisibility));
        }
    }

    public Visibility PrivateStatusVisibility => string.IsNullOrEmpty(_privateStatus) && !_privateLoading ? Visibility.Collapsed : Visibility.Visible;

    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    public string SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (_updatingRegions)
                return;
            string region = value ?? "";
            if (SetProperty(ref _selectedRegion, region))
                Rebuild();
        }
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (SetProperty(ref _sortIndex, value))
                Rebuild();
        }
    }

    public bool HideFull
    {
        get => _hideFull;
        set
        {
            if (SetProperty(ref _hideFull, value))
                Rebuild();
        }
    }

    public bool IsServersTabSelected
    {
        get => _isServersTabSelected;
        set
        {
            if (!SetProperty(ref _isServersTabSelected, value) || !value || _publicLoaded)
                return;
            _ = LoadPublicAsync();
        }
    }

    public bool IsPrivateServersTabSelected
    {
        get => _isPrivateServersTabSelected;
        set
        {
            if (!SetProperty(ref _isPrivateServersTabSelected, value) || !value)
                return;
            _ = LoadPrivateAsync(false);
        }
    }

    public void SetGame(LibraryGameEntry? game)
    {
        _game = game;
        Interlocked.Increment(ref _publicGeneration);
        Interlocked.Increment(ref _privateGeneration);
        Interlocked.Exchange(ref _placeId, 0);
        CancelPublic();
        CancelPrivate();
        DropAvatarQueue();
        _publicAll.Clear();
        _seen.Clear();
        _incoming.Clear();
        Servers.Clear();
        PrivateServers.Clear();
        ResetRegions();
        CountText = "";
        PublicStatus = "";
        PrivateStatus = "";
        _publicLoaded = false;
        _privateLoaded = false;
        if (game == null || !_viewActive)
            return;
        if (_isServersTabSelected)
            _ = LoadPublicAsync();
        if (_isPrivateServersTabSelected)
            _ = LoadPrivateAsync(false);
    }

    public void SetViewActive(bool active)
    {
        if (_viewActive == active)
            return;
        _viewActive = active;
        if (active)
        {
            if (_isServersTabSelected && !_publicLoaded)
                _ = LoadPublicAsync();
            if (_isPrivateServersTabSelected)
                _ = LoadPrivateAsync(false);
            return;
        }
        Interlocked.Increment(ref _publicGeneration);
        Interlocked.Increment(ref _privateGeneration);
        CancelPublic();
        CancelPrivate();
        DropAvatarQueue();
    }

    public async Task LoadPublicAsync()
    {
        if (!_viewActive)
            return;
        LibraryGameEntry? game = _game;
        if (game == null)
            return;
        CancelPublic();
        int generation = Interlocked.Increment(ref _publicGeneration);
        _publicAll.Clear();
        _seen.Clear();
        _incoming.Clear();
        Servers.Clear();
        if (Regions.Count == 0)
            ResetRegions();
        CountText = "";
        PublicLoading = true;
        PublicStatus = Strings.ContextMenu_ServerBrowser_Loading;
        using CancellationTokenSource request = new(ScanTimeout);
        _publicCts = request;
        try
        {
            long placeId = await ResolvePlaceAsync(game);
            if (generation != Volatile.Read(ref _publicGeneration))
                return;
            if (placeId <= 0)
            {
                PublicStatus = Strings.ContextMenu_ServerBrowser_Failed;
                return;
            }
            if (ActivityWatcher.ServerListRateLimited)
            {
                PublicStatus = Strings.ContextMenu_ServerBrowser_RateLimited;
                return;
            }
            _blocked = VoidstrapMatchmaker.GetBlockedDatacenters();
            MatchmakerServerScan scan = await VoidstrapMatchmaker.ScanServersAsync(
                placeId,
                ServerMatchmaker.ResolvePreferredDatacenterKey(placeId),
                null,
                candidate => QueueResult(candidate, generation),
                request.Token);
            if (generation != Volatile.Read(ref _publicGeneration))
                return;
            _incoming.Clear();
            _publicAll.Clear();
            _seen.Clear();
            foreach (MatchmakerCandidate server in scan.Servers)
            {
                if (_seen.Add(server.JobId))
                    _publicAll.Add(CreateCard(server));
            }
            UpdateRegions();
            Rebuild();
            PublicStatus = DescribeScan(scan);
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _publicGeneration))
                FinishPartialScan();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "The public server list could not be loaded: " + ex.Message);
            if (generation == Volatile.Read(ref _publicGeneration))
                FinishPartialScan();
        }
        finally
        {
            if (ReferenceEquals(_publicCts, request))
            {
                _publicCts = null;
                PublicLoading = false;
                _publicLoaded = true;
            }
        }
    }

    private async Task RefreshPrivateAsync() => await LoadPrivateAsync(true).ConfigureAwait(true);

    public async Task LoadPrivateAsync(bool force)
    {
        if (!_viewActive)
            return;
        LibraryGameEntry? game = _game;
        if (game == null)
            return;
        if (_privateLoaded && !force)
            return;
        CancelPrivate();
        int generation = Interlocked.Increment(ref _privateGeneration);
        _privateLoaded = true;
        PrivateServers.Clear();
        PrivateLoading = true;
        PrivateStatus = Strings.Library_Servers_PrivateLoading;
        using CancellationTokenSource request = new(ScanTimeout);
        _privateCts = request;
        try
        {
            long placeId = await ResolvePlaceAsync(game);
            if (generation != Volatile.Read(ref _privateGeneration))
                return;
            if (placeId <= 0)
            {
                PrivateStatus = Strings.Library_Servers_PrivateUnavailable;
                return;
            }
            (PrivateServersStatus status, List<RobloxPrivateServer> servers) = await RobloxServers.GetPrivateServersAsync(placeId, request.Token);
            if (generation != Volatile.Read(ref _privateGeneration))
                return;
            foreach (RobloxPrivateServer server in servers)
            {
                ServerCard card = ServerCard.ForPrivate(server);
                PrivateServers.Add(card);
                QueueAvatars(card);
            }
            PrivateStatus = status switch
            {
                PrivateServersStatus.NotSignedIn => Strings.Library_Servers_PrivateSignedOut,
                PrivateServersStatus.Unavailable => Strings.Library_Servers_PrivateUnavailable,
                _ => PrivateServers.Count == 0 ? Strings.Library_Servers_PrivateEmpty : ""
            };
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _privateGeneration))
                PrivateStatus = Strings.Library_Servers_PrivateUnavailable;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Private servers could not be loaded: " + ex.Message);
            if (generation == Volatile.Read(ref _privateGeneration))
                PrivateStatus = Strings.Library_Servers_PrivateUnavailable;
        }
        finally
        {
            if (ReferenceEquals(_privateCts, request))
            {
                _privateCts = null;
                PrivateLoading = false;
            }
        }
    }

    private Task JoinAsync(ServerCard? card)
    {
        long placeId = Volatile.Read(ref _placeId);
        if (_joining || card == null || !card.CanJoin || placeId <= 0 || (card.JobId.Length == 0 && card.AccessCode.Length == 0))
            return Task.CompletedTask;
        _joining = true;
        bool privateServer = card.AccessCode.Length > 0;
        string joining = string.Format(Strings.ContextMenu_ServerBrowser_Joining, card.Region.Length > 0 ? card.Region : card.Title);
        if (privateServer)
        {
            PrivateStatus = joining;
        }
        else
        {
            Interlocked.Increment(ref _publicGeneration);
            CancelPublic();
            PublicStatus = joining;
        }
        try
        {
            string uri = LaunchInterceptor.BuildExperienceUri(placeId, privateServer ? null : card.JobId, privateServer ? card.AccessCode : null);
            if (!LibraryViewModel.TryLaunchPlayer(uri))
            {
                SetJoinFailed(privateServer);
                return Task.CompletedTask;
            }
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "The selected server could not be opened: " + ex.Message);
            SetJoinFailed(privateServer);
        }
        finally
        {
            _joining = false;
        }
        return Task.CompletedTask;
    }

    private void SetJoinFailed(bool privateServer)
    {
        if (privateServer)
            PrivateStatus = Strings.ContextMenu_ServerBrowser_JoinFailed;
        else
            PublicStatus = Strings.ContextMenu_ServerBrowser_JoinFailed;
    }

    private async Task<long> ResolvePlaceAsync(LibraryGameEntry game)
    {
        long known = Volatile.Read(ref _placeId);
        if (known > 0)
            return known;
        long placeId = await LibraryViewModel.ResolveStartPlaceAsync(game).ConfigureAwait(true);
        if (placeId > 0)
            Interlocked.CompareExchange(ref _placeId, placeId, 0);
        return placeId;
    }

    private void CancelPublic()
    {
        CancellationTokenSource? previous = _publicCts;
        _publicCts = null;
        PublicLoading = false;
        if (previous == null)
            return;
        previous.Cancel();
        previous.Dispose();
    }

    private void CancelPrivate()
    {
        CancellationTokenSource? previous = _privateCts;
        _privateCts = null;
        PrivateLoading = false;
        if (previous == null)
            return;
        previous.Cancel();
        previous.Dispose();
    }

    private void QueueResult(MatchmakerCandidate candidate, int generation)
    {
        if (generation != Volatile.Read(ref _publicGeneration))
            return;
        _incoming.Enqueue(candidate);
        if (Interlocked.Exchange(ref _renderPending, 1) != 0)
            return;
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Interlocked.Exchange(ref _renderPending, 0);
            return;
        }
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushIncoming));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _renderPending, 0);
        }
    }

    private void FlushIncoming()
    {
        Interlocked.Exchange(ref _renderPending, 0);
        if (!_publicLoading || _incoming.IsEmpty)
            return;
        List<ServerCard> added = DrainIncoming();
        UpdateRegions();
        foreach (ServerCard card in added)
        {
            if (!MatchesFilters(card))
                continue;
            Servers.Add(card);
            QueueAvatars(card);
        }
        if (added.Count > 0)
            CountText = string.Format(Strings.ContextMenu_ServerBrowser_Count, Servers.Count);
    }

    private List<ServerCard> DrainIncoming()
    {
        List<ServerCard> added = new();
        while (_incoming.TryDequeue(out MatchmakerCandidate? candidate))
        {
            if (!_seen.Add(candidate.JobId))
                continue;
            ServerCard card = CreateCard(candidate);
            _publicAll.Add(card);
            added.Add(card);
        }
        return added;
    }

    private ServerCard CreateCard(MatchmakerCandidate server)
        => ServerCard.ForPublic(server, false, _blocked.Contains(VoidstrapMatchmaker.BlockKey(server.Datacenter)));

    private void FinishPartialScan()
    {
        _ = DrainIncoming();
        UpdateRegions();
        Rebuild();
        if (_publicAll.Count == 0)
            PublicStatus = Strings.ContextMenu_ServerBrowser_Failed;
    }

    private static string DescribeScan(MatchmakerServerScan scan) => scan.Status switch
    {
        MatchmakerScanStatus.SignedOut => Strings.ContextMenu_ServerBrowser_SignedOut,
        MatchmakerScanStatus.NoLocation => Strings.ContextMenu_ServerBrowser_NoLocation,
        MatchmakerScanStatus.NoServers => Strings.ContextMenu_ServerBrowser_NoServers,
        MatchmakerScanStatus.Unresolved => Strings.ContextMenu_ServerBrowser_Unresolved,
        MatchmakerScanStatus.AllBlocked => Strings.ContextMenu_ServerBrowser_AllBlocked,
        MatchmakerScanStatus.AllFar => Strings.ContextMenu_ServerBrowser_AllFar,
        _ => ""
    };

    private void ResetRegions()
    {
        string allRegions = Strings.ContextMenu_ServerBrowser_AllRegions;
        _updatingRegions = true;
        try
        {
            for (int index = Regions.Count - 1; index >= 1; index--)
                Regions.RemoveAt(index);
            if (Regions.Count == 0)
                Regions.Add(allRegions);
            else if (!string.Equals(Regions[0], allRegions, StringComparison.Ordinal))
                Regions[0] = allRegions;
            _selectedRegion = Regions[0];
        }
        finally
        {
            _updatingRegions = false;
        }
        OnPropertyChanged(nameof(SelectedRegion));
    }

    private void UpdateRegions()
    {
        string allRegions = Strings.ContextMenu_ServerBrowser_AllRegions;
        List<string> regions = _publicAll.Select(card => card.Region).Where(region => region.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(region => region, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (Regions.Count == regions.Count + 1 && string.Equals(Regions[0], allRegions, StringComparison.Ordinal)
            && regions.Select((region, index) => string.Equals(Regions[index + 1], region, StringComparison.Ordinal)).All(same => same))
            return;
        _updatingRegions = true;
        try
        {
            for (int index = Regions.Count - 1; index >= 1; index--)
            {
                if (!regions.Contains(Regions[index], StringComparer.OrdinalIgnoreCase))
                    Regions.RemoveAt(index);
            }
            foreach (string region in regions)
            {
                if (Regions.Contains(region, StringComparer.OrdinalIgnoreCase))
                    continue;
                int insertAt = Regions.Count;
                for (int index = 1; index < Regions.Count; index++)
                {
                    if (string.Compare(Regions[index], region, StringComparison.CurrentCultureIgnoreCase) > 0)
                    {
                        insertAt = index;
                        break;
                    }
                }
                Regions.Insert(insertAt, region);
            }
            if (Regions.Count == 0)
                Regions.Add(allRegions);
            else if (!string.Equals(Regions[0], allRegions, StringComparison.Ordinal))
                Regions[0] = allRegions;
            if (!Regions.Contains(_selectedRegion))
                _selectedRegion = Regions[0];
        }
        finally
        {
            _updatingRegions = false;
        }
        OnPropertyChanged(nameof(SelectedRegion));
    }

    private bool MatchesFilters(ServerCard card)
    {
        if (_hideFull && card.IsFull)
            return false;
        string allRegions = Regions.Count > 0 ? Regions[0] : Strings.ContextMenu_ServerBrowser_AllRegions;
        return string.IsNullOrEmpty(_selectedRegion) || string.Equals(_selectedRegion, allRegions, StringComparison.Ordinal)
            || string.Equals(card.Region, _selectedRegion, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        IEnumerable<ServerCard> query = _publicAll.Where(MatchesFilters);
        query = _sortIndex switch
        {
            1 => query.OrderBy(card => card.IsBlocked).ThenBy(card => card.Playing).ThenBy(card => card.Ping),
            2 => query.OrderBy(card => card.IsBlocked).ThenBy(card => card.Ping),
            _ => query.OrderBy(card => card.IsBlocked).ThenByDescending(card => card.Playing).ThenBy(card => card.Ping)
        };
        List<ServerCard> cards = query.ToList();
        ReplaceServers(cards);
        CountText = string.Format(Strings.ContextMenu_ServerBrowser_Count, cards.Count);
        foreach (ServerCard card in cards)
            QueueAvatars(card);
    }

    private void ReplaceServers(List<ServerCard> cards)
    {
        if (Servers.Count == cards.Count)
        {
            bool unchanged = true;
            for (int index = 0; index < cards.Count; index++)
            {
                if (ReferenceEquals(Servers[index], cards[index]))
                    continue;
                unchanged = false;
                break;
            }
            if (unchanged)
                return;
        }
        Servers.Clear();
        foreach (ServerCard card in cards)
            Servers.Add(card);
    }

    private void QueueAvatars(ServerCard card)
    {
        if (card.AvatarsRequested || card.Tokens.Length == 0)
            return;
        card.AvatarsRequested = true;
        if (!_avatarQueued.Add(card))
            return;
        _avatarQueue.Add(card);
        if (!_avatarsBusy)
            _ = LoadQueuedAvatarsAsync();
    }

    private void DropAvatarQueue()
    {
        _avatarQueue.Clear();
        _avatarQueued.Clear();
    }

    private async Task LoadQueuedAvatarsAsync()
    {
        if (_avatarsBusy)
            return;
        _avatarsBusy = true;
        try
        {
            while (_avatarQueue.Count > 0)
            {
                int count = Math.Min(AvatarCardsPerRound, _avatarQueue.Count);
                List<ServerCard> batch = _avatarQueue.GetRange(0, count);
                _avatarQueue.RemoveRange(0, count);
                foreach (ServerCard card in batch)
                    _avatarQueued.Remove(card);
                List<string> tokens = batch.SelectMany(card => card.Tokens.Take(MaxAvatarTokens)).Distinct(StringComparer.Ordinal).ToList();
                Dictionary<string, string> urls;
                try
                {
                    urls = await RobloxServers.GetHeadshotsAsync(tokens, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LogIdent, "Player pictures could not be loaded: " + ex.Message);
                    continue;
                }
                List<ServerCard> retry = new();
                foreach (ServerCard card in batch)
                {
                    bool missing = false;
                    int limit = Math.Min(Math.Min(card.Avatars.Count, card.Tokens.Length), MaxAvatarTokens);
                    for (int index = 0; index < limit; index++)
                    {
                        if (card.Avatars[index].IsMore || !string.IsNullOrEmpty(card.Avatars[index].Url))
                            continue;
                        if (urls.TryGetValue(card.Tokens[index], out string? url))
                            card.Avatars[index].Url = url;
                        else
                            missing = true;
                    }
                    if (missing && ++card.AvatarAttempts < AvatarAttemptLimit)
                        retry.Add(card);
                }
                if (retry.Count > 0)
                    _ = RetryAvatarsAsync(retry);
            }
        }
        finally
        {
            _avatarsBusy = false;
        }
    }

    private async Task RetryAvatarsAsync(List<ServerCard> cards)
    {
        await Task.Delay(AvatarRetryDelay).ConfigureAwait(true);
        foreach (ServerCard card in cards)
        {
            if (_avatarQueued.Add(card))
                _avatarQueue.Add(card);
        }
        if (!_avatarsBusy)
            _ = LoadQueuedAvatarsAsync();
    }
}
