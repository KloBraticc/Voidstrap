using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.UI.Elements.Installer;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.Utility;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Voidstrap;

public static class LaunchHandler
{
	private static int _portableLaunchActive;

	public static void ProcessNextAction(NextAction action, bool isUnfinishedInstall = false)
	{
		switch (action)
		{
		case NextAction.LaunchSettings:
			App.Logger.WriteLine("LaunchHandler::ProcessNextAction", "Opening settings");
			LaunchSettings();
			break;
		case NextAction.LaunchRoblox:
			App.Logger.WriteLine("LaunchHandler::ProcessNextAction", "Opening Roblox");
			LaunchRoblox(LaunchMode.Player);
			break;
		case NextAction.LaunchRobloxStudio:
			App.Logger.WriteLine("LaunchHandler::ProcessNextAction", "Opening Roblox Studio");
			LaunchRoblox(LaunchMode.Studio);
			break;
		default:
			App.Logger.WriteLine("LaunchHandler::ProcessNextAction", "Closing");
			App.Terminate(isUnfinishedInstall ? ErrorCode.ERROR_INSTALL_USEREXIT : ErrorCode.ERROR_SUCCESS);
			break;
		}
	}

	private static void RunWindowAudit()
	{
		try
		{
			Voidstrap.Utility.WindowAudit.Run();
		}
		catch (Exception auditEx)
		{
			App.Logger.WriteLine("WindowAudit", "audit harness failed: " + auditEx);
		}
		App.Terminate();
	}

