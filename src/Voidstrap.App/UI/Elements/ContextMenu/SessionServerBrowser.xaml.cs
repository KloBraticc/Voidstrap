using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.ContextMenu;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionServerBrowser : Window
{
    private enum BrowserTab
    {
        Public,
        Private,
        Recent
    }

    private const string AllRegions = "All regions";
    private const double CardMinWidth = 260;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);
    private readonly ActivityWatcher _activity;
    private readonly ServerMatchmaker? _matchmaker;
    private readonly ActivityData _gameData;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ServerCard> _all = new();
    private readonly ConcurrentQueue<MatchmakerCandidate> _incoming = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _avatarImages = new(StringComparer.Ordinal);
    private readonly HashSet<ServerCard> _avatarQueue = new();
    private readonly DispatcherTimer _avatarTimer;
    private List<ServerCard> _private = new();
    private List<ServerCard> _recent = new();
    private BrowserTab _tab = BrowserTab.Public;
    private CancellationTokenSource? _scanCts;
    private string? _bestJobId;
    private long _placeId;
    private int _columns = 3;
    private bool _rescanPending;
    private int _scanGeneration;
    private int _renderPending;
    private bool _started;
    private bool _busy;
    private bool _joining;
    private bool _closed;
    private bool _privateLoaded;
    private bool _joinBestWhenReady;
    private bool _avatarsLoading;

    public SessionServerBrowser(ActivityWatcher activity, ServerMatchmaker? matchmaker = null)
    {
        _activity = activity;
        _matchmaker = matchmaker;
        _gameData = activity.Data;
        _placeId = _gameData.PlaceId;
        InitializeComponent();
        _avatarTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _avatarTimer.Tick += (_, _) =>
        {
            _avatarTimer.Stop();
            _ = LoadQueuedAvatarsAsync();
        };
        RegionBox.Items.Add(AllRegions);
        RegionBox.SelectedIndex = 0;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _ = LoadCurrentServerAsync();
        _ = LoadPlacesAsync(_gameData);
        _ = ScanServersAsync();
    }

    // ---- The server you are in ----

    private async Task LoadCurrentServerAsync()
    {
        ActivityData data = _gameData;
        TypeText.Text = data.ServerType.ToTranslatedString() + " server";
        InstanceText.Text = data.JobId;
        InstanceText.ToolTip = data.JobId;
        LocationText.Text = "Looking up";
        UptimeText.Text = data.ServerStartedUtc is DateTimeOffset started ? ServerInformationViewModel.FormatUptime(DateTimeOffset.UtcNow - started) : "Looking up";
        try
        {
            Task<string?> location = data.QueryServerLocation(_lifetime.Token);
            Task<ServerStartLookup>? start = data.ServerStartedUtc.HasValue ? null : VoidstrapMatchmaker.GetServerStartAsync(data.PlaceId, data.JobId, _lifetime.Token);
            string? place = await location;
            if (_closed)
                return;
            LocationText.Text = string.IsNullOrWhiteSpace(place) ? "Unavailable" : place;
            LocationText.ToolTip = LocationText.Text;
            if (start != null)
            {
                ServerStartLookup lookup = await start;
                if (_closed)
                    return;
                if (lookup.Status == ServerStartStatus.Found)
                    data.ServerStartedUtc ??= lookup.StartedUtc;
            }
            UptimeText.Text = data.ServerStartedUtc is DateTimeOffset known ? ServerInformationViewModel.FormatUptime(DateTimeOffset.UtcNow - known) : "Unavailable";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "The current server could not be described: " + ex.Message);
            if (!_closed && LocationText.Text == "Looking up")
                LocationText.Text = "Unavailable";
            if (!_closed && UptimeText.Text == "Looking up")
                UptimeText.Text = "Unavailable";
        }
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_gameData.JobId);
            CopyGlyph.Text = "";
            await Task.Delay(1200);
            if (!_closed)
                CopyGlyph.Text = "";
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "The instance ID could not be copied: " + ex.Message);
        }
    }

    private void ClosestButton_Click(object sender, RoutedEventArgs e)
    {
        ServerCard? best = _all.FirstOrDefault(card => card.CanJoin && card.Server != null && string.Equals(card.JobId, _bestJobId, StringComparison.OrdinalIgnoreCase))
            ?? _all.Where(card => card.CanJoin && card.Server != null && !card.IsBlocked).OrderBy(card => card.Server!.EstimatedPingMs).FirstOrDefault();
        if (best != null)
        {
            _ = JoinAsync(best);
            return;
        }
        // Still scanning, the best server is joined as soon as the scan settles
        _joinBestWhenReady = true;
        StatusText.Text = "Finding the closest server";
        if (!_busy)
            _ = ScanServersAsync();
    }

    private void HopButton_Click(object sender, RoutedEventArgs e)
    {
        List<ServerCard> choices = _all.Where(card => card.CanJoin && card.Server != null && !card.IsBlocked && !card.IsFull).ToList();
        if (choices.Count == 0)
        {
            StatusText.Text = _busy ? "Still looking for servers, try again in a moment" : "There is no other server to hop to";
            return;
        }
        _ = JoinAsync(choices[Random.Shared.Next(choices.Count)]);
    }

    // ---- Tabs ----

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_started || _closed)
            return;
        _tab = ReferenceEquals(sender, PrivateTab) ? BrowserTab.Private : ReferenceEquals(sender, RecentTab) ? BrowserTab.Recent : BrowserTab.Public;
        FilterBar.Visibility = _tab == BrowserTab.Public ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = string.Empty;
        if (_tab == BrowserTab.Private && !_privateLoaded)
            _ = LoadPrivateServersAsync();
        else if (_tab == BrowserTab.Recent)
            LoadRecentServers();
        Render();
    }

    private async Task LoadPrivateServersAsync()
    {
        _privateLoaded = true;
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            (PrivateServersStatus status, List<RobloxPrivateServer> servers) = await RobloxServers.GetPrivateServersAsync(_placeId, _lifetime.Token);
            if (_closed)
                return;
            _private = servers.Select(server => ServerCard.ForPrivate(server)).ToList();
            if (_tab == BrowserTab.Private)
            {
                Render();
                StatusText.Text = status switch
                {
                    PrivateServersStatus.NotSignedIn => "Allow cookie access in Voidstrap's settings to see your private servers.",
                    PrivateServersStatus.Unavailable => "Private servers could not be loaded right now.",
                    _ => _private.Count == 0 ? "You have no private servers for this game." : string.Empty
                };
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _privateLoaded = false;
            App.Logger.WriteLine("SessionServerBrowser", "Private servers could not be loaded: " + ex.Message);
        }
        finally
        {
            if (!_closed)
                LoadingBar.Visibility = _busy && _tab == BrowserTab.Public ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void LoadRecentServers()
    {
        List<ActivityData> history;
        lock (_activity.History)
            history = _activity.History.ToList();
        _recent = history
            .Where(entry => !string.IsNullOrEmpty(entry.JobId)
                && (entry.PlaceId == _placeId || (_gameData.UniverseId > 0 && entry.UniverseId == _gameData.UniverseId))
                && !string.Equals(entry.JobId, _gameData.JobId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.JobId, StringComparer.OrdinalIgnoreCase)
            .Select(group => ServerCard.ForRecent(group.First()))
            .ToList();
        StatusText.Text = _recent.Count == 0 ? "Servers you leave or teleport out of in this game show up here." : string.Empty;
    }

    // ---- Public servers ----

    private async Task ScanServersAsync()
    {
        if (_closed)
            return;
        if (_busy)
        {
            // A different place was picked mid scan, scan again once this one stops
            _rescanPending = true;
            _scanCts?.Cancel();
            return;
        }
        if (!_activity.InGame || _placeId <= 0)
        {
            StatusText.Text = Strings.ContextMenu_ServerBrowser_NotInGame;
            return;
        }
        if (ActivityWatcher.ServerListRateLimited)
        {
            StatusText.Text = Strings.ContextMenu_ServerBrowser_RateLimited;
            return;
        }

        _busy = true;
        int generation = ++_scanGeneration;
        _all.Clear();
        _seen.Clear();
        _incoming.Clear();
        _blocked = VoidstrapMatchmaker.GetBlockedDatacenters();
        _bestJobId = null;
        Render();
        LoadingBar.Visibility = Visibility.Visible;
        if (!_joinBestWhenReady)
            StatusText.Text = Strings.ContextMenu_ServerBrowser_Loading;
        RefreshButton.IsEnabled = false;
        CountText.Text = string.Empty;
        ScanSummaryText.Text = string.Empty;
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(ScanTimeout);
        _scanCts = request;
        try
        {
            long placeId = _placeId;
            MatchmakerServerScan scan = await VoidstrapMatchmaker.ScanServersAsync(
                placeId,
                ServerMatchmaker.ResolvePreferredDatacenterKey(placeId),
                placeId == _gameData.PlaceId ? _gameData.JobId : null,
                candidate => QueueResult(candidate, generation),
                request.Token);
            if (_closed || _rescanPending)
                return;
            _incoming.Clear();
            _all.Clear();
            _seen.Clear();
            _bestJobId = scan.Best?.JobId;
            foreach (MatchmakerCandidate server in scan.Servers)
            {
                if (_seen.Add(server.JobId))
                    _all.Add(CreateCard(server));
            }
            UpdateRegions();
            Render();
            if (_tab == BrowserTab.Public)
                StatusText.Text = DescribeScan(scan);
            if (scan.Listed > 0)
                ScanSummaryText.Text = $"Found {_all.Count} of {scan.Listed} listed servers";
            if (_joinBestWhenReady)
            {
                _joinBestWhenReady = false;
                ClosestButton_Click(this, new RoutedEventArgs());
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!_closed && !_rescanPending)
                FinishPartialScan();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "The matchmaker server scan could not be loaded: " + ex.Message);
            if (!_closed)
                FinishPartialScan();
        }
        finally
        {
            if (ReferenceEquals(_scanCts, request))
                _scanCts = null;
            _busy = false;
            if (!_closed)
            {
                LoadingBar.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                if (_rescanPending)
                {
                    _rescanPending = false;
                    _ = ScanServersAsync();
                }
            }
        }
    }

    // Lists the start place and its subplaces; the picker only shows when there is more than one
    private async Task LoadPlacesAsync(ActivityData data)
    {
        try
        {
            await UniverseDetails.FetchForEntriesAsync(new[] { data }, _lifetime.Token);
            if (_closed)
                return;
            long universeId = data.UniverseId > 0 ? data.UniverseId : data.UniverseDetails?.Data?.Id ?? 0;
            long rootPlaceId = data.UniverseDetails?.Data?.RootPlaceId ?? 0;
            List<RobloxPlace> places = universeId > 0
                ? await RobloxPlaces.GetUniversePlacesAsync(universeId, rootPlaceId, _lifetime.Token)
                : new List<RobloxPlace>();
            if (_closed)
                return;
            if (!places.Any(place => place.Id == data.PlaceId))
                places.Insert(0, new RobloxPlace { Id = data.PlaceId, Name = data.GameName, IsRoot = data.PlaceId == rootPlaceId });
            if (places.Count > 1)
            {
                List<PlaceOption> options = places.Select(place => new PlaceOption(place.Id, place.IsRoot ? place.Name + "  (start place)" : place.Name)).ToList();
                PlaceBox.SelectionChanged -= PlaceBox_SelectionChanged;
                PlaceBox.ItemsSource = options;
                PlaceBox.SelectedItem = options.FirstOrDefault(option => option.Id == _placeId);
                PlaceBox.SelectionChanged += PlaceBox_SelectionChanged;
                PlaceBox.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "The places of this game could not be loaded: " + ex.Message);
        }
    }

    private void PlaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || PlaceBox.SelectedItem is not PlaceOption option || option.Id == _placeId)
            return;
        _placeId = option.Id;
        _privateLoaded = false;
        _ = ScanServersAsync();
    }

    private void FinishPartialScan()
    {
        DrainIncoming();
        UpdateRegions();
        Render();
        if (_tab == BrowserTab.Public)
            StatusText.Text = _all.Count == 0 ? Strings.ContextMenu_ServerBrowser_Failed : string.Empty;
    }

    private static string DescribeScan(MatchmakerServerScan scan) => scan.Status switch
    {
        MatchmakerScanStatus.SignedOut => Strings.ContextMenu_ServerBrowser_SignedOut,
        MatchmakerScanStatus.NoLocation => Strings.ContextMenu_ServerBrowser_NoLocation,
        MatchmakerScanStatus.NoServers => Strings.ContextMenu_ServerBrowser_NoServers,
        MatchmakerScanStatus.Unresolved => Strings.ContextMenu_ServerBrowser_Unresolved,
        MatchmakerScanStatus.AllBlocked => Strings.ContextMenu_ServerBrowser_AllBlocked,
        MatchmakerScanStatus.AllFar => Strings.ContextMenu_ServerBrowser_AllFar,
        _ => string.Empty
    };

    private void QueueResult(MatchmakerCandidate candidate, int generation)
    {
        // Results from a scan that was replaced, for example by picking another place, are dropped
        if (_closed || generation != Volatile.Read(ref _scanGeneration))
            return;
        _incoming.Enqueue(candidate);
        if (Interlocked.Exchange(ref _renderPending, 1) != 0)
            return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushIncoming));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _renderPending, 0);
        }
    }

    private void FlushIncoming()
    {
        Interlocked.Exchange(ref _renderPending, 0);
        if (_closed || !_busy)
            return;
        if (!DrainIncoming())
            return;
        UpdateRegions();
        Render();
        if (_tab == BrowserTab.Public && !_joinBestWhenReady)
            StatusText.Text = string.Empty;
    }

    private bool DrainIncoming()
    {
        bool added = false;
        while (_incoming.TryDequeue(out MatchmakerCandidate? candidate))
        {
            if (!_seen.Add(candidate.JobId))
                continue;
            _all.Add(CreateCard(candidate));
            added = true;
        }
        return added;
    }

    private ServerCard CreateCard(MatchmakerCandidate server)
    {
        bool blocked = _blocked.Contains(VoidstrapMatchmaker.BlockKey(server.Datacenter));
        bool current = _placeId == _gameData.PlaceId && string.Equals(server.JobId, _gameData.JobId, StringComparison.OrdinalIgnoreCase);
        return ServerCard.ForPublic(server, current, blocked);
    }

    private void UpdateRegions()
    {
        string selected = RegionBox.SelectedItem as string ?? AllRegions;
        List<string> regions = _all.Select(card => card.Region).Where(region => region.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(region => region, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (RegionBox.Items.Count == regions.Count + 1 && regions.Select((region, index) => string.Equals(RegionBox.Items[index + 1] as string, region, StringComparison.Ordinal)).All(same => same))
            return;
        RegionBox.SelectionChanged -= Filter_Changed;
        RegionBox.Items.Clear();
        RegionBox.Items.Add(AllRegions);
        foreach (string region in regions)
            RegionBox.Items.Add(region);
        RegionBox.SelectedItem = RegionBox.Items.Contains(selected) ? selected : AllRegions;
        RegionBox.SelectionChanged += Filter_Changed;
    }

    // ---- Showing cards ----

    private void Render()
    {
        if (_closed)
            return;
        List<ServerCard> cards;
        if (_tab == BrowserTab.Private)
            cards = _private;
        else if (_tab == BrowserTab.Recent)
            cards = _recent;
        else
        {
            IEnumerable<ServerCard> query = _all;
            if (RegionBox.SelectedItem is string region && region != AllRegions)
                query = query.Where(card => string.Equals(card.Region, region, StringComparison.OrdinalIgnoreCase));
            if (HideFullToggle.IsChecked == true)
                query = query.Where(card => !card.IsFull);
            query = SortBox.SelectedIndex switch
            {
                1 => query.OrderBy(card => card.IsBlocked).ThenBy(card => card.Playing).ThenBy(card => card.Ping),
                2 => query.OrderBy(card => card.IsBlocked).ThenBy(card => card.Ping),
                _ => query.OrderBy(card => card.IsBlocked).ThenByDescending(card => card.Playing).ThenBy(card => card.Ping)
            };
            cards = query.ToList();
            CountText.Text = cards.Count == 1 ? "1 server" : cards.Count.ToString(CultureInfo.CurrentCulture) + " servers";
        }
        List<ServerRow> rows = new();
        for (int i = 0; i < cards.Count; i += _columns)
            rows.Add(new ServerRow(cards.Skip(i).Take(_columns).ToList(), _columns));
        ServerList.ItemsSource = rows;
    }

    private void ServerList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Cards are at least about 260 wide, more columns as the panel gets wider
        int columns = Math.Clamp((int)((e.NewSize.Width - 8) / CardMinWidth), 1, 5);
        if (columns == _columns)
            return;
        _columns = columns;
        Render();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_closed && _started)
            Render();
    }

    // Player pictures are only fetched for cards that are actually on screen, a few at a time.
    // The list reuses card views while scrolling, so a reused view asks for its new card's pictures too.
    private void Card_Loaded(object sender, RoutedEventArgs e) => RequestAvatars((sender as FrameworkElement)?.DataContext as ServerCard);

    private void Card_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsLoaded: true })
            RequestAvatars(e.NewValue as ServerCard);
    }

    private void RequestAvatars(ServerCard? card)
    {
        if (_closed || card == null || card.AvatarsRequested || card.Tokens.Length == 0)
            return;
        card.AvatarsRequested = true;
        _avatarQueue.Add(card);
        if (!_avatarTimer.IsEnabled && !_avatarsLoading)
            _avatarTimer.Start();
    }

    private async Task LoadQueuedAvatarsAsync()
    {
        if (_closed || _avatarQueue.Count == 0)
            return;
        _avatarsLoading = true;
        List<ServerCard> cards = _avatarQueue.ToList();
        _avatarQueue.Clear();
        try
        {
            Dictionary<string, string> urls = await RobloxServers.GetHeadshotsAsync(cards.SelectMany(card => card.Tokens), _lifetime.Token);
            if (_closed)
                return;
            List<ServerCard> retry = new();
            foreach (ServerCard card in cards)
            {
                bool missing = false;
                for (int i = 0; i < card.Tokens.Length && i < card.Avatars.Count; i++)
                {
                    if (card.Avatars[i].IsMore || card.Avatars[i].Image != null)
                        continue;
                    if (urls.TryGetValue(card.Tokens[i], out string? url))
                        card.Avatars[i].Image = GetAvatarImage(url);
                    else
                        missing = true;
                }
                // Roblox answers "still being made" for some pictures, those are asked for again a little later
                if (missing && ++card.AvatarAttempts < 4)
                    retry.Add(card);
            }
            if (retry.Count > 0)
                _ = RetryAvatarsAsync(retry);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "Player pictures could not be loaded: " + ex.Message);
        }
        finally
        {
            _avatarsLoading = false;
            if (!_closed && _avatarQueue.Count > 0)
                _avatarTimer.Start();
        }
    }

    private async Task RetryAvatarsAsync(List<ServerCard> cards)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_closed)
            return;
        foreach (ServerCard card in cards)
            _avatarQueue.Add(card);
        if (!_avatarTimer.IsEnabled && !_avatarsLoading)
            _avatarTimer.Start();
    }

    private ImageSource? GetAvatarImage(string url)
    {
        if (_avatarImages.TryGetValue(url, out ImageSource? cached))
            return cached;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 72;
            image.CacheOption = BitmapCacheOption.OnLoad;
            // A picture that fails to download is forgotten so the next request tries it again
            image.DownloadFailed += (_, _) => _avatarImages.Remove(url);
            image.EndInit();
            _avatarImages[url] = image;
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Joining ----

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = ScanServersAsync();

    private void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_joining || sender is not FrameworkElement { DataContext: ServerCard card } || !card.CanJoin)
            return;
        _ = JoinAsync(card);
    }

    private async Task JoinAsync(ServerCard card)
    {
        if (_joining)
            return;
        _joining = true;
        ServerList.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusText.Text = "Joining " + (card.Region.Length > 0 ? card.Region : "the server");
        _scanCts?.Cancel();
        try
        {
            if (card.AccessCode.Length > 0)
            {
                LaunchUri($"roblox://experiences/start?placeId={_placeId}&accessCode={Uri.EscapeDataString(card.AccessCode)}");
                return;
            }
            MatchmakerCandidate server = card.Server ?? new MatchmakerCandidate { JobId = card.JobId };
            if (_matchmaker != null)
            {
                // The handoff closes the current Roblox and this window with it, so it must not be tied to the window lifetime
                if (await Task.Run(() => _matchmaker.JoinServerAsync(_placeId, server, CancellationToken.None)))
                    return;
            }
            else
            {
                LaunchUri($"roblox://experiences/start?placeId={_placeId}&gameInstanceId={Uri.EscapeDataString(server.JobId)}");
                return;
            }
            if (!_closed)
                StatusText.Text = Strings.ContextMenu_ServerBrowser_JoinFailed;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "The selected server could not be opened: " + ex.Message);
            if (!_closed)
                StatusText.Text = Strings.ContextMenu_ServerBrowser_JoinFailed;
        }
        finally
        {
            _joining = false;
            if (!_closed)
            {
                ServerList.IsEnabled = true;
                LoadingBar.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private static void LaunchUri(string uri)
    {
        string processPath = Paths.LaunchExecutable;
        ProcessStartInfo startInfo = new()
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(processPath) ?? string.Empty
        };
        startInfo.ArgumentList.Add("-player");
        startInfo.ArgumentList.Add(uri);
        Process.Start(startInfo)?.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _avatarTimer.Stop();
        // Cancelled but not disposed, scans and picture loads that are still finishing read these tokens
        _lifetime.Cancel();
        _scanCts?.Cancel();
        _scanCts = null;
        _all.Clear();
        _seen.Clear();
        _incoming.Clear();
        _avatarQueue.Clear();
        _avatarImages.Clear();
        ServerList.ItemsSource = null;
    }
}

