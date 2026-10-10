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
using System.Windows.Media.Animation;
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
    private const double PopoutHeight = 388;
    private const double PopoutGap = 12;
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
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(2400);
    private static readonly AnimationTimeline RingGrow = Frozen(new DoubleAnimation(1, 2.6, TimeSpan.FromMilliseconds(900)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    private static readonly AnimationTimeline RingFade = Frozen(new DoubleAnimation(0.45, 0, TimeSpan.FromMilliseconds(900)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    private static readonly AnimationTimeline DotBeat = Frozen(CreateDotBeat());
    private DispatcherTimer? _heartbeat;
    private static readonly AnimationTimeline DockFadeIn = Smooth(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    private static readonly AnimationTimeline DockRise = Smooth(new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    // Set when the dock was hidden only because the game lost focus, so it comes back with the game
    private bool _resumeOnFocus;
    private bool _resumePopout;
    private bool _resumeQueued;
    private Window? _popout;
    private IntPtr _popoutHandle;
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
        DetachServerPanel();
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
        _resumeOnFocus = false;
        _resumePopout = false;
        if (_closed || !_opened)
            return;
        _opened = false;
        bool returnFocus = RobloxWindowTracker.IsRobloxForeground();
        _clock.Stop();
        StopHeartbeat();
        CancelRefresh();
        _anchor?.Dispose();
        _anchor = null;
        HidePopout();
        if (IsVisible)
            Hide();
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
            panel.SetInteractive(false);
        FadeDimmer(0);
        IntPtr game = RobloxWindowTracker.Current.Hwnd;
        if (returnFocus && game != IntPtr.Zero)
            SetForegroundWindow(game);
    }

    private void ResumeDock()
    {
        _resumeQueued = false;
        if (_closed || _opened || !_resumeOnFocus)
            return;
        bool popout = _resumePopout;
        if (!RobloxWindowTracker.IsRobloxForeground())
            return;
        _resumeOnFocus = false;
        _resumePopout = false;
        if (!App.Settings.Prop.SessionDockEnabled || !_activity.InGame)
            return;
        // The player just clicked back into the game, so nothing takes the keyboard away from it
        Open(activatePanels: false);
        if (popout && _opened)
            ShowPopout();
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
        Open();
    }

    private void Open(bool activatePanels = true)
    {
        _resumeOnFocus = false;
        _resumePopout = false;
        // The caller already checked that Roblox is in front, make sure the cached bounds agree before anchoring
        RobloxWindowTracker.Refresh();
        _opened = true;
        if (_refreshTask is not { IsCompleted: false } && DateTime.UtcNow - _lastRefreshUtc >= RefreshInterval)
            StartRefresh();
        UpdateReadout();
        ShowDimmer();
        _anchor ??= new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.BottomCenter);
        // Started before showing so the very first frame is already the start of the fade
        PlayEntrance();
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
        StartHeartbeat();
        // Start loading the popout's banner and friends now so they are ready when it is opened
        _ = LoadPopoutAsync();
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
            panel.Present(true, activatePanels);
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
            bool popout = PopoutOpen;
            HideDock();
            _resumeOnFocus = true;
            _resumePopout = popout;
            return;
        }
        if (!_opened && _resumeOnFocus && bounds.Valid && bounds.Foreground && !_resumeQueued)
        {
            // Not from inside the tracker's own event, opening refreshes the tracker
            _resumeQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ResumeDock));
        }
        _dimmer?.Place(bounds);
        if (PopoutOpen)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PlacePopout));
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
        if (PopoutOpen && _popoutHandle != IntPtr.Zero)
            SetWindowPos(_popoutHandle, HwndTopmost, 0, 0, 0, 0, flags);
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
        if (PopoutOpen && _data is { } data && DateTime.UtcNow - _friendsLoadedUtc >= FriendsInterval)
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
        if (!PopoutOpen)
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

    // Two quick beats, like a pulse: up, back, a smaller second beat, then rest until the next tick
    private static DoubleAnimationUsingKeyFrames CreateDotBeat()
    {
        DoubleAnimationUsingKeyFrames beat = new() { Duration = TimeSpan.FromMilliseconds(520) };
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.28, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        return beat;
    }

    // Fades in while rising a few pixels, the dimmer behind it fades at the same time
    private void PlayEntrance()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            DockRoot.BeginAnimation(OpacityProperty, null);
            DockShift.BeginAnimation(TranslateTransform.YProperty, null);
            DockRoot.Opacity = 1;
            DockShift.Y = 0;
            return;
        }
        DockRoot.BeginAnimation(OpacityProperty, DockFadeIn);
        DockShift.BeginAnimation(TranslateTransform.YProperty, DockRise);
    }

    private static AnimationTimeline Smooth(AnimationTimeline timeline)
    {
        Timeline.SetDesiredFrameRate(timeline, 60);
        timeline.Freeze();
        return timeline;
    }

    private static AnimationTimeline Frozen(AnimationTimeline timeline)
    {
        // A low frame rate is plenty for a 10 pixel dot and keeps the cost of each beat tiny
        Timeline.SetDesiredFrameRate(timeline, 30);
        timeline.Freeze();
        return timeline;
    }

    private void StartHeartbeat()
    {
        if (_closed || !SystemParameters.ClientAreaAnimation)
            return;
        if (_heartbeat == null)
        {
            _heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = HeartbeatInterval };
            _heartbeat.Tick += OnHeartbeat;
        }
        _heartbeat.Start();
        OnHeartbeat(null, EventArgs.Empty);
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Stop();
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PulseDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PulseRing.Opacity = 0;
    }

    // Between beats nothing animates and nothing is redrawn; each beat lasts under a second
    private void OnHeartbeat(object? sender, EventArgs e)
    {
        if (_closed || !IsVisible)
            return;
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, RingGrow);
        PulseRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, RingGrow);
        PulseRing.BeginAnimation(OpacityProperty, RingFade);
        PulseDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, DotBeat);
        PulseDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, DotBeat);
    }

    private bool PopoutOpen => _popout is { IsVisible: true };

    // The popout lives in its own opaque window above the dock. Drawn inside the dock it made the dock a large
    // transparent window that Windows renders in software, which made every hover and update stutter.
    private void DetachServerPanel()
    {
        if (ServerPanel.Parent is Panel parent)
            parent.Children.Remove(ServerPanel);
        ServerPanel.Margin = new Thickness(0);
        ServerPanel.BorderThickness = new Thickness(0);
        ServerPanel.CornerRadius = new CornerRadius(0);
        ServerPanel.Visibility = Visibility.Visible;
        if (ServerPanel.Child is Panel content)
        {
            foreach (UIElement child in content.Children)
            {
                if (child is Border layer)
                    layer.CornerRadius = new CornerRadius(0);
            }
        }
    }

    private void ToggleServerPanel()
    {
        if (_closed)
            return;
        if (PopoutOpen)
        {
            HidePopout();
            return;
        }
        ShowPopout();
    }

    private void ShowPopout()
    {
        if (_closed || !_opened || !Voidstrap.Utility.Platform.IsWindows)
            return;
        if (_popout == null)
        {
            _popout = new Window
            {
                Title = "Session server",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = ActualWidth > 0 ? ActualWidth : Width,
                Height = PopoutHeight,
                Content = ServerPanel,
                FontFamily = FontFamily
            };
            _popout.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            _popout.SourceInitialized += OnPopoutSourceInitialized;
        }
        GameChevron.Text = "\uE70D";
        UpdateReadout();
        try
        {
            _popout.Show();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionDock", "The server popout could not be shown: " + ex.Message);
            return;
        }
        PlacePopout();
        RaiseAboveDimmer();
        UpdateReadout();
        _ = LoadPopoutAsync();
    }

    private void HidePopout()
    {
        GameChevron.Text = "\uE70E";
        if (_popout is { IsVisible: true } popout)
            popout.Hide();
    }

    private void OnPopoutSourceInitialized(object? sender, EventArgs e)
    {
        if (_popout == null)
            return;
        _popoutHandle = new WindowInteropHelper(_popout).Handle;
        if (_popoutHandle == IntPtr.Zero)
            return;
        nint style = GetWindowLongPtrW(_popoutHandle, GwlExStyle);
        SetWindowLongPtrW(_popoutHandle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        int round = 2;
        _ = DwmSetWindowAttribute(_popoutHandle, 33, ref round, sizeof(int));
        OverlayDiagnostics.RegisterOverlayHandle(_popoutHandle);
    }

    // Sits directly above the dock with the same width, following it when the game window moves
    private void PlacePopout()
    {
        if (!PopoutOpen || _popoutHandle == IntPtr.Zero)
            return;
        IntPtr dock = new WindowInteropHelper(this).Handle;
        if (dock == IntPtr.Zero || !GetWindowRect(dock, out NativeRect rect))
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int height = (int)Math.Ceiling(PopoutHeight * dpi.DpiScaleY);
        int gap = (int)Math.Round(PopoutGap * dpi.DpiScaleY);
        SetWindowPos(_popoutHandle, HwndTopmost, rect.Left, rect.Top - gap - height, rect.Right - rect.Left, height, 0x0010 | 0x0040);
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
        if (_heartbeat != null)
        {
            _heartbeat.Stop();
            _heartbeat.Tick -= OnHeartbeat;
            _heartbeat = null;
        }
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        DestroyDimmer();
        if (_popout != null)
        {
            _popout.SourceInitialized -= OnPopoutSourceInitialized;
            OverlayDiagnostics.UnregisterOverlayHandle(_popoutHandle);
            _popout.Content = null;
            try
            {
                _popout.Close();
            }
            catch (InvalidOperationException)
            {
            }
            _popout = null;
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
