using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models;
using Voidstrap.Models.APIs;
using Voidstrap.Models.Entities;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI.Chat;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Crosshair;
using Voidstrap.UI.Elements.Overlay;
using Voidstrap.UI.ViewModels.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class MenuContainer : WpfUiWindow
{
    private const int SessionDockHotkeyId = 0x564C;
    private static MenuContainer? _currentInstance;
    private readonly Watcher _watcher;
    private readonly ActivityWatcher? _activityWatcher;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _playTimer;
    private readonly HwndSource? _source;
    private readonly IntPtr _handle;

    private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

    private readonly CancellationToken _lifetime;
    private readonly object _sessionSync = new object();

    private DateTime _closestServerBackoffUntilUtc = DateTime.MinValue;
    private ServerInfo? _lastClosestServer;
    private long _lastClosestPlaceId;
    private int _memoryUpdateActive;
    private DateTime _lastMemoryRefreshUtc = DateTime.MinValue;
    private int _joinClosestActive;

    private ServerInformation? _serverInformationWindow;
    private SessionDock? _sessionDock;

    private ServerHistory? _gameHistoryWindow;

    private MusicPlayer? _musicPlayerWindow;

    private GamePassConsole? _gamePassWindow;

    private OutputConsole? _outputConsole;

    private DiagnosticsWindow? _diagnosticsWindow;

    private CancellationTokenSource? _sessionCts;

    private bool _closed;
    private bool _sessionDockHotkeyRegistered;
    private OverlayShortcut? _registeredShortcut;
    private bool _sessionDockHotkeySuspended;
    private SessionNotifier? _sessionNotifier;

    private static string TrimWithThreeDots(string text, int maxChars = 18)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }
        int num = maxChars - "...".Length;
        if (num <= 0)
        {
            return "...";
        }
        return string.Concat(text.AsSpan(0, num), "...");
    }

    private void LoadFlags()
    {
        try
        {
            string path = Path.Combine(Paths.Mods, "ClientSettings", "ClientAppSettings.json");
            if (!File.Exists(path))
            {
                FlagsTextBlock.Text = "Flags: 0";
                return;
            }
			int totalFlags = Voidstrap.Utility.JsonFile.Deserialize<Dictionary<string, JsonElement>>(path, JsonOptions.Tolerant, 16777216).Count;
            FlagsTextBlock.Text = $"Flags: {totalFlags}";
        }
        catch
        {
            FlagsTextBlock.Text = "Flags: Error";
        }
    }

    public MenuContainer(Watcher watcher)
    {
        _lifetime = _lifetimeCts.Token;
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _activityWatcher = watcher.ActivityWatcher;
        InitializeComponent();
        _currentInstance = this;
        _handle = new WindowInteropHelper(this).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessage);
        RefreshSessionDockHotkey();
        MenuContainerViewModel dataContext = (MenuContainerViewModel)(base.DataContext = new MenuContainerViewModel());
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            LoadFlags();
#if CROSSPLAT
            AttachTrayMenuProvider();