public sealed class ServerRow
{
    public IReadOnlyList<ServerCard> Cards { get; }
    public int Columns { get; }

    public ServerRow(IReadOnlyList<ServerCard> cards, int columns)
    {
        Cards = cards;
        Columns = columns;
    }
}

public sealed class ServerCardLine
{
    public string Glyph { get; }
    public string Text { get; }

    public ServerCardLine(string glyph, string text)
    {
        Glyph = glyph;
        Text = text;
    }
}

public sealed class AvatarSlot : INotifyPropertyChanged
{
    private ImageSource? _image;

    public string Text { get; init; } = string.Empty;

    public bool IsMore => Text.Length > 0;

    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value))
                return;
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// One server card: who is in it, how full and how healthy it is, and how to get in
public sealed class ServerCard
{
    private const int MaxFaces = 5;

    public string JobId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<ServerCardLine> Lines { get; init; } = Array.Empty<ServerCardLine>();
    public IReadOnlyList<AvatarSlot> Avatars { get; init; } = Array.Empty<AvatarSlot>();
    public string[] Tokens { get; init; } = [];
    public double Fill { get; init; }
    public bool CanJoin { get; init; } = true;
    public string JoinText { get; init; } = "Join";
    public string Region { get; init; } = string.Empty;
    public int Playing { get; init; }
    public int Ping { get; init; } = int.MaxValue;
    public bool IsFull { get; init; }
    public bool IsBlocked { get; init; }
    public string AccessCode { get; init; } = string.Empty;
    public MatchmakerCandidate? Server { get; init; }
    public bool AvatarsRequested { get; set; }
    public int AvatarAttempts { get; set; }

