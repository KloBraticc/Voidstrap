using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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

public partial class SessionServerBrowser : Window
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);
    private readonly ActivityWatcher _activity;
    private readonly ServerMatchmaker? _matchmaker;
    private readonly ActivityData _gameData;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ServerBrowserEntry> _all = new();
    private readonly ConcurrentQueue<MatchmakerCandidate> _incoming = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, ImageSource> _placeIcons = new();
    private CancellationTokenSource? _scanCts;
    private string? _bestJobId;
    private long _placeId;
    private bool _rescanPending;
    private int _scanGeneration;
    private int _renderPending;
    private bool _started;
    private bool _busy;
    private bool _joining;
    private bool _closed;

    public SessionServerBrowser(ActivityWatcher activity, ServerMatchmaker? matchmaker = null)
    {
        _activity = activity;
        _matchmaker = matchmaker;
        _gameData = activity.Data;
        _placeId = _gameData.PlaceId;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _ = LoadGameDetailsAsync();
        _ = ScanServersAsync();
    }

    private async Task LoadGameDetailsAsync()
    {
        ActivityData data = _gameData;
        GameNameText.Text = data.GameName;
        GameDetailsText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_Place, data.PlaceId);
        try
        {
            await UniverseDetails.FetchForEntriesAsync(new[] { data }, _lifetime.Token);
            if (_closed)
                return;
            GameNameText.Text = data.GameName;
            if (!string.IsNullOrWhiteSpace(data.UniverseDetails?.Data?.Creator?.Name))
                GameDetailsText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_GameDetails, data.UniverseDetails.Data.Creator.Name, data.PlaceId);
            string? iconUrl = data.UniverseDetails?.Thumbnail?.ImageUrl;
            if (!string.IsNullOrWhiteSpace(iconUrl) && Uri.TryCreate(iconUrl, UriKind.Absolute, out Uri? iconUri))
            {
                BitmapImage? icon = await FetchIconAsync(iconUri, _lifetime.Token);
                if (!_closed)
                    GameIcon.Source = icon;
            }
            await LoadPlacesAsync(data);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionServerBrowser", "The experience details could not be loaded: " + ex.Message);
        }
    }

    private static async Task<BitmapImage?> FetchIconAsync(Uri uri, CancellationToken token)
    {
        using HttpResponseMessage response = await App.HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxIconBytes)
            return null;
        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxIconBytes)
                return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }
        buffer.Position = 0;
        BitmapImage icon = new();
        icon.BeginInit();
        icon.CacheOption = BitmapCacheOption.OnLoad;
        icon.DecodePixelWidth = 128;
        icon.StreamSource = buffer;
        icon.EndInit();
        icon.Freeze();
        return icon;
    }

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
        ServerList.ItemsSource = null;
        LoadingBar.Visibility = Visibility.Visible;
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
                    _all.Add(CreateEntry(server));
            }
            RenderServers();
            StatusText.Text = DescribeScan(scan);
            CountText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_Count, _all.Count);
            if (scan.Listed > 0)
                ScanSummaryText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_ProbeSummary, _all.Count, scan.Listed);
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
            long universeId = data.UniverseId > 0 ? data.UniverseId : data.UniverseDetails?.Data?.Id ?? 0;
            long rootPlaceId = data.UniverseDetails?.Data?.RootPlaceId ?? 0;
            List<RobloxPlace> places = universeId > 0
                ? await RobloxPlaces.GetUniversePlacesAsync(universeId, rootPlaceId, _lifetime.Token)
                : new List<RobloxPlace>();
            if (_closed)
                return;
            if (!places.Any(place => place.Id == data.PlaceId))
                places.Insert(0, new RobloxPlace { Id = data.PlaceId, Name = data.GameName, IsRoot = data.PlaceId == rootPlaceId });

            Dictionary<long, string> urls = await RobloxPlaces.GetPlaceIconUrlsAsync(places.Select(place => place.Id), _lifetime.Token);
            if (_closed)
                return;
            foreach ((long id, string url) in urls)
            {
                try
                {
                    BitmapImage image = new();
                    image.BeginInit();
                    image.UriSource = new Uri(url);
                    image.DecodePixelWidth = 88;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    _placeIcons[id] = image;
                }
                catch (Exception)
                {
                }
            }
            if (places.Count > 1)
            {
                List<PlaceOption> options = places.Select(place => new PlaceOption(place.Id, place.IsRoot ? place.Name + "  (start place)" : place.Name)).ToList();
                PlaceBox.SelectionChanged -= PlaceBox_SelectionChanged;
                PlaceBox.ItemsSource = options;
                PlaceBox.SelectedItem = options.FirstOrDefault(option => option.Id == _placeId);
                PlaceBox.SelectionChanged += PlaceBox_SelectionChanged;
                PlaceBox.Visibility = Visibility.Visible;
            }
            RenderServers();
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
        _ = ScanServersAsync();
    }

    private void FinishPartialScan()
    {
        DrainIncoming();
        RenderServers();
        StatusText.Text = _all.Count == 0 ? Strings.ContextMenu_ServerBrowser_Failed : string.Empty;
        CountText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_Count, _all.Count);
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
        RenderServers();
        StatusText.Text = string.Empty;
        CountText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_Probing, _all.Count);
    }

    private bool DrainIncoming()
    {
        bool added = false;
        while (_incoming.TryDequeue(out MatchmakerCandidate? candidate))
        {
            if (!_seen.Add(candidate.JobId))
                continue;
            _all.Add(CreateEntry(candidate));
            added = true;
        }
        return added;
    }

    private ServerBrowserEntry CreateEntry(MatchmakerCandidate server)
    {
        bool blocked = _blocked.Contains(VoidstrapMatchmaker.BlockKey(server.Datacenter));
        return new ServerBrowserEntry(
            server,
            _placeId == _gameData.PlaceId && string.Equals(server.JobId, _gameData.JobId, StringComparison.OrdinalIgnoreCase),
            string.Equals(server.JobId, _bestJobId, StringComparison.OrdinalIgnoreCase),
            blocked);
    }

    private void RenderServers()
    {
        if (_closed)
            return;
        IEnumerable<ServerBrowserEntry> query = _all;
        string search = SearchBox.Text.Trim();
        if (search.Length > 0)
            query = query.Where(server => server.Matches(search));
        query = SortBox.SelectedIndex switch
        {
            1 => query.OrderBy(server => server.IsBlocked).ThenBy(server => server.Server.EstimatedPingMs).ThenBy(server => server.Server.Score),
            2 => query.OrderBy(server => server.IsBlocked).ThenByDescending(server => server.Server.Playing).ThenBy(server => server.Server.Score),
            3 => query.OrderBy(server => server.IsBlocked).ThenBy(server => server.Server.Playing).ThenBy(server => server.Server.Score),
            _ => query.OrderByDescending(server => server.IsBest).ThenBy(server => server.IsBlocked).ThenBy(server => server.Server.Score)
        };
        List<ServerBrowserEntry> items = query.ToList();
        ImageSource? icon = _placeIcons.TryGetValue(_placeId, out ImageSource? found) ? found : GameIcon.Source;
        foreach (ServerBrowserEntry entry in items)
            entry.PlaceIcon = icon;
        ServerList.ItemsSource = items;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ScanServersAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_closed && _started)
            RenderServers();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_closed && _started)
            RenderServers();
    }

    private void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_joining || sender is not Button { DataContext: ServerBrowserEntry entry } || !entry.CanJoin)
            return;
        _ = JoinAsync(entry);
    }

    private async Task JoinAsync(ServerBrowserEntry entry)
    {
        _joining = true;
        ServerList.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusText.Text = string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_Joining, entry.LocationText);
        _scanCts?.Cancel();
        try
        {
            if (_matchmaker != null)
            {
                // The handoff closes the current Roblox and this window with it, so it must not be tied to the window lifetime
                if (await Task.Run(() => _matchmaker.JoinServerAsync(_placeId, entry.Server, CancellationToken.None)))
                    return;
            }
            else
            {
                LaunchDirect(entry.Server.JobId);
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

    private void LaunchDirect(string jobId)
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
        startInfo.ArgumentList.Add($"roblox://experiences/start?placeId={_placeId}&gameInstanceId={Uri.EscapeDataString(jobId)}");
        Process.Start(startInfo)?.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        // Cancelled but not disposed, scans and icon loads that are still finishing read these tokens
        _lifetime.Cancel();
        _scanCts?.Cancel();
        _scanCts = null;
        _all.Clear();
        _seen.Clear();
        _incoming.Clear();
        ServerList.ItemsSource = null;
        GameIcon.Source = null;
    }
}

