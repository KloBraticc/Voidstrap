using System.Runtime.InteropServices;
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

public partial class Watcher : IDisposable
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

	public static Watcher? Current { get; private set; }

	public IntegrationWatcher? IntegrationWatcher;

	public HistoryPersister? HistoryPersister;

	public ServerMatchmaker? ServerMatchmaker;

	private WindowManipulation? _windowManipulation;

	private FileSystemWatcher? _settingsWatcher;

	private Timer? _settingsReloadTimer;

	private bool _activityTrackingEnabled;
	private bool _overlayGameStateEnabled;

	private bool _discordRichPresenceEnabled;

	private bool _disableAppPatchEnabled;

	private RobloxProcessOptimizer? _runtimeOptimizer;

	private TasxOptimizer? _tasxOptimizer;

	private Task? _windowManipulationTask;

	public Watcher()
	{
		if (!_lock.IsAcquired)
		{
			App.Logger.WriteLine("Watcher", "Watcher instance already exists");
			return;
		}
		Current = this;
		string? data = App.LaunchSettings.WatcherFlag.Data;
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
		_overlayGameStateEnabled = OverlaysNeedGameState();
		_discordRichPresenceEnabled = App.Settings.Prop.UseDiscordRichPresence;
		_disableAppPatchEnabled = App.Settings.Prop.UseDisableAppPatch;
		if (enableActivityTracking || flag || _overlayGameStateEnabled)
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
				ServerMatchmaker = new ServerMatchmaker(ActivityWatcher, this);
			}
		}
		if ((enableActivityTracking || App.LaunchSettings.TestModeFlag.Active) && Voidstrap.Utility.Platform.SupportsTrayIcon)
			_notifyIcon = new NotifyIconWrapper(this);
		if (ServerMatchmaker != null)
			ServerMatchmaker.NotifyIconResolver = () => _notifyIcon;
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
		bool overlayGameStateEnabled = OverlaysNeedGameState();
		bool discordRichPresenceEnabled = App.Settings.Prop.UseDiscordRichPresence;
		bool disableAppPatchEnabled = App.Settings.Prop.UseDisableAppPatch;
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
		if (overlayGameStateEnabled != _overlayGameStateEnabled)
		{
			_overlayGameStateEnabled = overlayGameStateEnabled;
			ReconcileOverlayGameState();
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
		UpdateRuntimeOptimizer();
		Voidstrap.KeyRouting.SnapTapHook.ApplyFromSettings();
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
		UpdateTasxOptimizer();
		if (!RobloxProcessOptimizer.ShouldRun(App.Settings.Prop))
		{
			_runtimeOptimizer?.Dispose();
			_runtimeOptimizer = null;
			return;
		}
		_runtimeOptimizer ??= new RobloxProcessOptimizer(_watcherData.ProcessId);
		_runtimeOptimizer.Start();
	}

	private void UpdateTasxOptimizer()
	{
		if (!TasxOptimizer.ShouldRun(App.Settings.Prop))
		{
			_tasxOptimizer?.Dispose();
			_tasxOptimizer = null;
			return;
		}
		_tasxOptimizer ??= new TasxOptimizer();
		_tasxOptimizer.Start();
	}

	private void EnableActivityIntegrations()
	{
		if (_watcherData == null)
		{
			return;
		}
		if (ActivityWatcher == null)
		{
			ActivityWatcher = new ActivityWatcher(_watcherData.LogFile);
			ActivityWatcher.OnGameJoin += OnRuntimeGameJoin;
			ActivityWatcher.OnGameLeave += OnRuntimeGameLeave;
			ActivityWatcher.Start();
		}
		SynchronizeLinuxGameState();
		if (App.Settings.Prop.UseDisableAppPatch)
		{
			ActivityWatcher.OnAppClose -= OnActivityAppClose;
			ActivityWatcher.OnAppClose += OnActivityAppClose;
		}
		if (App.Settings.Prop.UseDiscordRichPresence)
		{
			RichPresence ??= new DiscordRichPresence(ActivityWatcher);
		}
		IntegrationWatcher ??= new IntegrationWatcher(ActivityWatcher);
		HistoryPersister ??= new HistoryPersister(ActivityWatcher);
		ServerMatchmaker ??= new ServerMatchmaker(ActivityWatcher, this);
		ServerMatchmaker.NotifyIconResolver = () => _notifyIcon;
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
		ServerMatchmaker?.Dispose();
		ServerMatchmaker = null;
		HistoryPersister?.Dispose();
		HistoryPersister = null;
		IntegrationWatcher?.Dispose();
		IntegrationWatcher = null;
		RichPresence?.Dispose();
		RichPresence = null;
		if (ActivityWatcher != null)
		{
			ActivityWatcher.OnAppClose -= OnActivityAppClose;
			if (!Voidstrap.Utility.Platform.IsLinux && !_overlayGameStateEnabled)
			{
				ActivityWatcher.OnGameJoin -= OnRuntimeGameJoin;
				ActivityWatcher.OnGameLeave -= OnRuntimeGameLeave;
				ActivityWatcher.Dispose();
				ActivityWatcher = null;
			}
		}
		SynchronizeLinuxGameState();
		LaunchFlag testModeFlag = App.LaunchSettings.TestModeFlag;
		if (testModeFlag == null || !testModeFlag.Active)
		{
			_notifyIcon?.Dispose();
			_notifyIcon = null;
		}
	}

	private static bool OverlaysNeedGameState()
	{
		return Voidstrap.Integrations.Overlays.OverlaySettings.HomepageBackgroundEnabled
			|| (!Voidstrap.Utility.Platform.IsLinux && Voidstrap.Integrations.Overlays.OverlaySettings.GameEffectsEnabled);
	}

	private void ReconcileOverlayGameState()
	{
		if (_watcherData == null || _activityTrackingEnabled)
			return;
		if (_overlayGameStateEnabled)
		{
			if (ActivityWatcher == null)
			{
				ActivityWatcher = new ActivityWatcher(_watcherData.LogFile);
				ActivityWatcher.OnGameJoin += OnRuntimeGameJoin;
				ActivityWatcher.OnGameLeave += OnRuntimeGameLeave;
				ActivityWatcher.Start();
			}
		}
		SynchronizeLinuxGameState();
	}

	private void SynchronizeLinuxGameState()
	{
		if (Voidstrap.Utility.Platform.IsLinux && ActivityWatcher != null)
			Voidstrap.Integrations.Overlays.OverlayHub.SynchronizeLinuxGameState(ActivityWatcher.InGame);
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
		Voidstrap.Utility.RobloxProcessOptimizer.NoteGameTransition();
		if (Voidstrap.Utility.Platform.IsLinux)
			Voidstrap.Integrations.Overlays.OverlayHub.SynchronizeLinuxGameState(true);
		RunRuntimeAction(Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.OnGameJoin, "FullscreenJoin");
		RunRuntimeAction(Voidstrap.Integrations.RiShade.RiShadeManager.OnGameJoin, "RiShadeJoin");
		RunRuntimeAction(Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.OnGameJoin, "AntiAliasingJoin");
		RunOnApplicationDispatcher(EnsureRuntimeSessionWindows);
	}

	private void OnRuntimeGameLeave(object? sender, EventArgs e)
	{
		Voidstrap.Utility.RobloxProcessOptimizer.NoteGameTransition();
		if (Voidstrap.Utility.Platform.IsLinux)
			Voidstrap.Integrations.Overlays.OverlayHub.SynchronizeLinuxGameState(false);
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
		RunRuntimeAction(CrosshairWindow.Reconcile, "CreateCrosshair");
		RunRuntimeAction(Voidstrap.UI.Elements.ClassicTopBar.ClassicTopBarOverlay.Reconcile, "CreateClassicTopBar");
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
				if (Application.Current.Resources["OverlayWindow"] is OverlayWindow existing)
				{
					if (existing.IsLoaded)
						return;

					try
					{
						existing.Close();
					}
					catch (Exception ex)
					{
						App.Logger.WriteLine("Watcher::CreateOverlay", "The previous overlay could not be closed: " + ex.Message);
					}
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

		RunRuntimeAction(CrosshairWindow.Reconcile, "RefreshCrosshair");
		RunRuntimeAction(Voidstrap.UI.Elements.ClassicTopBar.ClassicTopBarOverlay.Reconcile, "RefreshClassicTopBar");

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
		Application? application = Application.Current;
		if (application is null)
		{
			return;
		}

		RunRuntimeAction(CrosshairWindow.CloseAll, "CloseCrosshair");
		RunRuntimeAction(Voidstrap.UI.Elements.ClassicTopBar.ClassicTopBarOverlay.CloseAll, "CloseClassicTopBar");
		if (application.Resources["OverlayWindow"] is OverlayWindow overlay)
		{
			RunRuntimeAction(overlay.Close, "CloseOverlay");
		}
		application.Resources.Remove("OverlayWindow");
		ScreenColorEffect.Reset();
	}

	public void KillRobloxProcess()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			try
			{
				if (Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.TryCloseSober())
				{
					App.Logger.WriteLine("Watcher::KillRobloxProcess", "Closed the Roblox runtime");
					return;
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Watcher::KillRobloxProcess", "The Roblox runtime could not be closed: " + ex.Message);
			}
		}

		CloseProcess(_watcherData!.ProcessId, force: true);
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
		catch (ArgumentException)
		{
			App.Logger.WriteLine("Watcher::CloseProcess", $"PID {pid} already exited before close");
		}
		catch (InvalidOperationException)
		{
			App.Logger.WriteLine("Watcher::CloseProcess", $"PID {pid} exited during close");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher::CloseProcess", $"PID {pid} could not be closed");
			App.Logger.WriteException("Watcher::CloseProcess", ex);
		}
	}

	private const int StartupCrashSeconds = 30;

	private const int CrashHandlerDelaySeconds = 60;

	private async Task MonitorModCrashAsync(DateTime sessionStartedUtc, CancellationToken token)
	{
		const int CheckIntervalMs = 2000;
		const int SettleSeconds = 120;
		try
		{
			while (!token.IsCancellationRequested)
			{
				await Task.Delay(CheckIntervalMs, token).ConfigureAwait(false);
				if (ModCrashGuard.HasCrashReportSince(sessionStartedUtc))
				{
					if (!await RobloxExitsSoonAsync().ConfigureAwait(false))
					{
						App.Logger.WriteLine("Watcher::MonitorModCrash", "Roblox wrote a crash report but kept running, so it was not treated as a crash");
						sessionStartedUtc = DateTime.UtcNow.AddSeconds(3);
						continue;
					}
					IReadOnlyList<string> disabled = ModCrashGuard.HandleCrash();
					RunOnApplicationDispatcher(() => ReportModCrash(disabled));
					return;
				}
				if ((DateTime.UtcNow - sessionStartedUtc).TotalSeconds >= SettleSeconds)
				{
					ModCrashGuard.MarkHealthy();
					return;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher::MonitorModCrash", "The crash guard stopped: " + ex.Message);
		}
	}

	private async Task<bool> RobloxExitsSoonAsync()
	{
		if (_watcherData == null)
		{
			return true;
		}
		try
		{
			using Process roblox = Process.GetProcessById(_watcherData.ProcessId);
			using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			await roblox.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (InvalidOperationException)
		{
			return true;
		}
		catch (ArgumentException)
		{
			return true;
		}
	}

	private async Task DisableCrashHandlerWhenSettledAsync(DateTime sessionStartedUtc, CancellationToken token)
	{
		if (App.Settings.Prop.DisableCrash != true || _watcherData == null)
		{
			return;
		}
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(CrashHandlerDelaySeconds), token).ConfigureAwait(false);
			if (ModCrashGuard.HasCrashReportSince(sessionStartedUtc))
			{
				return;
			}
			using Process roblox = Process.GetProcessById(_watcherData.ProcessId);
			if (roblox.HasExited)
			{
				return;
			}
			foreach (Process handler in Process.GetProcessesByName("RobloxCrashHandler"))
			{
				using (handler)
				{
					try
					{
						if (!handler.HasExited && handler.StartTime.ToUniversalTime() >= sessionStartedUtc.AddSeconds(-2))
						{
							handler.Kill();
							App.Logger.WriteLine("Watcher::DisableCrashHandler", "Closed CrashHandler " + handler.Id + " after the startup safety delay");
						}
					}
					catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
					{
						App.Logger.WriteLine("Watcher::DisableCrashHandler", "CrashHandler " + handler.Id + " could not be closed: " + ex.Message);
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			App.Logger.WriteLine("Watcher::DisableCrashHandler", "The crash handler check stopped: " + ex.Message);
		}
	}

	private void ReportModCrash(IReadOnlyList<string> disabled)
	{
		if (disabled.Count == 0)
		{
			Frontend.ShowMessageBox("Roblox crashed while starting. None of your recently added mods look responsible, so nothing was turned off. If it keeps happening, try turning off AssetWarp or recent mods in My Mods.", MessageBoxImage.Warning);
			return;
		}
		if (_watcherData != null)
		{
			CloseProcess(_watcherData.ProcessId, force: true);
		}
		string list = string.Join("\n", disabled.Select(name => "  " + name));
		MessageBoxResult answer = Frontend.ShowMessageBox(
			"Roblox crashed while loading your mods, so Voidstrap turned these off:\n\n" + list + "\n\nYou can turn them back on in My Mods. Launch Roblox again now?",
			MessageBoxImage.Warning,
			MessageBoxButton.YesNo,
			MessageBoxResult.Yes);
		if (answer == MessageBoxResult.Yes)
		{
			RelaunchRoblox();
		}
	}

	private static void RelaunchRoblox()
	{
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = Paths.LaunchExecutable,
				UseShellExecute = false
			};
			startInfo.ArgumentList.Add("-player");
			string launchArgs = App.LaunchSettings.RobloxLaunchArgs ?? "";
			if (launchArgs.StartsWith("roblox", StringComparison.OrdinalIgnoreCase))
			{
				startInfo.ArgumentList.Add(launchArgs);
			}
			using Process? relaunch = Process.Start(startInfo);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Watcher::ReportModCrash", "Roblox could not be launched again: " + ex.Message);
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
		DateTime sessionStartedUtc = ModCrashGuard.BeginSession();
		Task crashMonitor = MonitorModCrashAsync(sessionStartedUtc, _lifetimeCancellation.Token);
		_ = DisableCrashHandlerWhenSettledAsync(sessionStartedUtc, _lifetimeCancellation.Token);
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
		if (DateTime.UtcNow - sessionStartedUtc < TimeSpan.FromSeconds(StartupCrashSeconds))
		{
			await Task.WhenAny(crashMonitor, Task.Delay(5000)).ConfigureAwait(false);
		}
		if (_watcherData.AutoclosePids != null)
		{
			foreach (int autoclosePid in _watcherData.AutoclosePids)
			{
				CloseProcess(autoclosePid);
			}
		}
		ReopenSettingsForTestMode();
		await Bootstrapper.CompressInstallsAfterExitAsync().ConfigureAwait(false);
	}

	private void ReopenSettingsForTestMode()
	{
		if (_disposed || _lifetimeCancellation.IsCancellationRequested || !App.LaunchSettings.TestModeFlag.Active)
		{
			return;
		}
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = Paths.LaunchExecutable,
				UseShellExecute = false
			};
			startInfo.ArgumentList.Add("-settings");
			startInfo.ArgumentList.Add("-testmode");
			using Process? settingsProcess = Process.Start(startInfo);
			if (settingsProcess == null)
			{
				App.Logger.WriteLine("Watcher::ReopenSettingsForTestMode", "Settings did not reopen, the test mode cycle has stopped");
				return;
			}
			App.Logger.WriteLine("Watcher::ReopenSettingsForTestMode", "Roblox closed, settings reopened for the next test mode pass");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Watcher::ReopenSettingsForTestMode", ex);
		}
	}

	private static bool IsWindowManipulationEnabled()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return false;
		}

		var prop = App.Settings.Prop;
		if (prop.FakeBorderlessFullscreen || prop.CycleTitleWithGameName || prop.UseGameIconForRobloxWindow)
		{
			return true;
		}
		return !string.IsNullOrWhiteSpace(prop.RobloxTitle) && prop.RobloxTitle != "Voidstrap";
	}

	private delegate bool WatcherEnumWindowsProc(IntPtr hwnd, IntPtr lparam);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EnumWindows(IntPtr callback, IntPtr lparam);

	[LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

	[LibraryImport("user32.dll")]
	private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsWindowVisible(IntPtr hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsIconic(IntPtr hwnd);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private partial struct WatcherRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetClientRect(IntPtr hwnd, out WatcherRect rect);

	private static IntPtr FindRobloxGameWindow(int processId)
	{
		IntPtr best = IntPtr.Zero;
		long bestArea = 0;
		uint target = (uint)processId;

		try
		{
			WatcherEnumWindowsProc callback = delegate (IntPtr hwnd, IntPtr lparam)
			{
				if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
					return true;

				GetWindowThreadProcessId(hwnd, out uint owner);
				if (owner != target)
					return true;

				char[] className = new char[64];
				int classLength = GetClassName(hwnd, className, className.Length);
				if (!className.AsSpan(0, Math.Max(0, classLength)).SequenceEqual("WINDOWSCLIENT"))
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
			};
			_ = EnumWindows(Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero);
			GC.KeepAlive(callback);
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
		Voidstrap.Integrations.Overlays.OverlayHub.ReleaseLinuxGameplayLease();
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
			_tasxOptimizer?.Dispose();
			_tasxOptimizer = null;
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
			ServerMatchmaker?.Dispose();
			ServerMatchmaker = null;
		}
		catch
		{
		}
		try
		{
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
