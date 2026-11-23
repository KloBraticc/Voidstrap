using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Entities;
using Voidstrap.Models.Persistable;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionDock : Window
{
    private const double DockHeight = 70;
    private const double PopoutHeight = 388;
    private const double PopoutGap = 12;
    private static readonly double DimmerOpacity = Voidstrap.Utility.Platform.IsWindows ? 0.45 : 0.22;
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
    private readonly CancellationToken _lifetimeToken;
    private readonly IDisposable _trackerLease;
    private RobloxOverlayAnchor? _anchor;
    private DispatcherTimer? _dimmerFade;
    private SessionDimmer? _dimmer;
    private double _dimmerAlpha;
    private double _dimmerFrom;
    private long _dimmerFadeStarted;
    private readonly TranslateTransform _linuxDimmerFade = new();
    private int _linuxDimmerGeneration;
    private ActivityData? _data;
    private DateTimeOffset? _serverStarted;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string _location = Strings.OverlayDiagnostics_Unavailable;
    private string _players = string.Empty;
    private string _serverType = string.Empty;
    private bool _opened;
    private bool _closed;
    private bool _fitWidthQueued;
    private bool _disposed;
    private double _dimmerTarget;
    private static readonly TimeSpan FriendsInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(2400);
    private static readonly AnimationTimeline RingGrow = Frozen(
        new DoubleAnimation(1, 2.6, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }
    );
    private static readonly AnimationTimeline RingFade = Frozen(
        new DoubleAnimation(0.45, 0, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }
    );
    private static readonly AnimationTimeline DotBeat = Frozen(CreateDotBeat());
    private DispatcherTimer? _heartbeat;

    // Far enough that the pill and its shadow start fully below the window's bottom edge
    private static readonly TimeSpan EntranceLength = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ExitLength = TimeSpan.FromMilliseconds(220);

    // Room around the pill for its shadow, the window is the pill plus this
    private const double ShadowRoomX = 40;

    private const double MaxDockWidth = 1180;
    private int _availableWidth;
    private OverlayDockSide? _appliedSide;
    private const double DockWindowThickness = 96;
    private const double SidePopoutWidth = 900;
    private readonly NativeSlide _slide;
    private readonly TranslateTransform _linuxDockMotion = new();
    private int _linuxMotionGeneration;
    private bool _linuxMotionActive;
    private Point _linuxMotionOrigin;
    private RobloxWindowRect _linuxMotionBounds;
    private int _linuxMotionTop = int.MinValue;
    private DispatcherTimer? _linuxMotionDeadline;
    private Action? _linuxMotionFinished;
    private bool _exiting;
    private static readonly TimeSpan FocusLossGrace = TimeSpan.FromMilliseconds(160);
    private DispatcherTimer? _focusLoss;
    private bool _activateOnSettle;
    private bool _popoutOnSettle;
    private bool _pinnedRestored;
    private bool _prewarming;
    private bool _prewarmLogged;
    private int _prewarmIndex;
    private DispatcherTimer? _prewarmTimer;

    private bool _fitAnchorPending;
    public static SessionDock? ActiveInstance { get; private set; }
    private bool _screenDragDimOverride;
    private static readonly string[] PrewarmActions =
    {
        "browser",
        "games",
        "chat",
        "layout",
        "adjustments",
        "details",
        "history",
        "music",
        "clips",
    };
    private bool _profileLoaded;
    private bool _profileLoading;

    private enum PopoutView
    {
        Server,
        Profile,
        Robux,
    }

    private sealed record RobuxPurchaseRow(string Name, string Subtitle, string AmountText);

    private PopoutView _popoutView = PopoutView.Server;
    private long _selfUserId;
    private DispatcherTimer? _robuxTimer;
    private bool _robuxRefreshing;
    private bool _robuxPurchasesLoading;
    private (long UniverseId, long PlaceId) _robuxPurchasesCacheKey;
    private DateTime _robuxPurchasesUtc = DateTime.MinValue;
    private IReadOnlyList<RobloxPurchase>? _robuxPurchasesCache;
    private RobloxProfileInfo? _profileInfo;
    private DateTime _profileInfoUtc = DateTime.MinValue;
    private bool _profileInfoLoading;
    private bool _restoringPinned;
    private static readonly Dictionary<string, string> PanelActions = new(StringComparer.Ordinal)
    {
        ["server"] = "details",
        ["notifications"] = "adjustments",
        ["layout"] = "layout",
        ["browser"] = "browser",
        ["games"] = "games",
        ["chat"] = "chat",
        ["history"] = "history",
        ["music"] = "music",
        ["reviews"] = "reviews",
        ["web"] = "web",
        ["clips"] = "clips",
    };
    internal bool IsAnimating => _slide.IsRunning || _linuxMotionActive;

    private bool _resumeOnFocus;
    private bool _resumePopout;
    private bool _resumeQueued;
    private Window? _popout;
    private IntPtr _popoutHandle;
    private long _bannerUniverse;
    private DateTime _friendsLoadedUtc = DateTime.MinValue;
    private bool _friendsRefreshing;
    internal const string HubKey = "hub";
    private string _friendsJobId = string.Empty;
    private bool _friendsLoading;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;

    public SessionDock(ActivityWatcher activity, Action<string> action)
    {
        ActiveInstance = this;
        _lifetimeToken = _lifetime.Token;
        _activity = activity;
        _action = action;
        InitializeComponent();
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            DockRoot.Margin = new Thickness(0);
            DockRoot.RowDefinitions[0].Height = new GridLength(0);
            DockRoot.RowDefinitions[1].Height = new GridLength(DockHeight);
            DockShadow1.Visibility = Visibility.Collapsed;
            DockShadow2.Visibility = Visibility.Collapsed;
            DockShadow3.Visibility = Visibility.Collapsed;
            DockShadow4.Visibility = Visibility.Collapsed;
            DockShadow5.Visibility = Visibility.Collapsed;
            if (Pill.Parent is FrameworkElement container)
                container.HorizontalAlignment = HorizontalAlignment.Stretch;
            Pill.Height = DockHeight;
        }
        PortableOverlay.Prepare(this);
        PreviewKeyDown += OnDockKey;
        _slide = new NativeSlide(this);
        // The browser is built on Edge WebView2, which only exists on Windows
        if (!Voidstrap.Utility.Platform.IsWindows)
            WebButton.Visibility = Visibility.Collapsed;
        ClipsButton.Visibility = App.Settings.Prop.ClipperEnabled ? Visibility.Visible : Visibility.Collapsed;
        InitializeDockActionLayout();
        ApplyQuickToggleVisibility();
        Voidstrap.Integrations.Clipper.ClipperService.StateChanged += OnClipperStateChanged;
        FitWidth();
        DetachServerPanel();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClock;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnDockLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        LocationChanged += OnDockMoved;
        Pill.SizeChanged += OnPillSizeChanged;
        ProfileCard.SizeChanged += OnPillSizeChanged;
        GameCard.SizeChanged += OnPillSizeChanged;
        ActionButtons.SizeChanged += OnPillSizeChanged;
        TrailingButtons.SizeChanged += OnPillSizeChanged;
        RobloxWindowTracker.Changed += OnTrackerChanged;
        _trackerLease = RobloxWindowTracker.Acquire();
    }

    private void OnClipperStateChanged()
    {
        if (_closed || Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(new Action(UpdateClipperVisibility));
    }

    public void UpdateClipperVisibility()
    {
        if (_closed)
            return;
        bool enabled = App.Settings.Prop.ClipperEnabled && Voidstrap.Integrations.Clipper.ClipperService.Supported;
        RefreshDockActionVisibilityLive("clips");
        if (!enabled && _panels.TryGetValue("clips", out SessionPanelWindow? panel) && !panel.IsClosed)
        {
            panel.Close();
        }
    }

    private void OnDockKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        e.Handled = true;
        CloseDock();
    }

    public bool IsOpen => _opened && !_closed;

    public bool ShowsNowPlaying => IsOpen && NowPlayingPopup.IsOpen;

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
        QueueFitWidth();
        if (!changed)
            return;
        if (
            previous != null
            && (previous.PlaceId != data.PlaceId || !string.Equals(previous.JobId, data.JobId, StringComparison.OrdinalIgnoreCase))
        )
            CloseServerPanels();
        _serverStarted = data.ServerStartedUtc;
        _location = Strings.OverlayDiagnostics_Unavailable;
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
        if (_prewarming)
        {
            panel.Prepare();
            return;
        }
        // Panels brought back because they were pinned must not take the keyboard from the game
        panel.Present(true, !_restoringPinned);
        UpdateActiveButtons();
        if (key != HubKey)
            UpdateHub();
    }

    private void UpdateHub()
    {
        if (_closed || !_opened || _prewarming)
            return;
        bool otherOpen = _panels.Any(pair => pair.Key != HubKey && pair.Value.IsRequested);
        bool hubOpen = _panels.TryGetValue(HubKey, out SessionPanelWindow? hub) && hub.IsRequested;
        if (otherOpen && hubOpen)
            CloseTool(HubKey);
        else if (!otherOpen && !hubOpen && App.Settings.Prop.ShowDockNews)
            _action(HubKey);
    }

    public void CloseTool(string key)
    {
        if (_panels.TryGetValue(key, out SessionPanelWindow? panel) && !panel.IsClosed)
            panel.Close();
    }

    private void CloseServerPanels()
    {
        // These panels describe one specific server, so they are stale after a teleport
        foreach (string key in new[] { "server", "browser", "reviews", HubKey })
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
        if (key == "web" && panel.IsPinned)
            _pinnedRestored = false;
        UpdateActiveButtons();
        if (key != HubKey)
            UpdateHub();
    }

    private void OnPanelDismissed(object? sender, EventArgs e) => CloseDock();

    private static string PanelKeyFor(string action) =>
        action switch
        {
            "details" => "server",
            "adjustments" => "notifications",
            _ => action,
        };

    // A dock button whose panel is open is filled with the accent colour, like a selected tab
    private readonly DispatcherTimer _nowPlayingTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };

    private SpotifyDesktopTrack? _nowPlaying;

    private string? _nowPlayingTitle;

    private bool _nowPlayingPolling;

    private readonly DispatcherTimer _islandClock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    private bool _islandExpanded;

    private bool _islandMeterStarted;

    private const double IslandExpandedWidth = 372;

    private const double IslandExpandedHeight = 172;

    private const double IslandCompactHeight = 37;

    private const double IslandExpandedRadius = 38;

    private static readonly Duration IslandMorph = new(TimeSpan.FromMilliseconds(560));

    public void RefreshNowPlaying()
    {
        if (_closed)
            return;
        bool wanted = _opened && App.Settings.Prop.SessionDockSpotify;
        if (wanted && !_nowPlayingTimer.IsEnabled)
        {
            _nowPlayingTimer.Tick -= OnNowPlayingTick;
            _nowPlayingTimer.Tick += OnNowPlayingTick;
            _nowPlayingTimer.Start();
            OnNowPlayingTick(null, EventArgs.Empty);
        }
        else if (!wanted && _nowPlayingTimer.IsEnabled)
        {
            _nowPlayingTimer.Stop();
            _nowPlayingTimer.Tick -= OnNowPlayingTick;
            _nowPlaying = null;
        }
        UpdateNowPlayingVisibility();
    }

    private async void OnNowPlayingTick(object? sender, EventArgs e)
    {
        if (_nowPlayingPolling || _closed)
            return;
        _nowPlayingPolling = true;
        SpotifyDesktopTrack? track = await SpotifyNowPlaying.ReadAsync(_nowPlayingTitle);
        _nowPlayingPolling = false;
        if (_closed || !_nowPlayingTimer.IsEnabled)
            return;
        _nowPlaying = track != null && (track.Playing || _islandExpanded) ? track : null;
        if (_nowPlaying != null)
        {
            if (_nowPlaying.Title != _nowPlayingTitle)
            {
                NowPlayingCover.Source = null;
                NowPlayingCoverLarge.Source = null;
            }
            _nowPlayingTitle = _nowPlaying.Title;
            NowPlayingTitle.Text = _nowPlaying.Title;
            NowPlayingArtist.Text = _nowPlaying.Artist;
            NowPlayingArtist.Visibility = _nowPlaying.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            IslandTitle.Text = _nowPlaying.Title;
            IslandArtist.Text = _nowPlaying.Artist;
            if (_nowPlaying.Cover != null)
            {
                System.Windows.Media.Imaging.BitmapImage? cover = LoadNowPlayingCover(_nowPlaying.Cover);
                NowPlayingCover.Source = cover;
                NowPlayingCoverLarge.Source = cover;
            }
        }
        UpdateNowPlayingVisibility();
        if (_nowPlaying != null && NowPlayingPopup.IsOpen)
        {
            ApplyIslandPlayState();
            if (!_islandExpanded)
                AnimateIsland(NowPlayingIsland, WidthProperty, CompactIslandWidth());
            UpdateIslandClock();
        }
    }

    private double CompactIslandWidth()
    {
        IslandCompact.Measure(new Size(double.PositiveInfinity, IslandCompactHeight));
        return Math.Ceiling(IslandCompact.DesiredSize.Width);
    }

    private static void AnimateIsland(IAnimatable element, DependencyProperty property, double to)
    {
        DoubleAnimation animation = new(to, IslandMorph) { EasingFunction = new Voidstrap.UI.SpringEase() };
        element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void FadeIsland(UIElement element, double to, int milliseconds, int delay)
    {
        DoubleAnimation animation = new(to, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void MorphIsland(bool expand)
    {
        if (_closed)
            return;
        _islandExpanded = expand;
        double width = expand ? IslandExpandedWidth : CompactIslandWidth();
        double height = expand ? IslandExpandedHeight : IslandCompactHeight;
        double radius = expand ? IslandExpandedRadius : IslandCompactHeight / 2;
        AnimateIsland(NowPlayingIsland, WidthProperty, width);
        AnimateIsland(NowPlayingIsland, HeightProperty, height);
        AnimateIsland(IslandShape, System.Windows.Shapes.Rectangle.RadiusXProperty, radius);
        AnimateIsland(IslandShape, System.Windows.Shapes.Rectangle.RadiusYProperty, radius);
        if (expand)
        {
            IslandExpanded.Visibility = Visibility.Visible;
            IslandCompact.IsHitTestVisible = false;
            FadeIsland(IslandCompact, 0, 120, 0);
            FadeIsland(IslandExpanded, 1, 220, 120);
            UpdateIslandClock();
            _islandClock.Tick -= OnIslandClockTick;
            _islandClock.Tick += OnIslandClockTick;
            _islandClock.Start();
        }
        else
        {
            IslandCompact.IsHitTestVisible = true;
            FadeIsland(IslandExpanded, 0, 120, 0);
            FadeIsland(IslandCompact, 1, 200, 140);
            _islandClock.Stop();
            _islandClock.Tick -= OnIslandClockTick;
            if (_nowPlaying is { Playing: false })
                OnNowPlayingTick(null, EventArgs.Empty);
        }
    }

    private void ResetIsland()
    {
        _islandExpanded = false;
        _islandClock.Stop();
        _islandClock.Tick -= OnIslandClockTick;
        if (_robuxTimer != null)
        {
            _robuxTimer.Stop();
            _robuxTimer.Tick -= OnRobuxTimerTick;
            _robuxTimer = null;
        }
        NowPlayingIsland.BeginAnimation(WidthProperty, null);
        NowPlayingIsland.BeginAnimation(HeightProperty, null);
        IslandShape.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusXProperty, null);
        IslandShape.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusYProperty, null);
        IslandCompact.BeginAnimation(OpacityProperty, null);
        IslandExpanded.BeginAnimation(OpacityProperty, null);
        NowPlayingIsland.Height = IslandCompactHeight;
        IslandShape.RadiusX = IslandCompactHeight / 2;
        IslandShape.RadiusY = IslandCompactHeight / 2;
        IslandCompact.Opacity = 1;
        IslandCompact.IsHitTestVisible = true;
        IslandExpanded.Opacity = 0;
        IslandExpanded.Visibility = Visibility.Collapsed;
        _islandPressAt = null;
        _islandDragging = false;
        IslandPull.BeginAnimation(TranslateTransform.XProperty, null);
        IslandPull.BeginAnimation(TranslateTransform.YProperty, null);
        IslandPull.X = 0;
        IslandPull.Y = 0;
        if (_islandMeterStarted && IslandStage.Resources["IslandMeter"] is Storyboard meter)
            meter.Stop(IslandStage);
        _islandMeterStarted = false;
    }

    private void IslandAppear()
    {
        ResetIsland();
        NowPlayingIsland.Width = CompactIslandWidth();
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, IslandMorph) { EasingFunction = new Voidstrap.UI.SpringEase() });
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, IslandMorph) { EasingFunction = new Voidstrap.UI.SpringEase() });
        NowPlayingIsland.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));
        ApplyIslandPlayState();
    }

    private void ApplyIslandPlayState()
    {
        bool playing = _nowPlaying?.Playing == true;
        IslandToggleGlyph.Text = playing ? "\uE769" : "\uE768";
        if (IslandStage.Resources["IslandMeter"] is not Storyboard meter)
            return;
        if (!_islandMeterStarted)
        {
            meter.Begin(IslandStage, true);
            _islandMeterStarted = true;
        }
        if (playing)
            meter.Resume(IslandStage);
        else
            meter.Pause(IslandStage);
    }

    private void OnIslandClockTick(object? sender, EventArgs e) => UpdateIslandClock();

    private void UpdateIslandClock()
    {
        if (_nowPlaying == null)
            return;
        TimeSpan duration = _nowPlaying.Duration;
        TimeSpan position = _nowPlaying.PositionNow;
        IslandElapsed.Text = FormatIslandTime(position);
        IslandRemaining.Text = duration > TimeSpan.Zero ? FormatIslandTime(duration) : string.Empty;
        IslandProgress.ScaleX = duration > TimeSpan.Zero ? Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds, 0, 1) : 0;
    }

    private static string FormatIslandTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + time.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + time.Seconds.ToString("00", CultureInfo.InvariantCulture)
            : ((int)time.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + time.Seconds.ToString("00", CultureInfo.InvariantCulture);

    private void NowPlayingIsland_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_islandExpanded && _nowPlaying != null)
            MorphIsland(true);
    }

    private void NowPlayingIsland_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_islandExpanded && !_islandDragging)
            MorphIsland(false);
    }

    private async void IslandTransport_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closed || sender is not FrameworkElement { Tag: string action })
            return;
        e.Handled = true;
        Task work = action switch
        {
            "previous" => SpotifyNowPlaying.SkipAsync(false),
            "next" => SpotifyNowPlaying.SkipAsync(true),
            _ => SpotifyNowPlaying.ToggleAsync(),
        };
        if (action == "toggle" && _nowPlaying != null)
        {
            _nowPlaying = _nowPlaying with { Playing = !_nowPlaying.Playing, Position = _nowPlaying.PositionNow, SampledAt = DateTime.UtcNow };
            ApplyIslandPlayState();
        }
        await work;
        await Task.Delay(350);
        if (!_closed)
            OnNowPlayingTick(null, EventArgs.Empty);
    }

    private void OnDockMoved(object? sender, EventArgs e)
    {
        if (NowPlayingPopup.IsOpen)
            UpdateNowPlayingVisibility();
    }

    private void UpdateNowPlayingVisibility()
    {
        bool busy = PopoutOpen || _panels.Any(static entry => entry.Key != HubKey && entry.Value.IsRequested);
        bool show = _opened && !_closed && !_slide.IsRunning && _nowPlaying != null && !busy && App.Settings.Prop.SessionDockSpotify;
        if (show)
        {
            NowPlayingPopup.CustomPopupPlacementCallback ??= PlaceNowPlaying;
            AlignIslandToDock();
            bool appearing = !NowPlayingPopup.IsOpen;
            NowPlayingPopup.IsOpen = true;
            if (appearing)
                IslandAppear();
            NowPlayingPopup.HorizontalOffset = NowPlayingPopup.HorizontalOffset == 0 ? 0.01 : 0;
        }
        else
        {
            if (NowPlayingPopup.IsOpen)
                ResetIsland();
            NowPlayingPopup.IsOpen = false;
        }
    }

    private static bool IslandBelowDock => (App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings()).DockTopEdge;

    private void AlignIslandToDock()
    {
        OverlayDockSide side = VisualSide;
        if (side != OverlayDockSide.None)
        {
            bool left = side == OverlayDockSide.Left;
            NowPlayingIsland.VerticalAlignment = VerticalAlignment.Center;
            NowPlayingIsland.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            NowPlayingIsland.Margin = left ? new Thickness(IslandDragRoom, 0, 0, 0) : new Thickness(0, 0, IslandDragRoom, 0);
            NowPlayingIsland.RenderTransformOrigin = left ? new Point(0, 0.5) : new Point(1, 0.5);
            return;
        }
        NowPlayingIsland.HorizontalAlignment = HorizontalAlignment.Center;
        bool below = IslandBelowDock;
        NowPlayingIsland.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        NowPlayingIsland.Margin = below ? new Thickness(0, IslandDragRoom, 0, 0) : new Thickness(0, 0, 0, IslandDragRoom);
        NowPlayingIsland.RenderTransformOrigin = below ? new Point(0.5, 0) : new Point(0.5, 1);
    }

    private static CustomPopupPlacement[] PlaceNowPlaying(Size popupSize, Size targetSize, Point offset)
    {
        OverlayDockSide side = ActiveInstance?.VisualSide ?? CurrentSide;
        if (side != OverlayDockSide.None)
        {
            double x = side == OverlayDockSide.Left ? targetSize.Width - IslandDragRoom + 8 : -popupSize.Width + IslandDragRoom - 8;
            return [new CustomPopupPlacement(new Point(x, (targetSize.Height - popupSize.Height) / 2), PopupPrimaryAxis.None)];
        }
        double y = IslandBelowDock ? targetSize.Height - IslandDragRoom + 8 : -popupSize.Height + IslandDragRoom - 8;
        return [new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, y), PopupPrimaryAxis.None)];
    }

    private const double IslandDragRoom = 28;

    private const double IslandDragReach = 22;

    private const double IslandDragThreshold = 3;

    private Point? _islandPressAt;

    private bool _islandDragging;

    private static double RubberBand(double offset, double reach) =>
        Math.Sign(offset) * reach * (1 - 1 / (Math.Abs(offset) * 0.55 / reach + 1));

    private void NowPlayingIsland_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _islandPressAt = e.GetPosition(IslandStage);
        _islandDragging = false;
        AnimateIsland(IslandScale, ScaleTransform.ScaleXProperty, 0.97);
        AnimateIsland(IslandScale, ScaleTransform.ScaleYProperty, 0.97);
    }

    private void NowPlayingIsland_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_islandPressAt is not Point start || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;
        Vector delta = e.GetPosition(IslandStage) - start;
        if (!_islandDragging)
        {
            if (Math.Abs(delta.X) < IslandDragThreshold && Math.Abs(delta.Y) < IslandDragThreshold)
                return;
            _islandDragging = true;
            NowPlayingIsland.CaptureMouse();
        }
        IslandPull.BeginAnimation(TranslateTransform.XProperty, null);
        IslandPull.BeginAnimation(TranslateTransform.YProperty, null);
        IslandPull.X = RubberBand(delta.X, IslandDragReach);
        IslandPull.Y = RubberBand(delta.Y, IslandDragReach);
        double stretch = 1 + Math.Min(0.06, (Math.Abs(IslandPull.X) + Math.Abs(IslandPull.Y)) / 400);
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IslandScale.ScaleX = stretch;
        IslandScale.ScaleY = stretch;
        e.Handled = true;
    }

    private void NowPlayingIsland_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool dragged = _islandDragging;
        ReleaseIsland();
        if (dragged)
            e.Handled = true;
    }

    private void NowPlayingIsland_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_islandDragging)
            ReleaseIsland();
    }

    private void ReleaseIsland()
    {
        bool dragging = _islandDragging;
        _islandPressAt = null;
        _islandDragging = false;
        if (dragging && NowPlayingIsland.IsMouseCaptured)
            NowPlayingIsland.ReleaseMouseCapture();
        AnimateIsland(IslandPull, TranslateTransform.XProperty, 0);
        AnimateIsland(IslandPull, TranslateTransform.YProperty, 0);
        AnimateIsland(IslandScale, ScaleTransform.ScaleXProperty, 1);
        AnimateIsland(IslandScale, ScaleTransform.ScaleYProperty, 1);
        if (dragging && !NowPlayingIsland.IsMouseOver && _islandExpanded)
            MorphIsland(false);
    }

    internal static System.Windows.Media.Imaging.BitmapImage? LoadNowPlayingCover(byte[] bytes)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapImage image = new();
            using System.IO.MemoryStream stream = new(bytes);
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 120;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or ArgumentException)
        {
            return null;
        }
    }

    private void UpdateActiveButtons()
    {
        if (_closed)
            return;
        UpdateNowPlayingVisibility();
        foreach (object child in ActionButtons.Children)
        {
            if (child is not Wpf.Ui.Controls.Button { Tag: string action } button)
                continue;
            bool open = IsQuickAction(action)
                ? QuickState(action[QuickPrefix.Length..])
                : _panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? panel) && panel.IsRequested;
            Wpf.Ui.Common.ControlAppearance appearance = open
                ? Wpf.Ui.Common.ControlAppearance.Primary
                : Wpf.Ui.Common.ControlAppearance.Transparent;
            if (button.Appearance != appearance)
                button.Appearance = appearance;
            if (button.Content is TextBlock glyph)
            {
                // Black on a light accent and white on a dark one, a near white accent left white icons unreadable
                if (open)
                    glyph.Foreground = Voidstrap.UI.Converters.ContrastForegroundConverter.For(
                        TryFindResource("AccentFillColorDefaultBrush")
                    );
                else
                    glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            }
        }
        _dimmer?.SetCatchesPointer(_panels.Any(static entry => entry.Key != HubKey && entry.Value.IsRequested && !entry.Value.IsPinned));
    }

    private void OnDimmerClicked()
    {
        foreach (SessionPanelWindow panel in _panels.Where(static entry => entry.Key != HubKey && entry.Value.IsRequested && !entry.Value.IsPinned).Select(static entry => entry.Value).ToList())
            panel.HideForSession();
        UpdateActiveButtons();
        PortableOverlay.ReturnFocus();
    }

    // Your Roblox avatar, display name and username on the left of the dock
    private async Task LoadProfileAsync()
    {
        if (_profileLoaded || _profileLoading || _closed)
            return;
        _profileLoading = true;
        try
        {
            RobloxChatResult<long> self = await RobloxChat.GetSelfAsync(_lifetimeToken);
            if (_closed)
                return;
            if (self.Status != RobloxChatStatus.Ready)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ProfileName.Text = "Not signed in";
                    ProfileHandle.Text = self.Status == RobloxChatStatus.SignInExpired ? "Sign in again" : string.Empty;
                    QueueFitWidth();
                });
                return;
            }
            _selfUserId = self.Value;
            StartRobuxTimer();
            _ = RefreshRobuxAsync();
            RobloxChatUser? user = RobloxChat.GetCachedUser(self.Value);
            await Dispatcher.InvokeAsync(() =>
            {
                ProfileName.Text = user?.Label is { Length: > 0 } label ? label : "Roblox";
                ProfileHandle.Text = user?.Name is { Length: > 0 } name ? "@" + name : string.Empty;
                QueueFitWidth();
            });
            Dictionary<long, string> urls = await RobloxChat.GetHeadshotUrlsAsync(new[] { self.Value }, _lifetimeToken);
            if (
                _closed
                || !urls.TryGetValue(self.Value, out string? url)
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
            )
                return;
            System.Windows.Media.Imaging.BitmapSource? avatar = await Voidstrap.Utility.SafeImaging.FromHttpAsync(
                uri.AbsoluteUri,
                96,
                _lifetimeToken
            );
            if (avatar != null)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closed)
                        return;
                    ProfileAvatar.ImageSource = avatar;
                    _profileLoaded = true;
                    ProfileFallback.Visibility = Visibility.Collapsed;
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your profile could not be loaded: " + ex.Message);
        }
        finally
        {
            _profileLoading = false;
        }
    }

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
        catch (InvalidOperationException) { }
    }

    // Closed by the player: slides back down into the bottom edge
    public void CloseDock()
    {
        if (PortableOverlay.Active)
            Dispatcher.BeginInvoke(new Action(() => HideDock(true)));
        else
            HideDock(true);
    }

    public void HideDock() => HideDock(false);

    // keepPanels is for a hide that only lasts until the game is focused again, every panel comes back with the dock
    private void HideDock(bool animate, bool keepPanels = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => HideDock(animate, keepPanels)));
            return;
        }
        _resumeOnFocus = false;
        _resumePopout = false;
        if (_closed || !_opened)
            return;
        if (Voidstrap.Utility.Platform.UsesLinuxHost && RobloxWindowTracker.Current.Hwnd == IntPtr.Zero)
        {
            System.Diagnostics.StackTrace trace = new(1, false);
            List<string> callers = new();
            for (int index = 0; index < Math.Min(4, trace.FrameCount); index++)
                if (trace.GetFrame(index)?.GetMethod() is { } method)
                    callers.Add(method.DeclaringType?.Name + "." + method.Name);
            App.Logger.WriteLine("SessionDock", "Hiding the dock, called from " + string.Join(" < ", callers));
        }
        _opened = false;
        RefreshNowPlaying();
        bool returnFocus = RobloxWindowTracker.IsRobloxForeground();
        _focusLoss?.Stop();
        bool linuxSlide = animate && Voidstrap.Utility.Platform.UsesLinuxHost && IsVisible;
        bool slide =
            animate && NativeSlide.Supported && !Voidstrap.Utility.Platform.UsesLinuxHost && IsVisible && RobloxWindowTracker.Current.Valid;
        CancelEntrance();
        _clock.Stop();
        StopHeartbeat();
        CancelRefresh();
        _anchor?.Dispose();
        _anchor = null;
        HidePopout();
        if (linuxSlide)
            StartLinuxExit();
        else if (!slide || !StartExit())
        {
            if (IsVisible)
                Hide();
        }
        // Pinned panels belong to the dock: they hide with it and come back when it opens. The rest close.
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
        {
            if (panel.IsPinned || keepPanels || !panel.IsRequested || panel.KeepsRunning)
                panel.SetInteractive(false);
            else
                panel.Close();
        }
        _prewarmTimer?.Stop();
        FadeDimmer(0);
        if (Voidstrap.Utility.Platform.UsesLinuxHost && !linuxSlide)
            Voidstrap.Platform.Linux.LinuxWindowInterop.ReleaseFocusAnchor();
        IntPtr game = RobloxWindowTracker.Current.Hwnd;
        if (returnFocus && PortableOverlay.Active)
            PortableOverlay.ReturnFocus();
        else if (returnFocus && game != IntPtr.Zero)
            SetForegroundWindow(game);
    }

    private void OnFocusLossTick(object? sender, EventArgs e)
    {
        _focusLoss?.Stop();
        if (_closed || !_opened)
            return;
        if (RobloxWindowTracker.Current.Valid && RobloxWindowTracker.IsRobloxForeground())
            return;
        if (Voidstrap.Utility.Platform.UsesLinuxHost && RobloxWindowTracker.Current.Hwnd == IntPtr.Zero)
            App.Logger.WriteLine(
                "SessionDock",
                string.Format(
                    "Closing because the game lost focus, active window 0x{0:x}, keyboard window 0x{1:x}, focus window 0x{2:x}, tracker valid {3}",
                    (long)Voidstrap.Platform.Linux.LinuxWindowInterop.GetActiveTopLevelWindow(),
                    (long)Voidstrap.Platform.Linux.LinuxWindowInterop.GetFocusedTopLevelWindow(),
                    (long)Voidstrap.Platform.Linux.LinuxWindowInterop.FocusAnchor,
                    RobloxWindowTracker.Current.Valid
                )
            );
        SuspendDock();
    }

    // Hidden because the game lost focus, it comes back on its own when the game is focused again
    private void SuspendDock()
    {
        if (Voidstrap.Utility.Platform.UsesLinuxHost && RobloxWindowTracker.Current.Hwnd == IntPtr.Zero)
        {
            HideDock(true);
            return;
        }
        bool popout = PopoutOpen;
        HideDock(false, true);
        _resumeOnFocus = true;
        _resumePopout = popout;
    }

    private void ResumeDock()
    {
        _resumeQueued = false;
        if (_closed || _opened || !_resumeOnFocus)
            return;
        bool popout = _resumePopout;
        if (!RobloxWindowTracker.IsRobloxWindowActive())
            return;
        _resumeOnFocus = false;
        _resumePopout = false;
        if (!App.Settings.Prop.SessionDockEnabled || !_activity.InGame)
            return;
        // The player just clicked back into the game, so nothing takes the keyboard away from it
        Open(activatePanels: false);
        if (!popout || !_opened)
            return;
        if (_slide.IsRunning)
            _popoutOnSettle = true;
        else
            ShowPopout();
    }

    private void Toggle()
    {
        if (_closed || !App.Settings.Prop.SessionDockEnabled)
            return;
        if (_opened)
        {
            HideDock(true);
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
        _focusLoss?.Stop();
        string shortcut = Voidstrap.UI.Elements.ContextMenu.MenuContainer.ShortcutText;
        CloseButton.ToolTip = shortcut.Length > 0 ? "Close, " + shortcut : "Close";
        RobloxWindowTracker.Refresh();
        _opened = true;
        UpdateClipperVisibility();
        RefreshNowPlaying();
        UpdateReadout();
        ShowDimmer();
        _anchor ??= new RobloxOverlayAnchor(
            this,
            hideWhenUnfocused: false,
            placement: RobloxOverlayPlacement.BottomCenter,
            customPosition: ComputeDockPosition
        );
        Point? resumeFrom = _exiting ? _slide.CurrentPosition() : null;
        CancelEntrance();
        if (resumeFrom == null && !IsVisible && NativeSlide.Supported && RobloxWindowTracker.Current.Valid)
            _slide.Conceal();
        try
        {
            Show();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionDock", "The dock could not be shown: " + ex.Message);
            _opened = false;
            CancelEntrance();
            FadeDimmer(0);
            return;
        }
        FitWidth();
        _anchor.Refresh();
        if (
            activatePanels
            && Voidstrap.Utility.Platform.UsesLinuxHost
            && RobloxWindowTracker.Current is { Valid: true, Hwnd: var game }
            && game == IntPtr.Zero
        )
        {
            bool taken = Voidstrap.Platform.Linux.LinuxWindowInterop.TryTakeFocusFromNativeWindow(
                (int)Math.Round(Left + Width / 2),
                (int)Math.Round(Top + Height / 2)
            );
            App.Logger.WriteLine(
                "SessionDock",
                taken
                    ? "Asked the desktop to focus the dock over the game, focus window 0x"
                        + ((long)Voidstrap.Platform.Linux.LinuxWindowInterop.FocusAnchor).ToString("x")
                    : "The dock could not take focus from the game"
            );
        }
        ApplyLinuxDockShape();
        RaiseAboveDimmer();
        _clock.Start();
        _activateOnSettle = activatePanels;
        StartEntrance(resumeFrom);
    }

    internal static (int Left, int Top) ComputeDockPosition(RobloxWindowRect rect, double scaleX, double scaleY, int width, int height)
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        if (layout.DockSideways)
        {
            int along = Math.Max(0, (int)Math.Round(12 * scaleY));
            int across = Math.Max(1, (int)Math.Round(layout.SafeDockEdgeSpacing * scaleX));
            int minTop = rect.Top + along;
            int maxTop = Math.Max(minTop, rect.Top + rect.Height - height - along);
            int sideTop = minTop + (int)Math.Round((maxTop - minTop) * (layout.SafeDockHorizontal / 100.0));
            int sideLeft = layout.DockSide == OverlayDockSide.Left ? rect.Left + across : rect.Left + Math.Max(0, rect.Width - width - across);
            return (sideLeft, sideTop);
        }
        int sideMargin = Math.Max(0, (int)Math.Round(12 * scaleX));
        int edgeMargin = Math.Max(1, (int)Math.Round(layout.SafeDockEdgeSpacing * scaleY));
        int minLeft = rect.Left + sideMargin;
        int maxLeft = Math.Max(minLeft, rect.Left + rect.Width - width - sideMargin);
        int left = minLeft + (int)Math.Round((maxLeft - minLeft) * (layout.SafeDockHorizontal / 100.0));
        int top = layout.DockTopEdge ? rect.Top + edgeMargin : rect.Top + Math.Max(0, rect.Height - height - edgeMargin);
        return (left, top);
    }

    private static double ResolveConfiguredDimmerOpacity()
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        return layout.ResolveDockBackdropOpacity(DimmerOpacity);
    }

    public void SetScreenDragDimOverride(bool active)
    {
        _screenDragDimOverride = active;
        DockRoot.Opacity = active ? 0 : 1;
        DockRoot.IsHitTestVisible = !active;
        if (_opened && _dimmer != null)
            FadeDimmer(active ? Math.Min(0.10, ResolveConfiguredDimmerOpacity()) : ResolveConfiguredDimmerOpacity());
    }

    internal (
        string ProfileName,
        string ProfileHandle,
        ImageSource? ProfileAvatar,
        string GameTitle,
        ImageSource? GameIcon,
        string SessionTime,
        string ServerTime
    ) GetLiveSnapshot() =>
        (
            string.IsNullOrWhiteSpace(ProfileName.Text) ? "Roblox" : ProfileName.Text,
            string.IsNullOrWhiteSpace(ProfileHandle.Text) ? Voidstrap.Resources.Strings.Ui_Session_SignedOut : ProfileHandle.Text,
            ProfileAvatar.ImageSource,
            string.IsNullOrWhiteSpace(GameTitle.Text) ? "Roblox" : GameTitle.Text,
            GamePicture.Source,
            string.IsNullOrWhiteSpace(SessionClock.Text) ? "0:00" : SessionClock.Text,
            string.IsNullOrWhiteSpace(ServerClock.Text) ? "--" : ServerClock.Text
        );

    public void RefreshLayoutLive()
    {
        if (_closed)
            return;
        FitWidth();
        if (_opened && !_slide.IsRunning && !_linuxMotionActive)
        {
            _anchor?.Refresh();
            ApplyLinuxDockShape();
            if (PopoutOpen)
                PlacePopout();
            if (_dimmer != null)
            {
                double target = _screenDragDimOverride
                    ? Math.Min(0.10, ResolveConfiguredDimmerOpacity())
                    : ResolveConfiguredDimmerOpacity();
                SetDimmerAlpha(target);
            }
            foreach (SessionPanelWindow panel in _panels.Values.ToArray())
            {
                if (!panel.IsClosed)
                    panel.ReloadLayoutLive();
            }
            RaiseAboveDimmer();
        }
    }

    public void OpenAction(string action)
    {
        if (_closed)
            return;
        try
        {
            _action(action);
            _panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? active);
            RaiseAboveDimmer(active);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("SessionDock::OpenAction", ex);
        }
    }

    public void CenterPanelLive(string key)
    {
        if (_panels.TryGetValue(key, out SessionPanelWindow? panel) && !panel.IsClosed)
        {
            panel.CenterInBoundsLive();
            return;
        }
        App.State.Prop.SessionPanels ??= new();
        if (!App.State.Prop.SessionPanels.TryGetValue(key, out SessionPanelLayout? saved) || saved == null)
            saved = new SessionPanelLayout { Width = 540, Height = 460 };
        saved.X = 0.5;
        saved.Y = 0.5;
        App.State.Prop.SessionPanels[key] = saved;
        App.State.SaveDeferred();
    }

    public void SetPanelPinnedLive(string key, bool pinned)
    {
        if (_panels.TryGetValue(key, out SessionPanelWindow? panel) && !panel.IsClosed)
        {
            panel.SetPinStateLive(pinned);
            return;
        }
        App.State.Prop.SessionPanels ??= new();
        if (!App.State.Prop.SessionPanels.TryGetValue(key, out SessionPanelLayout? saved) || saved == null)
            saved = new SessionPanelLayout
            {
                X = 0.15,
                Y = 0.12,
                Width = 540,
                Height = 460,
            };
        saved.Pinned = pinned;
        App.State.Prop.SessionPanels[key] = saved;
        App.State.SaveDeferred();
    }

    public void ResetAllPanelLayoutsLive()
    {
        App.State.Prop.SessionPanels?.Clear();
        App.State.SaveDeferred();
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
        {
            if (!panel.IsClosed)
                panel.ReloadLayoutLive();
        }
    }

    private void StartEntrance(Point? resumeFrom)
    {
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            StartLinuxEntrance();
            return;
        }
        RobloxWindowRect game = RobloxWindowTracker.Current;
        if (!NativeSlide.Supported || !game.Valid || _slide.CurrentPosition() is not Point target)
        {
            _slide.RemoveClip();
            Settle();
            return;
        }
        Point from = resumeFrom ?? OffscreenPoint(target, game);
        Int32Rect clip = new(game.Left, game.Top, game.Width, game.Height);
        if (!_slide.Start(from, target, clip, EntranceLength, true, OnEntranceFinished))
        {
            _slide.RemoveClip();
            Settle();
        }
    }

    private void OnEntranceFinished()
    {
        if (_closed || !_opened)
            return;
        _slide.RemoveClip();
        _anchor?.Refresh();
        Settle();
        QueueFitWidth();
        UpdateNowPlayingVisibility();
    }

    private void Settle()
    {
        if (_closed || !_opened)
            return;
        ApplyLinuxDockShape();
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            InvalidateVisual();
            LinuxOverlaySurface.WakePresentation(this);
        }
        StartHeartbeat();
        if (_refreshTask is not { IsCompleted: false } && DateTime.UtcNow - _lastRefreshUtc >= RefreshInterval)
            StartRefresh();
        _ = PortableOverlay.RunAsync(this, LoadPopoutAsync);
        _ = PortableOverlay.RunAsync(this, LoadProfileAsync);
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
        {
            if (panel.IsRequested)
                panel.Present(true, _activateOnSettle);
        }
        RestorePinnedPanels();
        UpdateHub();
        SpotifyEngine.Prewarm();
        StartPrewarm();
        if (_popoutOnSettle)
        {
            _popoutOnSettle = false;
            ShowPopout();
        }
        RaiseAboveDimmer();
    }

    public void Preload()
    {
        if (_closed)
            return;
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            try
            {
                new WindowInteropHelper(this).EnsureHandle();
            }
            catch (InvalidOperationException ex)
            {
                App.Logger.WriteLine("SessionDock", "The dock window could not be built ahead of time: " + ex.Message);
            }
        }
        StartPrewarm();
    }

    private void StartPrewarm()
    {
        if (_closed || _prewarmIndex >= PrewarmActions.Length)
            return;
        if (_prewarmTimer == null)
        {
            _prewarmTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(500) };
            _prewarmTimer.Tick += OnPrewarmTick;
        }
        _prewarmTimer.Start();
    }

    private void OnPrewarmTick(object? sender, EventArgs e)
    {
        if (_closed)
        {
            _prewarmTimer?.Stop();
            return;
        }
        if (_slide.IsRunning || NotificationWindow.AnyPresenting)
            return;
        while (_prewarmIndex < PrewarmActions.Length)
        {
            string action = PrewarmActions[_prewarmIndex++];
            if (action == "clips" && !App.Settings.Prop.ClipperEnabled)
                continue;
            if (_panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? existing) && !existing.IsClosed)
                continue;
            _prewarming = true;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _action(action);
                App.Logger.WriteLine(
                    "SessionDock",
                    $"Built the {action} panel ahead of time in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms"
                );
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionDock", $"The {action} panel could not be built ahead of time: " + ex.Message);
            }
            finally
            {
                _prewarming = false;
            }
            return;
        }
        _prewarmTimer?.Stop();
        if (!_prewarmLogged)
        {
            _prewarmLogged = true;
            App.Logger.WriteLine(
                "SessionDock",
                _opened ? "Every dock panel is ready" : "Every dock panel is ready before the dock was opened"
            );
        }
    }

    private void RestorePinnedPanels()
    {
        if (_pinnedRestored)
            return;
        _pinnedRestored = true;
        Dictionary<string, SessionPanelLayout>? saved = App.State.Prop.SessionPanels;
        if (saved == null || saved.Count == 0)
            return;
        string[] pinned = saved
            .Where(pair => pair.Value is { Pinned: true } && !_panels.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();
        if (pinned.Length == 0)
            return;
        _restoringPinned = true;
        try
        {
            foreach (string key in pinned)
            {
                if (_closed || !_opened)
                    break;
                if (key == "clips" && !App.Settings.Prop.ClipperEnabled)
                    continue;
                if (!PanelActions.TryGetValue(key, out string? action))
                    continue;
                try
                {
                    _action(action);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("SessionDock", $"The pinned panel '{key}' could not be restored: " + ex.Message);
                }
            }
        }
        finally
        {
            _restoringPinned = false;
        }
        App.Logger.WriteLine("SessionDock", $"Restored {pinned.Length} pinned panel(s)");
    }

    private void CancelEntrance()
    {
        if (!Voidstrap.Utility.Platform.UsesLinuxHost)
            _slide.Stop(true);
        CancelLinuxDockAnimation();
        _exiting = false;
        _popoutOnSettle = false;
    }

    private void StartLinuxEntrance()
    {
        _exiting = false;
        StartLinuxDockAnimation(true, EntranceLength, EasingMode.EaseOut, Settle);
    }

    private void StartLinuxExit()
    {
        _exiting = true;
        StartLinuxDockAnimation(false, ExitLength, EasingMode.EaseIn, OnExitFinished);
    }

    private void StartLinuxDockAnimation(bool entering, TimeSpan duration, EasingMode easingMode, Action completed)
    {
        CancelLinuxDockAnimation();
        RobloxWindowRect bounds = RobloxWindowTracker.Current;
        if (!bounds.Valid || !IsVisible || Wpf.Ui.Animations.RenderReady.ReducedMotion || CurrentSide != OverlayDockSide.None)
        {
            completed();
            return;
        }
        int generation = _linuxMotionGeneration;
        _linuxMotionOrigin = new Point(Left, Top);
        _linuxMotionBounds = bounds;
        bool topEdge = App.Settings.Prop.OverlayLayout?.DockTopEdge == true;
        double distance = topEdge
            ? -Math.Max(DockHeight, Top + Height - bounds.Top)
            : Math.Max(DockHeight, bounds.Top + bounds.Height - Top);
        double from = entering ? distance : 0;
        double to = entering ? 0 : distance;
        _linuxDockMotion.Y = to;
        _linuxMotionActive = true;
        _linuxMotionFinished = completed;
        _linuxMotionDeadline = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = duration + TimeSpan.FromMilliseconds(1500),
        };
        _linuxMotionDeadline.Tick += OnLinuxMotionDeadline;
        Voidstrap.Platform.Linux.LinuxWindowShadow.Track(PortableOverlay.Handle(this), 16, 1, true, unmanaged: true);
        PlaceLinuxDockFrame(from);
        Wpf.Ui.Animations.RenderReady.Run(
            this,
            () =>
            {
                if (_closed || generation != _linuxMotionGeneration)
                    return;
                _linuxMotionDeadline?.Start();
                DoubleAnimation motion = new(from, to, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = easingMode },
                    FillBehavior = FillBehavior.Stop,
                };
                motion.Completed += (_, _) => FinishLinuxDockAnimation(generation);
                CompositionTarget.Rendering += OnLinuxDockFrame;
                Wpf.Ui.Animations.RenderReady.Hold(this, duration);
                _linuxDockMotion.BeginAnimation(TranslateTransform.YProperty, motion, HandoffBehavior.SnapshotAndReplace);
            }
        );
    }

    private void OnLinuxDockFrame(object? sender, EventArgs e)
    {
        if (_closed || !_linuxMotionActive)
            return;
        PlaceLinuxDockFrame(_linuxDockMotion.Y);
    }

    private void PlaceLinuxDockFrame(double offset)
    {
        int top = (int)Math.Round(_linuxMotionOrigin.Y + offset);
        if (top == _linuxMotionTop)
            return;
        _linuxMotionTop = top;
        nint handle = PortableOverlay.Handle(this);
        int width = Math.Max(1, (int)Math.Ceiling(Width));
        int height = Math.Max(1, (int)Math.Ceiling(Height));
        int clipTop = Math.Max(0, _linuxMotionBounds.Top - top);
        int clipBottom = Math.Clamp(_linuxMotionBounds.Top + _linuxMotionBounds.Height - top, 0, height);
        Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(handle, width, height, 16, clipTop, clipBottom);
        Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(handle, (int)Math.Round(_linuxMotionOrigin.X), top, width, height);
    }

    private void OnLinuxMotionDeadline(object? sender, EventArgs e) => FinishLinuxDockAnimation(_linuxMotionGeneration);

    private void FinishLinuxDockAnimation(int generation)
    {
        if (generation != _linuxMotionGeneration || !_linuxMotionActive)
            return;
        Action? completed = _linuxMotionFinished;
        if (_opened && !_closed)
            PlaceLinuxDockFrame(0);
        CancelLinuxDockAnimation();
        if (_closed)
            return;
        if (_opened)
        {
            _fitAnchorPending = false;
            _anchor?.Refresh();
            ApplyLinuxDockShape();
            QueueFitWidth();
        }
        completed?.Invoke();
    }

    private void CancelLinuxDockAnimation()
    {
        _linuxMotionGeneration++;
        _linuxMotionActive = false;
        _linuxMotionFinished = null;
        _linuxMotionTop = int.MinValue;
        CompositionTarget.Rendering -= OnLinuxDockFrame;
        if (_linuxMotionDeadline != null)
        {
            _linuxMotionDeadline.Stop();
            _linuxMotionDeadline.Tick -= OnLinuxMotionDeadline;
            _linuxMotionDeadline = null;
        }
        _linuxDockMotion.BeginAnimation(TranslateTransform.YProperty, null);
        _linuxDockMotion.Y = 0;
    }

    private bool StartExit()
    {
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
            return false;
        RobloxWindowRect game = RobloxWindowTracker.Current;
        if (_slide.CurrentPosition() is not Point from)
            return false;
        Int32Rect clip = new(game.Left, game.Top, game.Width, game.Height);
        _exiting = _slide.Start(
            from,
            OffscreenPoint(from, game),
            clip,
            ExitLength,
            false,
            OnExitFinished
        );
        return _exiting;
    }

    private void OnExitFinished()
    {
        _exiting = false;
        if (Voidstrap.Utility.Platform.UsesLinuxHost && !_opened)
            Voidstrap.Platform.Linux.LinuxWindowInterop.ReleaseFocusAnchor();
        if (_closed || _opened)
            return;
        if (IsVisible)
            Hide();
        if (!Voidstrap.Utility.Platform.UsesLinuxHost)
            _slide.RemoveClip();
    }

    private static OverlayDockSide CurrentSide =>
        App.Settings.Prop.OverlayLayout is { DockSideways: true } layout ? layout.DockSide : OverlayDockSide.None;

    private OverlayDockSide VisualSide => _appliedSide ?? OverlayDockSide.None;

    private Point OffscreenPoint(Point target, RobloxWindowRect game) =>
        VisualSide switch
        {
            OverlayDockSide.Left => new Point(game.Left - Width, target.Y),
            OverlayDockSide.Right => new Point(game.Left + game.Width, target.Y),
            _ => new Point(target.X, App.Settings.Prop.OverlayLayout?.DockTopEdge == true ? game.Top - Height : game.Top + game.Height),
        };

    private static int DockExtent(RobloxWindowRect bounds) => CurrentSide == OverlayDockSide.None ? bounds.Width : bounds.Height;

    private void ApplyOrientation()
    {
        OverlayDockSide side = CurrentSide;
        if (_appliedSide == side)
            return;
        _appliedSide = side;
        bool sideways = side != OverlayDockSide.None;
        bool linux = Voidstrap.Utility.Platform.UsesLinuxHost;
        double thickness = linux ? DockHeight : DockWindowThickness;
        ArrangeDockAxis(sideways, linux);
        if (!linux)
            DockRoot.Margin = sideways ? new Thickness(14, 20, 18, 20) : new Thickness(20, 18, 20, 14);
        DockRoot.RowDefinitions[0].Height = sideways || linux ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        DockRoot.RowDefinitions[1].Height = sideways ? new GridLength(1, GridUnitType.Star) : new GridLength(linux ? DockHeight : 64);
        if (sideways)
            Width = thickness;
        else
            Height = thickness;
        NowPlayingPopup.PlacementTarget = sideways ? DockRoot : Pill;
        _availableWidth = 0;
    }

    private void ArrangeDockAxis(bool vertical, bool linux)
    {
        Orientation flow = vertical ? Orientation.Vertical : Orientation.Horizontal;
        if (Pill.Parent is FrameworkElement host)
        {
            host.LayoutTransform = Transform.Identity;
            host.HorizontalAlignment = linux && !vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            host.VerticalAlignment = linux && vertical ? VerticalAlignment.Stretch : vertical ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }
        double thickness = linux ? DockHeight : 64;
        Pill.Width = vertical ? thickness : double.NaN;
        Pill.Height = vertical ? double.NaN : linux ? DockHeight : double.NaN;
        Pill.Padding = vertical ? new Thickness(0, 14, 0, 12) : new Thickness(14, 0, 12, 0);
        PillLayout.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        PillLayout.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SwapAxis(PillLayout, vertical);
        SwapAxis(DockContent, vertical);
        DockContent.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        DockContent.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        DockScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        DockScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        DockScroll.ScrollToHome();
        foreach (FrameworkElement divider in new FrameworkElement[] { ProfileDivider, RobuxDivider, GameDivider, ActionsDivider })
        {
            divider.Width = vertical ? 30 : 1;
            divider.Height = vertical ? 1 : 30;
            divider.Margin = vertical ? new Thickness(0, 10, 0, 10) : new Thickness(10, 0, 10, 0);
            divider.HorizontalAlignment = vertical ? HorizontalAlignment.Center : ReferenceEquals(divider, GameDivider) ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            divider.VerticalAlignment = vertical && ReferenceEquals(divider, GameDivider) ? VerticalAlignment.Top : VerticalAlignment.Center;
        }
        Visibility text = vertical ? Visibility.Collapsed : Visibility.Visible;
        ProfileText.Visibility = text;
        ProfileChevron.Visibility = text;
        RobuxText.Visibility = text;
        RobuxChevron.Visibility = text;
        GameText.Visibility = text;
        GameChevron.Visibility = text;
        ProfileAvatarHost.Margin = vertical ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        RobuxGlyph.Margin = vertical ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        GamePicture.Margin = vertical ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        ProfileCard.HorizontalAlignment = HorizontalAlignment.Center;
        RobuxCard.HorizontalAlignment = HorizontalAlignment.Center;
        GameCard.HorizontalAlignment = HorizontalAlignment.Center;
        ActionButtons.Orientation = flow;
        ActionButtons.Margin = vertical ? new Thickness(0, 6, 0, 6) : new Thickness(6, 0, 6, 0);
        ActionButtons.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (object child in ActionButtons.Children)
            if (child is FrameworkElement button)
                button.Margin = vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
        TrailingButtons.Orientation = flow;
        TrailingButtons.HorizontalAlignment = HorizontalAlignment.Center;
        CloseButton.Margin = vertical ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
    }

    private static void SwapAxis(Grid grid, bool vertical)
    {
        int count = Math.Max(grid.ColumnDefinitions.Count, grid.RowDefinitions.Count);
        List<GridLength> lengths = new(count);
        foreach (ColumnDefinition column in grid.ColumnDefinitions)
            lengths.Add(column.Width);
        foreach (RowDefinition row in grid.RowDefinitions)
            lengths.Add(row.Height);
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        foreach (GridLength length in lengths)
        {
            if (vertical)
                grid.RowDefinitions.Add(new RowDefinition { Height = length });
            else
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
        }
        foreach (UIElement child in grid.Children)
        {
            int index = Math.Max(Grid.GetColumn(child), Grid.GetRow(child));
            Grid.SetColumn(child, vertical ? 0 : index);
            Grid.SetRow(child, vertical ? index : 0);
        }
    }

    public static readonly CustomPopupPlacementCallback ToolTipPlacement = PlaceToolTip;

    private static CustomPopupPlacement[] PlaceToolTip(Size popupSize, Size targetSize, Point offset)
    {
        OverlayDockSide side = ActiveInstance?.VisualSide ?? CurrentSide;
        if (side == OverlayDockSide.None)
            return Voidstrap.UI.OverlayToolTipPlacement.Above(popupSize, targetSize, offset);
        double top = (targetSize.Height - popupSize.Height) / 2;
        CustomPopupPlacement after = new(new Point(targetSize.Width + 6, top), PopupPrimaryAxis.Vertical);
        CustomPopupPlacement before = new(new Point(-popupSize.Width - 6, top), PopupPrimaryAxis.Vertical);
        return side == OverlayDockSide.Left ? [after, before] : [before, after];
    }

    private void FitWidth()
    {
        ApplyOrientation();
        bool sideways = _appliedSide is OverlayDockSide.Left or OverlayDockSide.Right;
        double userScale = (App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings()).SafeDockScale;
        SetPillScale(userScale);
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double shadowRoom = Voidstrap.Utility.Platform.UsesLinuxHost ? 0 : ShadowRoomX;
        double width = Math.Min(MaxDockWidth * userScale, Math.Ceiling(PillLength(sideways))) + shadowRoom;
        if (RobloxWindowTracker.Current is { Valid: true } bounds && DockExtent(bounds) > 0)
        {
            _availableWidth = DockExtent(bounds);
            DpiScale windowDpi = VisualTreeHelper.GetDpi(this);
            double dpiX = PortableOverlay.Active ? 1.0 : Math.Max(0.5, sideways ? windowDpi.DpiScaleY : windowDpi.DpiScaleX);
            double gameWidth = _availableWidth / dpiX;
            double available = Math.Max(1, gameWidth - shadowRoom - (PortableOverlay.Active ? 0 : 24));
            double fitScale = Math.Min(1, available / Math.Max(1, width - shadowRoom));
            double combinedScale = userScale * fitScale;
            SetPillScale(combinedScale);
            Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Min(gameWidth, Math.Ceiling(PillLength(sideways)) + shadowRoom);
        }
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            width = Math.Ceiling(width / 32) * 32;
            if (PortableOverlay.Active && _availableWidth > 0)
                width = Math.Min(_availableWidth, width);
        }
        bool changed = width > shadowRoom && Math.Abs(width - (sideways ? Height : Width)) >= 0.5;
        if (changed && sideways)
            Height = width;
        else if (changed)
            Width = width;
        if (!changed && !_fitAnchorPending)
            return;
        if (_opened && !_slide.IsRunning && !_linuxMotionActive)
        {
            _fitAnchorPending = false;
            _anchor?.Refresh();
            ApplyLinuxDockShape();
        }
        else
            _fitAnchorPending = true;
    }

    private double PillLength(bool vertical) => vertical ? Pill.DesiredSize.Height : Pill.DesiredSize.Width;

    private void SetPillScale(double scale)
    {
        double current = Pill.LayoutTransform is ScaleTransform existing ? existing.ScaleX : 1.0;
        if (Math.Abs(current - scale) < 0.005)
            return;
        Pill.LayoutTransform = Math.Abs(scale - 1.0) > 0.005 ? new ScaleTransform(scale, scale) : Transform.Identity;
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
        if (_opened && !bounds.Valid)
        {
            SuspendDock();
            return;
        }
        if (_linuxMotionActive && !bounds.Matches(_linuxMotionBounds))
            FinishLinuxDockAnimation(_linuxMotionGeneration);
        if (bounds.Valid && DockExtent(bounds) != _availableWidth)
            FitWidth();
        if (_opened && !bounds.Foreground)
        {
            if (_focusLoss == null)
            {
                _focusLoss = new DispatcherTimer(DispatcherPriority.Input) { Interval = FocusLossGrace };
                _focusLoss.Tick += OnFocusLossTick;
            }
            if (!_focusLoss.IsEnabled)
                _focusLoss.Start();
            return;
        }
        _focusLoss?.Stop();
        if (!_opened && _resumeOnFocus && bounds.Valid && bounds.Foreground && !_resumeQueued && RobloxWindowTracker.IsRobloxWindowActive())
        {
            _resumeQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ResumeDock));
        }
        _dimmer?.Place(bounds);
        if (PopoutOpen)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PlacePopout));
    }

    private bool _dimmerFrameHooked;

    private void ShowDimmer()
    {
        if (_dimmer == null)
        {
            _dimmer = new SessionDimmer();
            _dimmer.Clicked += OnDimmerClicked;
            _dimmerFade = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _dimmerFade.Tick += DimmerFade_Tick;
        }
        if (!_dimmer.IsVisible)
            _dimmer.Show(RobloxWindowTracker.Current);
        double target = _screenDragDimOverride ? Math.Min(0.10, ResolveConfiguredDimmerOpacity()) : ResolveConfiguredDimmerOpacity();
        FadeDimmer(target);
    }

    private void RaiseAboveDimmer(Window? active = null)
    {
        if (PortableOverlay.Active)
        {
            foreach (SessionPanelWindow panel in _panels.Values)
                if (panel.IsVisible)
                    PortableOverlay.Raise(panel);
            if (_popout is { IsVisible: true })
                PortableOverlay.Raise(_popout);
            if (active is { IsVisible: true })
                PortableOverlay.Raise(active);
            PortableOverlay.Raise(this);
            return;
        }
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
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            CompositionTarget.Rendering -= OnLinuxDimmerFrame;
            _linuxDimmerFade.BeginAnimation(TranslateTransform.YProperty, null);
            _linuxDimmerGeneration++;
        }
        _dimmerFrom = _dimmerAlpha;
        _dimmerTarget = target;
        if (Math.Abs(_dimmerFrom - target) < 0.005)
        {
            UnhookDimmerFrame();
            _dimmerFade.Stop();
            SetDimmerAlpha(target);
            if (target == 0)
                ParkDimmer();
            return;
        }
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            int generation = _linuxDimmerGeneration;
            TimeSpan duration = TimeSpan.FromMilliseconds(DimmerFadeMs);
            _dimmerFade.Stop();
            Wpf.Ui.Animations.RenderReady.Run(
                this,
                () =>
                {
                    if (_closed || _dimmerFade == null || generation != _linuxDimmerGeneration)
                        return;
                    _linuxDimmerFade.Y = target;
                    DoubleAnimation fade = new(_dimmerFrom, target, duration)
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                        FillBehavior = FillBehavior.Stop,
                    };
                    fade.Completed += (_, _) => FinishLinuxDimmerFade(generation);
                    CompositionTarget.Rendering += OnLinuxDimmerFrame;
                    Wpf.Ui.Animations.RenderReady.Hold(this, duration);
                    _linuxDimmerFade.BeginAnimation(TranslateTransform.YProperty, fade);
                    _dimmerFade.Stop();
                    _dimmerFade.Interval = duration + TimeSpan.FromMilliseconds(1500);
                    _dimmerFade.Start();
                }
            );
            return;
        }
        _dimmerFadeStarted = Environment.TickCount64;
        if (!_dimmerFrameHooked)
        {
            CompositionTarget.Rendering += OnDimmerFrame;
            _dimmerFrameHooked = true;
        }
    }

    private void OnDimmerFrame(object? sender, EventArgs e)
    {
        double progress = Math.Clamp((Environment.TickCount64 - _dimmerFadeStarted) / DimmerFadeMs, 0, 1);
        double eased = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;
        SetDimmerAlpha(_dimmerFrom + (_dimmerTarget - _dimmerFrom) * eased);
        if (progress < 1)
            return;
        UnhookDimmerFrame();
        if (_dimmerTarget == 0)
            ParkDimmer();
    }

    private void UnhookDimmerFrame()
    {
        if (!_dimmerFrameHooked)
            return;
        CompositionTarget.Rendering -= OnDimmerFrame;
        _dimmerFrameHooked = false;
    }

    private void OnLinuxDimmerFrame(object? sender, EventArgs e) => SetDimmerAlpha(_linuxDimmerFade.Y);

    private void FinishLinuxDimmerFade(int generation)
    {
        if (generation != _linuxDimmerGeneration)
            return;
        _linuxDimmerGeneration++;
        CompositionTarget.Rendering -= OnLinuxDimmerFrame;
        _linuxDimmerFade.BeginAnimation(TranslateTransform.YProperty, null);
        _dimmerFade?.Stop();
        SetDimmerAlpha(_dimmerTarget);
        if (_dimmerTarget == 0)
            ParkDimmer();
    }

    private void DimmerFade_Tick(object? sender, EventArgs e)
    {
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            FinishLinuxDimmerFade(_linuxDimmerGeneration);
            return;
        }
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
        UnhookDimmerFrame();
        _linuxDimmerGeneration++;
        CompositionTarget.Rendering -= OnLinuxDimmerFrame;
        _linuxDimmerFade.BeginAnimation(TranslateTransform.YProperty, null);
        if (_dimmerFade != null)
        {
            _dimmerFade.Stop();
            _dimmerFade.Tick -= DimmerFade_Tick;
            _dimmerFade = null;
        }
        if (_dimmer != null)
            _dimmer.Clicked -= OnDimmerClicked;
        _dimmer?.Dispose();
        _dimmer = null;
        _dimmerAlpha = 0;
    }

    private void StartRefresh()
    {
        if (_closed || _data == null)
            return;
        CancelRefresh();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _lastRefreshUtc = DateTime.UtcNow;
        ActivityData data = _data;
        CancellationToken token = _refreshCts.Token;
        _refreshTask = PortableOverlay.RunAsync(this, () => RefreshServerAsync(data, token));
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionDock", "Some server details are unavailable: " + ex.Message);
            }
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(_data, data))
                return;
            if (location.IsCompletedSuccessfully)
                _location = location.Result ?? Strings.OverlayDiagnostics_Unavailable;
            if (uptime.IsCompletedSuccessfully && uptime.Result.Status == ServerStartStatus.Found)
                _serverStarted = uptime.Result.StartedUtc;
            if (players.IsCompletedSuccessfully)
            {
                var count = players.Result;
                _players =
                    count.Current > 0
                        ? count.Max > 0
                            ? $"{count.Current}/{count.Max}"
                            : count.Current.ToString(Locale.CurrentCulture)
                        : Strings.OverlayDiagnostics_Unavailable;
            }
            UpdateReadout();
        }
        catch (OperationCanceledException) { }
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
        if (_appliedSide != CurrentSide)
            RefreshLayoutLive();
        UpdateReadout();
        if (PopoutOpen && _popoutView == PopoutView.Server && _data is { } data && !_friendsRefreshing && DateTime.UtcNow - _friendsLoadedUtc >= FriendsInterval)
        {
            _friendsRefreshing = true;
            _ = PortableOverlay.RunAsync(this, () => RefreshFriendsQuietlyAsync(data));
        }
    }

    private async Task RefreshFriendsQuietlyAsync(ActivityData data)
    {
        try
        {
            await LoadFriendsAsync(data);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Friends in this server could not be refreshed: " + ex.Message);
        }
        finally
        {
            _friendsLoadedUtc = DateTime.UtcNow;
            _friendsRefreshing = false;
        }
    }

    private void UpdateReadout()
    {
        ActivityData? data = _data;
        if (data == null || _closed)
            return;
        _serverStarted ??= data.ServerStartedUtc;
        string uptime = _serverStarted.HasValue
            ? ServerInformationViewModel.FormatUptime(DateTimeOffset.UtcNow - _serverStarted.Value)
            : Strings.OverlayDiagnostics_Unavailable;
        SetRun(SessionClock, data.TimeJoined == default ? "0:00" : FormatClock(DateTime.Now - data.TimeJoined));
        SetRun(ServerClock, _serverStarted.HasValue ? FormatClock(DateTimeOffset.UtcNow - _serverStarted.Value) : "--");
        if (!PopoutOpen)
            return;
        if (_serverType.Length == 0)
            _serverType = data.ServerType.ToConnectedString();
        SetText(BannerTitle, GameTitle.Text);
        SetText(BannerSubtitle, _serverType);
        SetText(
            ServerReadout,
            string.Format(Strings.Ui_Session_ServerReadout, _location, uptime, _players.Length == 0 ? Strings.OverlayDiagnostics_Unavailable : _players)
        );
    }

    private static string FormatClock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    private void SetRun(System.Windows.Documents.Run run, string text)
    {
        if (string.Equals(run.Text, text, StringComparison.Ordinal))
            return;
        bool lengthChanged = run.Text.Length != text.Length;
        run.Text = text;
        if (!lengthChanged)
            return;
        SessionTimes.InvalidateMeasure();
        GameCard.InvalidateMeasure();
        _fitAnchorPending = true;
        QueueFitWidth();
    }

    private void DockScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (VisualSide != OverlayDockSide.None)
        {
            if (DockScroll.ScrollableHeight <= 0)
                return;
            DockScroll.ScrollToVerticalOffset(Math.Clamp(DockScroll.VerticalOffset - e.Delta, 0, DockScroll.ScrollableHeight));
            e.Handled = true;
            return;
        }
        if (DockScroll.ScrollableWidth <= 0)
            return;
        DockScroll.ScrollToHorizontalOffset(Math.Clamp(DockScroll.HorizontalOffset - e.Delta, 0, DockScroll.ScrollableWidth));
        e.Handled = true;
    }

    private void OnPillSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VisualSide == OverlayDockSide.None ? e.WidthChanged : e.HeightChanged)
            QueueFitWidth();
    }

    private void QueueFitWidth()
    {
        if (_closed || _fitWidthQueued)
            return;
        _fitWidthQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RunQueuedFitWidth));
    }

    private void RunQueuedFitWidth()
    {
        _fitWidthQueued = false;
        if (!_closed)
            FitWidth();
    }

    private static void SetText(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
            block.Text = text;
    }

    // Your avatar and name open your profile in the popout above the dock, a second click closes it again
    private void ProfileCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        TogglePopout(PopoutView.Profile);
    }

    private void RobuxCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            TogglePopout(PopoutView.Robux);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your Robux purchases could not be opened: " + ex.Message);
        }
    }

    private void GameCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        TogglePopout(PopoutView.Server);
    }

    // Two quick beats, like a pulse: up, back, a smaller second beat, then rest until the next tick
    private static DoubleAnimationUsingKeyFrames CreateDotBeat()
    {
        DoubleAnimationUsingKeyFrames beat = new() { Duration = TimeSpan.FromMilliseconds(520) };
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        beat.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                1.28,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }
            )
        );
        beat.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                1.0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }
            )
        );
        beat.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                1.14,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }
            )
        );
        beat.KeyFrames.Add(
            new EasingDoubleKeyFrame(
                1.0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }
            )
        );
        return beat;
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
        Timeline.SetDesiredFrameRate(timeline, 20);
        timeline.Freeze();
        return timeline;
    }

    private void StartHeartbeat()
    {
        if (_closed || (!Voidstrap.Utility.Platform.UsesLinuxHost && !SystemParameters.ClientAreaAnimation))
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
        if (_closed || !_opened || !IsVisible)
            return;
        Wpf.Ui.Animations.RenderReady.Hold(this, TimeSpan.FromMilliseconds(900));
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
        if (ProfilePanel.Parent is Panel profileParent)
            profileParent.Children.Remove(ProfilePanel);
        ProfilePanel.Visibility = Visibility.Visible;
        if (RobuxPanel.Parent is Panel robuxParent)
            robuxParent.Children.Remove(RobuxPanel);
        RobuxPanel.Visibility = Visibility.Visible;
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

    private void ToggleServerPanel() => TogglePopout(PopoutView.Server);

    // The same popout shows either the server or your profile; clicking the one already shown closes it,
    // clicking the other switches it in place
    private void TogglePopout(PopoutView view)
    {
        if (_closed)
            return;
        if (PopoutOpen && _popoutView == view)
        {
            HidePopout();
            return;
        }
        _popoutView = view;
        ShowPopout();
    }

    private void ShowPopout()
    {
        if (_closed || !_opened)
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
                Width = Pill.ActualWidth > 0 ? Pill.ActualWidth : 900,
                Height = PopoutHeight,
                Content = ServerPanel,
                FontFamily = FontFamily,
            };
            PortableOverlay.Prepare(_popout);
            _popout.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            _popout.SourceInitialized += OnPopoutSourceInitialized;
            if (PortableOverlay.Active)
            {
                _popout.PreviewKeyDown += OnDockKey;
                _popout.PreviewMouseDown += OnPopoutPointerDown;
            }
        }
        object view = _popoutView switch
        {
            PopoutView.Profile => ProfilePanel,
            PopoutView.Robux => RobuxPanel,
            _ => ServerPanel,
        };
        if (!ReferenceEquals(_popout.Content, view))
            _popout.Content = view;
        GameChevron.Text = _popoutView == PopoutView.Server ? "\uE70D" : "\uE70E";
        ProfileChevron.Text = _popoutView == PopoutView.Profile ? "\uE70D" : "\uE70E";
        RobuxChevron.Text = _popoutView == PopoutView.Robux ? "\uE70D" : "\uE70E";
        MarkOpenCard(_popoutView switch
        {
            PopoutView.Profile => ProfileCard,
            PopoutView.Robux => RobuxCard,
            _ => GameCard,
        });
        UpdateReadout();
        bool wasVisible = _popout.IsVisible;
        try
        {
            _popout.Show();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionDock", "The server popout could not be shown: " + ex.Message);
            return;
        }
        bool sliding = !wasVisible;
        if (sliding)
            StartPopoutSlide();
        PlacePopout();
        UpdateNowPlayingVisibility();
        RaiseAboveDimmer();
        if (_popoutView == PopoutView.Profile)
        {
            _ = PortableOverlay.RunAsync(this, LoadProfileDetailsAsync);
            return;
        }
        if (_popoutView == PopoutView.Robux)
        {
            _ = PortableOverlay.RunAsync(this, LoadRobuxPurchasesAsync);
            return;
        }
        UpdateReadout();
        _ = PortableOverlay.RunAsync(this, LoadPopoutAsync);
    }

    // Fills the profile view: names, pictures, account details and counts, kept for a few minutes
    private async Task LoadProfileDetailsAsync()
    {
        if (_profileInfo != null)
            ShowProfileDetails(_profileInfo);
        else
        {
            ProfileStatus.Text = "Loading your profile";
            ProfileStatus.Visibility = Visibility.Visible;
            if (ProfileAccountText.Parent is UIElement account)
                account.Visibility = Visibility.Collapsed;
            if (ProfileSocialText.Parent is UIElement social)
                social.Visibility = Visibility.Collapsed;
        }
        if (_profileInfoLoading || (_profileInfo != null && DateTime.UtcNow - _profileInfoUtc < TimeSpan.FromMinutes(5)))
            return;
        _profileInfoLoading = true;
        try
        {
            (RobloxChatStatus status, RobloxProfileInfo? info) = await RobloxProfile.GetMineAsync(_lifetimeToken);
            if (_closed)
                return;
            if (info == null)
            {
                if (_profileInfo == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                        ProfileStatus.Text = status switch
                        {
                            RobloxChatStatus.NotSignedIn => "Sign in to Roblox to see your profile.",
                            RobloxChatStatus.SignInExpired => "Your Roblox sign in expired, sign in again to see your profile.",
                            _ => "Your profile could not be loaded right now.",
                        }
                    );
                }
                return;
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed)
                    return;
                _profileInfo = info;
                _profileInfoUtc = DateTime.UtcNow;
                ShowProfileDetails(info);
            });
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your profile could not be loaded: " + ex.Message);
        }
        finally
        {
            _profileInfoLoading = false;
        }
    }

    private void ShowProfileDetails(RobloxProfileInfo info)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ProfileStatus.Visibility = Visibility.Collapsed;
        if (ProfileAccountText.Parent is UIElement account)
            account.Visibility = Visibility.Visible;
        if (ProfileSocialText.Parent is UIElement social)
            social.Visibility = Visibility.Visible;
        ProfileBannerName.Text = info.DisplayName.Length > 0 ? info.DisplayName : info.Name;
        ProfileBannerHandle.Text = "@" + info.Name + (info.Premium == true ? "  ·  Premium" : string.Empty);
        ProfileVerified.Visibility = info.Verified ? Visibility.Visible : Visibility.Collapsed;
        string joined = info.Created is DateTimeOffset created
            ? created.LocalDateTime.ToString("d MMM yyyy", culture) + " (" + AccountAge(created) + ")"
            : "Unavailable";
        ProfileAccountText.Text =
            "User ID: "
            + info.UserId.ToString(CultureInfo.InvariantCulture)
            + "\nJoined: "
            + joined
            + "\nRobux: "
            + (info.Robux is long robux ? robux.ToString("N0", culture) : "Unavailable")
            + "\nPremium: "
            + (info.Premium is bool premium ? (premium ? "Yes" : "No") : "Unavailable");
        ProfileSocialText.Text =
            "Friends: "
            + Count(info.Friends)
            + "\nFollowers: "
            + Count(info.Followers)
            + "\nFollowing: "
            + Count(info.Following)
            + "\nPlaying: "
            + (string.IsNullOrWhiteSpace(GameTitle.Text) ? "Roblox" : GameTitle.Text);
        string about = info.Description.Trim();
        ProfileAboutText.Text = about;
        ProfileAboutText.Visibility = about.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadProfilePicturesAsync(info);
    }

    private async Task LoadProfilePicturesAsync(RobloxProfileInfo info)
    {
        try
        {
            Task<System.Windows.Media.Imaging.BitmapSource?> headshotTask = Voidstrap.Utility.SafeImaging.FromHttpAsync(
                info.HeadshotUrl,
                144,
                _lifetimeToken
            );
            Task<System.Windows.Media.Imaging.BitmapSource?> bannerTask = info.BackgroundUrl.Length > 0
                ? Voidstrap.Utility.SafeImaging.FromHttpAsync(info.BackgroundUrl, 768, _lifetimeToken)
                : Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(null);
            await Task.WhenAll(headshotTask, bannerTask);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || !ReferenceEquals(_profileInfo, info))
                    return;
                if (ProfileHeadshotBrush.ImageSource == null)
                    ProfileHeadshotBrush.ImageSource = headshotTask.Result;
                if (ProfileBannerBrush.ImageSource == null)
                    ProfileBannerBrush.ImageSource = bannerTask.Result;
            });
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Profile images could not be loaded: " + ex.Message);
        }
    }

    private static string Count(long? value)
    {
        if (value is not long number)
            return "Unavailable";
        CultureInfo culture = CultureInfo.CurrentCulture;
        return number >= 1_000_000 ? (number / 1_000_000d).ToString("0.#", culture) + "M"
            : number >= 10_000 ? (number / 1_000d).ToString("0.#", culture) + "K"
            : number.ToString("N0", culture);
    }

    private static string AccountAge(DateTimeOffset created)
    {
        double days = (DateTimeOffset.UtcNow - created).TotalDays;
        int years = (int)(days / 365.25);
        if (years >= 1)
            return years == 1 ? "1 year" : years + " years";
        int months = (int)(days / 30.44);
        if (months >= 1)
            return months == 1 ? "1 month" : months + " months";
        int whole = Math.Max(0, (int)days);
        return whole == 1 ? "1 day" : whole + " days";
    }

    private string? ProfileLink =>
        _profileInfo is { UserId: > 0 } info
            ? "https://www.roblox.com/users/" + info.UserId.ToString(CultureInfo.InvariantCulture) + "/profile"
            : null;

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileLink is not { } link)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your profile could not be opened: " + ex.Message);
        }
    }

    private void CopyProfile_Click(object sender, RoutedEventArgs e) => _ = PortableOverlay.RunAsync(this, () => CopyProfileAsync());

    private async Task CopyProfileAsync()
    {
        if (ProfileLink is not { } link)
            return;
        try
        {
            Clipboard.SetText(link);
            CopyProfileButton.Content = "Copied";
            await Task.Delay(1200);
            if (!_closed)
                CopyProfileButton.Content = "Copy profile link";
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your profile link could not be copied: " + ex.Message);
        }
    }

    private void StartRobuxTimer()
    {
        if (_closed || _robuxTimer != null)
            return;
        _robuxTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _robuxTimer.Tick += OnRobuxTimerTick;
        _robuxTimer.Start();
    }

    private void OnRobuxTimerTick(object? sender, EventArgs e)
    {
        try
        {
            if (!_closed)
                _ = RefreshRobuxAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your Robux timer failed: " + ex.Message);
        }
    }

    private async Task RefreshRobuxAsync()
    {
        if (_closed || _robuxRefreshing || _selfUserId <= 0)
            return;
        _robuxRefreshing = true;
        try
        {
            long? robux = await RobloxEconomy.GetRobuxAsync(_selfUserId, _lifetimeToken);
            if (_closed || robux is not long value)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_closed)
                        return;
                    RobuxText.Text = value.ToString("N0", CultureInfo.CurrentCulture);
                    RobuxCard.Visibility = Visibility.Visible;
                    RobuxDivider.Visibility = Visibility.Visible;
                    QueueFitWidth();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("SessionDock", "Your Robux balance could not be shown: " + ex.Message);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your Robux balance could not be loaded: " + ex.Message);
        }
        finally
        {
            _robuxRefreshing = false;
        }
    }

    private async Task LoadRobuxPurchasesAsync()
    {
        bool owns = false;
        try
        {
            _ = RefreshRobuxAsync();
            if (_closed)
                return;
            if (_selfUserId <= 0)
            {
                ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_SignIn, "Sign in to see purchases"));
                return;
            }

            long universeId = _data is { } data ? (data.UniverseId > 0 ? data.UniverseId : data.UniverseDetails?.Data?.Id ?? 0) : 0;
            long placeId = _data?.PlaceId ?? 0;
            if (universeId <= 0 && placeId <= 0)
            {
                ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_LoadFailed, "Could not load purchases"));
                return;
            }

            (long UniverseId, long PlaceId) key = (universeId, placeId);
            if (_robuxPurchasesCache != null && _robuxPurchasesCacheKey == key && DateTime.UtcNow - _robuxPurchasesUtc < TimeSpan.FromMinutes(2))
            {
                ShowRobuxPurchases(_robuxPurchasesCache);
                return;
            }

            if (_robuxPurchasesLoading)
                return;

            _robuxPurchasesLoading = true;
            owns = true;
            ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Misc2_Loading, "Loading..."));

            IReadOnlyList<RobloxPurchase>? purchases = await RobloxEconomy.GetGamePurchasesAsync(_selfUserId, universeId, placeId, _lifetimeToken);
            if (_closed || _popoutView != PopoutView.Robux)
                return;
            if (purchases == null)
            {
                ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_LoadFailed, "Could not load purchases"));
                return;
            }

            _robuxPurchasesCacheKey = key;
            _robuxPurchasesUtc = DateTime.UtcNow;
            _robuxPurchasesCache = purchases;
            ShowRobuxPurchases(purchases);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your game purchases could not be loaded: " + ex.Message);
            if (!_closed && _popoutView == PopoutView.Robux)
                ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_LoadFailed, "Could not load purchases"));
        }
        finally
        {
            if (owns)
                _robuxPurchasesLoading = false;
        }
    }

    private void ShowRobuxStatus(string message)
    {
        try
        {
            RobuxList.ItemsSource = null;
            RobuxList.Visibility = Visibility.Collapsed;
            RobuxTotalText.Text = string.Empty;
            RobuxStatus.Text = message ?? string.Empty;
            RobuxStatus.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your Robux status could not be updated: " + ex.Message);
        }
    }

    private void ShowRobuxPurchases(IReadOnlyList<RobloxPurchase> purchases)
    {
        try
        {
            if (purchases == null || purchases.Count == 0)
            {
                ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_None, "No purchases in this game yet"));
                return;
            }

            List<RobuxPurchaseRow> rows = new(purchases.Count);
            long total = 0;
            foreach (RobloxPurchase purchase in purchases)
            {
                if (purchase == null)
                    continue;
                total = purchase.Amount > long.MaxValue - total ? long.MaxValue : total + purchase.Amount;
                string name = string.IsNullOrWhiteSpace(purchase.Name)
                    ? Res(Voidstrap.Resources.Strings.Ui_Session_Robux_DefaultName, "Purchase")
                    : purchase.Name;
                string typeLabel = FormatPurchaseType(purchase.Type);
                string when = string.Empty;
                try
                {
                    when = purchase.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                }
                catch
                {
                }
                string subtitle = string.IsNullOrEmpty(typeLabel) ? when : (string.IsNullOrEmpty(when) ? typeLabel : typeLabel + " · " + when);
                string amountText = SafeFormat(
                    Voidstrap.Resources.Strings.Ui_Session_Robux_Amount,
                    "R$ {0}",
                    purchase.Amount.ToString("N0", CultureInfo.CurrentCulture)
                );
                rows.Add(new RobuxPurchaseRow(name, subtitle, amountText));
            }

            if (rows.Count == 0)
            {
                ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_None, "No purchases in this game yet"));
                return;
            }

            RobuxStatus.Visibility = Visibility.Collapsed;
            RobuxList.ItemsSource = rows;
            RobuxList.Visibility = Visibility.Visible;
            string countText = rows.Count == 1
                ? Res(Voidstrap.Resources.Strings.Ui_Session_Robux_OnePurchase, "1 purchase")
                : SafeFormat(
                    Voidstrap.Resources.Strings.Ui_Session_Robux_PurchaseCount,
                    "{0} purchases",
                    rows.Count.ToString("N0", CultureInfo.CurrentCulture)
                );
            RobuxTotalText.Text = SafeFormat(
                Voidstrap.Resources.Strings.Ui_Session_Robux_Spent,
                "{0} · R$ {1} spent",
                countText,
                total.ToString("N0", CultureInfo.CurrentCulture)
            );
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionDock", "Your game purchases could not be rendered: " + ex.Message);
            ShowRobuxStatus(Res(Voidstrap.Resources.Strings.Ui_Session_Robux_LoadFailed, "Could not load purchases"));
        }
    }

    private static string Res(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SafeFormat(string? format, string fallback, params object[] args)
    {
        string template = string.IsNullOrWhiteSpace(format) ? fallback : format;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, fallback, args);
            }
            catch
            {
                return string.Join(" ", args);
            }
        }
    }

    private static string FormatPurchaseType(string type) => type switch
    {
        "DeveloperProduct" => Res(Voidstrap.Resources.Strings.Ui_Session_Robux_DeveloperProduct, "Developer Product"),
        "GamePass" => Res(Voidstrap.Resources.Strings.Ui_Session_Robux_GamePass, "Game Pass"),
        "PrivateServer" => Res(Voidstrap.Resources.Strings.Ui_Session_Robux_PrivateServer, "Private Server"),
        _ => type ?? string.Empty,
    };

    private double OpenCardTop()
    {
        Border card = _popoutView switch
        {
            PopoutView.Profile => ProfileCard,
            PopoutView.Robux => RobuxCard,
            _ => GameCard,
        };
        if (!card.IsVisible || !card.IsDescendantOf(this))
            return Pill.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
        return card.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
    }

    private void MarkOpenCard(Border? open)
    {
        foreach (Border card in new[] { ProfileCard, RobuxCard, GameCard })
        {
            if (ReferenceEquals(card, open))
                card.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            else
                card.ClearValue(Border.BackgroundProperty);
        }
    }

    private void HidePopout()
    {
        MarkOpenCard(null);
        GameChevron.Text = "\uE70E";
        ProfileChevron.Text = "\uE70E";
        RobuxChevron.Text = "\uE70E";
        if (_popout is { IsVisible: true } popout)
            popout.Hide();
        UpdateNowPlayingVisibility();
    }

    private void OnPopoutPointerDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_opened && _popout is { IsVisible: true })
            PortableOverlay.Focus(_popout);
    }

    private void OnPopoutSourceInitialized(object? sender, EventArgs e)
    {
        if (_popout == null)
            return;
        if (PortableOverlay.Active)
        {
            _popoutHandle = PortableOverlay.Handle(_popout);
            return;
        }
        _popoutHandle = new WindowInteropHelper(_popout).Handle;
        if (_popoutHandle == IntPtr.Zero)
            return;
        nint style = GetWindowLongPtrW(_popoutHandle, GwlExStyle);
        SetWindowLongPtrW(_popoutHandle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        int round = 2;
        _ = DwmSetWindowAttribute(_popoutHandle, 33, ref round, sizeof(int));
        int disabled = 1;
        _ = DwmSetWindowAttribute(_popoutHandle, 3, ref disabled, sizeof(int));
        OverlayDiagnostics.RegisterOverlayHandle(_popoutHandle);
    }

    private void PlacePopout()
    {
        if (!PopoutOpen)
            return;
        bool topEdge = App.Settings.Prop.OverlayLayout?.DockTopEdge == true;
        OverlayDockSide side = VisualSide;
        if (PortableOverlay.Active && _popout != null)
        {
            if (_linuxMotionActive)
                return;
            Rect position = Pill.TransformToAncestor(this).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
            RobloxWindowRect game = RobloxWindowTracker.Current;
            double portableWidth = Math.Min(position.Width, game.Width);
            double portableLeft = Math.Clamp(Left + position.Left, game.Left, game.Left + Math.Max(0, game.Width - portableWidth));
            double portableHeight;
            double portableTop;
            if (side != OverlayDockSide.None)
            {
                bool portableOnLeft = side == OverlayDockSide.Left;
                double room = portableOnLeft ? game.Left + game.Width - (Left + position.Right + PopoutGap) : Left + position.Left - PopoutGap - game.Left;
                portableWidth = Math.Max(1, Math.Min(SidePopoutWidth, room));
                portableLeft = portableOnLeft ? Left + position.Right + PopoutGap : Left + position.Left - PopoutGap - portableWidth;
                portableLeft = Math.Clamp(portableLeft, game.Left, game.Left + Math.Max(0, game.Width - portableWidth));
                portableHeight = Math.Max(1, Math.Min(PopoutHeight, game.Height));
                portableTop = Math.Clamp(
                    Top + OpenCardTop() - 8,
                    game.Top,
                    game.Top + Math.Max(0, game.Height - portableHeight)
                );
            }
            else if (topEdge)
            {
                portableTop = Top + position.Bottom + PopoutGap;
                portableHeight = Math.Max(1, Math.Min(PopoutHeight, game.Top + game.Height - portableTop));
            }
            else
            {
                portableHeight = Math.Max(1, Math.Min(PopoutHeight, Top + position.Top - PopoutGap - game.Top));
                portableTop = Math.Max(game.Top, Top + position.Top - PopoutGap - portableHeight);
            }
            PortableOverlay.Place(_popout, portableLeft, portableTop, portableWidth, portableHeight);
            if (Voidstrap.Utility.Platform.UsesLinuxHost)
                Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(
                    PortableOverlay.Handle(_popout),
                    (int)Math.Ceiling(portableWidth),
                    (int)Math.Ceiling(portableHeight),
                    12
                );
            return;
        }
        if (_popoutHandle == IntPtr.Zero)
            return;
        IntPtr dock = new WindowInteropHelper(this).Handle;
        if (dock == IntPtr.Zero || !GetWindowRect(dock, out NativeRect rect) || Pill.ActualWidth <= 0)
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Rect pill = Pill.TransformToAncestor(this).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
        int left = rect.Left + (int)Math.Round(pill.Left * dpi.DpiScaleX);
        int top = rect.Top + (int)Math.Round(pill.Top * dpi.DpiScaleY);
        int bottom = rect.Top + (int)Math.Round(pill.Bottom * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(pill.Width * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(PopoutHeight * dpi.DpiScaleY);
        int gap = (int)Math.Round(PopoutGap * dpi.DpiScaleY);
        if (side != OverlayDockSide.None)
        {
            bool onLeft = side == OverlayDockSide.Left;
            int right = rect.Left + (int)Math.Round(pill.Right * dpi.DpiScaleX);
            int sideGap = (int)Math.Round(PopoutGap * dpi.DpiScaleX);
            int sideWidth = (int)Math.Ceiling(SidePopoutWidth * dpi.DpiScaleX);
            int cardTop = rect.Top + (int)Math.Round((OpenCardTop() - 8) * dpi.DpiScaleY);
            int sideY = cardTop;
            if (RobloxWindowTracker.Current is { Valid: true } sideGame)
            {
                int room = onLeft ? sideGame.Left + sideGame.Width - right - sideGap : left - sideGap - sideGame.Left;
                sideWidth = Math.Max(1, Math.Min(sideWidth, room));
                height = Math.Min(height, Math.Max(1, sideGame.Height));
                sideY = Math.Clamp(cardTop, sideGame.Top, sideGame.Top + Math.Max(0, sideGame.Height - height));
            }
            _popoutX = onLeft ? right + sideGap : left - sideGap - sideWidth;
            if (RobloxWindowTracker.Current is { Valid: true } clampGame)
                _popoutX = Math.Clamp(_popoutX, clampGame.Left, clampGame.Left + Math.Max(0, clampGame.Width - sideWidth));
            _popoutY = sideY;
            _popoutSlideHorizontal = true;
            _popoutSlideFrom = (int)Math.Round(PopoutSlideDistance * dpi.DpiScaleX) * (onLeft ? -1 : 1);
            int sideLeft = _popoutSlide is { IsEnabled: true } ? _popoutX + SlideOffset() : _popoutX;
            SetWindowPos(_popoutHandle, HwndTopmost, sideLeft, sideY, sideWidth, height, 0x0010 | 0x0040);
            return;
        }
        if (RobloxWindowTracker.Current is { Valid: true } gameRect)
        {
            int room = topEdge ? gameRect.Top + gameRect.Height - bottom - gap : top - gap - gameRect.Top;
            if (room > 80)
                height = Math.Min(height, room);
        }
        int targetY = topEdge ? bottom + gap : top - gap - height;
        _popoutX = left;
        _popoutY = targetY;
        _popoutSlideHorizontal = false;
        _popoutSlideFrom = topEdge ? -(int)Math.Round(PopoutSlideDistance * dpi.DpiScaleY) : (int)Math.Round(PopoutSlideDistance * dpi.DpiScaleY);
        int y = _popoutSlide is { IsEnabled: true } ? targetY + SlideOffset() : targetY;
        SetWindowPos(_popoutHandle, HwndTopmost, left, y, width, height, 0x0010 | 0x0040);
    }

    private const double PopoutSlideDistance = 22;

    private static readonly TimeSpan PopoutSlideDuration = TimeSpan.FromMilliseconds(420);

    private static readonly Voidstrap.UI.SpringEase PopoutSlideEase = new();

    private DispatcherTimer? _popoutSlide;

    private DateTime _popoutSlideStart;

    private int _popoutX;

    private int _popoutY;

    private int _popoutSlideFrom;

    private bool _popoutSlideHorizontal;

    private int SlideOffset()
    {
        double progress = Math.Clamp((DateTime.UtcNow - _popoutSlideStart).TotalMilliseconds / PopoutSlideDuration.TotalMilliseconds, 0, 1);
        return (int)Math.Round(_popoutSlideFrom * (1 - PopoutSlideEase.Ease(progress)));
    }

    private void StartPopoutSlide()
    {
        if (_popoutHandle == IntPtr.Zero || PortableOverlay.Active || !Voidstrap.Utility.Platform.IsWindows)
            return;
        if (_popoutSlide == null)
        {
            _popoutSlide = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _popoutSlide.Tick += OnPopoutSlideTick;
        }
        _popoutSlideStart = DateTime.UtcNow;
        _popoutSlide.Start();
        OnPopoutSlideTick(null, EventArgs.Empty);
    }

    private void OnPopoutSlideTick(object? sender, EventArgs e)
    {
        if (_closed || !PopoutOpen || _popoutHandle == IntPtr.Zero)
        {
            _popoutSlide?.Stop();
            return;
        }
        bool done = DateTime.UtcNow - _popoutSlideStart >= PopoutSlideDuration;
        int offset = done ? 0 : SlideOffset();
        int x = _popoutSlideHorizontal ? _popoutX + offset : _popoutX;
        int y = _popoutSlideHorizontal ? _popoutY : _popoutY + offset;
        SetWindowPos(_popoutHandle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        if (done)
            _popoutSlide?.Stop();
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
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
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
            await UniverseDetails.FetchForEntriesAsync(new[] { data }, _lifetimeToken);
            universeId = data.UniverseId > 0 ? data.UniverseId : data.UniverseDetails?.Data?.Id ?? 0;
        }
        if (universeId <= 0 || universeId == _bannerUniverse || _closed)
            return;
        string url =
            "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds="
            + universeId
            + "&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false";
        using System.Net.Http.HttpResponseMessage response = await App.HttpClient.GetAsync(url, _lifetimeToken);
        if (!response.IsSuccessStatusCode)
            return;
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 512 * 1024, _lifetimeToken)
        );
        string? image = null;
        if (
            document.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement items)
            && items.ValueKind == System.Text.Json.JsonValueKind.Array
        )
        {
            foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
            {
                if (
                    !item.TryGetProperty("thumbnails", out System.Text.Json.JsonElement thumbnails)
                    || thumbnails.ValueKind != System.Text.Json.JsonValueKind.Array
                )
                    continue;
                foreach (System.Text.Json.JsonElement thumbnail in thumbnails.EnumerateArray())
                {
                    if (
                        thumbnail.TryGetProperty("imageUrl", out System.Text.Json.JsonElement value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String
                        && value.GetString() is { } candidate
                        && candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    )
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
        System.Windows.Media.Imaging.BitmapSource? banner = await Voidstrap.Utility.SafeImaging.FromHttpAsync(image, 820, _lifetimeToken);
        if (banner == null || _closed || !ReferenceEquals(_data, data))
            return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed || !ReferenceEquals(_data, data))
                return;
            BannerBrush.ImageSource = banner;
            Voidstrap.Utility.DynamicRenderSystem.SetPortableBrushImage(BannerImageHost, banner);
            _bannerUniverse = universeId;
        });
    }

    private async Task LoadFriendsAsync(ActivityData data)
    {
        if (_friendsLoading || _closed)
            return;
        if (
            string.Equals(_friendsJobId, data.JobId, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _friendsLoadedUtc < FriendsInterval
        )
            return;
        _friendsLoading = true;
        try
        {
            if (FriendsList.ItemsSource == null)
                FriendsStatus.Text = "Looking for friends";
            FriendsInServerResult result = await RobloxPresence.GetFriendsInServerAsync(data.UserId, data.JobId, _lifetimeToken);
            if (_closed || !ReferenceEquals(_data, data))
                return;
            DockFriend[] loadedFriends = await Task.WhenAll(
                result.Friends.Select(
                    async (friend, index) =>
                    {
                        System.Windows.Media.Imaging.BitmapSource? avatar = null;
                        if (index < 12)
                        {
                            try
                            {
                                avatar = await Voidstrap.Utility.SafeImaging.FromHttpAsync(friend.HeadshotUrl, 56, _lifetimeToken);
                            }
                            catch (Exception) { }
                        }
                        return new DockFriend(friend, avatar);
                    }
                )
            );
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || !ReferenceEquals(_data, data))
                    return;
                _friendsJobId = data.JobId ?? string.Empty;
                _friendsLoadedUtc = DateTime.UtcNow;
                FriendCountText.Text =
                    result.FriendCount > 0 ? "Friends: " + result.FriendCount.ToString(Locale.CurrentCulture) : string.Empty;
                List<DockFriend> friends = loadedFriends.ToList();
                FriendsList.ItemsSource = friends;
                FriendsHeader.Text = friends.Count > 0 ? $"Friends in this server ({friends.Count})" : "Friends in this server";
                FriendsStatus.Text = result.Status switch
                {
                    FriendsInServerStatus.NotSignedIn => "Sign in to Roblox in Voidstrap to see friends in this server",
                    FriendsInServerStatus.SignInExpired => "Your Roblox sign in expired, sign in again to see friends",
                    FriendsInServerStatus.Unavailable => "Friends could not be checked right now",
                    _ => friends.Count == 0 ? "None of your friends are in this server" : string.Empty,
                };
                FriendsStatus.Visibility = FriendsStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            });
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
            CloseDock();
            return;
        }
        if (IsQuickAction(action))
        {
            RunQuickToggle(action);
            return;
        }
        // A highlighted button closes its open panel again, like toggling a tab
        if (
            PanelActions.ContainsValue(action)
            && _panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? open)
            && !open.IsClosed
            && open.IsVisible
        )
        {
            if (action == "web")
                open.HideForSession();
            else
                open.CloseFromDock();
            return;
        }
        try
        {
            _action(action);
            _panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? active);
            RaiseAboveDimmer(active);
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
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
            Voidstrap.Platform.Linux.LinuxWindowInterop.ReleaseFocusAnchor();
        if (ReferenceEquals(ActiveInstance, this))
            ActiveInstance = null;
        _closed = true;
        _opened = false;
        Closing -= OnClosing;
        LocationChanged -= OnDockMoved;
        _nowPlayingTimer.Stop();
        _nowPlayingTimer.Tick -= OnNowPlayingTick;
        _islandClock.Stop();
        _islandClock.Tick -= OnIslandClockTick;
        if (_robuxTimer != null)
        {
            _robuxTimer.Stop();
            _robuxTimer.Tick -= OnRobuxTimerTick;
            _robuxTimer = null;
        }
        if (_popoutSlide != null)
        {
            _popoutSlide.Stop();
            _popoutSlide.Tick -= OnPopoutSlideTick;
        }
        NowPlayingPopup.IsOpen = false;
        Voidstrap.Integrations.Clipper.ClipperService.StateChanged -= OnClipperStateChanged;
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
        _slide.Stop(false);
        CancelLinuxDockAnimation();
        StopHeartbeat();
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
            Voidstrap.Platform.Linux.LinuxWindowShadow.Forget(PortableOverlay.Handle(this));
        if (_prewarmTimer != null)
        {
            _prewarmTimer.Stop();
            _prewarmTimer.Tick -= OnPrewarmTick;
            _prewarmTimer = null;
        }
        if (_focusLoss != null)
        {
            _focusLoss.Stop();
            _focusLoss.Tick -= OnFocusLossTick;
            _focusLoss = null;
        }
        if (_heartbeat != null)
        {
            _heartbeat.Stop();
            _heartbeat.Tick -= OnHeartbeat;
            _heartbeat = null;
        }
        PreviewKeyDown -= OnDockKey;
        PortableOverlay.Release(this);
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        Pill.SizeChanged -= OnPillSizeChanged;
        ProfileCard.SizeChanged -= OnPillSizeChanged;
        GameCard.SizeChanged -= OnPillSizeChanged;
        ActionButtons.SizeChanged -= OnPillSizeChanged;
        TrailingButtons.SizeChanged -= OnPillSizeChanged;
        DestroyDimmer();
        if (_popout != null)
        {
            PortableOverlay.Release(_popout);
            _popout.SourceInitialized -= OnPopoutSourceInitialized;
            _popout.PreviewKeyDown -= OnDockKey;
            _popout.PreviewMouseDown -= OnPopoutPointerDown;
            OverlayDiagnostics.UnregisterOverlayHandle(_popoutHandle);
            _popout.Content = null;
            try
            {
                _popout.Close();
            }
            catch (InvalidOperationException) { }
            _popout = null;
        }
        _anchor?.Dispose();
        _anchor = null;
        CancelRefresh();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _trackerLease.Dispose();
        GamePicture.Source = null;
        _data = null;
        GC.SuppressFinalize(this);
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
        // Its own slide is the only animation, Windows' fade when a window appears would fight it
        int disabled = 1;
        _ = DwmSetWindowAttribute(handle, 3, ref disabled, sizeof(int));
    }

    private void OnDockLoaded(object sender, RoutedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.UsesLinuxHost)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyLinuxDockShape));
    }

    private void ApplyLinuxDockShape()
    {
        if (_closed || !Voidstrap.Utility.Platform.UsesLinuxHost || !IsVisible || _linuxMotionActive)
            return;
        nint handle = PortableOverlay.Handle(this);
        if (
            handle == 0
            || !Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int width, out int height)
        )
            return;
        double scale = ActualWidth > 0 ? width / ActualWidth : 1;
        int radius = Math.Max(1, (int)Math.Round(16 * Math.Max(0.5, scale)));
        bool rounded = Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(handle, width, height, radius);
        Voidstrap.Platform.Linux.LinuxWindowShadow.Track(
            handle,
            rounded ? radius : 0,
            scale,
            !rounded,
            message => App.Logger.WriteLine("LinuxWindowShadow", message),
            true
        );
        if (!rounded)
            App.Logger.WriteLine("SessionDock", "Linux could not round the dock window, its soft shadow is suppressed");
    }
}

public sealed class DockFriend
{
    public string Name { get; }
    public string Username { get; }
    public ImageSource? Avatar { get; }

    public DockFriend(ServerFriend friend, ImageSource? avatar = null)
    {
        Name = friend.Label;
        Username = string.IsNullOrWhiteSpace(friend.Username) ? friend.Label : "@" + friend.Username;
        Avatar = avatar;
    }
}