public sealed class ServerBrowserEntry
{
    public MatchmakerCandidate Server { get; }
    public bool IsCurrent { get; }
    public bool IsBest { get; }
    public bool IsBlocked { get; }
    public bool CanJoin => !IsCurrent;
    public ImageSource? PlaceIcon { get; set; }
    public string Id => Server.JobId;
    public string LocationText { get; }
    public string CapacityText => Server.MaxPlayers > 0
        ? string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_Capacity, Server.Playing, Server.MaxPlayers)
        : string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_CapacityUnknown, Server.Playing);
    public string MetricsText => string.Format(Locale.CurrentCulture, Strings.ContextMenu_ServerBrowser_EstimatedPing, Server.EstimatedPingMs) + "  •  " + CapacityText;
    public string AddressText => string.IsNullOrEmpty(Server.MachineAddress) ? Server.JobId : Server.MachineAddress + "  •  " + Server.JobId;

    public ServerBrowserEntry(MatchmakerCandidate server, bool isCurrent, bool isBest, bool isBlocked)
    {
        Server = server;
        IsCurrent = isCurrent;
        IsBest = isBest;
        IsBlocked = isBlocked;
        RobloxDatacenter? dc = server.Datacenter;
        if (dc == null)
            LocationText = Strings.ContextMenu_ServerBrowser_Unknown;
        else
        {
            string country = string.IsNullOrEmpty(dc.Country) ? string.Empty : VoidstrapMatchmaker.CountryToDisplayName(dc.Country);
            string region = string.IsNullOrEmpty(dc.Region) || string.Equals(dc.Region, dc.City, StringComparison.OrdinalIgnoreCase) ? string.Empty : dc.Region;
            LocationText = string.Join(", ", new[] { dc.City, region, country }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public bool Matches(string search) =>
        Server.JobId.Contains(search, StringComparison.OrdinalIgnoreCase)
        || LocationText.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Server.Datacenter?.Country?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || Server.MachineAddress.Contains(search, StringComparison.OrdinalIgnoreCase);
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