    public static ServerCard ForPublic(MatchmakerCandidate server, bool current, bool blocked)
    {
        List<ServerCardLine> lines = new();
        if (server.Fps > 0)
            lines.Add(new ServerCardLine("", "Server performance " + Math.Round(Math.Clamp(server.Fps / 60, 0, 1) * 100).ToString(CultureInfo.CurrentCulture) + "%"));
        if (VoidstrapMatchmaker.KnownServerStart(server.JobId) is DateTimeOffset started)
            lines.Add(new ServerCardLine("", "~" + Compact(DateTimeOffset.UtcNow - started)));
        string region = DescribeRegion(server.Datacenter);
        lines.Add(new ServerCardLine("", (region.Length > 0 ? region : "Unknown location") + "  ·  ~" + server.EstimatedPingMs.ToString(CultureInfo.CurrentCulture) + " ms"));
        if (blocked)
            lines.Add(new ServerCardLine("", "Region you blocked"));
        return new ServerCard
        {
            JobId = server.JobId,
            Server = server,
            Title = server.MaxPlayers > 0 ? $"{server.Playing} of {server.MaxPlayers} people max" : $"{server.Playing} playing",
            Lines = lines,
            Avatars = Faces(server.Playing, server.PlayerTokens.Length),
            Tokens = server.PlayerTokens,
            Fill = server.MaxPlayers > 0 ? Math.Clamp((double)server.Playing / server.MaxPlayers, 0, 1) : 0,
            CanJoin = !current,
            JoinText = current ? "You are here" : "Join",
            Region = region,
            Playing = server.Playing,
            Ping = server.EstimatedPingMs,
            IsFull = server.MaxPlayers > 0 && server.Playing >= server.MaxPlayers,
            IsBlocked = blocked
        };
    }

