using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Crosshair;
using Voidstrap.UI.Elements.Overlay;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;

namespace Voidstrap;

public class Watcher : IDisposable
{
	private const int MaximumSettingsBytes = 4 * 1024 * 1024;

	private readonly InterProcessLock _lock = new InterProcessLock("Watcher", App.LaunchSettings.MatchmakerRejoinFlag.Active ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5));

	private readonly WatcherData? _watcherData;

	private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();

	private readonly object _lifecycleGate = new object();

	private bool _disposed;

	private NotifyIconWrapper? _notifyIcon;

	public ActivityWatcher? ActivityWatcher;

	public DiscordRichPresence? RichPresence;

	public QuestTracker? QuestTracker;

	public static Watcher? Current { get; private set; }

	public IntegrationWatcher? IntegrationWatcher;

	public HistoryPersister? HistoryPersister;

	public ServerMatchmaker? ServerMatchmaker;

	public Integrations.GameChat.GameChatIntegration? GameChat;

	private WindowManipulation? _windowManipulation;

	private FileSystemWatcher? _settingsWatcher;

	private Timer? _settingsReloadTimer;

	private bool _activityTrackingEnabled;

	private bool _discordRichPresenceEnabled;

	private bool _disableAppPatchEnabled;

	private bool _gameChatEnabled;

	private bool _questTrackingEnabled;

	private RobloxProcessOptimizer? _runtimeOptimizer;

	private Task? _windowManipulationTask;

	public Watcher()
	{
		if (!_lock.IsAcquired)
		{
			App.Logger.WriteLine("Watcher", "Watcher instance already exists");
			return;
		}
		Current = this;
		string data = App.LaunchSettings.WatcherFlag.Data;
		if (string.IsNullOrEmpty(data))
		{
			throw new Exception("Watcher data not specified");
		}
		if (!string.IsNullOrEmpty(data))
		{
			_watcherData = JsonSerializer.Deserialize<WatcherData>(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
		}
		if (_watcherData == null)
		{
			throw new Exception("Watcher data is invalid");
		}
		InitializeIntegrations();
	}

	internal Watcher(WatcherData data)
	{
		if (!_lock.IsAcquired)
		{
			App.Logger.WriteLine("Watcher::InProcess", "Watcher instance already exists");
			return;
		}
		_watcherData = data ?? throw new Exception("Watcher data is invalid");
		InitializeIntegrations();
	}

	private void InitializeIntegrations()
	{
		if (_watcherData == null)
		{
			return;
		}
		MemoryManager.SetGameplayActive(true);
		StartSettingsWatcher();
		bool enableActivityTracking = App.Settings.Prop.EnableActivityTracking;
		bool flag = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOIDSTRAP_STATUS_FILE"));
		_activityTrackingEnabled = enableActivityTracking;
		_discordRichPresenceEnabled = App.Settings.Prop.UseDiscordRichPresence;
		_disableAppPatchEnabled = App.Settings.Prop.UseDisableAppPatch;
		_gameChatEnabled = App.Settings.Prop.GameChatEnabled;
		_questTrackingEnabled = App.Settings.Prop.WebsiteQuestTracking && WebsiteAuth.IsSignedIn();
		if (enableActivityTracking || flag)
		{
			ActivityWatcher = new ActivityWatcher(_watcherData.LogFile);
			ActivityWatcher.OnGameJoin += OnRuntimeGameJoin;
			ActivityWatcher.OnGameLeave += OnRuntimeGameLeave;
			if (enableActivityTracking && App.Settings.Prop.UseDisableAppPatch)
			{
				ActivityWatcher.OnAppClose += OnActivityAppClose;
			}
			if ((enableActivityTracking && App.Settings.Prop.UseDiscordRichPresence) || flag)
			{
				RichPresence = new DiscordRichPresence(ActivityWatcher);
			}
			if (enableActivityTracking)
			{
				IntegrationWatcher = new IntegrationWatcher(ActivityWatcher);
				HistoryPersister = new HistoryPersister(ActivityWatcher);
				if (_questTrackingEnabled)
					QuestTracker = new QuestTracker(ActivityWatcher);
				ServerMatchmaker = new ServerMatchmaker(ActivityWatcher, this);
				if (App.Settings.Prop.GameChatEnabled)
				{
					GameChat = new Integrations.GameChat.GameChatIntegration(ActivityWatcher, _watcherData.ProcessId);
				}
			}
		}
		if ((enableActivityTracking || App.LaunchSettings.TestModeFlag.Active) && Voidstrap.Utility.Platform.SupportsTrayIcon)
			_notifyIcon = new NotifyIconWrapper(this);
		if (ServerMatchmaker != null)
			ServerMatchmaker.NotifyIconResolver = () => _notifyIcon;
		if (QuestTracker != null)
			QuestTracker.NotifyIconResolver = () => _notifyIcon;
	}

	private void StartSettingsWatcher()
	{
		try
		{
			_settingsReloadTimer = new Timer(SettingsReloadTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
			_settingsWatcher = new FileSystemWatcher(Paths.Config, "AppSettings.json")
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
			};
			_settingsWatcher.Changed += SettingsFileChanged;
			_settingsWatcher.Created += SettingsFileChanged;
			_settingsWatcher.Renamed += SettingsFileRenamed;
			_settingsWatcher.EnableRaisingEvents = true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher", "Settings watcher failed: " + ex.Message);
		}
	}

	private void SettingsFileChanged(object sender, FileSystemEventArgs e)
	{
		ScheduleSettingsReload();
	}

	private void SettingsFileRenamed(object sender, RenamedEventArgs e)
	{
		ScheduleSettingsReload();
	}

	private void ScheduleSettingsReload()
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			_settingsReloadTimer?.Change(500, Timeout.Infinite);
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void SettingsReloadTimerCallback(object? state)
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			AppSettings? settings = JsonFile.Deserialize<AppSettings>(App.Settings.FileLocation, JsonOptions.Tolerant, MaximumSettingsBytes);
			if (settings == null)
			{
				return;
			}
			lock (_lifecycleGate)
			{
				if (_disposed)
				{
					return;
				}
				App.Settings.Prop = settings;
				ApplyLiveSettings();
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher", "Settings reload failed: " + ex.Message);
		}
	}

	private void ApplyLiveSettings()
	{
		bool activityTrackingEnabled = App.Settings.Prop.EnableActivityTracking;
		bool discordRichPresenceEnabled = App.Settings.Prop.UseDiscordRichPresence;
		bool disableAppPatchEnabled = App.Settings.Prop.UseDisableAppPatch;
		bool gameChatEnabled = App.Settings.Prop.GameChatEnabled;
		bool questTrackingEnabled = App.Settings.Prop.WebsiteQuestTracking && WebsiteAuth.IsSignedIn();
		if (activityTrackingEnabled != _activityTrackingEnabled)
		{
			_activityTrackingEnabled = activityTrackingEnabled;
			if (activityTrackingEnabled)
			{
				EnableActivityIntegrations();
			}
			else
			{
				DisableActivityIntegrations();
			}
		}
		if (discordRichPresenceEnabled != _discordRichPresenceEnabled)
		{
			_discordRichPresenceEnabled = discordRichPresenceEnabled;
			if (discordRichPresenceEnabled && _activityTrackingEnabled && ActivityWatcher != null)
			{
				RichPresence ??= new DiscordRichPresence(ActivityWatcher);
				_ = RichPresence.SetCurrentGame();
			}
			else
			{
				RichPresence?.Dispose();
				RichPresence = null;
			}
		}
		if (disableAppPatchEnabled != _disableAppPatchEnabled)
		{
			_disableAppPatchEnabled = disableAppPatchEnabled;
			if (_activityTrackingEnabled && ActivityWatcher != null)
			{
				ActivityWatcher.OnAppClose -= OnActivityAppClose;
				if (disableAppPatchEnabled)
				{
					ActivityWatcher.OnAppClose += OnActivityAppClose;
				}
			}
		}
		if (gameChatEnabled != _gameChatEnabled)
		{
			_gameChatEnabled = gameChatEnabled;
			if (gameChatEnabled && _activityTrackingEnabled && ActivityWatcher != null)
			{
				GameChat ??= new Integrations.GameChat.GameChatIntegration(ActivityWatcher, _watcherData?.ProcessId ?? 0);
			}
			else
			{
				GameChat?.Dispose();
				GameChat = null;
			}
		}
		if (questTrackingEnabled != _questTrackingEnabled)
		{
			_questTrackingEnabled = questTrackingEnabled;
			if (questTrackingEnabled && _activityTrackingEnabled && ActivityWatcher != null)
			{
				QuestTracker ??= new QuestTracker(ActivityWatcher);
				QuestTracker.NotifyIconResolver = () => _notifyIcon;
			}
			else
			{
				QuestTracker?.Dispose();
				QuestTracker = null;
			}
		}
		UpdateRuntimeOptimizer();
		_notifyIcon?.RefreshGameJoinSubscription();
		Voidstrap.Integrations.Overlays.OverlayHub.Refresh();
		RunOnApplicationDispatcher(ReconcileRuntimeSessionWindows);
	}

	private void StartRuntimeOptimizer()
	{
		UpdateRuntimeOptimizer();
	}

	private void UpdateRuntimeOptimizer()
	{
		if (_watcherData == null)
		{
			return;
		}
		if (!RobloxProcessOptimizer.ShouldRun(App.Settings.Prop))
		{
			_runtimeOptimizer?.Dispose();
			_runtimeOptimizer = null;
			return;
		}
		_runtimeOptimizer ??= new RobloxProcessOptimizer(_watcherData.ProcessId);
		_runtimeOptimizer.Start();
	}

	private void EnableActivityIntegrations()
	{
		if (_watcherData == null || ActivityWatcher != null)
		{
			return;
		}
		ActivityWatcher = new ActivityWatcher(_watcherData.LogFile);
		ActivityWatcher.OnGameJoin += OnRuntimeGameJoin;
		ActivityWatcher.OnGameLeave += OnRuntimeGameLeave;
		ActivityWatcher.Start();
		if (App.Settings.Prop.UseDisableAppPatch)
		{
			ActivityWatcher.OnAppClose += OnActivityAppClose;
		}
		if (App.Settings.Prop.UseDiscordRichPresence)
		{
			RichPresence = new DiscordRichPresence(ActivityWatcher);
		}
		IntegrationWatcher = new IntegrationWatcher(ActivityWatcher);
		HistoryPersister = new HistoryPersister(ActivityWatcher);
		if (App.Settings.Prop.WebsiteQuestTracking && WebsiteAuth.IsSignedIn())
			QuestTracker = new QuestTracker(ActivityWatcher);
		ServerMatchmaker = new ServerMatchmaker(ActivityWatcher, this);
		ServerMatchmaker.NotifyIconResolver = () => _notifyIcon;
		if (QuestTracker != null)
			QuestTracker.NotifyIconResolver = () => _notifyIcon;
		if (App.Settings.Prop.GameChatEnabled)
		{
			GameChat = new Integrations.GameChat.GameChatIntegration(ActivityWatcher, _watcherData.ProcessId);
		}
		if (_notifyIcon == null)
		{
			if (Voidstrap.Utility.Platform.SupportsTrayIcon)
			_notifyIcon = new NotifyIconWrapper(this);
		}
		else
		{
			_notifyIcon.RefreshGameJoinSubscription();
		}
	}

	private void DisableActivityIntegrations()
	{
		GameChat?.Dispose();
		GameChat = null;
		ServerMatchmaker?.Dispose();
		ServerMatchmaker = null;
		QuestTracker?.Dispose();
		QuestTracker = null;
		HistoryPersister?.Dispose();
		HistoryPersister = null;
		IntegrationWatcher?.Dispose();
		IntegrationWatcher = null;
		RichPresence?.Dispose();
		RichPresence = null;
		if (ActivityWatcher != null)
		{
			ActivityWatcher.OnGameJoin -= OnRuntimeGameJoin;
			ActivityWatcher.OnGameLeave -= OnRuntimeGameLeave;
			ActivityWatcher.OnAppClose -= OnActivityAppClose;
			ActivityWatcher.Dispose();
			ActivityWatcher = null;
		}
		LaunchFlag testModeFlag = App.LaunchSettings.TestModeFlag;
		if (testModeFlag == null || !testModeFlag.Active)
		{
			_notifyIcon?.Dispose();
			_notifyIcon = null;
		}
	}

	private void OnActivityAppClose(object? sender, EventArgs e)
	{
		if (_watcherData == null)
		{
			return;
		}
		try
		{
			App.Logger.WriteLine("Watcher", "Received desktop app exit, closing Roblox");
			using Process process = Process.GetProcessById(_watcherData.ProcessId);
			process.CloseMainWindow();
		}
		catch
		{
		}
	}

	private void OnRuntimeGameJoin(object? sender, EventArgs e)
	{
		RunRuntimeAction(Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.OnGameJoin, "FullscreenJoin");
		RunRuntimeAction(Voidstrap.Integrations.RiShade.RiShadeManager.OnGameJoin, "RiShadeJoin");
		RunRuntimeAction(Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.OnGameJoin, "AntiAliasingJoin");
		RunOnApplicationDispatcher(EnsureRuntimeSessionWindows);
	}

	private void OnRuntimeGameLeave(object? sender, EventArgs e)
	{
		RunRuntimeAction(Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.OnGameLeave, "FullscreenLeave");
		RunRuntimeAction(Voidstrap.Integrations.RiShade.RiShadeManager.OnGameLeave, "RiShadeLeave");
		RunRuntimeAction(Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.OnGameLeave, "AntiAliasingLeave");
		RunRuntimeAction(Voidstrap.Integrations.FrameGeneration.FrameGenManager.OnGameLeave, "FrameGenerationLeave");
		if (ActivityWatcher?.IsTeleporting != true)
		{
			RunOnApplicationDispatcher(CloseRuntimeSessionWindows);
		}
	}

	private static void RunRuntimeAction(Action action, string operation)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Watcher::" + operation, ex);
		}
	}

	private static void RunOnApplicationDispatcher(Action action)
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted)
		{
			return;
		}
		application.Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
	}

	private void EnsureRuntimeSessionWindows()
	{
		if (_disposed || ActivityWatcher?.InGame != true)
		{
			return;
		}
		if (Voidstrap.Integrations.Overlays.OverlayCrosshair.IsEnabled())
		{
			RunRuntimeAction(delegate
			{
				if (Voidstrap.Integrations.Overlays.OverlayHub.CompositorCrosshairActive)
				{
					return;
				}
				if (Application.Current.Resources["CrosshairWindow"] is CrosshairWindow existing && existing.IsLoaded)
				{
					return;
				}
				Application.Current.Resources.Remove("CrosshairWindow");
				Application.Current.Resources["CrosshairWindow"] = new CrosshairWindow(new ModsViewModel());
			}, "CreateCrosshair");
		}
		if (App.Settings.Prop.OverlaysEnabled)
		{
			ScreenColorEffect.ApplyConfigured();
		}
		else
		{
			ScreenColorEffect.Reset();
		}
		if (App.Settings.Prop.OverlaysEnabled && OverlayWindow.SurfaceRequired)
		{
			RunRuntimeAction(delegate
			{
				if (Application.Current.Resources["OverlayWindow"] is OverlayWindow existing && existing.IsLoaded)
				{
					return;
				}
				Application.Current.Resources.Remove("OverlayWindow");
				OverlayWindow overlay = new OverlayWindow(ActivityWatcher);
				overlay.Show();
				Application.Current.Resources["OverlayWindow"] = overlay;
			}, "CreateOverlay");
		}
	}

	private void ReconcileRuntimeSessionWindows()
	{
		if (_disposed || ActivityWatcher?.InGame != true)
		{
			return;
		}

		bool crosshairWanted = Voidstrap.Integrations.Overlays.OverlayCrosshair.IsEnabled()
			&& !Voidstrap.Integrations.Overlays.OverlayHub.CompositorCrosshairActive;
		if (Application.Current.Resources["CrosshairWindow"] is CrosshairWindow existingCrosshair)
		{
			if (!crosshairWanted)
			{
				Application.Current.Resources.Remove("CrosshairWindow");
				RunRuntimeAction(existingCrosshair.Close, "RefreshCrosshair");
			}
		}
		else if (crosshairWanted)
		{
			RunRuntimeAction(delegate
			{
				Application.Current.Resources["CrosshairWindow"] = new CrosshairWindow(new ModsViewModel());
			}, "RefreshCrosshair");
		}

		bool required = App.Settings.Prop.OverlaysEnabled && OverlayWindow.SurfaceRequired;
		if (Application.Current.Resources["OverlayWindow"] is OverlayWindow existing)
		{
			if (required && existing.IsLoaded && existing.MatchesCurrentSettings())
			{
				return;
			}
			RunRuntimeAction(existing.Close, "RefreshOverlay");
			Application.Current.Resources.Remove("OverlayWindow");
		}

		if (required)
		{
			RunRuntimeAction(delegate
			{
				OverlayWindow overlay = new OverlayWindow(ActivityWatcher);
				overlay.Show();
				Application.Current.Resources["OverlayWindow"] = overlay;
			}, "RefreshOverlay");
		}
	}

	private static void CloseRuntimeSessionWindows()
	{
		if (Application.Current.Resources["CrosshairWindow"] is CrosshairWindow crosshair)
		{
			RunRuntimeAction(crosshair.Close, "CloseCrosshair");
		}
		Application.Current.Resources.Remove("CrosshairWindow");
		if (Application.Current.Resources["OverlayWindow"] is OverlayWindow overlay)
		{
			RunRuntimeAction(overlay.Close, "CloseOverlay");
		}
		Application.Current.Resources.Remove("OverlayWindow");
		ScreenColorEffect.Reset();
	}

	public void KillRobloxProcess()
	{
		CloseProcess(_watcherData.ProcessId, force: true);
	}

	public static bool IsAnyRobloxRunning()
	{
		try
		{
			Process[] processesByName = Process.GetProcessesByName(Path.GetFileNameWithoutExtension("RobloxPlayerBeta"));
			bool result = processesByName.Length != 0;
			Process[] array = processesByName;
			foreach (Process process in array)
			{
				try
				{
					process.Dispose();
				}
				catch
				{
				}
			}
			return result;
		}
		catch
		{
			return false;
		}
	}

	public static void ForceShutdownAfterRobloxExit(string logIdent)
	{
		if (IsAnyRobloxRunning())
		{
			App.WebsiteTakeoverActive = false;
			App.Terminate();
			return;
		}
		try
		{
			Voidstrap.Integrations.AssetProxy.AssetProxyServer.Stop();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(logIdent, "AssetWarp shutdown failed: " + ex.Message);
		}
		App.WebsiteTakeoverActive = false;
		App.Terminate();
	}

	public void CloseProcess(int pid, bool force = false)
	{
		try
		{
			using Process process = Process.GetProcessById(pid);
			App.Logger.WriteLine("Watcher::CloseProcess", $"Killing process '{process.ProcessName}' (pid={pid}, force={force})");
			if (process.HasExited)
			{
				App.Logger.WriteLine("Watcher::CloseProcess", $"PID {pid} has already exited");
			}
			else if (force)
			{
				process.Kill();
			}
			else
			{
				process.CloseMainWindow();
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher::CloseProcess", $"PID {pid} could not be closed");
			App.Logger.WriteException("Watcher::CloseProcess", ex);
		}
	}

	public async Task Run()
	{
		if (_disposed || !_lock.IsAcquired || _watcherData == null)
		{
			return;
		}
		ActivityWatcher?.Start();
		StartWindowManipulation();
		StartRuntimeOptimizer();
		try
		{
			using Process process = Process.GetProcessById(_watcherData.ProcessId);
			await process.WaitForExitAsync(_lifetimeCancellation.Token);
		}
		catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
		{
			return;
		}
		catch (ArgumentException)
		{
		}
		catch (InvalidOperationException)
		{
		}
		if (_watcherData.AutoclosePids != null)
		{
			foreach (int autoclosePid in _watcherData.AutoclosePids)
			{
				CloseProcess(autoclosePid);
			}
		}
		if (App.LaunchSettings.TestModeFlag.Active)
		{
			using Process? settingsProcess = Process.Start(Paths.Process, "-settings -testmode");
		}
	}

	private static bool IsWindowManipulationEnabled()
	{
		var prop = App.Settings.Prop;
		if (prop.FakeBorderlessFullscreen || prop.CycleTitleWithGameName)
		{
			return true;
		}
		return !string.IsNullOrWhiteSpace(prop.RobloxTitle) && prop.RobloxTitle != "Voidstrap";
	}

	private delegate bool WatcherEnumWindowsProc(IntPtr hwnd, IntPtr lparam);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool EnumWindows(WatcherEnumWindowsProc callback, IntPtr lparam);

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hwnd);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hwnd);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct WatcherRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetClientRect(IntPtr hwnd, out WatcherRect rect);

	private static IntPtr FindRobloxGameWindow(int processId)
	{
		IntPtr best = IntPtr.Zero;
		long bestArea = 0;
		uint target = (uint)processId;

		try
		{
			EnumWindows(delegate (IntPtr hwnd, IntPtr lparam)
			{
				if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
					return true;

				GetWindowThreadProcessId(hwnd, out uint owner);
				if (owner != target)
					return true;

				System.Text.StringBuilder className = new System.Text.StringBuilder(64);
				GetClassName(hwnd, className, className.Capacity);
				if (!className.ToString().Equals("WINDOWSCLIENT", StringComparison.Ordinal))
					return true;

				if (!GetClientRect(hwnd, out WatcherRect rect))
					return true;

				long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
				if (area > bestArea)
				{
					bestArea = area;
					best = hwnd;
				}

				return true;
			}, IntPtr.Zero);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher::FindRobloxGameWindow", "Window search failed: " + ex.Message);
			return IntPtr.Zero;
		}

		return best;
	}

	private void StartWindowManipulation()
	{
		if (_watcherData == null || !IsWindowManipulationEnabled())
		{
			return;
		}
		int pid = _watcherData.ProcessId;
		CancellationToken token = _lifetimeCancellation.Token;
		_windowManipulationTask = Task.Run(async delegate
		{
			try
			{
				using Process process = Process.GetProcessById(pid);
				IntPtr handle = IntPtr.Zero;
				for (int i = 0; i < 120 && !token.IsCancellationRequested && !process.HasExited; i++)
				{
					try
					{
						process.Refresh();
						handle = FindRobloxGameWindow(pid);
						if (handle == IntPtr.Zero)
							handle = process.MainWindowHandle;
					}
					catch
					{
					}
					if (handle != IntPtr.Zero)
					{
						break;
					}
					await Task.Delay(500, token);
				}
				if (!token.IsCancellationRequested && !(handle == IntPtr.Zero))
				{
					WindowManipulation manipulation = new WindowManipulation((long)handle, pid, ActivityWatcher);
					lock (_lifecycleGate)
					{
						if (_disposed || token.IsCancellationRequested)
						{
							manipulation.Dispose();
							return;
						}
						_windowManipulation = manipulation;
						manipulation.Start();
					}
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Watcher::StartWindowManipulation", "Failed: " + ex.Message);
			}
		});
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (ReferenceEquals(Current, this))
		{
			Current = null;
		}
		_lifetimeCancellation.Cancel();
		lock (_lifecycleGate)
		{
		}
		App.Logger.WriteLine("Watcher::Dispose", "Disposing Watcher");
		try
		{
			if (_settingsWatcher != null)
			{
				_settingsWatcher.EnableRaisingEvents = false;
				_settingsWatcher.Changed -= SettingsFileChanged;
				_settingsWatcher.Created -= SettingsFileChanged;
				_settingsWatcher.Renamed -= SettingsFileRenamed;
				_settingsWatcher.Dispose();
				_settingsWatcher = null;
			}
			if (_settingsReloadTimer != null)
			{
				_settingsReloadTimer.DisposeAsync().AsTask().ConfigureAwait(continueOnCapturedContext: false).GetAwaiter().GetResult();
			}
			_settingsReloadTimer = null;
		}
		catch
		{
		}
		WaitForBackgroundTask(_windowManipulationTask);
		_windowManipulationTask = null;
		try
		{
			_runtimeOptimizer?.Dispose();
			_runtimeOptimizer = null;
		}
		catch
		{
		}
		try
		{
			_windowManipulation?.Dispose();
			_windowManipulation = null;
		}
		catch
		{
		}
		try
		{
			GameChat?.Dispose();
			GameChat = null;
		}
		catch
		{
		}
		try
		{
			ServerMatchmaker?.Dispose();
			ServerMatchmaker = null;
		}
		catch
		{
		}
		try
		{
			QuestTracker?.Dispose();
			QuestTracker = null;
			HistoryPersister?.Dispose();
			HistoryPersister = null;
		}
		catch
		{
		}
		try
		{
			IntegrationWatcher?.Dispose();
			IntegrationWatcher = null;
		}
		catch
		{
		}
		try
		{
			_notifyIcon?.Dispose();
			_notifyIcon = null;
		}
		catch
		{
		}
		try
		{
			RichPresence?.Dispose();
			RichPresence = null;
		}
		catch
		{
		}
		try
		{
			if (ActivityWatcher != null)
			{
				ActivityWatcher.OnGameJoin -= OnRuntimeGameJoin;
				ActivityWatcher.OnGameLeave -= OnRuntimeGameLeave;
			}
			ActivityWatcher?.OnAppClose -= OnActivityAppClose;
			ActivityWatcher?.Dispose();
			ActivityWatcher = null;
		}
		catch
		{
		}
		RunOnApplicationDispatcher(CloseRuntimeSessionWindows);
		try
		{
			_lock.Dispose();
		}
		catch
		{
		}
		_lifetimeCancellation.Dispose();
		MemoryManager.SetGameplayActive(false);
		GC.SuppressFinalize(this);
	}

	private static void WaitForBackgroundTask(Task? task)
	{
		if (task == null)
		{
			return;
		}
		try
		{
			task.ConfigureAwait(continueOnCapturedContext: false).GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}
		catch
		{
		}
	}
}
