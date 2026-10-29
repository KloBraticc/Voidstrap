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
    private string _location = "Unavailable";
    private string _players = string.Empty;
    private string _serverType = string.Empty;
    private bool _opened;
    private bool _closed;
    private bool _fitWidthQueued;
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

    private const double MaxDockWidth = 1180;
    private int _availableWidth;
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
    public static SessionDock? ActiveInstance { get; private set; }
    private bool _screenDragDimOverride;
    private static readonly string[] PrewarmActions = { "browser", "games", "chat", "layout", "adjustments", "details", "history", "music", "clips" };
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
        ["clips"] = "clips"
    };
    internal bool IsAnimating => _slide.IsRunning || _linuxMotionActive;

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
        ActiveInstance = this;
        _lifetimeToken = _lifetime.Token;
        _activity = activity;
        _action = action;
        InitializeComponent();
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            AllowsTransparency = false;
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
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
            Height = DockHeight;
        }
        PortableOverlay.Prepare(this);
        PreviewKeyDown += OnDockKey;
        _slide = new NativeSlide(this);
        // The browser is built on Edge WebView2, which only exists on Windows
        if (!Voidstrap.Utility.Platform.IsWindows)
            WebButton.Visibility = Visibility.Collapsed;
        ClipsButton.Visibility = App.Settings.Prop.ClipperEnabled ? Visibility.Visible : Visibility.Collapsed;
        Voidstrap.Integrations.Clipper.ClipperService.StateChanged += OnClipperStateChanged;
        FitWidth();
        DetachServerPanel();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClock;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnDockLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
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
        Visibility target = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (ClipsButton.Visibility != target)
        {
            ClipsButton.Visibility = target;
            QueueFitWidth();
        }
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

    public void CloseTool(string key)
    {
        if (_panels.TryGetValue(key, out SessionPanelWindow? panel) && !panel.IsClosed)
            panel.Close();
    }

    private void CloseServerPanels()
    {
        // These panels describe one specific server, so they are stale after a teleport
        foreach (string key in new[] { "server", "browser", "reviews" })
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
            Wpf.Ui.Controls.ControlAppearance appearance = open ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
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
                    QueueFitWidth();
                });
                return;
            }
            RobloxChatUser? user = RobloxChat.GetCachedUser(self.Value);
            await Dispatcher.InvokeAsync(() =>
            {
                ProfileName.Text = user?.Label is { Length: > 0 } label ? label : "Roblox";
                ProfileHandle.Text = user?.Name is { Length: > 0 } name ? "@" + name : string.Empty;
                QueueFitWidth();
            });
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
                    _profileLoaded = true;
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
        if (Voidstrap.Utility.Platform.IsLinux && RobloxWindowTracker.Current.Hwnd == IntPtr.Zero)
        {
            System.Diagnostics.StackTrace trace = new(1, false);
            List<string> callers = new();
            for (int index = 0; index < Math.Min(4, trace.FrameCount); index++)
                if (trace.GetFrame(index)?.GetMethod() is { } method)
                    callers.Add(method.DeclaringType?.Name + "." + method.Name);
            App.Logger.WriteLine("SessionDock", "Hiding the dock, called from " + string.Join(" < ", callers));
        }
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
            else
                panel.Close();
        }
        _prewarmTimer?.Stop();
        FadeDimmer(0);
        if (Voidstrap.Utility.Platform.IsLinux && !linuxSlide)
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
        if (Voidstrap.Utility.Platform.IsLinux && RobloxWindowTracker.Current.Hwnd == IntPtr.Zero)
            App.Logger.WriteLine("SessionDock", string.Format(
                "Closing because the game lost focus, active window 0x{0:x}, keyboard window 0x{1:x}, focus window 0x{2:x}, tracker valid {3}",
                (long)Voidstrap.Platform.Linux.LinuxWindowInterop.GetActiveTopLevelWindow(),
                (long)Voidstrap.Platform.Linux.LinuxWindowInterop.GetFocusedTopLevelWindow(),
                (long)Voidstrap.Platform.Linux.LinuxWindowInterop.FocusAnchor,
                RobloxWindowTracker.Current.Valid));
        SuspendDock();
    }

    // Hidden because the game lost focus, it comes back on its own when the game is focused again
    private void SuspendDock()
    {
        if (Voidstrap.Utility.Platform.IsLinux && RobloxWindowTracker.Current.Hwnd == IntPtr.Zero)
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
        RobloxWindowTracker.Refresh();
        _opened = true;
        UpdateClipperVisibility();
        UpdateReadout();
        ShowDimmer();
        _anchor ??= new RobloxOverlayAnchor(this, hideWhenUnfocused: false, placement: RobloxOverlayPlacement.BottomCenter, customPosition: ComputeDockPosition);
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
        if (activatePanels && Voidstrap.Utility.Platform.IsLinux && RobloxWindowTracker.Current is { Valid: true, Hwnd: var game } && game == IntPtr.Zero)
        {
            bool taken = Voidstrap.Platform.Linux.LinuxWindowInterop.TryTakeFocusFromNativeWindow((int)Math.Round(Left + Width / 2), (int)Math.Round(Top + Height / 2));
            App.Logger.WriteLine("SessionDock", taken
                ? "Asked the desktop to focus the dock over the game, focus window 0x" + ((long)Voidstrap.Platform.Linux.LinuxWindowInterop.FocusAnchor).ToString("x")
                : "The dock could not take focus from the game");
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
        int sideMargin = Math.Max(0, (int)Math.Round(12 * scaleX));
        int edgeMargin = Math.Max(1, (int)Math.Round(layout.SafeDockEdgeSpacing * scaleY));
        int minLeft = rect.Left + sideMargin;
        int maxLeft = Math.Max(minLeft, rect.Left + rect.Width - width - sideMargin);
        int left = minLeft + (int)Math.Round((maxLeft - minLeft) * (layout.SafeDockHorizontal / 100.0));
        int top = layout.DockTopEdge
            ? rect.Top + edgeMargin
            : rect.Top + Math.Max(0, rect.Height - height - edgeMargin);
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

    internal (string ProfileName, string ProfileHandle, ImageSource? ProfileAvatar, string GameTitle, ImageSource? GameIcon, string SessionTime, string ServerTime) GetLiveSnapshot()
        => (
            string.IsNullOrWhiteSpace(ProfileName.Text) ? "Roblox" : ProfileName.Text,
            string.IsNullOrWhiteSpace(ProfileHandle.Text) ? Voidstrap.Resources.Strings.Ui_Session_SignedOut : ProfileHandle.Text,
            ProfileAvatar.ImageSource,
            string.IsNullOrWhiteSpace(GameTitle.Text) ? "Roblox" : GameTitle.Text,
            GamePicture.Source,
            string.IsNullOrWhiteSpace(SessionClock.Text) ? "0:00" : SessionClock.Text,
            string.IsNullOrWhiteSpace(ServerClock.Text) ? "--" : ServerClock.Text);

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
            saved = new SessionPanelLayout { X = 0.15, Y = 0.12, Width = 540, Height = 460 };
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
        if (Voidstrap.Utility.Platform.IsLinux)
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
        bool topEdge = App.Settings.Prop.OverlayLayout?.DockTopEdge == true;
        Point from = resumeFrom ?? new Point(target.X, topEdge ? game.Top - Height : game.Top + game.Height);
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
    }

    private void Settle()
    {
        if (_closed || !_opened)
            return;
        ApplyLinuxDockShape();
        if (Voidstrap.Utility.Platform.IsLinux)
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
                App.Logger.WriteLine("SessionDock", $"Built the {action} panel ahead of time in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
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
            App.Logger.WriteLine("SessionDock", _opened ? "Every dock panel is ready" : "Every dock panel is ready before the dock was opened");
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
        if (!Voidstrap.Utility.Platform.IsLinux)
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
        if (!bounds.Valid || !IsVisible || Wpf.Ui.Animations.RenderReady.ReducedMotion)
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
            Interval = duration + TimeSpan.FromMilliseconds(1500)
        };
        _linuxMotionDeadline.Tick += OnLinuxMotionDeadline;
        Voidstrap.Platform.Linux.LinuxWindowShadow.Track(PortableOverlay.Handle(this), 16, 1, true, unmanaged: true);
        PlaceLinuxDockFrame(from);
        Wpf.Ui.Animations.RenderReady.Run(this, () =>
        {
            if (_closed || generation != _linuxMotionGeneration)
                return;
            _linuxMotionDeadline?.Start();
            DoubleAnimation motion = new(from, to, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = easingMode },
                FillBehavior = FillBehavior.Stop
            };
            motion.Completed += (_, _) => FinishLinuxDockAnimation(generation);
            CompositionTarget.Rendering += OnLinuxDockFrame;
            Wpf.Ui.Animations.RenderReady.Hold(this, duration);
            _linuxDockMotion.BeginAnimation(TranslateTransform.YProperty, motion, HandoffBehavior.SnapshotAndReplace);
        });
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
            _anchor?.Refresh();
            ApplyLinuxDockShape();
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
        if (Voidstrap.Utility.Platform.IsLinux)
            return false;
        RobloxWindowRect game = RobloxWindowTracker.Current;
        if (_slide.CurrentPosition() is not Point from)
            return false;
        bool topEdge = App.Settings.Prop.OverlayLayout?.DockTopEdge == true;
        Int32Rect clip = new(game.Left, game.Top, game.Width, game.Height);
        _exiting = _slide.Start(from, new Point(from.X, topEdge ? game.Top - Height : game.Top + game.Height), clip, ExitLength, false, OnExitFinished);
        return _exiting;
    }

    private void OnExitFinished()
    {
        _exiting = false;
        if (Voidstrap.Utility.Platform.IsLinux && !_opened)
            Voidstrap.Platform.Linux.LinuxWindowInterop.ReleaseFocusAnchor();
        if (_closed || _opened)
            return;
        if (IsVisible)
            Hide();
        if (!Voidstrap.Utility.Platform.IsLinux)
            _slide.RemoveClip();
    }

    private void FitWidth()
    {
        double userScale = (App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings()).SafeDockScale;
        Pill.LayoutTransform = Math.Abs(userScale - 1.0) > 0.005 ? new ScaleTransform(userScale, userScale) : Transform.Identity;
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double shadowRoom = Voidstrap.Utility.Platform.IsLinux ? 0 : ShadowRoomX;
        double width = Math.Min(MaxDockWidth * userScale, Math.Ceiling(Pill.DesiredSize.Width)) + shadowRoom;
        if (PortableOverlay.Active && RobloxWindowTracker.Current is { Valid: true } bounds)
        {
            _availableWidth = bounds.Width;
            double available = Math.Max(1, bounds.Width - shadowRoom);
            double fitScale = Math.Min(1, available / Math.Max(1, width - shadowRoom));
            double combinedScale = userScale * fitScale;
            Pill.LayoutTransform = Math.Abs(combinedScale - 1.0) > 0.005 ? new ScaleTransform(combinedScale, combinedScale) : Transform.Identity;
            Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Min(bounds.Width, Math.Ceiling(Pill.DesiredSize.Width) + shadowRoom);
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
        if (_opened && !_slide.IsRunning && !_linuxMotionActive)
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
        if (_linuxMotionActive && !bounds.Matches(_linuxMotionBounds))
            FinishLinuxDockAnimation(_linuxMotionGeneration);
        if (PortableOverlay.Active && bounds.Valid && bounds.Width != _availableWidth)
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
        if (!_opened && _resumeOnFocus && bounds.Valid && bounds.Foreground && !_resumeQueued)
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
        if (Voidstrap.Utility.Platform.IsLinux)
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
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            int generation = _linuxDimmerGeneration;
            TimeSpan duration = TimeSpan.FromMilliseconds(DimmerFadeMs);
            _dimmerFade.Stop();
            Wpf.Ui.Animations.RenderReady.Run(this, () =>
            {
                if (_closed || _dimmerFade == null || generation != _linuxDimmerGeneration)
                    return;
                _linuxDimmerFade.Y = target;
                DoubleAnimation fade = new(_dimmerFrom, target, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.Stop
                };
                fade.Completed += (_, _) => FinishLinuxDimmerFade(generation);
                CompositionTarget.Rendering += OnLinuxDimmerFrame;
                Wpf.Ui.Animations.RenderReady.Hold(this, duration);
                _linuxDimmerFade.BeginAnimation(TranslateTransform.YProperty, fade);
                _dimmerFade.Stop();
                _dimmerFade.Interval = duration + TimeSpan.FromMilliseconds(1500);
                _dimmerFade.Start();
            });
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

    private void OnLinuxDimmerFrame(object? sender, EventArgs e)
        => SetDimmerAlpha(_linuxDimmerFade.Y);

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
        if (Voidstrap.Utility.Platform.IsLinux)
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
            _ = PortableOverlay.RunAsync(this, () => RefreshFriendsQuietlyAsync(data));
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

    private void SetRun(System.Windows.Documents.Run run, string text)
    {
        if (string.Equals(run.Text, text, StringComparison.Ordinal))
            return;
        bool lengthChanged = run.Text.Length != text.Length;
        run.Text = text;
        if (lengthChanged)
            QueueFitWidth();
    }

    private void DockScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (DockScroll.ScrollableWidth <= 0)
            return;
        DockScroll.ScrollToHorizontalOffset(Math.Clamp(DockScroll.HorizontalOffset - e.Delta, 0, DockScroll.ScrollableWidth));
        e.Handled = true;
    }

    private void OnPillSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
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
            _ = PortableOverlay.RunAsync(this, LoadProfileDetailsAsync);
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

    private void CopyProfile_Click(object sender, RoutedEventArgs e)
        => _ = PortableOverlay.RunAsync(this, () => CopyProfileAsync());

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

    private void PlacePopout()
    {
        if (!PopoutOpen)
            return;
        bool topEdge = App.Settings.Prop.OverlayLayout?.DockTopEdge == true;
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
            if (topEdge)
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
            if (Voidstrap.Utility.Platform.IsLinux)
                Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(PortableOverlay.Handle(_popout), (int)Math.Ceiling(portableWidth), (int)Math.Ceiling(portableHeight), 12);
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
        int targetY = topEdge ? bottom + gap : top - gap - height;
        SetWindowPos(_popoutHandle, HwndTopmost, left, targetY, width, height, 0x0010 | 0x0040);
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
        if (Voidstrap.Utility.Platform.IsLinux)
            Voidstrap.Platform.Linux.LinuxWindowInterop.ReleaseFocusAnchor();
        if (ReferenceEquals(ActiveInstance, this))
            ActiveInstance = null;
        _closed = true;
        _opened = false;
        Closing -= OnClosing;
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
        if (_closed || !Voidstrap.Utility.Platform.IsLinux || !IsVisible || _linuxMotionActive)
            return;
        nint handle = PortableOverlay.Handle(this);
        if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int width, out int height))
            return;
        double scale = ActualWidth > 0 ? width / ActualWidth : 1;
        int radius = Math.Max(1, (int)Math.Round(16 * Math.Max(0.5, scale)));
        bool rounded = Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(handle, width, height, radius);
        Voidstrap.Platform.Linux.LinuxWindowShadow.Track(handle, rounded ? radius : 0, scale, !rounded, message => App.Logger.WriteLine("LinuxWindowShadow", message), true);
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