	public static void ProcessLaunchArgs()
	{
		if (App.LaunchSettings.ResumeLaunchFlag.Active && App.State.Prop.PendingLaunchMode > 0)
		{
			int pendingMode = App.State.Prop.PendingLaunchMode;
			App.State.Prop.PendingLaunchMode = 0;
			App.State.Save();
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Resuming deferred launch as mode " + pendingMode);
			LaunchRoblox((LaunchMode)pendingMode);
			return;
		}
		if (App.LaunchSettings.WindowAuditFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Running window audit");
			Application? auditApplication = Application.Current;
			if (auditApplication == null)
			{
				RunWindowAudit();
				return;
			}
			auditApplication.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RunWindowAudit));
			return;
		}
		if (App.LaunchSettings.NvApplyFlag.Active)
		{
			string staged = App.LaunchSettings.NvApplyFlag.Data ?? "";
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Applying staged NVIDIA profile: " + staged);
			bool applied = false;
			try
			{
				applied = Voidstrap.Integrations.NvidiaProfileManager.ApplyStagedFile(staged);
			}
			catch (Exception nvEx)
			{
				App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Staged NVIDIA apply threw: " + nvEx.Message);
			}
			Environment.ExitCode = applied ? 0 : 1;
			App.Terminate(applied ? ErrorCode.ERROR_SUCCESS : ErrorCode.ERROR_INSTALL_FAILURE);
			return;
		}
		if (App.LaunchSettings.TelemetryBlockFlag.Active)
		{
			bool enable = !string.Equals(App.LaunchSettings.TelemetryBlockFlag.Data, "off", StringComparison.OrdinalIgnoreCase);
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Applying telemetry block state: " + (enable ? "on" : "off"));
			if (enable)
			{
				TelemetryBlocker.Apply();
			}
			else
			{
				TelemetryBlocker.Remove();
			}
			App.Terminate();
			return;
		}
		if (App.LaunchSettings.OrcRedirectFlag.Active)
		{
			string data = App.LaunchSettings.OrcRedirectFlag.Data ?? "";
			int separator = data.IndexOf(':');
			string mode = separator >= 0 ? data.Substring(0, separator) : data;
			string owner = separator >= 0 ? data.Substring(separator + 1) : "";
			bool enable = !string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Applying classic client host redirect: " + (enable ? "on" : "off"));
			if (enable)
			{
				ClassicHostRedirect.Apply();
				if (int.TryParse(owner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ownerPid) && ownerPid > 0)
				{
					ClassicHostRedirect.RemoveWhenSessionEnds(ownerPid);
				}
			}
			else
			{
				ClassicHostRedirect.Remove();
			}
			App.Terminate();
			return;
		}
		if (!App.LaunchSettings.WatcherFlag.Active)
		{
			CloseOtherInstances();
		}
		if (App.LaunchSettings.UninstallFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Opening uninstaller");
			LaunchUninstaller();
		}
		else if (App.LaunchSettings.MenuFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Opening settings");
			LaunchSettings();
		}
		else if (App.LaunchSettings.WatcherFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Opening watcher");
			LaunchWatcher();
		}
		else if (App.LaunchSettings.RobloxLaunchMode != LaunchMode.None)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", $"Opening bootstrapper ({App.LaunchSettings.RobloxLaunchMode})");
			LaunchRoblox(App.LaunchSettings.RobloxLaunchMode);
		}
		else if (App.LaunchSettings.BloxshadeFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Opening Bloxshade");
			LaunchBloxshadeConfig();
		}
		else if (!App.LaunchSettings.QuietFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Opening menu");
			LaunchMenu();
		}
		else
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Closing (quiet flag active)");
			App.Terminate();
		}
	}

	public static void LaunchInstaller()
	{
		InterProcessLock interProcessLock = new InterProcessLock("Installer");
		try
		{
			if (!interProcessLock.IsAcquired)
			{
				Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Installer, MessageBoxImage.Hand);
				App.Terminate();
				return;
			}
			else if (App.LaunchSettings.UninstallFlag.Active)
			{
				Frontend.ShowMessageBox(Strings.Bootstrapper_FirstRunUninstall, MessageBoxImage.Hand);
				App.Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
				return;
			}
			else if (App.LaunchSettings.QuietFlag.Active)
			{
				Installer installer = new Installer();
				if (!installer.CheckInstallLocation())
				{
					App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
					return;
				}
				try
				{
					installer.DoInstall();
				}
				catch (Exception ex)
				{
					App.Logger.WriteException("LaunchHandler::LaunchInstaller", ex);
					Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Hand);
					App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
					return;
				}
				interProcessLock.Dispose();
				ProcessLaunchArgs();
			}
			else
			{
				if (new LanguageSelectorDialog().ShowOwnedDialog() != true)
				{
					App.Terminate(ErrorCode.ERROR_INSTALL_USEREXIT);
					return;
				}
				Voidstrap.UI.Elements.Installer.MainWindow mainWindow = new Voidstrap.UI.Elements.Installer.MainWindow();
				mainWindow.ShowOwnedDialog();
				interProcessLock.Dispose();
				ProcessNextAction(mainWindow.CloseAction, !mainWindow.Finished);
			}
		}
		finally
		{
			interProcessLock.Dispose();
		}
	}

	public static void LaunchUninstaller()
	{
		if (Voidstrap.Utility.Platform.IsWindows && !ProcessElevation.IsAdministrator())
		{
			if (ProcessElevation.TryRestartElevated(App.LaunchSettings.Args))
			{
				App.SoftTerminate();
				return;
			}
			App.Terminate(ErrorCode.ERROR_CANCELLED);
			return;
		}
		using InterProcessLock interProcessLock = new InterProcessLock("Uninstaller");
		if (!interProcessLock.IsAcquired)
		{
			Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Uninstaller, MessageBoxImage.Hand);
			App.Terminate();
			return;
		}
		bool keepData = true;
		bool flag;
		if (App.LaunchSettings.QuietFlag.Active)
		{
			flag = true;
		}
		else
		{
			UninstallerDialog uninstallerDialog = new UninstallerDialog();
			uninstallerDialog.ShowOwnedDialog();
			flag = uninstallerDialog.Confirmed;
			keepData = uninstallerDialog.KeepData;
		}
		if (!flag)
		{
			App.Terminate();
			return;
		}
		Installer.DoUninstall(keepData);
		Frontend.ShowMessageBox(Strings.Bootstrapper_SuccessfullyUninstalled, MessageBoxImage.Asterisk);
		App.Terminate();
	}

	public static void LaunchSettings()
	{
		WaitForElevationPredecessor();
		using InterProcessLock interProcessLock = new InterProcessLock("Settings");
		if (interProcessLock.IsAcquired)
		{
			Voidstrap.UI.Elements.Settings.MainWindow window = new(Process.GetProcessesByName("Voidstrap").Length > 1);
			Application? application = Application.Current;
			Window? previousMainWindow = null;
			bool ownsLinuxMainWindow = Voidstrap.Utility.Platform.IsLinux && application != null;
			if (ownsLinuxMainWindow)
			{
				previousMainWindow = application!.MainWindow;
				application.MainWindow = window;
			}
			try
			{
				window.ShowOwnedDialog();
			}
			finally
			{
				if (ownsLinuxMainWindow && ReferenceEquals(application!.MainWindow, window))
					application.MainWindow = previousMainWindow!;
			}
			return;
		}
		App.Logger.WriteLine("LaunchHandler::LaunchSettings", "Found an already existing menu window");
		Process[] processesSafe = Utilities.GetProcessesSafe();
		try
		{
			Process? process = processesSafe.FirstOrDefault((Process x) => x.MainWindowTitle == Strings.Menu_Title);
			if (process != null && process.MainWindowHandle != IntPtr.Zero)
			{
				Windows.Win32.PInvoke.SetForegroundWindow(new HWND(process.MainWindowHandle));
			}
		}
		finally
		{
			Process[] array = processesSafe;
			foreach (Process process2 in array)
			{
				try
				{
					process2.Dispose();
				}
				catch
				{
				}
			}
		}
		App.Terminate();
	}

	private static void WaitForElevationPredecessor()
	{
		string[] args = App.LaunchSettings.Args;
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (!string.Equals(args[i], "-elevatedwait", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!int.TryParse(args[i + 1], out var result))
			{
				break;
			}
			try
			{
				using Process process = Process.GetProcessById(result);
				App.Logger.WriteLine("LaunchHandler::WaitForElevationPredecessor", $"Waiting for previous instance ({result}) to exit");
				process.WaitForExit(10000);
				break;
			}
			catch
			{
				break;
			}
		}
	}

	public static void LaunchMenu()
	{
		LaunchMenuDialog launchMenuDialog = new LaunchMenuDialog();
		launchMenuDialog.ShowOwnedDialog();
		ProcessNextAction(launchMenuDialog.CloseAction);
	}

	public static void LaunchRoblox(LaunchMode launchMode)
	{
		if (launchMode == LaunchMode.None)
		{
			throw new InvalidOperationException("No Roblox launch mode set");
		}
		App.Settings.FlushDeferred();
		App.FastFlags.FlushDeferred();
		if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
		{
			App.LaunchSettings.RobloxLaunchMode = launchMode;
			RunPortableLaunch(launchMode);
			return;
		}
		if (launchMode == LaunchMode.Player)
		{
			AssetWarpAutoEnable.EnsureEnabled(allowPrompt: !App.LaunchSettings.QuietFlag.Active, userAction: false);
		}
		bool useAssetWarp = launchMode == LaunchMode.Player && (AssetProxyServer.IsRequired || AssetWarpAutoEnable.IsActiveForMods());
		bool needsAssetWarpCleanup = !useAssetWarp && AssetProxyRouting.HasInstalledEntries();
		if (needsAssetWarpCleanup)
		{
			if (ProcessElevation.IsAdministrator())
			{
				AssetProxyRouting.Cleanup();
			}
			needsAssetWarpCleanup = AssetProxyRouting.HasInstalledEntries();
			if (!needsAssetWarpCleanup)
			{
				App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Leftover AssetWarp routing was cleared");
			}
		}
		string? elevationReason = null;
		if (useAssetWarp)
		{
			elevationReason = "AssetWarp needs administrator access to start";
		}
		else if (needsAssetWarpCleanup)
		{
			elevationReason = "Leftover AssetWarp routing could not be cleared automatically and needs administrator access to remove";
		}

		if (elevationReason != null && !ProcessElevation.IsAdministrator())
		{
			if (App.LaunchSettings.AdminRetriedFlag.Active)
			{
				App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Elevation already attempted or declined, continuing launch.");
			}
			else
			{
				App.State.Prop.PendingLaunchMode = (int)launchMode;
				App.State.Save();
				List<string> elevateArgs = ["-resumelaunch", "-adminretried"];
				if (App.LaunchSettings.RobloxLaunchArgs.Length > 0)
				{
					elevateArgs.Add(launchMode == LaunchMode.Player ? "-player" : "-studio");
					elevateArgs.Add(App.LaunchSettings.RobloxLaunchArgs);
				}
				App.Logger.WriteLine("LaunchHandler::LaunchRoblox", elevationReason + ". Requesting elevation to resume launch.");
				bool elevated = ProcessElevation.TryRestartElevated(elevateArgs);
				if (elevated)
				{
					App.SoftTerminate();
					return;
				}
				App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "User declined elevation. Continuing launch.");
				App.State.Prop.PendingLaunchMode = 0;
				App.State.Save();
			}
		}
		if (needsAssetWarpCleanup)
		{
			if (ProcessElevation.IsAdministrator())
			{
				AssetProxyRouting.Cleanup();
			}
			if (AssetProxyRouting.HasInstalledEntries())
			{
				App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Leftover AssetWarp routing is still in the hosts file, Roblox assets will fail to load until it is removed");
				if (!App.LaunchSettings.QuietFlag.Active)
				{
					Frontend.ShowMessageBox(Strings.Bootstrapper_AssetWarpRoutingLeftover, MessageBoxImage.Exclamation);
				}
			}
		}
		if (launchMode != LaunchMode.Player)
		{
			AssetProxyServer.DisableForUnsupportedClient();
		}
		App.LaunchSettings.RobloxLaunchMode = launchMode;
		if (!App.LaunchSettings.MatchmakerRejoinFlag.Active)
		{
			try
			{
				if (App.State.Prop.MatchmakerAttempts != null && App.State.Prop.MatchmakerAttempts.Count > 0)
				{
					App.State.Prop.MatchmakerAttempts.Clear();
					try
					{
						App.State.Save();
					}
					catch
					{
					}
					App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Fresh user-initiated launch detected cleared stale matchmaker attempt counters");
				}
			}
			catch
			{
			}
		}
		if (Voidstrap.Utility.Platform.SupportsWindowsClient && !File.Exists(Path.Combine(Paths.System, "mfplat.dll")))
		{
			Frontend.ShowMessageBox(Strings.Bootstrapper_WMFNotFound, MessageBoxImage.Hand);
			if (!App.LaunchSettings.QuietFlag.Active)
			{
				Utilities.ShellExecute("https://support.microsoft.com/en-us/topic/media-feature-pack-list-for-windows-n-editions-c1c6fffa-d052-8338-7a79-a4bb980a700a");
			}
			App.Terminate(ErrorCode.ERROR_FILE_NOT_FOUND);
			return;
		}
		bool flag = false;
		Mutex? result;
		try
		{
			flag = Mutex.TryOpenExisting("Global\\ROBLOX_singletonMutex", out result);
		}
		catch (UnauthorizedAccessException)
		{
			flag = false;
		}
		catch
		{
			flag = false;
		}
		if (!flag)
		{
			try
			{
				flag = Mutex.TryOpenExisting("ROBLOX_singletonMutex", out result);
			}
			catch
			{
				flag = false;
			}
		}
		if (App.Settings.Prop.ConfirmLaunches && flag && !App.LaunchSettings.MatchmakerRejoinFlag.Active && (!App.Settings.Prop.IsGameEnabled || string.IsNullOrWhiteSpace(App.Settings.Prop.LaunchGameID)) && Frontend.ShowMessageBox(Strings.Bootstrapper_ConfirmLaunch, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			App.Terminate();
			return;
		}
		bool flag2 = string.Equals(Environment.GetEnvironmentVariable("VOIDSTRAP_FORCE_NATIVE"), "1", StringComparison.Ordinal);
		if (!flag2)
		{
			CloseOtherInstances();
		}
		App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Initializing bootstrapper");
		App.Bootstrapper = new Bootstrapper(launchMode);
		IBootstrapperDialog? bootstrapperDialog = null;
		if (!App.LaunchSettings.QuietFlag.Active && !flag2)
		{
			App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Initializing bootstrapper dialog");
			bootstrapperDialog = App.Settings.Prop.BootstrapperStyle.GetNew();
			App.Bootstrapper.Dialog = bootstrapperDialog;
			bootstrapperDialog.Bootstrapper = App.Bootstrapper;
		}
		if (App.Settings.Prop.ExclusiveFullscreen)
		{
			_ = RobloxFullscreen.WaitAndTriggerFullscreenAsync(App.Bootstrapper.CancellationToken);
		}
		Task.Run((Func<Task?>)App.Bootstrapper.Run).ContinueWith(delegate(Task t)
		{
			App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Bootstrapper task has finished");
			try
			{
				if (t.IsFaulted)
				{
					App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "An exception occurred when running the bootstrapper");
					if (t.Exception != null)
					{
						App.FinalizeExceptionHandling(t.Exception);
					}
				}
			}
			finally
			{
				App.SoftTerminate();
			}
		});
		bootstrapperDialog?.ShowBootstrapper();
		App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Exiting");
	}

	private static readonly CancellationTokenSource _residentCancellation = new();

	private static int _residentActive;

	private static readonly TaskCompletionSource<bool> PortableSessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal static bool PortableSessionActive { get; private set; }

	private static void RunPortableLaunch(LaunchMode launchMode)
	{
		PortableSessionActive = true;
		KeepAliveUntilPortableSessionEnds();
		_ = LaunchPortableRuntimeAsync(launchMode);
	}

	private const string LaunchKeepAliveTitle = "Voidstrap Launch";

	private static Window? _launchKeepAliveWindow;

	private static void KeepAliveUntilPortableSessionEnds()
	{
		Application? application = Application.Current;
		if (application is null || !application.Dispatcher.CheckAccess())
		{
			WaitForPortableSession();
			return;
		}

		try
		{
			application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::KeepAliveUntilPortableSessionEnds", "The shutdown mode could not be set: " + ex.Message);
		}

		try
		{
			Window window = new()
			{
				Title = LaunchKeepAliveTitle,
				Width = 1,
				Height = 1,
				Left = -32000,
				Top = -32000,
				ShowInTaskbar = false,
				ShowActivated = false,
				WindowStyle = WindowStyle.None,
				ResizeMode = ResizeMode.NoResize,
				WindowState = System.Windows.WindowState.Minimized
			};
			window.Show();
			window.Hide();
			_launchKeepAliveWindow = window;
			application.MainWindow = window;
			Voidstrap.UI.LinuxHiddenWindow.Hide(LaunchKeepAliveTitle);
			CloseSettingsWindows(application);
			StopAppPresence();
			App.Logger.WriteLine("LaunchHandler::KeepAliveUntilPortableSessionEnds", "Holding the session open while Roblox starts");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::KeepAliveUntilPortableSessionEnds", "The session could not be held open: " + ex.Message);
		}

		PortableSessionEnded.Task.ContinueWith(
			delegate { ReleaseLaunchKeepAlive(); },
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private static void CloseSettingsWindows(Application application)
	{
		try
		{
			foreach (Window open in application.Windows.OfType<Window>().ToArray())
			{
				if (open is Voidstrap.UI.Elements.Settings.MainWindow settings)
				{
					App.Logger.WriteLine("LaunchHandler::CloseSettingsWindows", "Closing the Voidstrap window so the session runs from the tray");
					settings.Close();
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::CloseSettingsWindows", "The Voidstrap window could not be closed: " + ex.Message);
		}
	}

	private static void StopAppPresence()
	{
		try
		{
			App.StopCustomRpc();
			App.Logger.WriteLine("LaunchHandler::StopAppPresence", "Voidstrap presence stopped so the Roblox presence is the only one shown");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::StopAppPresence", "The Voidstrap presence could not be stopped: " + ex.Message);
		}
	}

	private static void ReleaseLaunchKeepAlive()
	{
		Voidstrap.UI.LinuxTaskbarPresence.StopForShutdown();
		Window? window = Interlocked.Exchange(ref _launchKeepAliveWindow, null);
		if (window is null)
		{
			return;
		}

		Application? application = Application.Current;
		if (application is null)
		{
			return;
		}

		void close()
		{
			try
			{
				window.Close();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LaunchHandler::ReleaseLaunchKeepAlive", "The session window could not be closed: " + ex.Message);
			}
		}

		try
		{
			if (application.Dispatcher.CheckAccess())
			{
				close();
			}
			else
			{
				application.Dispatcher.Invoke(close);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::ReleaseLaunchKeepAlive", "The session window could not be reached: " + ex.Message);
		}
	}

	private static void WaitForPortableSession()
	{
		try
		{
			PortableSessionEnded.Task.Wait();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::WaitForPortableSession", "The launch wait ended early: " + ex.Message);
		}
	}

	private const int SoberStartupAttempts = 60;

	private const int SoberExitConfirmations = 3;

	private static async Task<bool> WaitForSoberExitAsync(CancellationToken cancellationToken)
	{
		Voidstrap.Platform.Linux.LinuxSoberProcessProbe probe =
			new Voidstrap.Platform.Linux.LinuxSoberProcessProbe(new Voidstrap.Core.SystemProcessService());

		bool started = false;

		for (int attempt = 0; attempt < SoberStartupAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (await probe.IsRunningAsync(cancellationToken))
			{
				started = true;
				break;
			}

			await Task.Delay(1000, cancellationToken);
		}

		if (!started)
		{
			App.Logger.WriteLine("LaunchHandler::WaitForSoberExitAsync", "Roblox never appeared in the process list, falling back to the launch process lifetime");
			return false;
		}

		int missed = 0;
		bool waitingForRejoin = false;

		while (!cancellationToken.IsCancellationRequested)
		{
			if (await probe.IsRunningAsync(cancellationToken))
			{
				missed = 0;
				waitingForRejoin = false;
			}
			else if (Voidstrap.Integrations.ServerMatchmaker.LinuxRejoinInProgress)
			{
				missed = 0;
				if (!waitingForRejoin)
				{
					waitingForRejoin = true;
					App.Logger.WriteLine("LaunchHandler::WaitForSoberExitAsync", "Sober is restarting for a matchmaker rejoin, keeping Voidstrap resident");
				}
			}
			else
			{
				missed++;

				if (missed >= SoberExitConfirmations)
				{
					return true;
				}

				App.Logger.WriteLine(
					"LaunchHandler::WaitForSoberExitAsync",
					"Roblox was not visible on check " + missed + " of " + SoberExitConfirmations + ", waiting before deciding it closed");
			}

			await Task.Delay(2000, cancellationToken);
		}

		return false;
	}

	private static bool StartResidentWatcher(int processId)
	{
		try
		{
			Voidstrap.Models.WatcherData watcherData = new()
			{
				ProcessId = processId,
				AutoclosePids = []
			};

			Watcher resident = new(watcherData);
			Voidstrap.Integrations.LinuxAutoFullscreen? autoFullscreen = null;

			if (Voidstrap.Utility.Platform.IsLinux)
			{
				System.Windows.Application? application = System.Windows.Application.Current;

				application?.Dispatcher.Invoke(delegate
				{
					autoFullscreen = new Voidstrap.Integrations.LinuxAutoFullscreen();
					autoFullscreen.Start();
				});
			}

			_ = Task.Run(async delegate
			{
				try
				{
					Task residentTask = resident.Run();
					bool soberExitObserved = await WaitForSoberExitAsync(_residentCancellation.Token);
					if (!soberExitObserved)
						await residentTask;
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("LaunchHandler::StartResidentWatcher", "The resident watcher stopped unexpectedly: " + ex.Message);
				}

				App.Logger.WriteLine("LaunchHandler::StartResidentWatcher", "Roblox has exited, shutting down");
				try
				{
					autoFullscreen?.Dispose();
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("LaunchHandler::StartResidentWatcher", "Auto fullscreen could not be released: " + ex.Message);
				}

				try
				{
					resident.Dispose();
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("LaunchHandler::StartResidentWatcher", "The watcher could not be disposed: " + ex.Message);
				}

				Volatile.Write(ref _residentActive, 0);
				PortableSessionEnded.TrySetResult(true);
				App.SoftTerminate();
			});

			Volatile.Write(ref _residentActive, 1);
			App.Logger.WriteLine("LaunchHandler::StartResidentWatcher", "Voidstrap is staying resident so overlays and integrations keep running");
			return true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::StartResidentWatcher", "The resident watcher could not start: " + ex.Message);
			return false;
		}
	}

	private static async Task LaunchPortableRuntimeAsync(LaunchMode launchMode)
	{
		if (Interlocked.CompareExchange(ref _portableLaunchActive, 1, 0) != 0)
		{
			ShowPortableLaunchFailure("A Roblox launch is already in progress.");
			PortableSessionEnded.TrySetResult(true);
			return;
		}

		bool stayResident = false;
		long assetPreloadPlaceId = 0;

		try
		{
			Voidstrap.Platform.IPlatformHost? host = Voidstrap.Utility.Platform.RuntimeHost;
			if (host == null)
			{
				ShowPortableLaunchFailure("Roblox runtime services are not available on this platform.");
				return;
			}

			Voidstrap.Platform.RuntimeKind runtimeKind = launchMode == LaunchMode.Player
				? Voidstrap.Platform.RuntimeKind.Player
				: Voidstrap.Platform.RuntimeKind.Studio;
			string launchTarget = App.LaunchSettings.RobloxLaunchArgs;
			if (string.IsNullOrWhiteSpace(launchTarget))
			{
				launchTarget = runtimeKind == Voidstrap.Platform.RuntimeKind.Player
					? "roblox://experiences/start"
					: "roblox-studio://launch";
			}
			string rewrittenTarget = await Bootstrapper.RewriteVoidstrapMatchmakerBeforeDispatchAsync(
				launchTarget,
				launchMode,
				CancellationToken.None);
			if (!string.Equals(rewrittenTarget, launchTarget, StringComparison.Ordinal))
			{
				launchTarget = rewrittenTarget;
				App.LaunchSettings.RobloxLaunchArgs = rewrittenTarget;
			}
			assetPreloadPlaceId = LaunchInterceptor.ExtractPlaceId(launchTarget);

			if (OperatingSystem.IsLinux())
			{
				Bootstrapper bootstrapper = new(launchMode);
				ShowPortableLaunchDialog(bootstrapper);
				if (await bootstrapper.TryUpdateLauncherAsync())
				{
					return;
				}
				if (runtimeKind == Voidstrap.Platform.RuntimeKind.Player)
				{
					Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.ForceX11Session = Voidstrap.Integrations.Overlays.OverlaySettings.RequiresLinuxX11Session
						&& !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
					App.Logger.WriteLine("LaunchHandler::LaunchPortableRuntime", Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.ForceX11Session
						? "Fake fullscreen or the homepage background is on, starting Sober on X11"
						: "Starting Sober in its default display mode");
					await PrepareLinuxEffectLayersAsync();
					SetPortableLaunchStatus("Closing the current Roblox session");
					if (!await Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.TryCloseSoberAsync(CancellationToken.None))
					{
						ShowPortableLaunchFailure("The current Sober session could not be closed. Close Sober and try joining again.");
						return;
					}
					try
					{
						SetPortableLaunchStatus(Strings.Bootstrapper_Status_Configuring);
						await bootstrapper.PrepareLinuxLaunchAsync(CancellationToken.None);
					}
					catch (Exception ex)
					{
						App.Logger.WriteLine("LaunchHandler::LaunchPortableRuntime", "Linux modifications could not be prepared: " + ex.Message);
					}
				}

				Voidstrap.Platform.IRobloxRuntimeProvider provider = runtimeKind == Voidstrap.Platform.RuntimeKind.Player
					? host.PlayerRuntime
					: host.StudioRuntime;
				SetPortableLaunchStatus(Strings.Bootstrapper_Status_Connecting);
				Voidstrap.Platform.RuntimeInstallation installation = await provider.FindInstallationAsync();
				if (runtimeKind == Voidstrap.Platform.RuntimeKind.Player)
				{
					installation = await Bootstrapper.EnsureSoberInstalledAsync(provider, installation, host, SetPortableLaunchStatus, CancellationToken.None);
				}
				else
				{
					installation = await Bootstrapper.EnsureVinegarInstalledAsync(provider, installation, host, SetPortableLaunchStatus, CancellationToken.None);
				}
				if (!installation.Capability.IsAvailable)
				{
					ShowPortableLaunchFailure(installation.Capability.Reason);
					return;
				}

				if (runtimeKind == Voidstrap.Platform.RuntimeKind.Player
					&& !Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.IsRobloxPackageInstalled())
				{
					SetPortableLaunchStatus("Downloading Roblox for the first time, this can take a few minutes");
					App.Logger.WriteLine(
						"LaunchHandler::FirstRun",
						"Roblox is not downloaded yet, fetching it before applying settings and mods");

					bool downloaded = await Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider
						.TryDownloadRobloxPackageAsync(CancellationToken.None, SetPortableLaunchStatus);

					App.Logger.WriteLine(
						"LaunchHandler::FirstRun",
						downloaded
							? "Roblox downloaded, settings and mods will apply on this launch"
							: "Roblox did not finish downloading in Sober");
					if (!downloaded)
					{
						ShowPortableLaunchFailure("Sober has not finished downloading Roblox yet. Finish the setup in the Sober window, then launch again.");
						return;
					}
				}

				SetPortableLaunchStatus(Strings.Bootstrapper_Status_Configuring);
				Voidstrap.Platform.Linux.LinuxRuntimeConfiguration configuration = Voidstrap.Platform.Linux.LinuxRuntimeConfiguration.CreateDefault(Paths.Mods, host.Processes);
				Voidstrap.Platform.OperationResult prepared = await configuration.PrepareAsync(
					installation,
					Voidstrap.Utility.SoberConfigurationMapper.CreatePlayerOptions(App.Settings.Prop),
					Voidstrap.Utility.VinegarConfigurationMapper.CreateStudioOptions(App.Settings.Prop));
				if (!prepared.Succeeded)
				{
					ShowPortableLaunchFailure(prepared.Failure?.Message ?? "Linux runtime preparation failed.");
					return;
				}

				if (configuration.AddedAssets.Count > 0)
				{
					App.Logger.WriteLine(
						"LaunchHandler::LaunchPortableRuntime",
						configuration.AddedAssets.Count + " mod files were added to the Sober Roblox package: "
							+ string.Join(", ", configuration.AddedAssets.Take(20)));
				}

				if (configuration.SkippedAssets.Count > 0)
				{
					App.Logger.WriteLine(
						"LaunchHandler::LaunchPortableRuntime",
						configuration.SkippedAssets.Count + " mod files have no matching asset in the installed Sober Roblox package and were not applied: "
							+ string.Join(", ", configuration.SkippedAssets.Take(20)));
				}

				if (runtimeKind == Voidstrap.Platform.RuntimeKind.Player)
				{
					AssetWarpAutoEnable.EnsureEnabled(allowPrompt: !App.LaunchSettings.QuietFlag.Active, userAction: false);
					if (!App.Settings.Prop.AssetWarpEnabled)
					{
						AssetProxyServer.Stop();
					}
					else if (App.Settings.Prop.AssetWarpPreloadEnabled && assetPreloadPlaceId > 0)
					{
						AssetPreloadCache.SwitchSession(assetPreloadPlaceId);
					}
					SetPortableLaunchStatus("Starting AssetWarp");
					await Bootstrapper.StartAssetProxyIfEnabled(CancellationToken.None);
					if (AssetProxyServer.IsRequired && !AssetProxyServer.IsRunning)
					{
						throw new InvalidOperationException("AssetWarp could not start its Linux proxy. Check the Voidstrap log for the exact failure.");
					}
				}
			}

			SetPortableLaunchStatus(Strings.Bootstrapper_Status_Starting);
			Voidstrap.Core.RuntimeLaunchCoordinator coordinator = new(host.PlayerRuntime, host.StudioRuntime);
			Voidstrap.Platform.OperationResult<Voidstrap.Platform.LaunchSession> result = await coordinator.LaunchAsync(runtimeKind, launchTarget);
			if (!result.Succeeded || result.Value == null)
			{
				ShowPortableLaunchFailure(result.Failure?.Message ?? "The Roblox runtime did not accept the launch request.");
				return;
			}

			App.Logger.WriteLine("LaunchHandler::LaunchPortableRuntime", result.Value.Provider + " accepted the Roblox launch request");
			if (AssetProxyServer.IsRunning && assetPreloadPlaceId > 0)
			{
				AssetPreloadCache.StartBackgroundWarm(assetPreloadPlaceId, CancellationToken.None);
			}

			ClosePortableLaunchDialog();
			if (OperatingSystem.IsLinux() && StartResidentWatcher(result.Value.ProcessId))
			{
				stayResident = true;
				Voidstrap.UI.LinuxTaskbarPresence.HideWhileSessionRuns();
			}
		}
		catch (Exception ex)
		{
			ShowPortableLaunchFailure("Roblox could not start: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _portableLaunchActive, 0);
			if (!stayResident)
			{
				AssetProxyServer.Stop();
				PortableSessionEnded.TrySetResult(true);
				App.SoftTerminate();
			}
		}
	}

	private static IBootstrapperDialog? _portableDialog;
    private static readonly char[] anyOf = new[] { '/', '?', '#', '&' };

    private static void ShowPortableLaunchDialog(Bootstrapper bootstrapper)
	{
		if (App.LaunchSettings.QuietFlag.Active)
		{
			return;
		}

		Application? application = Application.Current;
		if (application is null)
		{
			return;
		}

		void show()
		{
			try
			{
				IBootstrapperDialog dialog = App.Settings.Prop.BootstrapperStyle.GetNew();
				dialog.Bootstrapper = bootstrapper;
				bootstrapper.Dialog = dialog;
				dialog.CancelEnabled = false;
				dialog.Message = FormatLaunchStatus(Strings.Bootstrapper_Status_Starting);
				_portableDialog = dialog;
				if (dialog is Window window)
				{
					window.Show();
				}
				else
				{
					dialog.ShowBootstrapper();
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LaunchHandler::ShowPortableLaunchDialog", "The launch dialog could not be shown: " + ex.Message);
			}
		}

		try
		{
			if (application.Dispatcher.CheckAccess())
			{
				show();
			}
			else
			{
				application.Dispatcher.Invoke(show);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::ShowPortableLaunchDialog", "The launch dialog could not be reached: " + ex.Message);
		}
	}

	private static async Task PrepareLinuxCompositorAsync()
	{
		bool wanted = Voidstrap.Utility.LinuxEffectMapper.HasLiveColorEffect();
		Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.UseCompositor = wanted;

		if (!wanted)
			return;

		if (!Voidstrap.Platform.Linux.LinuxGamescope.IsInstalled())
		{
			Voidstrap.Platform.OperationResult installed = await Voidstrap.Platform.Linux.LinuxEffectLayers.InstallAsync(
				new Voidstrap.Core.SystemProcessService(),
				Voidstrap.Platform.Linux.LinuxGamescope.LayerId);

			if (!installed.Succeeded)
			{
				App.Logger.WriteLine(
					"LaunchHandler::PrepareLinuxCompositorAsync",
					"The compositor could not be installed, colour effects will not update live: "
						+ (installed.Failure?.Message ?? "unknown reason"));
				Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.UseCompositor = false;
				return;
			}
		}

		await Voidstrap.Integrations.LinuxLiveColor.WriteAsync();
		Voidstrap.Integrations.LinuxLiveColor.BeginTracking();
	}

	private static async Task PrepareLinuxEffectLayersAsync()
	{
		try
		{
			await PrepareLinuxCompositorAsync();

			Voidstrap.Platform.Linux.LinuxEffectOptions options = Voidstrap.Utility.LinuxEffectMapper.CreateOptions();
			if (!options.Enabled)
			{
				Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.EffectLayerArguments = [];
				Voidstrap.Integrations.Overlays.OverlayHub.SetLinuxHomepageNativeShaderActive(false);
				return;
			}

			await EnsureEffectLayersInstalledAsync(options);

			Voidstrap.Platform.OperationResult written = Voidstrap.Platform.Linux.LinuxEffectLayers.WriteConfiguration(options);
			if (!written.Succeeded)
			{
				App.Logger.WriteLine("LaunchHandler::PrepareLinuxEffectLayers", written.Failure?.Message ?? "The effect configuration could not be written");
				Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.EffectLayerArguments = [];
				return;
			}

			IReadOnlyList<string> arguments = Voidstrap.Platform.Linux.LinuxEffectLayers.BuildLaunchArguments(options);
			Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.EffectLayerArguments = arguments;
			Voidstrap.Integrations.Overlays.OverlayHub.SetLinuxHomepageNativeShaderActive(
				!string.IsNullOrWhiteSpace(options.HomepageShader)
				&& arguments.Any(argument => argument.Contains("ENABLE_VKBASALT=1", StringComparison.Ordinal)));
			App.Logger.WriteLine(
				"LaunchHandler::PrepareLinuxEffectLayers",
				arguments.Count == 0
					? "Effects are enabled but the Vulkan layers are not installed, launching without them"
					: "Applying " + arguments.Count + " Vulkan layer arguments for the enabled effects");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::PrepareLinuxEffectLayers", "The effect layers could not be prepared: " + ex.Message);
			Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.EffectLayerArguments = [];
			Voidstrap.Integrations.Overlays.OverlayHub.SetLinuxHomepageNativeShaderActive(false);
		}
	}

	private static async Task EnsureEffectLayersInstalledAsync(Voidstrap.Platform.Linux.LinuxEffectOptions options)
	{
		Voidstrap.Platform.IPlatformHost? host = Voidstrap.Utility.Platform.RuntimeHost;
		if (host is null || App.LaunchSettings.QuietFlag.Active)
		{
			return;
		}

		bool wantsShaders = !string.IsNullOrWhiteSpace(options.AntiAliasing)
			|| options.Sharpening
			|| !string.IsNullOrWhiteSpace(options.GradingShader)
			|| !string.IsNullOrWhiteSpace(options.HomepageShader);

		if (wantsShaders && !Voidstrap.Platform.Linux.LinuxEffectLayers.IsInstalled(Voidstrap.Platform.Linux.LinuxEffectLayers.ShaderLayerId))
		{
			await PromptInstallEffectLayerAsync(
				host,
				Voidstrap.Platform.Linux.LinuxEffectLayers.ShaderLayerId,
				"Shader effects and anti aliasing need the vkBasalt Vulkan layer. Install it now?");
		}

		if (options.FrameGenMultiplier > 1 && !Voidstrap.Platform.Linux.LinuxEffectLayers.IsInstalled(Voidstrap.Platform.Linux.LinuxEffectLayers.FrameGenLayerId))
		{
			await PromptInstallEffectLayerAsync(
				host,
				Voidstrap.Platform.Linux.LinuxEffectLayers.FrameGenLayerId,
				"Frame generation needs the LSFG Vulkan layer. Install it now?");
		}
	}

	private static async Task PromptInstallEffectLayerAsync(Voidstrap.Platform.IPlatformHost host, string layerId, string question)
	{
		MessageBoxResult answer = Frontend.ShowMessageBox(question, MessageBoxImage.Question, MessageBoxButton.YesNo);
		if (answer != MessageBoxResult.Yes)
		{
			App.Logger.WriteLine("LaunchHandler::EffectLayers", "The user declined installing " + layerId);
			return;
		}

		SetPortableLaunchStatus("Installing the effect layer, this can take a while");
		Voidstrap.Platform.OperationResult result = await Voidstrap.Platform.Linux.LinuxEffectLayers
			.InstallAsync(host.Processes, layerId, CancellationToken.None);
		App.Logger.WriteLine(
			"LaunchHandler::EffectLayers",
			result.Succeeded ? layerId + " installed" : (result.Failure?.Message ?? "The effect layer could not be installed"));
		if (!result.Succeeded)
			Frontend.ShowMessageBox("The effect layer could not be installed, Roblox will start without those effects.", MessageBoxImage.Warning);
	}

	private static string FormatLaunchStatus(string message)
	{
		return string.IsNullOrEmpty(message) || !message.Contains("{product}", StringComparison.Ordinal)
			? message
			: message.Replace("{product}", "Roblox", StringComparison.Ordinal);
	}

	private static void SetPortableLaunchStatus(string message)
	{
		IBootstrapperDialog? dialog = _portableDialog;
		if (dialog is null)
		{
			return;
		}

		if (dialog is System.Windows.Threading.DispatcherObject owner && !owner.CheckAccess())
		{
			owner.Dispatcher.BeginInvoke(new Action<string>(SetPortableLaunchStatus), message);
			return;
		}

		try
		{
			dialog.Message = FormatLaunchStatus(message);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::SetPortableLaunchStatus", "The launch dialog could not be updated: " + ex.Message);
		}
	}

	private static void ClosePortableLaunchDialog()
	{
		IBootstrapperDialog? dialog = Interlocked.Exchange(ref _portableDialog, null);
		if (dialog is null)
		{
			return;
		}

		try
		{
			dialog.CloseBootstrapper();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchHandler::ClosePortableLaunchDialog", "The launch dialog could not be closed: " + ex.Message);
		}
	}

	private static void ShowPortableLaunchFailure(string message)
	{
		ClosePortableLaunchDialog();
		App.Logger.WriteLine("LaunchHandler::LaunchPortableRuntime", message);
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.CheckAccess())
		{
			Frontend.ShowMessageBox(message, MessageBoxImage.Hand);
			return;
		}

		application.Dispatcher.Invoke(() => Frontend.ShowMessageBox(message, MessageBoxImage.Hand));
	}

	internal static void CloseOtherInstances()
	{
		try
		{
			int processId = Environment.ProcessId;
			Process[] processesByName = Process.GetProcessesByName("Voidstrap");
			foreach (Process process in processesByName)
			{
				try
				{
					if (process.Id == processId || process.MainWindowHandle == IntPtr.Zero)
					{
						continue;
					}
					App.Logger.WriteLine("LaunchHandler::CloseOtherInstances", $"Closing other {"Voidstrap"} window (pid {process.Id})");
					if (!process.CloseMainWindow() || !process.WaitForExit(1500))
					{
						try
						{
							process.Kill();
						}
						catch
						{
						}
					}
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("LaunchHandler::CloseOtherInstances", $"Couldn't close pid {process.Id}: {ex.Message}");
				}
				finally
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
			}
		}
		catch (Exception ex2)
		{
			App.Logger.WriteLine("LaunchHandler::CloseOtherInstances", "Failed: " + ex2.Message);
		}
	}

	public static void LaunchWatcher()
	{
		Watcher watcher = new Watcher();
		Task.Run((Func<Task?>)watcher.Run).ContinueWith(delegate(Task t)
		{
			App.Logger.WriteLine("LaunchHandler::LaunchWatcher", "Watcher task has finished");
			watcher.Dispose();
			if (t.IsFaulted)
			{
				App.Logger.WriteLine("LaunchHandler::LaunchWatcher", "An exception occurred when running the watcher");
				if (t.Exception != null)
				{
					App.FinalizeExceptionHandling(t.Exception);
				}
			}
			if (App.Settings.Prop.CleanerOptions != CleanerOptions.Never)
			{
				Cleaner.DoCleaning();
			}
			Watcher.ForceShutdownAfterRobloxExit("LaunchHandler::LaunchWatcher");
		});
	}

	public static void LaunchBloxshadeConfig()
	{
		App.Logger.WriteLine("LaunchHandler::LaunchBloxshade", "Showing unsupported warning");
		new BloxshadeDialog().ShowOwnedDialog();
		App.SoftTerminate();
	}
}
