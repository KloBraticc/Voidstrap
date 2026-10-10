using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Extensions;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Entities;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionDock : Window
{
    private const double DockHeight = 70;
    private const double ExpandedHeight = 470;
    private const double DimmerOpacity = 0.35;
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExNoActivate = 0x08000000;
    private const double DimmerFadeMs = 190;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly ActivityWatcher _activity;
    private readonly Dictionary<string, SessionPanelWindow> _panels = new();
    private readonly Action<string> _action;
    private readonly DispatcherTimer _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _trackerLease;
    private RobloxOverlayAnchor? _anchor;
    private DispatcherTimer? _dimmerFade;
    private SessionDimmer? _dimmer;
    private double _dimmerAlpha;
    private double _dimmerFrom;
    private long _dimmerFadeStarted;
    private ActivityData? _data;
    private DateTimeOffset? _serverStarted;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string _location = "Unavailable";
    private string _players = string.Empty;
    private string _serverType = string.Empty;
    private bool _opened;
    private bool _closed;
    private bool _disposed;
    private double _dimmerTarget;
    private static readonly TimeSpan FriendsInterval = TimeSpan.FromSeconds(30);
    private long _bannerUniverse;
    private DateTime _friendsLoadedUtc = DateTime.MinValue;
    private string _friendsJobId = string.Empty;
    private bool _friendsLoading;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;

    public SessionDock(ActivityWatcher activity, Action<string> action)
    {
        _activity = activity;
        _action = action;
        InitializeComponent();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClock;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        RobloxWindowTracker.Changed += OnTrackerChanged;
        _trackerLease = RobloxWindowTracker.Acquire();
    }

    public bool IsOpen => _opened && !_closed;

    // Set as soon as closing starts, showing or hiding a closing window throws
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!e.Cancel)
            _closed = true;
    }

    public void ToggleFromHotkey()
    {
        if (!_closed && App.Settings.Prop.SessionDockEnabled)
            Toggle();
    }

    public void SetGame(ActivityData data, string name, BitmapSource? image)
    {
        if (_closed)
            return;
        ActivityData? previous = _data;
        bool changed = !ReferenceEquals(previous, data);
        _data = data;
        GameTitle.Text = string.IsNullOrWhiteSpace(name) ? "Roblox" : name;
        if (!ReferenceEquals(GamePicture.Source, image))
            GamePicture.Source = image;
        GamePicture.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        if (!changed)
            return;
        if (previous != null && (previous.PlaceId != data.PlaceId || !string.Equals(previous.JobId, data.JobId, StringComparison.OrdinalIgnoreCase)))
            CloseServerPanels();
        _serverStarted = data.ServerStartedUtc;
        _location = "Unavailable";
        _players = string.Empty;
        _serverType = data.ServerType.ToConnectedString();
        _lastRefreshUtc = DateTime.MinValue;
        CancelRefresh();
        if (_opened)
            StartRefresh();
        UpdateReadout();
    }

    public void ShowTool(string key, string title, Func<Window> create)
    {
        if (_closed)
            return;
        if (!_panels.TryGetValue(key, out SessionPanelWindow? panel) || panel.IsClosed)
        {
            panel = new SessionPanelWindow(key, title, create());
            panel.Closed += OnPanelClosed;
            panel.DismissRequested += OnPanelDismissed;
            _panels[key] = panel;
        }
        panel.Present(true);
    }

    private void CloseServerPanels()
    {
        // These panels describe one specific server, so they are stale after a teleport
        foreach (string key in new[] { "server", "browser" })
        {
            if (_panels.TryGetValue(key, out SessionPanelWindow? panel))
                panel.Close();
        }
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        if (sender is not SessionPanelWindow panel)
            return;
        panel.Closed -= OnPanelClosed;
        panel.DismissRequested -= OnPanelDismissed;
        string? key = _panels.FirstOrDefault(pair => ReferenceEquals(pair.Value, panel)).Key;
        if (key != null)
            _panels.Remove(key);
    }

    private void OnPanelDismissed(object? sender, EventArgs e) => HideDock();

    public void EndSession()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(EndSession));
            return;
        }
        if (_closed)
            return;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void HideDock()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(HideDock));
            return;
        }
        if (_closed || !_opened)
            return;
        _opened = false;
        bool returnFocus = RobloxWindowTracker.IsRobloxForeground();
        _clock.Stop();
        CancelRefresh();
        _anchor?.Dispose();
        _anchor = null;
        if (IsVisible)
            Hide();
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
            panel.SetInteractive(false);
        FadeDimmer(0);
        IntPtr game = RobloxWindowTracker.Current.Hwnd;
        if (returnFocus && game != IntPtr.Zero)
            SetForegroundWindow(game);
    }

    private void Toggle()
    {
        if (_closed || !App.Settings.Prop.SessionDockEnabled)
            return;
        if (_opened)
        {
            HideDock();
            return;
        }
        if (!_activity.InGame)
            return;
        // The caller already checked that Roblox is in front, make sure the cached bounds agree before anchoring
        RobloxWindowTracker.Refresh();
        _opened = true;
        if (_refreshTask is not { IsCompleted: false } && DateTime.UtcNow - _lastRefreshUtc >= RefreshInterval)
            StartRefresh();
        UpdateReadout();
        ShowDimmer();
        _anchor ??= new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.BottomCenter);
        try
        {
            Show();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionDock", "The dock could not be shown: " + ex.Message);
            _opened = false;
            FadeDimmer(0);
            return;
        }
        _anchor.Refresh();
        _clock.Start();
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
            panel.Present(true);
        RaiseAboveDimmer();
    }

    private void OnTrackerChanged(object? sender, RobloxWindowRect bounds)
    {
        if (_closed)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnTrackerChanged(sender, bounds)));
            return;
        }
        if (_opened && (!bounds.Valid || !bounds.Foreground))
        {
            HideDock();
            return;
        }
        _dimmer?.Place(bounds);
    }

    private void ShowDimmer()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        if (_dimmer == null)
        {
            _dimmer = new SessionDimmer();
            _dimmerFade = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _dimmerFade.Tick += DimmerFade_Tick;
        }
        if (!_dimmer.IsVisible)
            _dimmer.Show(RobloxWindowTracker.Current);
        FadeDimmer(DimmerOpacity);
    }

    private void RaiseAboveDimmer()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        // The dimmer is shown last, so lift the dock and its panels back over it without moving them
        const uint flags = 0x0001 | 0x0002 | 0x0010;
        foreach (SessionPanelWindow panel in _panels.Values)
        {
            if (panel.IsVisible)
                SetWindowPos(new WindowInteropHelper(panel).Handle, HwndTopmost, 0, 0, 0, 0, flags);
        }
        IntPtr dock = new WindowInteropHelper(this).Handle;
        if (dock != IntPtr.Zero && IsVisible)
            SetWindowPos(dock, HwndTopmost, 0, 0, 0, 0, flags);
    }

    private void FadeDimmer(double target)
    {
        if (_dimmer == null || _dimmerFade == null)
            return;
        _dimmerFrom = _dimmerAlpha;
        _dimmerTarget = target;
        if (Math.Abs(_dimmerFrom - target) < 0.005)
        {
            _dimmerFade.Stop();
            SetDimmerAlpha(target);
            if (target == 0)
                ParkDimmer();
            return;
        }
        _dimmerFadeStarted = Environment.TickCount64;
        if (!_dimmerFade.IsEnabled)
            _dimmerFade.Start();
    }

    private void DimmerFade_Tick(object? sender, EventArgs e)
    {
        double progress = Math.Clamp((Environment.TickCount64 - _dimmerFadeStarted) / DimmerFadeMs, 0, 1);
        double eased = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;
        SetDimmerAlpha(_dimmerFrom + (_dimmerTarget - _dimmerFrom) * eased);
        if (progress < 1)
            return;
        _dimmerFade?.Stop();
        if (_dimmerTarget == 0)
            ParkDimmer();
    }

    private void SetDimmerAlpha(double opacity)
    {
        _dimmerAlpha = Math.Clamp(opacity, 0, 1);
        _dimmer?.SetOpacity(_dimmerAlpha);
    }

    private void ParkDimmer()
    {
        if (_dimmer == null || _opened)
            return;
        _dimmer.Hide();
    }

    private void DestroyDimmer()
    {
        if (_dimmerFade != null)
        {
            _dimmerFade.Stop();
            _dimmerFade.Tick -= DimmerFade_Tick;
            _dimmerFade = null;
        }
        _dimmer?.Dispose();
        _dimmer = null;
        _dimmerAlpha = 0;
    }

    private void StartRefresh()
    {
        if (_closed || _data == null)
            return;
        CancelRefresh();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lastRefreshUtc = DateTime.UtcNow;
        _refreshTask = RefreshServerAsync(_data, _refreshCts.Token);
    }

    private void CancelRefresh()
    {
        CancellationTokenSource? cts = _refreshCts;
        _refreshCts = null;
        _refreshTask = null;
        if (cts == null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task RefreshServerAsync(ActivityData data, CancellationToken token)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            Task<string?> location = data.QueryServerLocation(timeout.Token);
            Task<ServerStartLookup> uptime = data.ServerStartedUtc.HasValue
                ? Task.FromResult(new ServerStartLookup(ServerStartStatus.Found, data.ServerStartedUtc.Value))
                : VoidstrapMatchmaker.GetServerStartAsync(data.PlaceId, data.JobId, timeout.Token);
            Task<(int Current, int Max, int GameTotal, bool ServerFound)> players = _activity.GetServerPlayerStatsAsync();
            try
            {
                await Task.WhenAll(location, uptime, players).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionDock", "Some server details are unavailable: " + ex.Message);
            }
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(_data, data))
                return;
            if (location.IsCompletedSuccessfully)
                _location = location.Result ?? "Unavailable";
            if (uptime.IsCompletedSuccessfully && uptime.Result.Status == ServerStartStatus.Found)
                _serverStarted = uptime.Result.StartedUtc;
            if (players.IsCompletedSuccessfully)
            {
                var count = players.Result;
                _players = count.Current > 0 ? count.Max > 0 ? $"{count.Current}/{count.Max}" : count.Current.ToString(Locale.CurrentCulture) : "Unavailable";
            }
            UpdateReadout();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Server information could not be loaded: " + ex.Message);
        }
    }

    private void OnClock(object? sender, EventArgs e)
    {
        if (!_activity.InGame)
        {
            HideDock();
            return;
        }
        UpdateReadout();
        if (ServerPanel.Visibility == Visibility.Visible && _data is { } data && DateTime.UtcNow - _friendsLoadedUtc >= FriendsInterval)
            _ = RefreshFriendsQuietlyAsync(data);
    }

    private async Task RefreshFriendsQuietlyAsync(ActivityData data)
    {
        try
        {
            await LoadFriendsAsync(data);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Friends in this server could not be refreshed: " + ex.Message);
        }
    }

    private void UpdateReadout()
    {
        ActivityData? data = _data;
        if (data == null || _closed)
            return;
        _serverStarted ??= data.ServerStartedUtc;
        string uptime = _serverStarted.HasValue ? ServerInformationViewModel.FormatUptime(DateTimeOffset.UtcNow - _serverStarted.Value) : "Unavailable";
        string session = data.TimeJoined == default ? "0m" : ServerInformationViewModel.FormatUptime(DateTime.Now - data.TimeJoined);
        SetText(SessionTimes, "Session: " + session + "   Server: " + uptime);
        if (ServerPanel.Visibility != Visibility.Visible)
            return;
        if (_serverType.Length == 0)
            _serverType = data.ServerType.ToConnectedString();
        SetText(BannerTitle, GameTitle.Text);
        SetText(BannerSubtitle, _serverType);
        SetText(ServerReadout, "Location: " + _location + "\nUptime: " + uptime + "\nPlayers: " + (_players.Length == 0 ? "Unavailable" : _players));
    }

    private static void SetText(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
            block.Text = text;
    }

    private void GameCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleServerPanel();
    }

    private void ToggleServerPanel()
    {
        if (_closed)
            return;
        bool expand = ServerPanel.Visibility != Visibility.Visible;
        ServerPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        Height = expand ? ExpandedHeight : DockHeight;
        GameChevron.Text = expand ? "\uE70D" : "\uE70E";
        UpdateReadout();
        _anchor?.Refresh();
        if (expand)
            _ = LoadPopoutAsync();
    }

    // Fills the popout: the game's main thumbnail as the banner and the friends who are in this server
    private async Task LoadPopoutAsync()
    {
        ActivityData? data = _data;
        if (data == null || _closed)
            return;
        try
        {
            await Task.WhenAll(LoadBannerAsync(data), LoadFriendsAsync(data));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "The server popout could not be filled: " + ex.Message);
        }
    }

    private async Task LoadBannerAsync(ActivityData data)
    {
        long universeId = data.UniverseId;
        if (universeId <= 0)
        {
            await UniverseDetails.FetchForEntriesAsync(new[] { data }, _lifetime.Token);
            universeId = data.UniverseId > 0 ? data.UniverseId : data.UniverseDetails?.Data?.Id ?? 0;
        }
        if (universeId <= 0 || universeId == _bannerUniverse || _closed)
            return;
        string url = "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=" + universeId
            + "&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false";
        using System.Net.Http.HttpResponseMessage response = await App.HttpClient.GetAsync(url, _lifetime.Token);
        if (!response.IsSuccessStatusCode)
            return;
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 512 * 1024, _lifetime.Token));
        string? image = null;
        if (document.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("thumbnails", out System.Text.Json.JsonElement thumbnails) || thumbnails.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                foreach (System.Text.Json.JsonElement thumbnail in thumbnails.EnumerateArray())
                {
                    if (thumbnail.TryGetProperty("imageUrl", out System.Text.Json.JsonElement value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                        && value.GetString() is { } candidate && candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        image = candidate;
                        break;
                    }
                }
                if (image != null)
                    break;
            }
        }
        if (image == null || _closed)
            return;
        BitmapImage banner = new();
        banner.BeginInit();
        banner.UriSource = new Uri(image);
        banner.DecodePixelWidth = 820;
        banner.CacheOption = BitmapCacheOption.OnLoad;
        banner.EndInit();
        BannerBrush.ImageSource = banner;
        _bannerUniverse = universeId;
    }

    private async Task LoadFriendsAsync(ActivityData data)
    {
        if (_friendsLoading || _closed)
            return;
        if (string.Equals(_friendsJobId, data.JobId, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _friendsLoadedUtc < FriendsInterval)
            return;
        _friendsLoading = true;
        try
        {
            if (FriendsList.ItemsSource == null)
                FriendsStatus.Text = "Looking for friends";
            FriendsInServerResult result = await RobloxPresence.GetFriendsInServerAsync(data.UserId, data.JobId, _lifetime.Token);
            if (_closed || !ReferenceEquals(_data, data))
                return;
            _friendsJobId = data.JobId ?? string.Empty;
            _friendsLoadedUtc = DateTime.UtcNow;
            FriendCountText.Text = result.FriendCount > 0 ? "Friends: " + result.FriendCount.ToString(Locale.CurrentCulture) : string.Empty;
            List<DockFriend> friends = result.Friends.Select(friend => new DockFriend(friend)).ToList();
            FriendsList.ItemsSource = friends;
            FriendsHeader.Text = friends.Count > 0 ? $"Friends in this server ({friends.Count})" : "Friends in this server";
            FriendsStatus.Text = result.Status switch
            {
                FriendsInServerStatus.NotSignedIn => "Sign in to Roblox in Voidstrap to see friends in this server",
                FriendsInServerStatus.SignInExpired => "Your Roblox sign in expired, sign in again to see friends",
                FriendsInServerStatus.Unavailable => "Friends could not be checked right now",
                _ => friends.Count == 0 ? "None of your friends are in this server" : string.Empty
            };
            FriendsStatus.Visibility = FriendsStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _friendsLoading = false;
        }
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action })
            return;
        if (action == "server")
        {
            ToggleServerPanel();
            return;
        }
        if (action == "close")
        {
            HideDock();
            return;
        }
        try
        {
            _action(action);
            RaiseAboveDimmer();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("SessionDock::Action", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        _disposed = true;
        _closed = true;
        _opened = false;
        Closing -= OnClosing;
        RobloxWindowTracker.Changed -= OnTrackerChanged;
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
        {
            panel.Closed -= OnPanelClosed;
            panel.DismissRequested -= OnPanelDismissed;
            panel.Close();
        }
        _panels.Clear();
        _clock.Stop();
        _clock.Tick -= OnClock;
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        DestroyDimmer();
        _anchor?.Dispose();
        _anchor = null;
        CancelRefresh();
        // Cancelled but not disposed, loads that are still finishing read this token
        _lifetime.Cancel();
        _trackerLease.Dispose();
        GamePicture.Source = null;
        _data = null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        // The dock is clicked while Roblox keeps focus, so the game stays foreground and the hotkey keeps working
        nint style = GetWindowLongPtrW(handle, GwlExStyle);
        SetWindowLongPtrW(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }
}

public sealed class DockFriend
{
    public string Name { get; }
    public string Username { get; }
    public ImageSource? Avatar { get; }

    public DockFriend(ServerFriend friend)
    {
        Name = friend.Label;
        Username = string.IsNullOrWhiteSpace(friend.Username) ? friend.Label : "@" + friend.Username;
        if (!Uri.TryCreate(friend.HeadshotUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 56;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            Avatar = image;
        }
        catch (Exception)
        {
        }
    }
}