#endif
        }

        if (base.ContextMenu != null)
        {
            base.ContextMenu.DataContext = dataContext;
            base.ContextMenu.Opened += ContextMenu_Opened;
            base.ContextMenu.Closed += ContextMenu_Closed;
        }
        _memoryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10L)
        };
        _memoryTimer.Tick += MemoryTimer_Tick;
        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1L)
        };
        _playTimer.Tick += PlayTimer_Tick;
        if (_activityWatcher != null)
        {
            _activityWatcher.OnLogOpen += ActivityWatcher_OnLogOpen;
            _activityWatcher.OnGameJoin += ActivityWatcher_OnGameJoin;
            _activityWatcher.OnGameLeave += ActivityWatcher_OnGameLeave;
            if (!App.Settings.Prop.UseDisableAppPatch)
            {
                GameHistoryMenuItem.Visibility = Visibility.Visible;
            }
            MusicMenuItem.Visibility = Visibility.Visible;
        }
        if (_watcher.RichPresence != null)
        {
            RichPresenceMenuItem.Visibility = Visibility.Visible;
        }
        if (Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0 || App.Settings.Prop.FrameGenResumeIndex > 0)
        {
            FrameGenParentMenuItem.Visibility = Visibility.Visible;
            FrameGenMenuItem.IsChecked = Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0;
            FrameGenOverlayMenuItem.IsChecked = App.Settings.Prop.FrameGenOverlayShow;
            FrameGenSplitMenuItem.IsChecked = App.Settings.Prop.FrameGenSplitCompare;
        }
        VersionTextBlock.Text = "Voidstrap v" + App.Version;
        if (!string.IsNullOrEmpty(_activityWatcher?.LogLocation))
            LogTracerMenuItem.Visibility = Visibility.Visible;
        if (_activityWatcher?.InGame == true)
            ActivityWatcher_OnGameJoin(_activityWatcher, EventArgs.Empty);
        PrewarmJoinNotification();
    }

    public static void SyncSessionDockHotkey()
    {
        MenuContainer? instance = _currentInstance;
        if (instance == null || instance._closed)
            return;
        if (!instance.Dispatcher.CheckAccess())
        {
            instance.Dispatcher.BeginInvoke(new Action(instance.RefreshSessionDockHotkey));
            return;
        }
        instance.RefreshSessionDockHotkey();
    }

    // Lets the settings record a new shortcut without the current one firing while it is pressed
    public static void SuspendSessionDockHotkey(bool suspend)
    {
        MenuContainer? instance = _currentInstance;
        if (instance == null || instance._closed)
            return;
        instance.Dispatcher.BeginInvoke(new Action(() =>
        {
            instance._sessionDockHotkeySuspended = suspend;
            instance.RefreshSessionDockHotkey();
        }));
    }

    // Whether a shortcut can be registered, it fails when another app already owns it
    public static bool TryShortcut(OverlayShortcut shortcut)
    {
        MenuContainer? instance = _currentInstance;
        if (instance == null || instance._closed || !Voidstrap.Utility.Platform.IsWindows || !shortcut.IsSet)
            return true;
        const int probeId = 0x564D;
        if (!RegisterHotKey(instance._handle, probeId, shortcut.Modifiers | 0x4000, shortcut.Key))
            return false;
        UnregisterHotKey(instance._handle, probeId);
        return true;
    }

    public static string ShortcutText => App.Settings.Prop.SessionDockShortcut is { IsSet: true } shortcut ? shortcut.Describe() : string.Empty;

    private void RefreshSessionDockHotkey()
    {
        if (_closed || !Voidstrap.Utility.Platform.IsWindows)
            return;
        OverlayShortcut shortcut = App.Settings.Prop.SessionDockShortcut ??= new OverlayShortcut();
        bool enabled = App.Settings.Prop.SessionDockEnabled && _activityWatcher != null && shortcut.IsSet && !_sessionDockHotkeySuspended;
        if (enabled && _sessionDockHotkeyRegistered && shortcut.SameAs(_registeredShortcut))
            return;
        if (_sessionDockHotkeyRegistered)
        {
            UnregisterHotKey(_handle, SessionDockHotkeyId);
            _sessionDockHotkeyRegistered = false;
            _registeredShortcut = null;
        }
        if (!enabled)
        {
            // Only turning the dock off closes it, pausing the shortcut while a new one is recorded does not
            if (!App.Settings.Prop.SessionDockEnabled)
                CloseSessionDock();
            return;
        }
        // MOD_NOREPEAT so holding the keys does not toggle the dock over and over
        _sessionDockHotkeyRegistered = RegisterHotKey(_handle, SessionDockHotkeyId, shortcut.Modifiers | 0x4000, shortcut.Key);
        if (_sessionDockHotkeyRegistered)
            _registeredShortcut = shortcut.Copy();
        else
            App.Logger.WriteLine("MenuContainer", shortcut.Describe() + " could not be registered because the shortcut is unavailable");
    }

    private void CloseSessionDock()
    {
        SessionDock? dock = _sessionDock;
        _sessionDock = null;
        if (dock == null)
            return;
        dock.Closed -= SessionDock_Closed;
        try
        {
            dock.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SessionDock_Closed(object? sender, EventArgs e)
    {
        if (sender is not SessionDock dock)
            return;
        dock.Closed -= SessionDock_Closed;
        if (ReferenceEquals(_sessionDock, dock))
            _sessionDock = null;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312 || wParam.ToInt32() != SessionDockHotkeyId)
            return IntPtr.Zero;
        handled = true;
        // Anything thrown here would escape the window procedure and close Voidstrap, so the dock is fenced off
        try
        {
            ToggleSessionDock();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::SessionDock", ex);
            CloseSessionDock();
        }
        return IntPtr.Zero;
    }

    private void ToggleSessionDock()
    {
        if (_closed)
            return;
        // Closing always works, even when one of the dock panels holds focus instead of Roblox
        if (_sessionDock is { IsOpen: true } openDock)
        {
            openDock.CloseDock();
            return;
        }
        if (!App.Settings.Prop.SessionDockEnabled || _activityWatcher is not { InGame: true } activityWatcher
            || !(RobloxWindowTracker.IsProcessForeground(_watcher.RobloxProcessId) || RobloxWindowTracker.IsRobloxForeground()))
            return;
        if (_sessionDock == null)
        {
            _sessionDock = new SessionDock(activityWatcher, RunDockAction);
            _sessionDock.Closed += SessionDock_Closed;
        }
        _sessionDock.SetGame(activityWatcher.Data, activityWatcher.Data.GameName, CurrentGameIcon.Source as BitmapSource);
        _sessionDock.ToggleFromHotkey();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    private void RunDockAction(string action)
    {
        switch (action)
        {
            case "details":
                _sessionDock?.ShowTool("server", Strings.ContextMenu_ServerInformation_Title, () => new ServerInformation(_watcher));
                break;
            case "history":
                if (_activityWatcher != null)
                    _sessionDock?.ShowTool("history", Strings.ContextMenu_GameHistory_Title, () => new ServerHistory(_activityWatcher));
                break;
            case "browser":
                if (_activityWatcher is { } activityWatcher)
                    _sessionDock?.ShowTool("browser", "Servers", () => new SessionServerBrowser(activityWatcher, _watcher.ServerMatchmaker));
                break;
            case "games":
                if (_activityWatcher is { } gameActivityWatcher)
                    _sessionDock?.ShowTool("games", "Games", () => new SessionGameBrowser(gameActivityWatcher, _watcher.ServerMatchmaker));
                break;
            case "chat":
                _sessionDock?.ShowTool("chat", "Messages", () => new SessionChatWindow());
                break;
            case "music":
                _sessionDock?.ShowTool("music", "Music", () => new MusicPlayer(_activityWatcher));
                break;
            case "adjustments":
                _sessionDock?.ShowTool("notifications", "Settings",
                    () => new SessionNotificationSettings(CurrentGameIcon.Source, _activityWatcher?.Data?.GameName, SendTestNotification));
                break;
            case "invite":
                InviteDeeplinkMenuItem_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void PrewarmJoinNotification()
    {
        if (!Voidstrap.Utility.Platform.IsLinux || _activityWatcher == null || !App.Settings.Prop.NotificationWindowShow || !App.Settings.Prop.VoidNotify || !App.Settings.Prop.NotifyGameJoins)
            return;

        try
        {
            if (Application.Current.Resources["NotificationWindow"] is not NotificationWindow window || !window.IsUsable)
            {
                window = new NotificationWindow();
                Application.Current.Resources["NotificationWindow"] = window;
            }

            window.Prewarm();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::PrewarmJoinNotification", "The join notification could not be prepared: " + ex.Message);
        }
    }

    private void UpdateCurrentGameInfo(string gameName, BitmapSource? gameIcon)
    {
        if (string.IsNullOrEmpty(gameName))
        {
            CurrentGameMenuItem.Visibility = Visibility.Collapsed;
            CurrentGameIcon.Source = null;
            CurrentGameNameTextBlock.Text = "";
            RequestTrayRefresh();
            return;
        }
        CurrentGameMenuItem.Visibility = Visibility.Visible;
        CurrentGameIcon.Source = gameIcon;
        CurrentGameNameTextBlock.Text = TrimWithThreeDots(gameName);
        RequestTrayRefresh();
    }

    private void RequestTrayRefresh()
    {
#if CROSSPLAT
        if (!_closed && Voidstrap.Utility.Platform.IsLinux)
            Voidstrap.UI.Tray.LinuxTray.RequestMenuRefresh();
#endif
    }

    private static bool OverlayHelpAvailable()
    {
        if (!PlatformFeatureVisibility.IsSupported(Voidstrap.Platform.FeatureId.Overlay))
            return false;
        var prop = App.Settings.Prop;
        return OverlayWindow.SurfaceRequired || prop.Crosshair || Voidstrap.Integrations.Overlays.OverlaySettings.AnyEnabled;
    }

    private void SyncMenuState()
    {
        if (_closed)
            return;
        bool inGame = _activityWatcher?.InGame == true;
        bool transitioning = _activityWatcher?.IsTeleporting == true;
        DiscordRichPresence? richPresence = _watcher.RichPresence;
        RichPresenceMenuItem.Visibility = richPresence != null ? Visibility.Visible : Visibility.Collapsed;
        if (richPresence != null)
            RichPresenceMenuItem.IsChecked = richPresence.IsUserVisible;
        GameHistoryMenuItem.Visibility = _activityWatcher != null && !App.Settings.Prop.UseDisableAppPatch ? Visibility.Visible : Visibility.Collapsed;
        MusicMenuItem.Visibility = _activityWatcher != null ? Visibility.Visible : Visibility.Collapsed;
        bool frameGen = PlatformFeatureVisibility.IsSupported(Voidstrap.Platform.FeatureId.FrameGeneration)
            && (Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0 || App.Settings.Prop.FrameGenResumeIndex > 0);
        FrameGenParentMenuItem.Visibility = frameGen ? Visibility.Visible : Visibility.Collapsed;
        if (frameGen)
        {
            FrameGenMenuItem.IsChecked = Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0;
            FrameGenOverlayMenuItem.IsChecked = App.Settings.Prop.FrameGenOverlayShow;
            FrameGenSplitMenuItem.IsChecked = App.Settings.Prop.FrameGenSplitCompare;
        }
        CantSeeOverlaysMenuItem.Visibility = OverlayHelpAvailable() ? Visibility.Visible : Visibility.Collapsed;
        LogTracerMenuItem.Visibility = !string.IsNullOrEmpty(_activityWatcher?.LogLocation) ? Visibility.Visible : Visibility.Collapsed;
        OutputConsoleMenuItem.Visibility = inGame && ActivityWatcher.PlayerLoggingEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (inGame)
        {
            BrightnessTrackerLog.Visibility = Visibility.Visible;
            ColorsTrackerLog.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Collapsed : BrightnessTrackerLog.Visibility;
        }
        else if (!transitioning)
        {
            InviteDeeplinkMenuItem.Visibility = Visibility.Collapsed;
            ServerDetailsMenuItem.Visibility = Visibility.Collapsed;
            GamePassDetailsMenuItem.Visibility = Visibility.Collapsed;
            JoinClosestServerMenuItem.Visibility = Visibility.Collapsed;
            BrightnessTrackerLog.Visibility = Visibility.Collapsed;
            ColorsTrackerLog.Visibility = Visibility.Collapsed;
            CurrentGameMenuItem.Visibility = Visibility.Collapsed;
        }
        if (base.ContextMenu != null)
            UpdateSeparators(base.ContextMenu.Items);
    }

    private static void UpdateSeparators(ItemCollection items)
    {
        Separator? pending = null;
        bool anyVisible = false;
        foreach (object? entry in items)
        {
            if (entry is Separator separator)
            {
                separator.Visibility = Visibility.Collapsed;
                if (anyVisible && pending == null)
                    pending = separator;
                continue;
            }
            if (entry is UIElement element && element.Visibility == Visibility.Visible)
            {
                if (pending != null)
                {
                    pending.Visibility = Visibility.Visible;
                    pending = null;
                }
                anyVisible = true;
            }
        }
    }

    private CancellationToken StartSession()
    {
        lock (_sessionSync)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();
            return _sessionCts.Token;
        }
    }

    private void CancelSession()
    {
        lock (_sessionSync)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }

    private bool IsCurrentSession(ActivityData data, CancellationToken token)
    {
        return !_closed && !token.IsCancellationRequested && _activityWatcher?.InGame == true && ReferenceEquals(_activityWatcher.Data, data);
    }

    private static async Task<(string Name, BitmapSource? Icon)> LoadGamePresentationAsync(ActivityData data, CancellationToken token)
    {
        string universeName = data.UniverseDetails?.Data.Name ?? "Roblox Experience";
        string? iconUrl = data.UniverseDetails?.Thumbnail.ImageUrl;
        if (data.UniverseDetails == null)
        {
            try
            {
                await UniverseDetails.FetchForEntriesAsync([data], token);
                iconUrl = data.UniverseDetails?.Thumbnail.ImageUrl;
                universeName = data.UniverseDetails?.Data.Name ?? universeName;
            }
			catch (OperationCanceledException)
			{
				throw;
			}
            catch
            {
            }
        }
        token.ThrowIfCancellationRequested();
        BitmapSource? gameIcon = null;
        try
        {
            gameIcon = await Voidstrap.Utility.AppImage.LoadAsync(iconUrl ?? string.Empty, 128, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::LoadGameIcon", ex);
        }
        return (universeName, gameIcon);
    }

    private async Task UpdateCurrentGameIconAsync(ActivityData data, Task<(string Name, BitmapSource? Icon)> presentationTask, CancellationToken token)
    {
        try
        {
            (string name, BitmapSource? icon) = await presentationTask;
            if (!IsCurrentSession(data, token))
                return;
            await Dispatcher.InvokeAsync(delegate
            {
                if (!IsCurrentSession(data, token))
                    return;
                UpdateCurrentGameInfo(name, icon);
                _sessionDock?.SetGame(data, name, icon);
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void ShowServerInformationWindow()
    {
        ShowChildWindow(ref _serverInformationWindow, () => new ServerInformation(_watcher));
    }

    private void ShowChildWindow<T>(ref T? window, Func<T> factory) where T : Window
    {
        if (_closed)
            return;
        try
        {
            if (window == null)
            {
                window = factory();
                window.Closed += ChildWindow_Closed;
            }
            if (window.IsVisible)
                window.Activate();
            else
                window.ShowOwnedDialog();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::ShowChildWindow", ex);
            if (window != null)
                window.Closed -= ChildWindow_Closed;
            window = null;
        }
    }

    private async Task<ServerInfo?> FetchClosestServerAsync(long placeId, CancellationToken token)
    {
        if (ActivityWatcher.ServerListRateLimited)
        {
            _closestServerBackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            return null;
        }
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await App.HttpClient.GetAsync($"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100", HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _closestServerBackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            ActivityWatcher.NoteServerListRateLimited(TimeSpan.FromMinutes(2));
            return null;
        }
        response.EnsureSuccessStatusCode();
        ServerListResponse? servers = JsonSerializer.Deserialize<ServerListResponse>(await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, timeoutCts.Token), JsonOptions.Tolerant);
        return servers?.Data?.OrderBy(server => server.Ping).FirstOrDefault();
    }

    private async Task UpdateClosestServerMenuItemText()
    {
        ActivityData? data = _activityWatcher?.Data;
        if (_closed || data == null || data.PlaceId == 0)
        {
            JoinClosestServerMenuItem.Visibility = Visibility.Collapsed;
            return;
        }
        JoinClosestServerMenuItem.Visibility = Visibility.Visible;
        if (DateTime.UtcNow < _closestServerBackoffUntilUtc)
        {
            JoinClosestServerTextBlock.Text = _lastClosestServer != null && _lastClosestPlaceId == data.PlaceId
                ? $"Join Closest ({_lastClosestServer.Ping}ms)"
                : "Rate Limited";
            return;
        }
        try
        {
            ServerInfo? server = await FetchClosestServerAsync(data.PlaceId, CancellationToken.None);
            if (_closed || !ReferenceEquals(_activityWatcher?.Data, data))
                return;
            if (server != null)
            {
                _lastClosestServer = server;
                _lastClosestPlaceId = data.PlaceId;
            }
            JoinClosestServerTextBlock.Text = _lastClosestServer != null && _lastClosestPlaceId == data.PlaceId
                ? $"Join Closest ({_lastClosestServer.Ping}ms)"
                : DateTime.UtcNow < _closestServerBackoffUntilUtc ? "Rate Limited" : "No Servers Detected";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::UpdateClosestServer", ex);
            JoinClosestServerTextBlock.Text = _lastClosestServer != null && _lastClosestPlaceId == data.PlaceId ? $"Join Closest ({_lastClosestServer.Ping}ms)" : "Error Fetching Servers";
        }
    }

    private async void MemoryTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateRobloxMemoryAsync();
    }

    private async Task UpdateRobloxMemoryAsync()
    {
        if (_closed || DateTime.UtcNow - _lastMemoryRefreshUtc < TimeSpan.FromSeconds(10)
            || Interlocked.Exchange(ref _memoryUpdateActive, 1) != 0)
            return;
        try
        {
            _lastMemoryRefreshUtc = DateTime.UtcNow;
#if CROSSPLAT
            _trayInfoRefreshedUtc = DateTime.UtcNow;
#endif
            long? robloxMemory = Voidstrap.Utility.Platform.IsLinux
                ? await Voidstrap.Platform.Linux.LinuxSoberMemory.ReadAsync(new Voidstrap.Core.SystemProcessService(), _lifetime)
                : await Task.Run(ReadRobloxMemory, _lifetime);
            if (!_closed && (Voidstrap.Utility.Platform.IsLinux || _activityWatcher?.InGame == true))
            {
                string text = "Roblox: " + (robloxMemory.HasValue ? FormatBytes(robloxMemory.Value) : "N/A");
                if (MemoryTextBlock.Text != text)
                {
                    MemoryTextBlock.Text = text;
                    RequestTrayRefresh();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::RefreshTrayInfo", "Roblox memory could not be read: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _memoryUpdateActive, 0);
        }
    }

    private static long ReadRobloxMemory()
    {
        long total = 0;
        foreach (Process process in Process.GetProcessesByName(Voidstrap.Utility.Platform.RobloxPlayerProcessName))
        {
            using (process)
            {
                try
                {
                    total += process.WorkingSet64;
                }
                catch
                {
                }
            }
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.##} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.##} KB";
        return $"{Math.Max(0, bytes)} B";
    }

    private async void JoinClosestServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ActivityData? data = _activityWatcher?.Data;
        if (data == null || data.PlaceId == 0)
        {
            Frontend.ShowMessageBox("No active game detected.");
            return;
        }
        if (Interlocked.Exchange(ref _joinClosestActive, 1) != 0)
            return;
        JoinClosestServerMenuItem.IsEnabled = false;
        try
        {
            ServerInfo? server = await FetchClosestServerAsync(data.PlaceId, _lifetime);
            if (server != null)
            {
                _lastClosestServer = server;
                _lastClosestPlaceId = data.PlaceId;
            }
            if (_lastClosestServer == null || _lastClosestPlaceId != data.PlaceId)
                Frontend.ShowMessageBox(DateTime.UtcNow < _closestServerBackoffUntilUtc ? "Rate limited and no cached server available." : "No servers available.");
            else
                JoinServer(_lastClosestServer.Id, data.PlaceId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::JoinClosestServer", ex);
            Frontend.ShowMessageBox("Failed to join server:\n" + ex.Message);
        }
        finally
        {
            JoinClosestServerMenuItem.IsEnabled = true;
            Interlocked.Exchange(ref _joinClosestActive, 0);
        }
    }

    private void JoinServer(string serverId, long placeId)
    {
        try
        {
            string processPath = Paths.LaunchExecutable;
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(processPath) ?? string.Empty
            };
            startInfo.ArgumentList.Add("-player");
            startInfo.ArgumentList.Add($"roblox://experiences/start?placeId={placeId}&gameInstanceId={Uri.EscapeDataString(serverId)}");
            using Process? successor = Process.Start(startInfo);
            if (successor == null)
                throw new InvalidOperationException("Voidstrap did not start.");
            _watcher.KillRobloxProcess();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Failed to join server:\n" + ex.Message);
        }
    }

    public void ActivityWatcher_OnLogOpen(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (!_closed)
                {
                    LogTracerMenuItem.Visibility = Visibility.Visible;
                    RequestTrayRefresh();
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static readonly TimeSpan NotificationEnrichTimeout = TimeSpan.FromSeconds(8);

    private static string CompactServerLocation(string location)
    {
        string[] parts = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3 ? parts[0] + ", " + parts[^1] : location;
    }

    private async Task ShowJoinNotification(ActivityData data, Task<(string Name, BitmapSource? Icon)> presentationTask, CancellationToken token)
    {
        if (!App.Settings.Prop.VoidNotify || !App.Settings.Prop.NotifyGameJoins || !App.Settings.Prop.NotificationWindowShow || _activityWatcher == null)
        {
            return;
        }
        if (Voidstrap.Utility.Platform.IsLinux && Voidstrap.Platform.Linux.SoberNativeSettings.IsServerLocationIndicatorEnabled())
        {
            App.Logger.WriteLine("MenuContainer::ShowJoinNotification", "Sober shows its own server location notice, skipping the join notification");
            return;
        }
        Task<string?> locationTask = data.QueryServerLocation(token);
        Task<string> uptimeTask = LoadJoinUptimeAsync(data, token);
        Task delayTask = Task.Delay(2500, token);
        string universeName;
        BitmapSource? notificationIcon;
        try
        {
            (universeName, notificationIcon) = await presentationTask.WaitAsync(NotificationEnrichTimeout, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
            universeName = data.GameName ?? "Roblox";
            notificationIcon = null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::LoadGamePresentation", ex);
            universeName = data.GameName ?? "Roblox";
            notificationIcon = null;
        }
        try
        {
            await delayTask;
        }
		catch (OperationCanceledException)
		{
			return;
		}
        if (!App.Settings.Prop.VoidNotify || !App.Settings.Prop.NotifyGameJoins || !App.Settings.Prop.NotificationWindowShow || !IsCurrentSession(data, token))
            return;
		Task<(int Current, int Max, int GameTotal, bool ServerFound)> statsTask = _activityWatcher.GetServerPlayerStatsAsync();
        string serverLocation = "Server location unavailable";
        try
        {
            string? location = await locationTask.WaitAsync(NotificationEnrichTimeout, token);
            if (!string.IsNullOrWhiteSpace(location))
                serverLocation = location;
        }
        catch (OperationCanceledException)
        {
            return;
        }
		catch
		{
		}
		(int, int, int, bool) tuple = (0, 0, 0, false);
		try
		{
			tuple = await statsTask.WaitAsync(NotificationEnrichTimeout, token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (TimeoutException)
		{
			App.Logger.WriteLine("MenuContainer::ShowJoinNotification", "The player count was not ready in time, showing the join notification without it");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("MenuContainer::GetServerPlayerStats", ex);
		}
        if (!App.Settings.Prop.VoidNotify || !App.Settings.Prop.NotifyGameJoins || !App.Settings.Prop.NotificationWindowShow || !IsCurrentSession(data, token))
            return;
        string text2 = tuple.Item1 > 0 && tuple.Item2 > 0
            ? $" • {tuple.Item1}/{tuple.Item2} players"
            : tuple.Item1 == 1
                ? " • 1 player"
                : tuple.Item1 > 1 ? $" • {tuple.Item1} players" : string.Empty;
        BitmapSource? flagImage = null;
        try
        {
            flagImage = await Voidstrap.Utility.CountryFlag.GetImageAsync(data.ServerCountryCode, token).WaitAsync(NotificationEnrichTimeout, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::ShowJoinNotification", "Flag lookup failed: " + ex.Message);
        }
        if (Voidstrap.Utility.Platform.IsLinux && text2.Length > 0)
            serverLocation = CompactServerLocation(serverLocation);
        string uptime = await uptimeTask;
        // Uptime is left out when Roblox does not reveal it, the player count then gets the line to itself
        string lastLine = uptime.Length > 0
            ? "Uptime: " + uptime + text2
            : text2.StartsWith(" • ", StringComparison.Ordinal) ? text2[3..] : text2.Trim();
        string details = "Location: " + (flagImage != null ? NotificationWindow.FlagPlaceholder.ToString() : string.Empty)
            + serverLocation + (lastLine.Length > 0 ? "\n" + lastLine : string.Empty);
        string status = data.ServerType.ToConnectedString();
        try
        {
            await Dispatcher.InvokeAsync(delegate
            {
                if (!App.Settings.Prop.VoidNotify || !App.Settings.Prop.NotifyGameJoins || !App.Settings.Prop.NotificationWindowShow || !IsCurrentSession(data, token))
                    return;
                if (!App.Settings.Prop.ServerDetailsInOverlay)
                {
                    // Chosen in the notification settings: a Windows notification instead of the in game one
                    Frontend.ShowBalloonTip(universeName, status + "\n" + details.Replace(NotificationWindow.FlagPlaceholder.ToString(), string.Empty), System.Windows.Forms.ToolTipIcon.None, 8);
                    return;
                }
                try
                {
                    NotificationWindow? notificationWindow = Application.Current.Resources["NotificationWindow"] as NotificationWindow;
                    if (notificationWindow == null || !notificationWindow.IsUsable)
                    {
                        notificationWindow = new NotificationWindow();
                        Application.Current.Resources["NotificationWindow"] = notificationWindow;
                    }
                    App.Logger.WriteLine(
                        "MenuContainer::ShowJoinNotification",
                        "Join notification icon: " + (notificationIcon != null ? notificationIcon.PixelWidth + "x" + notificationIcon.PixelHeight + " frozen " + notificationIcon.IsFrozen : "none, thumbnail url was " + (string.IsNullOrEmpty(data.UniverseDetails?.Thumbnail?.ImageUrl) ? "empty" : data.UniverseDetails.Thumbnail.ImageUrl)));
                    notificationWindow.ShowNotification(universeName, details, notificationIcon, 8.0, flagImage, status, token, Voidstrap.UI.NotificationKind.Server);
                    QueueSessionDockTip(notificationWindow);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("MenuContainer::ShowJoinNotification", ex);
                }
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    // Reminds about the dock shortcut after the join notification, the very first time or every game if chosen
    private void QueueSessionDockTip(NotificationWindow notificationWindow)
    {
        try
        {
            if (!App.Settings.Prop.SessionDockEnabled || !_sessionDockHotkeyRegistered || !Voidstrap.Utility.Platform.IsWindows)
                return;
            if (App.State.Prop.SessionDockTipShown && !App.Settings.Prop.NotifyShortcutEveryGame)
                return;
            string shortcut = ShortcutText;
            if (shortcut.Length == 0)
                return;
            if (!App.State.Prop.SessionDockTipShown)
            {
                App.State.Prop.SessionDockTipShown = true;
                App.State.SaveDeferred();
            }
            notificationWindow.ShowNotification("Voidstrap overlay is ready", "Press " + shortcut + " to open it.", null, 7.0, null, string.Empty,
                default, Voidstrap.UI.NotificationKind.Server);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::SessionDockTip", "The session dock tip could not be shown: " + ex.Message);
        }
    }

    private NotificationWindow GetNotificationWindow()
    {
        if (Application.Current.Resources["NotificationWindow"] is not NotificationWindow window || !window.IsUsable)
        {
            window = new NotificationWindow();
            Application.Current.Resources["NotificationWindow"] = window;
        }
        return window;
    }

    private static bool OverlayNotificationsOn => App.Settings.Prop.VoidNotify && App.Settings.Prop.NotificationWindowShow;

    // From the notification settings: shows a notification with the saved look right now
    public void SendTestNotification()
    {
        if (_closed)
            return;
        try
        {
            GetNotificationWindow().ShowNotification("Test notification", "This is how Voidstrap notifications look.",
                CurrentGameIcon.Source as BitmapSource, 5.0, null, string.Empty, default, Voidstrap.UI.NotificationKind.Server);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::TestNotification", "The test notification could not be shown: " + ex.Message);
        }
    }

    private void StartSessionNotifier()
    {
        if (_sessionNotifier != null || _activityWatcher == null || !Voidstrap.Utility.Platform.IsWindows)
            return;
        ActivityWatcher watcher = _activityWatcher;
        _sessionNotifier = new SessionNotifier(() => watcher.InGame ? watcher.Data : null, note =>
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ShowSessionNoteAsync(note)));
            }
            catch (InvalidOperationException)
            {
            }
        });
        _sessionNotifier.Start();
    }

    private void StopSessionNotifier()
    {
        _sessionNotifier?.Dispose();
        _sessionNotifier = null;
    }

    private async Task ShowSessionNoteAsync(SessionNote note)
    {
        if (_closed || !OverlayNotificationsOn || _activityWatcher?.InGame != true)
            return;
        BitmapSource? image = null;
        if (Uri.TryCreate(note.ImageUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                image = await LoadRemoteImageAsync(uri, 96);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("MenuContainer::SessionNote", "The picture could not be loaded: " + ex.Message);
            }
        }
        if (_closed || !OverlayNotificationsOn)
            return;
        GetNotificationWindow().ShowNotification(note.Title, note.Text, image, 6.0, null, string.Empty, default, note.Kind);
    }

    private static readonly HttpClient NoteImageClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static async Task<BitmapSource?> LoadRemoteImageAsync(Uri uri, int size)
    {
        byte[] bytes = await NoteImageClient.GetByteArrayAsync(uri);
        if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024)
            return null;
        BitmapImage image = new();
        using MemoryStream stream = new(bytes);
        image.BeginInit();
        image.StreamSource = stream;
        image.DecodePixelWidth = size;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // Right after joining, Roblox often does not answer the start time lookup yet and the start time from the
    // game log arrives a few seconds later, so both are retried briefly before giving up
    private static async Task<string> LoadJoinUptimeAsync(ActivityData data, CancellationToken token)
    {
        const int attempts = 4;
        try
        {
            using CancellationTokenSource lookupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            lookupCts.CancelAfter(NotificationEnrichTimeout);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (data.ServerStartedUtc is DateTimeOffset fromLog)
                    return FormatJoinUptime(fromLog);
                ServerStartLookup lookup = await VoidstrapMatchmaker.GetServerStartAsync(data.PlaceId, data.JobId, lookupCts.Token);
                if (lookup.Status == ServerStartStatus.Found)
                {
                    data.ServerStartedUtc ??= lookup.StartedUtc;
                    return FormatJoinUptime(lookup.StartedUtc);
                }
                if (lookup.Status == ServerStartStatus.NotSignedIn)
                    break;
                if (attempt < attempts - 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), lookupCts.Token);
            }
            return data.ServerStartedUtc is DateTimeOffset late ? FormatJoinUptime(late) : string.Empty;
        }
        catch (OperationCanceledException)
        {
            return data.ServerStartedUtc is DateTimeOffset late ? FormatJoinUptime(late) : string.Empty;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::JoinUptime", "Server uptime could not be loaded: " + ex.Message);
            return string.Empty;
        }
    }

    private static string FormatJoinUptime(DateTimeOffset started)
        => Voidstrap.UI.ViewModels.ContextMenu.ServerInformationViewModel.FormatUptime(DateTimeOffset.UtcNow - started);

    private Task UpdateSessionMenuAsync(ActivityData data, CancellationToken token)
    {
        try
        {
            if (!IsCurrentSession(data, token))
                return Task.CompletedTask;
            InviteDeeplinkMenuItem.Visibility = data.ServerType == ServerType.Public ? Visibility.Visible : Visibility.Collapsed;
            ServerDetailsMenuItem.Visibility = Visibility.Visible;
            GamePassDetailsMenuItem.Visibility = Visibility.Visible;
            JoinClosestServerMenuItem.Visibility = data.ServerType == ServerType.Public ? Visibility.Visible : Visibility.Collapsed;
            JoinClosestServerTextBlock.Text = "Join Closest Server";
            if (!IsCurrentSession(data, token))
				return Task.CompletedTask;
			bool trace = ActivityWatcher.PlayerLoggingEnabled;
			OutputConsoleMenuItem.Visibility = trace ? Visibility.Visible : Visibility.Collapsed;
            BrightnessTrackerLog.Visibility = Visibility.Visible;
            ColorsTrackerLog.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Collapsed : BrightnessTrackerLog.Visibility;
            RequestTrayRefresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::UpdateSessionMenu", ex);
        }
		return Task.CompletedTask;
    }

    private void ResetSessionMenu()
    {
        if (_activityWatcher?.InGame == true)
            return;
		if (!Voidstrap.Utility.Platform.IsLinux)
			_memoryTimer.Stop();
		_playTimer.Stop();
        InviteDeeplinkMenuItem.Visibility = Visibility.Collapsed;
        ServerDetailsMenuItem.Visibility = Visibility.Collapsed;
        GamePassDetailsMenuItem.Visibility = Visibility.Collapsed;
        JoinClosestServerMenuItem.Visibility = Visibility.Collapsed;
        OutputConsoleMenuItem.Visibility = Visibility.Collapsed;
        BrightnessTrackerLog.Visibility = Visibility.Collapsed;
        ColorsTrackerLog.Visibility = Visibility.Collapsed;
        _outputConsole?.Close();
        _serverInformationWindow?.Close();
        UpdateCurrentGameInfo(string.Empty, null);
        UpdatePlayTime(TimeSpan.Zero);
		if (!Voidstrap.Utility.Platform.IsLinux)
			MemoryTextBlock.Text = "Roblox: 0 MB";
        RequestTrayRefresh();
    }

    public void ActivityWatcher_OnGameJoin(object? sender, EventArgs e)
    {
        if (_closed || _activityWatcher?.InGame != true)
            return;
        ActivityData data = _activityWatcher.Data;
        if (_lastClosestPlaceId != data.PlaceId)
        {
            _lastClosestServer = null;
            _lastClosestPlaceId = data.PlaceId;
            _closestServerBackoffUntilUtc = DateTime.MinValue;
        }
        CancellationToken token = StartSession();
        Task<(string Name, BitmapSource? Icon)> presentationTask = LoadGamePresentationAsync(data, token);
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                _ = UpdateSessionMenuAsync(data, token);
            }));
        }
        catch (InvalidOperationException)
        {
        }
        _ = UpdateCurrentGameIconAsync(data, presentationTask, token);
        _ = ShowJoinNotification(data, presentationTask, token);
        StartSessionNotifier();
    }

    public void ActivityWatcher_OnGameLeave(object? sender, EventArgs e)
    {
        bool transitioning = _activityWatcher?.IsTeleporting == true;
        _sessionDock?.HideDock();
        CancelSession();
        if (transitioning || _closed)
            return;
        StopSessionNotifier();
        _sessionDock?.EndSession();
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ResetSessionMenu));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_activityWatcher?.InGame != true || _activityWatcher.Data.TimeJoined == default)
            return;
        UpdatePlayTime(DateTime.Now - _activityWatcher.Data.TimeJoined);
    }

    private void UpdatePlayTime(TimeSpan value)
    {
        PlayTimeTextBlock.Text = $"PlayTime: {value:hh\\:mm\\:ss}";
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            HWND hWnd = (HWND)new WindowInteropHelper(this).Handle;
            int windowLong = Windows.Win32.PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            windowLong |= 0x80;
            _ = Windows.Win32.PInvoke.SetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, windowLong);
        }
        LoadFlags();
    }

#if CROSSPLAT
    private Func<Task<List<Voidstrap.UI.Tray.LinuxTrayMenuItem>>>? _trayMenuProvider;

    private DateTime _trayInfoRefreshedUtc = DateTime.MinValue;

    private DateTime _trayFlagsRefreshedUtc = DateTime.MinValue;

    internal void AttachTrayMenuProvider()
    {
        if (_closed)
            return;
        try
        {
            bool first = _trayMenuProvider == null;
            _trayMenuProvider ??= BuildTrayMenuAsync;
            Voidstrap.UI.Tray.LinuxTray.SetMenuProvider(_trayMenuProvider);
            if (first)
                App.Logger.WriteLine("MenuContainer::AttachTrayMenuProvider", "Tray menu is now served over D-Bus");
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::AttachTrayMenuProvider", "The tray menu could not be published: " + ex.Message);
        }
    }

    private void DetachTrayMenuProvider()
    {
        Func<Task<List<Voidstrap.UI.Tray.LinuxTrayMenuItem>>>? provider = _trayMenuProvider;
        if (provider == null)
            return;
        try
        {
            Voidstrap.UI.Tray.LinuxTray.ClearMenuProvider(provider);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::DetachTrayMenuProvider", "The tray menu could not be released: " + ex.Message);
        }
    }

    private Task<List<Voidstrap.UI.Tray.LinuxTrayMenuItem>> BuildTrayMenuAsync()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return Task.FromResult(new List<Voidstrap.UI.Tray.LinuxTrayMenuItem>());
        return Dispatcher.InvokeAsync(BuildTrayMenuOnDispatcherAsync, DispatcherPriority.Normal).Task.Unwrap();
    }

    private async Task<List<Voidstrap.UI.Tray.LinuxTrayMenuItem>> BuildTrayMenuOnDispatcherAsync()
    {
        List<Voidstrap.UI.Tray.LinuxTrayMenuItem> items = [];
        if (_closed || base.ContextMenu is null)
            return items;

        await RefreshTrayInfoAsync();
        if (_closed)
            return items;
        SyncMenuState();
        AppendTrayItems(base.ContextMenu.Items, items);
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            for (int index = 0; index < items.Count; index++)
            {
                List<Voidstrap.UI.Tray.LinuxTrayMenuItem> children = items[index].Children;
                for (int childIndex = 0; childIndex < children.Count; childIndex++)
                {
                    Voidstrap.UI.Tray.LinuxTrayMenuItem child = children[childIndex];
                    if (child.Label != PlayTimeTextBlock.Text)
                        continue;
                    children.RemoveAt(childIndex);
                    items.Insert(index + 1, child);
                    return items;
                }
            }
        }
        return items;
    }

    private async Task RefreshTrayInfoAsync()
    {
        if (DateTime.UtcNow - _trayFlagsRefreshedUtc > TimeSpan.FromSeconds(5))
        {
            _trayFlagsRefreshedUtc = DateTime.UtcNow;
            LoadFlags();
        }
        if (_activityWatcher?.InGame != true)
        {
            UpdatePlayTime(TimeSpan.Zero);
            if (!Voidstrap.Utility.Platform.IsLinux)
            {
                MemoryTextBlock.Text = "Roblox: 0 MB";
                return;
            }
        }
        else
        {
            PlayTimer_Tick(null, EventArgs.Empty);
        }
        if (Voidstrap.Utility.Platform.IsLinux)
            _memoryTimer.Start();
        if (DateTime.UtcNow - _trayInfoRefreshedUtc < TimeSpan.FromSeconds(2))
            return;
        await UpdateRobloxMemoryAsync();
    }

    private void AppendTrayItems(ItemCollection source, List<Voidstrap.UI.Tray.LinuxTrayMenuItem> target)
    {
        foreach (object? entry in source)
        {
            if (entry is Separator separator)
            {
                if (separator.Visibility == Visibility.Visible)
                    target.Add(new Voidstrap.UI.Tray.LinuxTrayMenuItem { IsSeparator = true });
                continue;
            }

            if (entry is not MenuItem menuItem)
            {
                if (entry is DependencyObject content)
                    AppendInfoLines(content, target);
                continue;
            }

            if (menuItem.Visibility != Visibility.Visible)
            {
                continue;
            }

            Voidstrap.UI.Tray.LinuxTrayMenuItem item = new()
            {
                Label = GetTrayLabel(menuItem),
                Enabled = menuItem.IsEnabled,
                IsCheckable = menuItem.IsCheckable,
                IsChecked = menuItem.IsChecked
            };

            if (menuItem.Items.Count > 0)
            {
                AppendTrayItems(menuItem.Items, item.Children);
                if (item.Children.Count == 0)
                    continue;
            }
            else if (!Voidstrap.Utility.Platform.IsWindows && ContainsSlider(menuItem))
            {
                item.Activated = ShowAdjustmentsWindow;
            }
            else
            {
                MenuItem target2 = menuItem;
                item.Activated = () => RaiseTrayClick(target2);
            }

            target.Add(item);
        }
    }

    private static void AppendInfoLines(DependencyObject node, List<Voidstrap.UI.Tray.LinuxTrayMenuItem> target)
    {
        if (node is UIElement element && element.Visibility != Visibility.Visible)
            return;

        if (node is TextBlock textBlock)
        {
            string text = textBlock.Text?.Trim() ?? string.Empty;
            if (text.Length > 0)
                target.Add(new Voidstrap.UI.Tray.LinuxTrayMenuItem { Label = text, Enabled = false });
            return;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyObject)
                AppendInfoLines(dependencyObject, target);
        }
    }