    public static ServerCard ForPrivate(RobloxPrivateServer server)
    {
        List<ServerCardLine> lines = new();
        if (server.OwnerName.Length > 0)
            lines.Add(new ServerCardLine("", "Owned by " + server.OwnerName));
        lines.Add(new ServerCardLine("", server.MaxPlayers > 0 ? $"{server.Playing} of {server.MaxPlayers} people max" : $"{server.Playing} playing"));
        return new ServerCard
        {
            JobId = server.JobId,
            Title = server.Name.Length > 0 ? server.Name : "Private server",
            Lines = lines,
            Avatars = Faces(server.Playing, server.PlayerTokens.Length),
            Tokens = server.PlayerTokens,
            Fill = server.MaxPlayers > 0 ? Math.Clamp((double)server.Playing / server.MaxPlayers, 0, 1) : 0,
            AccessCode = server.AccessCode,
            Playing = server.Playing
        };
    }

    public static ServerCard ForRecent(ActivityData entry)
    {
        List<ServerCardLine> lines = new()
        {
            new ServerCardLine("", "Joined " + entry.TimeJoined.ToString("t", CultureInfo.CurrentCulture)
                + (entry.TimeLeft is DateTime left ? ", left " + left.ToString("t", CultureInfo.CurrentCulture) : string.Empty)),
            new ServerCardLine("", entry.JobId.Length > 13 ? entry.JobId[..13] + "…" : entry.JobId)
        };
        return new ServerCard
        {
            JobId = entry.JobId,
            Title = string.IsNullOrWhiteSpace(entry.GameName) ? "Server" : entry.GameName,
            Lines = lines,
            JoinText = "Rejoin"
        };
    }

    // Up to five faces and a "+N" bubble for everyone else
    private static IReadOnlyList<AvatarSlot> Faces(int playing, int tokens)
    {
        int people = Math.Max(playing, tokens);
        List<AvatarSlot> slots = new();
        if (people <= MaxFaces + 1)
        {
            for (int i = 0; i < people; i++)
                slots.Add(new AvatarSlot());
            return slots;
        }
        for (int i = 0; i < MaxFaces; i++)
            slots.Add(new AvatarSlot());
        slots.Add(new AvatarSlot { Text = "+" + (people - MaxFaces).ToString(CultureInfo.CurrentCulture) });
        return slots;
    }

    private static string DescribeRegion(RobloxDatacenter? dc)
    {
        if (dc == null)
            return string.Empty;
        string country = string.IsNullOrEmpty(dc.Country) ? string.Empty : VoidstrapMatchmaker.CountryToDisplayName(dc.Country);
        return string.Join(", ", new[] { dc.City, country }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Compact(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        return span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }
}

public sealed class PlaceOption
{
    public long Id { get; }
    public string Name { get; }

    public PlaceOption(long id, string name)
    {
        Id = id;
        Name = name;
    }
}
