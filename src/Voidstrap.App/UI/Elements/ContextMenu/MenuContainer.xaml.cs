using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.Models.APIs;
using Voidstrap.Models.Entities;
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
    private readonly Watcher _watcher;
    private readonly ActivityWatcher? _activityWatcher;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _playTimer;

    private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
    private readonly object _sessionSync = new object();

    private DateTime _closestServerBackoffUntilUtc = DateTime.MinValue;
    private ServerInfo? _lastClosestServer;
    private long _lastClosestPlaceId;
    private int _memoryUpdateActive;
    private int _joinClosestActive;

    private ServerInformation? _serverInformationWindow;

    private ServerHistory? _gameHistoryWindow;

    private MusicPlayer? _musicPlayerWindow;

    private GamePassConsole? _gamePassWindow;

    private OutputConsole? _outputConsole;

    private CancellationTokenSource? _sessionCts;

    private bool _closed;

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
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _activityWatcher = watcher.ActivityWatcher;
        InitializeComponent();
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

    private void PrewarmJoinNotification()
    {
        if (!Voidstrap.Utility.Platform.IsLinux || _activityWatcher == null || !App.Settings.Prop.NotificationWindowShow)
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
            return;
        }
        CurrentGameMenuItem.Visibility = Visibility.Visible;
        CurrentGameIcon.Source = gameIcon;
        CurrentGameNameTextBlock.Text = TrimWithThreeDots(gameName);
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
                if (IsCurrentSession(data, token))
                    UpdateCurrentGameInfo(name, icon);
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
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await App.HttpClient.GetAsync($"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100", HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _closestServerBackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
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
        if (_closed || Interlocked.Exchange(ref _memoryUpdateActive, 1) != 0)
            return;
        try
        {
            long robloxMemory = await Task.Run(ReadRobloxMemory, _lifetimeCts.Token);
            if (!_closed && _activityWatcher?.InGame == true)
                MemoryTextBlock.Text = "Roblox: " + FormatBytes(robloxMemory);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _memoryUpdateActive, 0);
        }
    }

    private static long ReadRobloxMemory()
    {
        if (Voidstrap.Utility.Platform.IsLinux)
            return ReadSoberMemory();
        long total = 0;
        foreach (Process process in Process.GetProcessesByName("RobloxPlayerBeta"))
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

    private static long ReadSoberMemory()
    {
        long total = 0;
        foreach (int processId in Voidstrap.Platform.Linux.LinuxSoberProcessProbe.GetSandboxProcessIds())
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                total += process.WorkingSet64;
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
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
            ServerInfo? server = await FetchClosestServerAsync(data.PlaceId, _lifetimeCts.Token);
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
                    LogTracerMenuItem.Visibility = Visibility.Visible;
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static readonly TimeSpan NotificationEnrichTimeout = TimeSpan.FromSeconds(8);

    private async Task ShowJoinNotification(ActivityData data, Task<(string Name, BitmapSource? Icon)> presentationTask, CancellationToken token)
    {
        if (!App.Settings.Prop.NotificationWindowShow || _activityWatcher == null)
        {
            return;
        }
        Task<string?> locationTask = data.QueryServerLocation(token);
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
        if (!IsCurrentSession(data, token))
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
		catch (Exception ex)
		{
			App.Logger.WriteException("MenuContainer::GetServerPlayerStats", ex);
		}
        if (!IsCurrentSession(data, token))
            return;
        string text2 = tuple.Item2 > 0
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
        string text3 = universeName + "\n" + (flagImage != null ? NotificationWindow.FlagPlaceholder.ToString() : string.Empty) + serverLocation + text2;
        try
        {
            await Dispatcher.InvokeAsync(delegate
            {
                if (!IsCurrentSession(data, token))
                    return;
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
                    notificationWindow.ShowNotification(text3, notificationIcon, 6.0, flagImage);
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
            BrightnessTrackerLog.Visibility = App.Settings.Prop.OverlaysEnabled ? Visibility.Visible : Visibility.Collapsed;
            ColorsTrackerLog.Visibility = BrightnessTrackerLog.Visibility;
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
		MemoryTextBlock.Text = "Roblox: 0 MB";
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
    }

    public void ActivityWatcher_OnGameLeave(object? sender, EventArgs e)
    {
        bool transitioning = _activityWatcher?.IsTeleporting == true;
        CancelSession();
        if (transitioning || _closed)
            return;
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
    internal void AttachTrayMenuProvider()
    {
        try
        {
            Voidstrap.UI.Tray.DbusMenuObject? menu = Voidstrap.UI.Tray.LinuxTray.Menu;
            if (menu is null)
            {
                return;
            }

            menu.MenuProvider = BuildTrayMenu;
            menu.Rebuild();
            App.Logger.WriteLine("MenuContainer::AttachTrayMenuProvider", "Tray menu is now served over D-Bus");
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MenuContainer::AttachTrayMenuProvider", "The tray menu could not be published: " + ex.Message);
        }
    }

    private List<Voidstrap.UI.Tray.LinuxTrayMenuItem> BuildTrayMenu()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(BuildTrayMenu);
        }

        List<Voidstrap.UI.Tray.LinuxTrayMenuItem> items = [];
        if (base.ContextMenu is not null)
        {
            AppendTrayItems(base.ContextMenu.Items, items);
        }

        return items;
    }

    private void AppendTrayItems(ItemCollection source, List<Voidstrap.UI.Tray.LinuxTrayMenuItem> target)
    {
        foreach (object? entry in source)
        {
            if (entry is Separator)
            {
                target.Add(new Voidstrap.UI.Tray.LinuxTrayMenuItem { IsSeparator = true });
                continue;
            }

            if (entry is not MenuItem menuItem || menuItem.Visibility != Visibility.Visible)
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
            try
            {
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
		_memoryTimer.Stop();
		_playTimer.Stop();
	}

    private void Window_Closed(object sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
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
    }

    private void ChildWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= ChildWindow_Closed;
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
            Voidstrap.Integrations.Overlays.OverlayDiagnostics.RaiseOverlayWindows();
            string report = Voidstrap.Integrations.Overlays.OverlayDiagnostics.BuildReport();
            Frontend.ShowMessageBox(report, MessageBoxImage.Information);
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