#endif

    private static bool ContainsSlider(DependencyObject node)
    {
        if (node is System.Windows.Controls.Primitives.RangeBase)
            return true;

        if (node is MenuItem menuItem && menuItem.Header is DependencyObject header && ContainsSlider(header))
            return true;

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < count; i++)
        {
            if (ContainsSlider(System.Windows.Media.VisualTreeHelper.GetChild(node, i)))
                return true;
        }

        if (node is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (ContainsSlider(child))
                    return true;
            }
        }

        if (node is Decorator decorator && decorator.Child is not null)
            return ContainsSlider(decorator.Child);

        if (node is ContentControl content && content.Content is DependencyObject inner)
            return ContainsSlider(inner);

        return false;
    }

    private LinuxAdjustmentsWindow? _adjustmentsWindow;

    private void ShowAdjustmentsWindow()
    {
        Dispatcher.BeginInvoke(new Action(delegate
        {
            try
            {
                (DataContext as MenuContainerViewModel)?.SyncAdjustmentsFromSettings();

                if (_adjustmentsWindow is null || !_adjustmentsWindow.IsLoaded)
                {
                    _adjustmentsWindow = new LinuxAdjustmentsWindow(DataContext);
                    _adjustmentsWindow.Closed += OnAdjustmentsWindowClosed;
                }

                _adjustmentsWindow.Show();
                _adjustmentsWindow.Activate();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("MenuContainer::ShowAdjustmentsWindow", "The adjustments window could not be opened: " + ex.Message);
            }
        }));
    }

    private void OnAdjustmentsWindowClosed(object? sender, EventArgs e)
    {
        if (sender is LinuxAdjustmentsWindow window)
            window.Closed -= OnAdjustmentsWindowClosed;

        _adjustmentsWindow = null;
    }

    private void RaiseTrayClick(MenuItem menuItem)
    {
        Dispatcher.BeginInvoke(new Action(delegate
        {
            if (_closed || !menuItem.IsEnabled || menuItem.Visibility != Visibility.Visible)
                return;
            try
            {
                if (menuItem.IsCheckable)
                    menuItem.IsChecked = !menuItem.IsChecked;
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("MenuContainer::RaiseTrayClick", "The tray menu action failed: " + ex.Message);
            }
        }));
    }

    private static string GetTrayLabel(MenuItem menuItem)
    {
        if (menuItem.Header is string header && !string.IsNullOrWhiteSpace(header))
        {
            return header;
        }

        string extracted = ExtractText(menuItem.Header);
        return string.IsNullOrWhiteSpace(extracted) ? menuItem.Name ?? string.Empty : extracted;
    }

    private static string ExtractText(object? content)
    {
        switch (content)
        {
            case null:
                return string.Empty;
            case string text:
                return text;
            case TextBlock textBlock:
                return textBlock.Text;
            case ContentControl contentControl:
                return ExtractText(contentControl.Content);
            case Panel panel:
            {
                foreach (UIElement child in panel.Children)
                {
                    string value = ExtractText(child);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }

                return string.Empty;
            }
            case Decorator decorator:
                return ExtractText(decorator.Child);
            default:
                return string.Empty;
        }
    }

    public void ApplyBackdrop()
    {
        if (base.ContextMenu?.IsOpen == true)
            Voidstrap.UI.WindowBackdrop.ApplyContextMenu(base.ContextMenu);
    }

	private void ContextMenu_Opened(object sender, RoutedEventArgs e)
	{
		SyncMenuState();
		if (_activityWatcher?.InGame == true)
		{
			_memoryTimer.Start();
			_playTimer.Start();
			MemoryTimer_Tick(null, EventArgs.Empty);
			PlayTimer_Tick(sender, EventArgs.Empty);
		}
		ApplyBackdrop();
		bool enabled = _activityWatcher?.InGame == true && ActivityWatcher.PlayerLoggingEnabled;
		OutputConsoleMenuItem.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
		if (!enabled)
		{
			_outputConsole?.Close();
		}
    }

	private void ContextMenu_Closed(object sender, RoutedEventArgs e)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			_memoryTimer.Stop();
		_playTimer.Stop();
	}

    private void Window_Closed(object sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
#if CROSSPLAT
        DetachTrayMenuProvider();
#endif
        _lifetimeCts.Cancel();
        CancelSession();
        _memoryTimer.Stop();
        _memoryTimer.Tick -= MemoryTimer_Tick;
        _playTimer.Stop();
        _playTimer.Tick -= PlayTimer_Tick;
        try
        {
            if (_activityWatcher != null)
            {
                _activityWatcher.OnLogOpen -= ActivityWatcher_OnLogOpen;
                _activityWatcher.OnGameJoin -= ActivityWatcher_OnGameJoin;
                _activityWatcher.OnGameLeave -= ActivityWatcher_OnGameLeave;
            }
        }
        catch
        {
        }
        CloseSessionDock();
        StopSessionNotifier();
        if (_sessionDockHotkeyRegistered)
            UnregisterHotKey(_handle, SessionDockHotkeyId);
        _source?.RemoveHook(WindowMessage);
        if (ReferenceEquals(_currentInstance, this))
            _currentInstance = null;
        CloseChildWindows();
        if (Application.Current.Resources["NotificationWindow"] is NotificationWindow notificationWindow)
        {
            try
            {
                notificationWindow.Close();
            }
            catch
            {
            }
            Application.Current.Resources.Remove("NotificationWindow");
        }
        CurrentGameIcon.Source = null;
        base.DataContext = null;
        if (base.ContextMenu != null)
        {
            base.ContextMenu.Opened -= ContextMenu_Opened;
            base.ContextMenu.Closed -= ContextMenu_Closed;
            base.ContextMenu.DataContext = null;
        }
        _lifetimeCts.Dispose();
        App.Logger.WriteLine("MenuContainer::Window_Closed", "Context menu container closed");
    }

    private void CloseChildWindows()
    {
        Window[] windows = new Window?[]
        {
            _serverInformationWindow,
            _gameHistoryWindow,
            _musicPlayerWindow,
            _gamePassWindow,
            _diagnosticsWindow,
            _outputConsole
        }.OfType<Window>().ToArray();
        foreach (Window window in windows)
        {
            window.Closed -= ChildWindow_Closed;
            try
            {
                window.Close();
            }
            catch
            {
            }
        }
        _serverInformationWindow = null;
        _gameHistoryWindow = null;
        _musicPlayerWindow = null;
        _gamePassWindow = null;
        _outputConsole = null;
        _diagnosticsWindow = null;
    }

    private void ChildWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= ChildWindow_Closed;
            Voidstrap.UI.LinuxWindowMemory.ReleaseAfterClose(window, compact: false);
        }
        if (ReferenceEquals(sender, _serverInformationWindow))
            _serverInformationWindow = null;
        else if (ReferenceEquals(sender, _gameHistoryWindow))
            _gameHistoryWindow = null;
        else if (ReferenceEquals(sender, _musicPlayerWindow))
            _musicPlayerWindow = null;
        else if (ReferenceEquals(sender, _gamePassWindow))
            _gamePassWindow = null;
        else if (ReferenceEquals(sender, _outputConsole))
            _outputConsole = null;
        else if (ReferenceEquals(sender, _diagnosticsWindow))
            _diagnosticsWindow = null;
    }

    private void RichPresenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _watcher.RichPresence?.SetVisibility(((MenuItem)sender).IsChecked);
    }

    private void FrameGenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (FrameGenMenuItem.IsChecked)
        {
            Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetMode(1);
            FrameGenMenuItem.IsChecked = Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0;
        }
        else
        {
            App.Settings.Prop.FrameGenResumeIndex = 1;
            Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetMode(0);
        }
        App.Settings.SaveDeferred();
    }

    private void FrameGenOverlayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Prop.FrameGenOverlayShow = FrameGenOverlayMenuItem.IsChecked;
        App.Settings.SaveDeferred();
    }

    private void FrameGenSplitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Prop.FrameGenSplitCompare = FrameGenSplitMenuItem.IsChecked;
        App.Settings.SaveDeferred();
    }

    private void InviteDeeplinkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        string? deeplink = _activityWatcher?.Data?.GetInviteDeeplink();
        if (string.IsNullOrEmpty(deeplink))
        {
            return;
        }
        try
        {
            Voidstrap.Utility.ClipboardService.SetDataObject(deeplink, true);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MenuContainer::InviteDeeplink", ex);
        }
    }

    private void ServerDetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowServerInformationWindow();
    }

    private void CantSeeOverlaysMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_closed)
                return;
            if (_diagnosticsWindow == null)
            {
                _diagnosticsWindow = new DiagnosticsWindow(_activityWatcher);
                _diagnosticsWindow.Closed += ChildWindow_Closed;
            }
            _diagnosticsWindow.Show();
            _diagnosticsWindow.Activate();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Could not build the overlay diagnostics: " + ex.Message, MessageBoxImage.Error);
        }
    }

    private void LogTracerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        string? text = _activityWatcher?.LogLocation;
        if (text != null)
        {
            Utilities.ShellExecute(text);
        }
    }

    private void CloseRobloxMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (Frontend.ShowMessageBox(Strings.ContextMenu_CloseRobloxMessage, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _watcher.KillRobloxProcess();
        }
    }

    private void JoinLastServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activityWatcher == null)
            return;
        ShowChildWindow(ref _gameHistoryWindow, () => new ServerHistory(_activityWatcher));
    }

    private void GamePassDetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activityWatcher == null)
            return;
        long userId = _activityWatcher.Data.UserId;
        ShowChildWindow(ref _gamePassWindow, () => new GamePassConsole(userId));
    }

    private void MusicPlayerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activityWatcher == null)
            return;
        ShowChildWindow(ref _musicPlayerWindow, () => new MusicPlayer());
    }

	private void OutputConsoleMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (_activityWatcher == null || !ActivityWatcher.PlayerLoggingEnabled)
			return;
        ShowChildWindow(ref _outputConsole, () => new OutputConsole(_activityWatcher));
    }
}
