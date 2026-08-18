using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
			try
			{
				Voidstrap.Utility.WindowAudit.Run();
			}
			catch (Exception auditEx)
			{
				App.Logger.WriteLine("WindowAudit", "audit harness failed: " + auditEx);
			}
			App.Terminate();
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
		if (App.LaunchSettings.ThemeFlag.Active)
		{
			App.Logger.WriteLine("LaunchHandler::ProcessLaunchArgs", "Installing a bootstrapper theme");
			LaunchThemeInstall(App.LaunchSettings.ThemeFlag.Data);
		}
		else if (App.LaunchSettings.UninstallFlag.Active)
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
				if (new LanguageSelectorDialog().ShowDialog() != true)
				{
					App.Terminate(ErrorCode.ERROR_INSTALL_USEREXIT);
					return;
				}
				Voidstrap.UI.Elements.Installer.MainWindow mainWindow = new Voidstrap.UI.Elements.Installer.MainWindow();
				mainWindow.ShowDialog();
				interProcessLock.Dispose();
				ProcessNextAction(mainWindow.CloseAction, !mainWindow.Finished);
			}
		}
		finally
		{
			interProcessLock.Dispose();
		}
	}

	private static string? ParseThemeId(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return null;

		string value = raw.Trim().Trim('"');

		const string scheme = "voidstrap://";
		if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
			value = value.Substring(scheme.Length);

		value = value.TrimStart('/');

		const string prefix = "theme/";
		if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			value = value.Substring(prefix.Length);

		int cut = value.IndexOfAny(new[] { '/', '?', '#', '&' });
		if (cut >= 0)
			value = value.Substring(0, cut);

		value = Uri.UnescapeDataString(value).Trim();

		if (value.Length == 0 || value.Length > 64)
			return null;

		foreach (char c in value)
		{
			if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
				return null;
		}

		return value;
	}

	public static void LaunchThemeInstall(string? raw)
	{
		string? themeId = ParseThemeId(raw);

		if (themeId == null)
		{
			Frontend.ShowMessageBox("That theme link is not valid.", MessageBoxImage.Hand);
			LaunchSettings();
			return;
		}

		Task.Run(async delegate
		{
			try
			{
				string folder = await Voidstrap.Integrations.BootstrapperThemes.InstallFromWebsiteAsync(themeId);

				App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
				App.Settings.Prop.SelectedCustomTheme = folder;
				App.Settings.Save();

				Frontend.ShowMessageBox(
					"The theme " + folder + " has been added and selected.",
					MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LaunchHandler::LaunchThemeInstall", "Install failed");
				App.Logger.WriteException("LaunchHandler::LaunchThemeInstall", ex);
				Frontend.ShowMessageBox("Could not install that theme: " + ex.Message, MessageBoxImage.Hand);
			}

			Application.Current?.Dispatcher.Invoke(delegate
			{
				LaunchSettings();
			});
		});
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
			uninstallerDialog.ShowDialog();
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
			new Voidstrap.UI.Elements.Settings.MainWindow(Process.GetProcessesByName("Voidstrap").Length > 1).ShowDialog();
			return;
		}
		App.Logger.WriteLine("LaunchHandler::LaunchSettings", "Found an already existing menu window");
		Process[] processesSafe = Utilities.GetProcessesSafe();
		try
		{
			Process process = processesSafe.FirstOrDefault((Process x) => x.MainWindowTitle == Strings.Menu_Title);
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
		launchMenuDialog.ShowDialog();
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
			_ = LaunchPortableRuntimeAsync(launchMode);
			return;
		}
		bool useAssetWarp = launchMode == LaunchMode.Player && AssetProxyServer.IsRequired;
		bool needsAssetWarpCleanup = !useAssetWarp && AssetProxyRouting.HasInstalledEntries();
		if (needsAssetWarpCleanup)
		{
			if (ProcessElevation.IsAdministrator())
			{
				AssetProxyRouting.Cleanup();
			}
			if (AssetProxyRouting.HasInstalledEntries())
			{
				AssetProxyRouting.TryRunRecoveryTask(waitForCompletion: true);
			}
			needsAssetWarpCleanup = AssetProxyRouting.HasInstalledEntries();
			if (!needsAssetWarpCleanup)
			{
				App.Logger.WriteLine("LaunchHandler::LaunchRoblox", "Leftover AssetWarp routing was cleared without administrator access");
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
		Mutex result;
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
		IBootstrapperDialog bootstrapperDialog = null;
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

	private static async Task LaunchPortableRuntimeAsync(LaunchMode launchMode)
	{
		if (Interlocked.CompareExchange(ref _portableLaunchActive, 1, 0) != 0)
		{
			ShowPortableLaunchFailure("A Roblox launch is already in progress.");
			return;
		}

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

			if (OperatingSystem.IsLinux())
			{
				Voidstrap.Platform.IRobloxRuntimeProvider provider = runtimeKind == Voidstrap.Platform.RuntimeKind.Player
					? host.PlayerRuntime
					: host.StudioRuntime;
				Voidstrap.Platform.RuntimeInstallation installation = await provider.FindInstallationAsync();
				if (!installation.Capability.IsAvailable)
				{
					ShowPortableLaunchFailure(installation.Capability.Reason);
					return;
				}

				Voidstrap.Platform.Linux.LinuxRuntimeConfiguration configuration = Voidstrap.Platform.Linux.LinuxRuntimeConfiguration.CreateDefault(Paths.Mods, host.Processes);
				Voidstrap.Platform.OperationResult prepared = await configuration.PrepareAsync(
					installation,
					Voidstrap.Utility.SoberConfigurationMapper.CreatePlayerOptions(App.Settings.Prop));
				if (!prepared.Succeeded)
				{
					ShowPortableLaunchFailure(prepared.Failure?.Message ?? "Linux runtime preparation failed.");
					return;
				}
			}

			Voidstrap.Core.RuntimeLaunchCoordinator coordinator = new(host.PlayerRuntime, host.StudioRuntime);
			Voidstrap.Platform.OperationResult<Voidstrap.Platform.LaunchSession> result = await coordinator.LaunchAsync(runtimeKind, launchTarget);
			if (!result.Succeeded || result.Value == null)
			{
				ShowPortableLaunchFailure(result.Failure?.Message ?? "The Roblox runtime did not accept the launch request.");
				return;
			}

			App.Logger.WriteLine("LaunchHandler::LaunchPortableRuntime", result.Value.Provider + " accepted the Roblox launch request");
		}
		catch (Exception ex)
		{
			ShowPortableLaunchFailure("Roblox could not start: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _portableLaunchActive, 0);
			App.SoftTerminate();
		}
	}

	private static void ShowPortableLaunchFailure(string message)
	{
		App.Logger.WriteLine("LaunchHandler::LaunchPortableRuntime", message);
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.CheckAccess())
		{
			Frontend.ShowMessageBox(message, MessageBoxImage.Hand);
			return;
		}

		application.Dispatcher.Invoke(() => Frontend.ShowMessageBox(message, MessageBoxImage.Hand));
	}

	private static void CloseOtherInstances()
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
		new BloxshadeDialog().ShowDialog();
		App.SoftTerminate();
	}
}
