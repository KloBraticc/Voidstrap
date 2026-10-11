using System;
using System.Collections.Generic;
using System.Globalization;
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
using Voidstrap.Models.Persistable;
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
    private readonly CancellationToken _lifetimeToken;
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
    // Far enough that the pill and its shadow start fully below the window's bottom edge
    private static readonly TimeSpan EntranceLength = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ExitLength = TimeSpan.FromMilliseconds(220);
    // Room around the pill for its shadow, the window is the pill plus this
    private const double ShadowRoomX = 40;
    private int _availableWidth;
    private readonly NativeSlide _slide;
    private readonly TranslateTransform _linuxDockMotion = new();
    private int _linuxMotionGeneration;
    private bool _exiting;
    private static readonly TimeSpan FocusLossGrace = TimeSpan.FromMilliseconds(160);
    private DispatcherTimer? _focusLoss;
    private bool _activateOnSettle;
    private bool _popoutOnSettle;
    private bool _pinnedRestored;
    private bool _prewarming;
    private int _prewarmIndex;
    private DispatcherTimer? _prewarmTimer;
    // Built ahead of time while the dock is open so they appear the moment their button is clicked.
    // Server details, music and game history start loading as soon as they are built (game history asks Roblox
    // for the server list of every past game, which got rate limited), so those wait for a click.
    private static readonly string[] PrewarmActions = { "browser", "games", "chat", "adjustments" };
    private bool _profileLoaded;
    private bool _profileLoading;
    private enum PopoutView
    {
        Server,
        Profile
    }
    private PopoutView _popoutView = PopoutView.Server;
    private RobloxProfileInfo? _profileInfo;
    private DateTime _profileInfoUtc = DateTime.MinValue;
    private bool _profileInfoLoading;
    private bool _restoringPinned;
    // Panel keys mapped back to the dock action that opens them
    private static readonly Dictionary<string, string> PanelActions = new(StringComparer.Ordinal)
    {
        ["server"] = "details",
        ["notifications"] = "adjustments",
        ["browser"] = "browser",
        ["games"] = "games",
        ["chat"] = "chat",
        ["history"] = "history",
        ["music"] = "music",
        ["web"] = "web"
    };
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
        _lifetimeToken = _lifetime.Token;
        _activity = activity;
        _action = action;
        InitializeComponent();
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            AllowsTransparency = false;
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            Pill.RenderTransform = _linuxDockMotion;
            DockRoot.Margin = new Thickness(0);
            DockRoot.RowDefinitions[0].Height = new GridLength(0);
            DockRoot.RowDefinitions[1].Height = new GridLength(DockHeight);
            DockShadow1.Visibility = Visibility.Collapsed;
            DockShadow2.Visibility = Visibility.Collapsed;
            DockShadow3.Visibility = Visibility.Collapsed;
            DockShadow4.Visibility = Visibility.Collapsed;
            DockShadow5.Visibility = Visibility.Collapsed;
            Pill.Height = DockHeight;
            Height = DockHeight;
        }
        PortableOverlay.Prepare(this);
        PreviewKeyDown += OnDockKey;
        _slide = new NativeSlide(this);
        // The browser is built on Edge WebView2, which only exists on Windows
        if (!Voidstrap.Utility.Platform.IsWindows)
            WebButton.Visibility = Visibility.Collapsed;
        FitWidth();
        DetachServerPanel();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClock;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnDockLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        RobloxWindowTracker.Changed += OnTrackerChanged;
        _trackerLease = RobloxWindowTracker.Acquire();
    }

    private void OnDockKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        e.Handled = true;
        CloseDock();
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
        if (_prewarming)
        {
            panel.Prepare();
            return;
        }
        // Panels brought back because they were pinned must not take the keyboard from the game
        panel.Present(true, !_restoringPinned);
        UpdateActiveButtons();
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
        UpdateActiveButtons();
    }

    private void OnPanelDismissed(object? sender, EventArgs e) => CloseDock();

    private static string PanelKeyFor(string action) => action switch
    {
        "details" => "server",
        "adjustments" => "notifications",
        _ => action
    };

    // A dock button whose panel is open is filled with the accent colour, like a selected tab
    private void UpdateActiveButtons()
    {
        if (_closed)
            return;
        foreach (object child in ActionButtons.Children)
        {
            if (child is not Wpf.Ui.Controls.Button { Tag: string action } button)
                continue;
            bool open = _panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? panel) && panel.IsRequested;
            Wpf.Ui.Common.ControlAppearance appearance = open ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Transparent;
            if (button.Appearance != appearance)
                button.Appearance = appearance;
            if (button.Content is TextBlock glyph)
            {
                // Black on a light accent and white on a dark one, a near white accent left white icons unreadable
                if (open)
                    glyph.Foreground = Voidstrap.UI.Converters.ContrastForegroundConverter.For(TryFindResource("AccentFillColorDefaultBrush"));
                else
                    glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            }
        }
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
                });
                return;
            }
            RobloxChatUser? user = RobloxChat.GetCachedUser(self.Value);
            await Dispatcher.InvokeAsync(() =>
            {
                ProfileName.Text = user?.Label is { Length: > 0 } label ? label : "Roblox";
                ProfileHandle.Text = user?.Name is { Length: > 0 } name ? "@" + name : string.Empty;
            });
            _profileLoaded = true;
            Dictionary<long, string> urls = await RobloxChat.GetHeadshotUrlsAsync(new[] { self.Value }, _lifetimeToken);
            if (_closed || !urls.TryGetValue(self.Value, out string? url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;
            System.Windows.Media.Imaging.BitmapSource? avatar = await Voidstrap.Utility.SafeImaging.FromHttpAsync(uri.AbsoluteUri, 96, _lifetimeToken);
            if (avatar != null)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closed)
                        return;
                    ProfileAvatar.ImageSource = avatar;
                    ProfileFallback.Visibility = Visibility.Collapsed;
                });
        }
        catch (OperationCanceledException)
        {
        }
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
        catch (InvalidOperationException)
        {
        }
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
        _opened = false;
        bool returnFocus = RobloxWindowTracker.IsRobloxForeground();
        _focusLoss?.Stop();
        bool linuxSlide = animate && Voidstrap.Utility.Platform.IsLinux && IsVisible;
        bool slide = animate && NativeSlide.Supported && !Voidstrap.Utility.Platform.IsLinux && IsVisible && RobloxWindowTracker.Current.Valid;
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
            if (panel.IsPinned || keepPanels || !panel.IsRequested)
                panel.SetInteractive(false);
            else if (panel.KeepsState)
                panel.HideForSession();
            else
                panel.Close();
        }
        _prewarmTimer?.Stop();
        FadeDimmer(0);
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
        SuspendDock();
    }

    // Hidden because the game lost focus, it comes back on its own when the game is focused again
    private void SuspendDock()
    {
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
        if (!RobloxWindowTracker.IsRobloxForeground())
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
        // The caller already checked that Roblox is in front, make sure the cached bounds agree before anchoring
        RobloxWindowTracker.Refresh();
        _opened = true;
        UpdateReadout();
        ShowDimmer();
        // Hiding on focus loss is handled here with a short grace period, the anchor hiding instantly made the dock flicker
        _anchor ??= new RobloxOverlayAnchor(this, hideWhenUnfocused: false, placement: RobloxOverlayPlacement.BottomCenter);
        // Reopened while it was still sliding away: it turns around from where it is
        Point? resumeFrom = _exiting ? _slide.CurrentPosition() : null;
        CancelEntrance();
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
        ApplyLinuxDockShape();
        RaiseAboveDimmer();
        _clock.Start();
        _activateOnSettle = activatePanels;
        StartEntrance(resumeFrom);
    }

    // Slides the whole window up out of the game's bottom edge. The window is moved by Windows, nothing in it
    // is redrawn while it moves, which is what made the old animation stutter and slow the game down.
    private void StartEntrance(Point? resumeFrom)
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            StartLinuxEntrance();
            return;
        }
        RobloxWindowRect game = RobloxWindowTracker.Current;
        if (!NativeSlide.Supported || !game.Valid || _slide.CurrentPosition() is not Point target)
        {
            Settle();
            return;
        }
        Point from = resumeFrom ?? new Point(target.X, game.Top + game.Height);
        Int32Rect clip = new(game.Left, game.Top, game.Width, game.Height);
        if (!_slide.Start(from, target, clip, EntranceLength, true, OnEntranceFinished))
            Settle();
    }

    private void OnEntranceFinished()
    {
        if (_closed || !_opened)
            return;
        _slide.RemoveClip();
        // Put back exactly where it belongs in case the game moved while it slid
        _anchor?.Refresh();
        Settle();
    }

    // Everything that does real work waits until the dock has finished appearing
    private void Settle()
    {
        StartHeartbeat();
        if (_refreshTask is not { IsCompleted: false } && DateTime.UtcNow - _lastRefreshUtc >= RefreshInterval)
            StartRefresh();
        // Start loading the popout's banner and friends now so they are ready when it is opened
        _ = LoadPopoutAsync();
        _ = LoadProfileAsync();
        // Only panels that were open come back, ones built ahead of time stay hidden until clicked
        foreach (SessionPanelWindow panel in _panels.Values.ToArray())
        {
            if (panel.IsRequested)
                panel.Present(true, _activateOnSettle);
        }
        RestorePinnedPanels();
        StartPrewarm();
        if (_popoutOnSettle)
        {
            _popoutOnSettle = false;
            ShowPopout();
        }
        RaiseAboveDimmer();
    }

    // One panel per idle moment, so building them never makes the dock or the game hitch
    private void StartPrewarm()
    {
        if (_closed || Voidstrap.Utility.Platform.IsLinux)
            return;
        if (_prewarmTimer == null)
        {
            _prewarmTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(500) };
            _prewarmTimer.Tick += OnPrewarmTick;
        }
        _prewarmIndex = 0;
        _prewarmTimer.Start();
    }

    private void OnPrewarmTick(object? sender, EventArgs e)
    {
        if (_closed || !_opened || _slide.IsRunning)
        {
            if (_closed || !_opened)
                _prewarmTimer?.Stop();
            return;
        }
        while (_prewarmIndex < PrewarmActions.Length)
        {
            string action = PrewarmActions[_prewarmIndex++];
            if (_panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? existing) && !existing.IsClosed)
                continue;
            _prewarming = true;
            try
            {
                _action(action);
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
    }

    // The first time the dock is opened in a game, every panel left pinned comes back where it was
    private void RestorePinnedPanels()
    {
        if (_pinnedRestored)
            return;
        _pinnedRestored = true;
        Dictionary<string, SessionPanelLayout>? saved = App.State.Prop.SessionPanels;
        if (saved == null || saved.Count == 0)
            return;
        string[] pinned = saved.Where(pair => pair.Value is { Pinned: true } && !_panels.ContainsKey(pair.Key))
            .Select(pair => pair.Key).ToArray();
        if (pinned.Length == 0)
            return;
        _restoringPinned = true;
        try
        {
            foreach (string key in pinned)
            {
                if (_closed || !_opened)
                    break;
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
        _slide.Stop(true);
        CancelLinuxDockAnimation();
        _exiting = false;
        _popoutOnSettle = false;
    }

    private void StartLinuxEntrance()
    {
        _exiting = false;
        StartLinuxDockAnimation(DockHeight, 0, 0, 1, EntranceLength, EasingMode.EaseOut, Settle);
    }

    private void StartLinuxExit()
    {
        _exiting = true;
        StartLinuxDockAnimation(0, DockHeight, 1, 0, ExitLength, EasingMode.EaseIn, OnExitFinished);
    }

    private void StartLinuxDockAnimation(double fromY, double toY, double fromOpacity, double toOpacity, TimeSpan duration, EasingMode easingMode, Action completed)
    {
        int generation = ++_linuxMotionGeneration;
        QuadraticEase easing = new() { EasingMode = easingMode };
        DoubleAnimation motion = new(fromY, toY, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        DoubleAnimation opacity = new(fromOpacity, toOpacity, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        motion.Completed += (_, _) =>
        {
            if (generation != _linuxMotionGeneration)
                return;
            CancelLinuxDockAnimation();
            completed();
        };
        _linuxDockMotion.BeginAnimation(TranslateTransform.YProperty, motion, HandoffBehavior.SnapshotAndReplace);
        Pill.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelLinuxDockAnimation()
    {
        _linuxMotionGeneration++;
        _linuxDockMotion.BeginAnimation(TranslateTransform.YProperty, null);
        Pill.BeginAnimation(UIElement.OpacityProperty, null);
        _linuxDockMotion.Y = 0;
        Pill.Opacity = 1;
    }

    // Slides back down into the game's bottom edge, then hides
    private bool StartExit()
    {
        if (Voidstrap.Utility.Platform.IsLinux)
            return false;
        RobloxWindowRect game = RobloxWindowTracker.Current;
        if (_slide.CurrentPosition() is not Point from)
            return false;
        Int32Rect clip = new(game.Left, game.Top, game.Width, game.Height);
        _exiting = _slide.Start(from, new Point(from.X, game.Top + game.Height), clip, ExitLength, false, OnExitFinished);
        return _exiting;
    }

    private void OnExitFinished()
    {
        _exiting = false;
        if (_closed || _opened)
            return;
        if (IsVisible)
            Hide();
        _slide.RemoveClip();
    }

    // The window is only as wide as the pill and its shadow, a smaller window is cheaper for Windows to draw
    private void FitWidth()
    {
        if (PortableOverlay.Active)
            Pill.LayoutTransform = Transform.Identity;
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double shadowRoom = Voidstrap.Utility.Platform.IsLinux ? 0 : ShadowRoomX;
        double width = Math.Ceiling(Pill.DesiredSize.Width) + shadowRoom;
        if (PortableOverlay.Active && RobloxWindowTracker.Current is { Valid: true } bounds)
        {
            _availableWidth = bounds.Width;
            double available = Math.Max(1, bounds.Width - shadowRoom);
            double scale = Math.Min(1, available / Math.Max(1, width - shadowRoom));
            Pill.LayoutTransform = new ScaleTransform(scale, scale);
            width = Math.Min(bounds.Width, width);
        }
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            width = Math.Ceiling(width / 32) * 32;
            if (PortableOverlay.Active && _availableWidth > 0)
                width = Math.Min(_availableWidth, width);
        }
        if (width <= shadowRoom || Math.Abs(width - Width) < 0.5)
            return;
        Width = width;
        if (_opened && !_slide.IsRunning)
        {
            _anchor?.Refresh();
            ApplyLinuxDockShape();
        }
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
        if (PortableOverlay.Active && bounds.Valid && bounds.Width != _availableWidth)
            FitWidth();
        if (_opened && !bounds.Foreground)
        {
            // Focus passes through nothing for a moment when it moves between windows, only hide if it stays away
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
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
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
        if (PopoutOpen && _popoutView == PopoutView.Server && _data is { } data && DateTime.UtcNow - _friendsLoadedUtc >= FriendsInterval)
            _ = RefreshFriendsQuietlyAsync(data);
    }

    private async Task RefreshFriendsQuietlyAsync(ActivityData data)
    {
        try
        {
            await LoadFriendsAsync(data);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
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
        SetRun(SessionClock, data.TimeJoined == default ? "0:00" : FormatClock(DateTime.Now - data.TimeJoined));
        SetRun(ServerClock, _serverStarted.HasValue ? FormatClock(DateTimeOffset.UtcNow - _serverStarted.Value) : "--");
        if (!PopoutOpen)
            return;
        if (_serverType.Length == 0)
            _serverType = data.ServerType.ToConnectedString();
        SetText(BannerTitle, GameTitle.Text);
        SetText(BannerSubtitle, _serverType);
        SetText(ServerReadout, "Location: " + _location + "\nUptime: " + uptime + "\nPlayers: " + (_players.Length == 0 ? "Unavailable" : _players));
    }

    // Short clock style, 2:08 or 9:33:02
    private static string FormatClock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static void SetRun(System.Windows.Documents.Run run, string text)
    {
        if (!string.Equals(run.Text, text, StringComparison.Ordinal))
            run.Text = text;
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
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.28, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
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
        if (_closed || (!Voidstrap.Utility.Platform.IsLinux && !SystemParameters.ClientAreaAnimation))
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
        if (ProfilePanel.Parent is Panel profileParent)
            profileParent.Children.Remove(ProfilePanel);
        ProfilePanel.Visibility = Visibility.Visible;
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
        bool profile = _popoutView == PopoutView.Profile;
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
                FontFamily = FontFamily
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
        object view = profile ? ProfilePanel : ServerPanel;
        if (!ReferenceEquals(_popout.Content, view))
            _popout.Content = view;
        GameChevron.Text = profile ? "\uE70E" : "\uE70D";
        ProfileChevron.Text = profile ? "\uE70D" : "\uE70E";
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
        if (profile)
        {
            _ = LoadProfileDetailsAsync();
            return;
        }
        UpdateReadout();
        _ = LoadPopoutAsync();
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
                    await Dispatcher.InvokeAsync(() => ProfileStatus.Text = status switch
                    {
                        RobloxChatStatus.NotSignedIn => "Sign in to Roblox to see your profile.",
                        RobloxChatStatus.SignInExpired => "Your Roblox sign in expired, sign in again to see your profile.",
                        _ => "Your profile could not be loaded right now."
                    });
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
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
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
        ProfileBannerName.Text = info.DisplayName.Length > 0 ? info.DisplayName : info.Name;
        ProfileBannerHandle.Text = "@" + info.Name + (info.Premium == true ? "  ·  Premium" : string.Empty);
        ProfileVerified.Visibility = info.Verified ? Visibility.Visible : Visibility.Collapsed;
        string joined = info.Created is DateTimeOffset created
            ? created.LocalDateTime.ToString("d MMM yyyy", culture) + " (" + AccountAge(created) + ")"
            : "Unavailable";
        ProfileAccountText.Text = "User ID: " + info.UserId.ToString(CultureInfo.InvariantCulture)
            + "\nJoined: " + joined
            + "\nRobux: " + (info.Robux is long robux ? robux.ToString("N0", culture) : "Unavailable")
            + "\nPremium: " + (info.Premium is bool premium ? (premium ? "Yes" : "No") : "Unavailable");
        ProfileSocialText.Text = "Friends: " + Count(info.Friends)
            + "\nFollowers: " + Count(info.Followers)
            + "\nFollowing: " + Count(info.Following)
            + "\nPlaying: " + (string.IsNullOrWhiteSpace(GameTitle.Text) ? "Roblox" : GameTitle.Text);
        string about = info.Description.Trim();
        ProfileAboutText.Text = about;
        ProfileAboutText.Visibility = about.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadProfilePicturesAsync(info);
    }

    private async Task LoadProfilePicturesAsync(RobloxProfileInfo info)
    {
        try
        {
            string bannerUrl = info.AvatarUrl.Length > 0 ? info.AvatarUrl : info.HeadshotUrl;
            Task<System.Windows.Media.Imaging.BitmapSource?> headshotTask = Voidstrap.Utility.SafeImaging.FromHttpAsync(info.HeadshotUrl, 144, _lifetimeToken);
            Task<System.Windows.Media.Imaging.BitmapSource?> bannerTask = Voidstrap.Utility.SafeImaging.FromHttpAsync(bannerUrl, 300, _lifetimeToken);
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
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
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

    private string? ProfileLink => _profileInfo is { UserId: > 0 } info ? "https://www.roblox.com/users/" + info.UserId.ToString(CultureInfo.InvariantCulture) + "/profile" : null;

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

    private async void CopyProfile_Click(object sender, RoutedEventArgs e)
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

    private void HidePopout()
    {
        GameChevron.Text = "\uE70E";
        ProfileChevron.Text = "\uE70E";
        if (_popout is { IsVisible: true } popout)
            popout.Hide();
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

    // Sits directly above the dock with the same width, following it when the game window moves
    private void PlacePopout()
    {
        if (!PopoutOpen)
            return;
        if (PortableOverlay.Active && _popout != null)
        {
            Rect position = Pill.TransformToAncestor(this).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
            RobloxWindowRect game = RobloxWindowTracker.Current;
            double portableHeight = Math.Max(1, Math.Min(PopoutHeight, Top + position.Top - PopoutGap - game.Top));
            double portableWidth = Math.Min(position.Width, game.Width);
            double portableLeft = Math.Clamp(Left + position.Left, game.Left, game.Left + Math.Max(0, game.Width - portableWidth));
            PortableOverlay.Place(_popout, portableLeft, Math.Max(game.Top, Top + position.Top - PopoutGap - portableHeight), portableWidth, portableHeight);
            return;
        }
        if (_popoutHandle == IntPtr.Zero)
            return;
        IntPtr dock = new WindowInteropHelper(this).Handle;
        if (dock == IntPtr.Zero || !GetWindowRect(dock, out NativeRect rect) || Pill.ActualWidth <= 0)
            return;
        // Lined up with the pill itself, not the larger window around it that holds the shadow
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Rect pill = Pill.TransformToAncestor(this).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
        int left = rect.Left + (int)Math.Round(pill.Left * dpi.DpiScaleX);
        int top = rect.Top + (int)Math.Round(pill.Top * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(pill.Width * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(PopoutHeight * dpi.DpiScaleY);
        int gap = (int)Math.Round(PopoutGap * dpi.DpiScaleY);
        SetWindowPos(_popoutHandle, HwndTopmost, left, top - gap - height, width, height, 0x0010 | 0x0040);
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
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
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
            await UniverseDetails.FetchForEntriesAsync(new[] { data }, _lifetimeToken);
            universeId = data.UniverseId > 0 ? data.UniverseId : data.UniverseDetails?.Data?.Id ?? 0;
        }
        if (universeId <= 0 || universeId == _bannerUniverse || _closed)
            return;
        string url = "https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=" + universeId
            + "&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false";
        using System.Net.Http.HttpResponseMessage response = await App.HttpClient.GetAsync(url, _lifetimeToken);
        if (!response.IsSuccessStatusCode)
            return;
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 512 * 1024, _lifetimeToken));
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
        System.Windows.Media.Imaging.BitmapSource? banner = await Voidstrap.Utility.SafeImaging.FromHttpAsync(image, 820, _lifetimeToken);
        if (banner == null || _closed)
            return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed)
                return;
            BannerBrush.ImageSource = banner;
            _bannerUniverse = universeId;
        });
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
            FriendsInServerResult result = await RobloxPresence.GetFriendsInServerAsync(data.UserId, data.JobId, _lifetimeToken);
            if (_closed || !ReferenceEquals(_data, data))
                return;
            DockFriend[] loadedFriends = await Task.WhenAll(result.Friends.Select(async (friend, index) =>
            {
                System.Windows.Media.Imaging.BitmapSource? avatar = null;
                if (index < 12)
                {
                    try
                    {
                        avatar = await Voidstrap.Utility.SafeImaging.FromHttpAsync(friend.HeadshotUrl, 56, _lifetimeToken);
                    }
                    catch (Exception)
                    {
                    }
                }
                return new DockFriend(friend, avatar);
            }));
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || !ReferenceEquals(_data, data))
                    return;
                _friendsJobId = data.JobId ?? string.Empty;
                _friendsLoadedUtc = DateTime.UtcNow;
                FriendCountText.Text = result.FriendCount > 0 ? "Friends: " + result.FriendCount.ToString(Locale.CurrentCulture) : string.Empty;
                List<DockFriend> friends = loadedFriends.ToList();
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
        // A highlighted button closes its open panel again, like toggling a tab
        if (PanelActions.ContainsValue(action) && _panels.TryGetValue(PanelKeyFor(action), out SessionPanelWindow? open) && !open.IsClosed && open.IsVisible)
        {
            if (open.KeepsState)
            {
                open.HideForSession();
                UpdateActiveButtons();
            }
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
        _slide.Stop(false);
        if (Voidstrap.Utility.Platform.IsLinux)
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
            catch (InvalidOperationException)
            {
            }
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
        if (Voidstrap.Utility.Platform.IsLinux)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyLinuxDockShape));
    }

    private void ApplyLinuxDockShape()
    {
        if (!Voidstrap.Utility.Platform.IsLinux || !IsVisible)
            return;
        nint handle = PortableOverlay.Handle(this);
        if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int width, out int height))
            return;
        double scale = ActualWidth > 0 ? width / ActualWidth : 1;
        int radius = Math.Max(1, (int)Math.Round(16 * Math.Max(0.5, scale)));
        bool rounded = Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(handle, width, height, radius);
        Voidstrap.Platform.Linux.LinuxWindowShadow.Track(handle, rounded ? radius : 0, scale, !rounded, message => App.Logger.WriteLine("LinuxWindowShadow", message));
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
