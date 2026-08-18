using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using ShellLink;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;

namespace Voidstrap;

internal partial class Installer
{
	private sealed class DeferredCleanupPayload
	{
		public int ProcessId { get; set; }
		public long ProcessStartTicks { get; set; }
		public string Target { get; set; } = "";
		public bool DeleteDirectory { get; set; }
		public string MarkerName { get; set; } = "";
		public string Token { get; set; } = "";
	}

	private const bool OpenReleaseNotes = false;

	public string InstallLocation = Path.Combine(Paths.LocalAppData, "Voidstrap");

	public bool CreateDesktopShortcuts = true;

	public bool CreateStartMenuShortcuts = true;

	public bool ExtractRobloxIcons;

	public bool CreatePlayerShortcut;

	public bool CreateStudioShortcut;

	public bool CreateSettingsShortcut;

	public bool EnableAnalytics = true;

	public bool VoidstrapRPCReal = true;

	public bool IsImplicitInstall;

	private static string DesktopShortcut => Path.Combine(Paths.Desktop, "Voidstrap.lnk");

	private static string StartMenuShortcut => Path.Combine(Paths.WindowsStartMenu, "Voidstrap.lnk");

	public bool ExistingDataPresent => File.Exists(Path.Combine(InstallLocation, "Settings.json"));

	public string InstallLocationError { get; set; } = "";

	public static string PendingAuthToken = "";

	public static string PendingAuthId = "";

	public static string PendingAuthLabel = "";

	public static string PendingAuthAvatar = "";

	public void DoInstall()
	{
		App.Logger.WriteLine("Installer::DoInstall", "Beginning installation");
		Directory.CreateDirectory(InstallLocation);
		Paths.Initialize(InstallLocation);
		Paths.EnsureDirectories();
		if (!IsImplicitInstall && !string.Equals(Paths.Process, Paths.Application, StringComparison.InvariantCultureIgnoreCase))
		{
			TrySafe("clear read only", () => Filesystem.AssertReadOnly(Paths.Application));
			try
			{
				File.Copy(Paths.Process, Paths.Application, overwrite: true);
				Voidstrap.Utility.InstallRecord.MakeExecutable(Paths.Application);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Installer::DoInstall", "Could not overwrite executable");
				App.Logger.WriteException("Installer::DoInstall", ex);
				throw new IOException(Strings.Installer_Install_CannotOverwrite, ex);
			}
		}
		TrySafe("install record", () => Voidstrap.Utility.InstallRecord.Write(Paths.Base));
		if (Voidstrap.Utility.Platform.SupportsRegistry)
		{
			TrySafe("uninstall registry entry", delegate
			{
				using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap");
				registryKey.SetValueSafe("DisplayIcon", Paths.Application + ",0");
				registryKey.SetValueSafe("DisplayName", "Voidstrap");
				registryKey.SetValueSafe("DisplayVersion", App.Version);
				if (registryKey.GetValue("InstallDate") == null)
				{
					registryKey.SetValueSafe("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
				}
				registryKey.SetValueSafe("InstallLocation", Paths.Base);
				registryKey.SetValueSafe("NoRepair", 1);
				registryKey.SetValueSafe("Publisher", "Voidstrap");
				registryKey.SetValueSafe("ModifyPath", "\"" + Paths.Application + "\" -settings");
				registryKey.SetValueSafe("QuietUninstallString", "\"" + Paths.Application + "\" -uninstall -quiet");
				registryKey.SetValueSafe("UninstallString", "\"" + Paths.Application + "\" -uninstall");
				registryKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
				registryKey.SetValueSafe("URLInfoAbout", Voidstrap.Utility.GitHubCache.PreferredRepository + "/issues/new");
				registryKey.SetValueSafe("URLUpdateInfo", Voidstrap.Utility.GitHubCache.PreferredRepository + "/releases");
			});
			TrySafe("api registration", WindowsRegistry.RegisterApis);
			TrySafe("player registration", WindowsRegistry.RegisterPlayer);
			TrySafe("studio protocol registration", () => WindowsRegistry.RegisterStudioProtocol(Paths.Application, "-studio \"%1\""));
			TrySafe("theme protocol registration", WindowsRegistry.RegisterVoidstrap);
		}
		if (Voidstrap.Utility.Platform.IsWindows)
		{
			if (CreateDesktopShortcuts)
			{
				TrySafe("desktop shortcut", () => Voidstrap.Utility.Shortcut.Create(Paths.Application, "", DesktopShortcut));
			}
			if (CreateStartMenuShortcuts)
			{
				TrySafe("start menu shortcut", () => Voidstrap.Utility.Shortcut.Create(Paths.Application, "", StartMenuShortcut));
			}
			TrySafe("function shortcuts", CreateFunctionShortcuts);
		}
		else
		{
			TrySafe("desktop entry", () => Voidstrap.Utility.LinuxDesktopEntry.Install(Paths.Application));
		}
		TrySafe("install location repair", () => InstallLocationResolver.Repair(Paths.Base));
		App.Settings.Load(alertFailure: false);
		App.State.Load(alertFailure: false);
		App.FastFlags.Load(alertFailure: false);
		App.Settings.Prop.EnableAnalytics = EnableAnalytics;
		if (App.IsStudioVisible)
		{
			TrySafe("studio registration", WindowsRegistry.RegisterStudio);
		}
		App.Settings.Save();
		ApplyPendingAuth();
		App.Logger.WriteLine("Installer::DoInstall", "Installation finished");
	}

	private static void ApplyPendingAuth()
	{
		if (string.IsNullOrWhiteSpace(PendingAuthToken))
		{
			return;
		}

		try
		{
			Voidstrap.Utility.WebsiteAuth.AddOrUpdateAccount(PendingAuthToken, PendingAuthId, PendingAuthLabel, PendingAuthAvatar);
			App.Logger.WriteLine("Installer::DoInstall", "Restored the onboarding sign in for " + (PendingAuthLabel.Length > 0 ? PendingAuthLabel : PendingAuthId));
			Voidstrap.Utility.WebsiteAuth.Notify();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Installer::DoInstall", "Could not restore the onboarding sign in: " + ex.Message);
		}
	}

	private bool ValidateLocation()
	{
		try
		{
			return ValidateLocationCore();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Installer::ValidateLocation", "Rejected malformed path: " + ex.Message);
			return false;
		}
	}

	private bool ValidateLocationCore()
	{
		if (InstallLocation.Length <= 3)
		{
			return false;
		}
		if (InstallLocation.StartsWith("\\\\"))
		{
			return false;
		}
		if (InstallLocation.StartsWith(Path.GetTempPath(), StringComparison.InvariantCultureIgnoreCase) || InstallLocation.Contains("\\Temp\\", StringComparison.InvariantCultureIgnoreCase))
		{
			return false;
		}
		if (IsCloudSyncedPath(InstallLocation))
		{
			return false;
		}
		if (string.Compare(Directory.GetParent(InstallLocation)?.FullName, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase) == 0)
		{
			return false;
		}
		if (InstallLocation.Contains("Program Files"))
		{
			return false;
		}
		return true;
	}

	private void CreateFunctionShortcuts()
	{
		TryCreateShortcut(CreatePlayerShortcut, "-player", Strings.LaunchMenu_LaunchRoblox);
		TryCreateShortcut(CreateStudioShortcut, "-studio", Strings.LaunchMenu_LaunchRobloxStudio);
		TryCreateShortcut(CreateSettingsShortcut, "-settings", Strings.Menu_Title);
		if (!ExtractRobloxIcons)
		{
			return;
		}
		try
		{
			Voidstrap.Models.SettingTasks.ExtractIconsTask.ExtractAll(overwrite: true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Installer::CreateFunctionShortcuts", "Could not extract Roblox icons: " + ex.Message);
		}
	}

	private static void TryCreateShortcut(bool wanted, string flags, string name)
	{
		if (!wanted)
		{
			return;
		}
		try
		{
			Voidstrap.Utility.Shortcut.Create(Paths.Application, flags, Path.Combine(Paths.Desktop, name + ".lnk"));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Installer::TryCreateShortcut", "Could not create the " + name + " shortcut: " + ex.Message);
		}
	}

	public bool CheckInstallLocation()
	{
		InstallLocationError = "";
		if (string.IsNullOrEmpty(InstallLocation))
		{
			InstallLocationError = Strings.Menu_InstallLocation_NotSet;
		}
		else if (!ValidateLocation())
		{
			InstallLocationError = Strings.Menu_InstallLocation_CantInstall;
		}
		else
		{
			if (!IsImplicitInstall && !InstallLocation.EndsWith("Voidstrap", StringComparison.InvariantCultureIgnoreCase) && Directory.Exists(InstallLocation) && Directory.EnumerateFileSystemEntries(InstallLocation).Any())
			{
				string text = Path.Combine(InstallLocation, "Voidstrap");
				switch (Frontend.ShowMessageBox(string.Format(Strings.Menu_InstallLocation_NotEmpty, text), MessageBoxImage.Exclamation, MessageBoxButton.YesNoCancel, MessageBoxResult.Yes))
				{
				case MessageBoxResult.Yes:
					InstallLocation = text;
					break;
				case MessageBoxResult.None:
				case MessageBoxResult.Cancel:
					return false;
				}
			}
			try
			{
				string path = Path.Combine(InstallLocation, "VoidstrapWriteTest.txt");
				Directory.CreateDirectory(InstallLocation);
				File.WriteAllText(path, "");
				File.Delete(path);
			}
			catch (UnauthorizedAccessException)
			{
				InstallLocationError = Strings.Menu_InstallLocation_NoWritePerms;
			}
			catch (Exception ex2)
			{
				InstallLocationError = ex2.Message;
			}
		}
		return string.IsNullOrEmpty(InstallLocationError);
	}

	private static readonly string[] PreservedFolders = ["Config", "VoidstrapMods", "Themes", "Modifications"];

	private static readonly string[] CloudEnvVars = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

	private static readonly string[] CloudFolderNames = ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloudDrive", "Creative Cloud Files", "Box Sync", "MEGAsync", "pCloudDrive", "Nextcloud", "Syncthing", "Yandex.Disk"];

	public static bool IsCloudSyncedPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		string full;
		try
		{
			full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
		}
		catch
		{
			return false;
		}

		foreach (string variable in CloudEnvVars)
		{
			string root;
			try
			{
				root = Environment.GetEnvironmentVariable(variable) ?? "";
			}
			catch
			{
				continue;
			}

			if (root.Length == 0)
			{
				continue;
			}

			try
			{
				root = Path.GetFullPath(root).TrimEnd('\\') + "\\";
			}
			catch
			{
				continue;
			}

			if (full.StartsWith(root, StringComparison.InvariantCultureIgnoreCase))
			{
				return true;
			}
		}

		foreach (string folder in CloudFolderNames)
		{
			if (full.Contains("\\" + folder + "\\", StringComparison.InvariantCultureIgnoreCase)
				|| full.Contains("\\" + folder + " - ", StringComparison.InvariantCultureIgnoreCase))
			{
				return true;
			}
		}

		return HasCloudPlaceholderAttribute(full);
	}

	private static bool HasCloudPlaceholderAttribute(string path)
	{
		try
		{
			string probe = path;
			for (int depth = 0; depth < 6 && probe.Length > 3; depth++)
			{
				if (Directory.Exists(probe))
				{
					FileAttributes attributes = File.GetAttributes(probe);
					if (((int)attributes & 0x400000) != 0 || ((int)attributes & 0x40000) != 0 || ((int)attributes & 0x1000) != 0)
					{
						return true;
					}
				}

				string? parent = Path.GetDirectoryName(probe.TrimEnd('\\'));
				if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase))
				{
					break;
				}

				probe = parent;
			}
		}
		catch
		{
		}

		return false;
	}

	private static void TrySafe(string label, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Installer::TrySafe", "Step failed (" + label + "): " + ex.Message);
		}
	}

	private static string[] SafeSubdirectories(string root)
	{
		try
		{
			return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
		}
		catch
		{
			return [];
		}
	}

	private static void ForceDeleteDirectory(string path)
	{
		if (!Directory.Exists(path))
			return;
		try
		{
			ClearReadOnly(path);
			Directory.Delete(path, recursive: true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Installer::DoUninstall", "Could not delete " + path + ": " + ex.Message);
		}
	}

	private static void ClearReadOnly(string path)
	{
		try
		{
			EnumerationOptions options = new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.ReparsePoint
			};
			foreach (string file in Directory.EnumerateFiles(path, "*", options))
			{
				try
				{
					FileAttributes attributes = File.GetAttributes(file);
					if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
					{
						File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	public static void DoUninstall(bool keepData)
	{
		List<Process> list = [];
		try
		{
			if (!string.IsNullOrEmpty(App.State.Prop.Player.VersionGuid))
			{
				list.AddRange(Process.GetProcessesByName("RobloxPlayerBeta"));
			}
			if (App.IsStudioVisible)
			{
				list.AddRange(Process.GetProcessesByName("RobloxStudioBeta"));
			}
			if (list.Count > 0 && Frontend.ShowMessageBox(Strings.Bootstrapper_Uninstall_RobloxRunning, MessageBoxImage.Asterisk, MessageBoxButton.OKCancel, MessageBoxResult.OK) != MessageBoxResult.OK)
			{
				App.Terminate(ErrorCode.ERROR_CANCELLED);
				return;
			}
			foreach (Process item in list)
			{
				try
				{
					item.Kill();
				}
				catch (Exception value)
				{
					App.Logger.WriteLine("Installer::DoUninstall", $"Failed to close process! {value}");
				}
			}
		}
		finally
		{
			foreach (Process item in list)
			{
				item.Dispose();
			}
		}
		Voidstrap.Integrations.AssetProxy.AssetProxyServer.Stop();
		Voidstrap.Integrations.AssetProxy.AssetProxyServer.CleanupStaleState();
		Voidstrap.Integrations.AssetProxy.AssetProxyServer.RemoveCertificates();
		Voidstrap.Utility.LinuxDesktopEntry.Remove();
		Voidstrap.Utility.InstallRecord.Delete();
		if (Voidstrap.Utility.Platform.IsWindows)
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\roblox-player");
			object obj = registryKey?.GetValue("InstallLocation");
			if (registryKey == null || obj is not string)
			{
				WindowsRegistry.Unregister("roblox");
				WindowsRegistry.Unregister("roblox-player");
			}
			else
			{
				WindowsRegistry.RegisterPlayer(Path.Combine((string)obj, "RobloxPlayerBeta.exe"), "%1");
			}
			using RegistryKey registryKey2 = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\roblox-studio");
			object obj2 = registryKey2?.GetValue("InstallLocation");
			if (registryKey2 == null || obj2 is not string)
			{
				WindowsRegistry.Unregister("roblox-studio");
				WindowsRegistry.Unregister("roblox-studio-auth");
				WindowsRegistry.Unregister("Roblox.Place");
				WindowsRegistry.Unregister(".rbxl");
				WindowsRegistry.Unregister(".rbxlx");
			}
			else
			{
				string handler = Path.Combine((string)obj2, "RobloxStudioBeta.exe");
				WindowsRegistry.RegisterStudioProtocol(handler, "%1");
				WindowsRegistry.RegisterStudioFileClass(handler, "-ide \"%1\"");
			}
			TrySafe("registry Software\\Voidstrap", delegate
			{
				Registry.CurrentUser.DeleteSubKeyTree("Software\\Voidstrap", throwOnMissingSubKey: false);
			});
		}
		List<Action> list2 =
		[
			delegate
			{
				if (!Voidstrap.Utility.Platform.IsWindows)
					return;
				foreach (string item2 in from x in Directory.GetFiles(Paths.Desktop)
					where x.EndsWith("lnk")
					select x)
				{
					if (ShellLink.Shortcut.ReadFromFile(item2).ExtraData.EnvironmentVariableDataBlock?.TargetUnicode == Paths.Application)
					{
						File.Delete(item2);
					}
				}
			},
			delegate
			{
				if (!Voidstrap.Utility.Platform.IsWindows)
					return;
				File.Delete(StartMenuShortcut);
			},
			delegate
			{
				ForceDeleteDirectory(Paths.Versions);
			},
			delegate
			{
				ForceDeleteDirectory(Paths.Downloads);
			},
			delegate
			{
				ForceDeleteDirectory(Paths.Cache);
			},
			delegate
			{
				ForceDeleteDirectory(Paths.RobloxClients);
			},
			delegate
			{
				File.Delete(App.State.FileLocation);
			},
			delegate
			{
				if (Paths.Roblox == Path.Combine(Paths.Base, "Roblox"))
				{
					ForceDeleteDirectory(Paths.Roblox);
				}
			}
		];
		if (!keepData)
		{
			list2.AddRange([
				delegate
				{
					ForceDeleteDirectory(Paths.Mods);
				},
				delegate
				{
					ForceDeleteDirectory(Paths.Logs);
				},
				delegate
				{
					File.Delete(App.Settings.FileLocation);
				}
			]);
		}
		bool flag3 = !keepData && Directory.Exists(Paths.Base) && Directory.GetFiles(Paths.Base).Length <= 3;
		if (flag3)
		{
			list2.Add(delegate
			{
				ForceDeleteDirectory(Paths.Base);
			});
		}
		foreach (string leftover in SafeSubdirectories(Paths.Base))
		{
			string target = leftover;
			string folderName = Path.GetFileName(target.TrimEnd('\\'));
			if (keepData && PreservedFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

			list2.Add(delegate
			{
				ForceDeleteDirectory(target);
			});
		}

		list2.Add(delegate
		{
			ForceDeleteDirectory(Paths.Logs);
		});

		list2.Add(delegate
		{
			ForceDeleteDirectory(Path.Combine(Path.GetTempPath(), "Voidstrap"));
		});

		list2.Add(delegate
		{
			if (Voidstrap.Utility.Platform.IsWindows)
				Registry.CurrentUser.DeleteSubKeyTree("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap", throwOnMissingSubKey: false);
		});
		foreach (Action item3 in list2)
		{
			try
			{
				item3();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Installer::DoUninstall", $"Encountered exception when running cleanup sequence (#{list2.IndexOf(item3)})");
				App.Logger.WriteException("Installer::DoUninstall", ex);
			}
		}
		if (Directory.Exists(Paths.Base))
		{
			ScheduleDeferredCleanup(Paths.Base, Paths.Application, flag3);
		}
	}

	private static void ScheduleDeferredCleanup(string basePath, string appPath, bool deleteFolder)
	{
		ScheduleDeferredCleanup(deleteFolder ? basePath : appPath, deleteFolder, "Installer::DoUninstall");
	}

	private static void ScheduleDeferredCleanup(string target, bool deleteDirectory, string logSource)
	{
		try
		{
			string fullTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string root = deleteDirectory ? fullTarget : Path.GetDirectoryName(fullTarget) ?? "";
			if (!IsOwnedInstallRoot(root) || (!deleteDirectory && !string.Equals(fullTarget, Path.Combine(root, Path.GetFileName(Paths.Application)), StringComparison.OrdinalIgnoreCase)))
				throw new InvalidOperationException("The deferred cleanup target is not an owned install path");

			string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			string markerName = ".Voidstrap.cleanup." + Guid.NewGuid().ToString("N");
			string markerPath = Path.Combine(root, markerName);
			File.WriteAllText(markerPath, token, new UTF8Encoding(false));
			using Process current = Process.GetCurrentProcess();
			var payload = new DeferredCleanupPayload
			{
				ProcessId = current.Id,
				ProcessStartTicks = current.StartTime.ToUniversalTime().Ticks,
				Target = fullTarget,
				DeleteDirectory = deleteDirectory,
				MarkerName = markerName,
				Token = token
			};
			string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
			string helperDirectory = Path.Combine(Path.GetTempPath(), "VoidstrapCleanup");
			Directory.CreateDirectory(helperDirectory);
			CleanupOldHelpers(helperDirectory);
			string helperPath = Path.Combine(helperDirectory, "VoidstrapCleanup." + Guid.NewGuid().ToString("N") + Path.GetExtension(Paths.Process));
			File.Copy(Paths.Process, helperPath, false);
			InstallRecord.MakeExecutable(helperPath);
			var start = new ProcessStartInfo
			{
				FileName = helperPath,
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			start.ArgumentList.Add("-deferredcleanup");
			start.ArgumentList.Add(encoded);
			using Process? helper = Process.Start(start);
			if (helper == null)
				throw new InvalidOperationException("The deferred cleanup helper did not start");
			App.Logger.WriteLine(logSource, "Scheduled deferred removal of " + fullTarget);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(logSource, "Could not schedule deferred cleanup: " + ex.Message);
		}
	}

	internal static async Task RunDeferredCleanupAsync(string? encodedPayload)
	{
		if (string.IsNullOrWhiteSpace(encodedPayload) || encodedPayload.Length > 32768)
			return;
		DeferredCleanupPayload? payload;
		try
		{
			byte[] serialized = Convert.FromBase64String(encodedPayload);
			if (serialized.Length > 16384)
				return;
			payload = JsonSerializer.Deserialize<DeferredCleanupPayload>(serialized);
		}
		catch
		{
			return;
		}
		if (payload == null || payload.ProcessId <= 0 || payload.ProcessStartTicks <= 0 || payload.Token.Length != 64 || !payload.Token.All(Uri.IsHexDigit))
			return;

		string fullTarget;
		string root;
		string markerPath;
		try
		{
			fullTarget = Path.GetFullPath(payload.Target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			root = payload.DeleteDirectory ? fullTarget : Path.GetDirectoryName(fullTarget) ?? "";
			if (!IsOwnedInstallRoot(root) || Path.GetFileName(payload.MarkerName) != payload.MarkerName || !payload.MarkerName.StartsWith(".Voidstrap.cleanup.", StringComparison.Ordinal))
				return;
			if (!payload.DeleteDirectory)
			{
				string fileName = Path.GetFileName(fullTarget);
				if (!string.Equals(fileName, "Voidstrap.exe", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "Voidstrap", StringComparison.OrdinalIgnoreCase))
					return;
			}
			markerPath = Path.Combine(root, payload.MarkerName);
			if (!File.Exists(markerPath) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(File.ReadAllText(markerPath)), Encoding.UTF8.GetBytes(payload.Token)))
				return;
		}
		catch
		{
			return;
		}

		try
		{
			using Process process = Process.GetProcessById(payload.ProcessId);
			if (process.StartTime.ToUniversalTime().Ticks != payload.ProcessStartTicks)
				return;
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (ArgumentException)
		{
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch
		{
			return;
		}

		for (int attempt = 0; attempt < 6; attempt++)
		{
			try
			{
				if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != payload.Token)
					return;
				if (payload.DeleteDirectory)
				{
					ForceDeleteDirectory(fullTarget);
					if (Directory.Exists(fullTarget))
						throw new IOException("The deferred cleanup directory still exists");
				}
				else
				{
					File.Delete(fullTarget);
					if (File.Exists(fullTarget))
						throw new IOException("The deferred cleanup file still exists");
					File.Delete(markerPath);
				}
				break;
			}
			catch when (attempt < 5)
			{
				await Task.Delay(500).ConfigureAwait(false);
			}
			catch
			{
				break;
			}
		}
		TryRemoveCleanupHelper();
	}

	private static bool IsOwnedInstallRoot(string root)
	{
		if (string.IsNullOrWhiteSpace(root))
			return false;
		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (string.Equals(fullRoot, Path.GetPathRoot(fullRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
			return false;
		return File.Exists(Path.Combine(fullRoot, "Voidstrap.exe")) || File.Exists(Path.Combine(fullRoot, "Voidstrap"));
	}

	private static void CleanupOldHelpers(string directory)
	{
		foreach (string file in Directory.EnumerateFiles(directory, "VoidstrapCleanup.*"))
		{
			try
			{
				if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
					File.Delete(file);
			}
			catch
			{
			}
		}
	}

	private static void TryRemoveCleanupHelper()
	{
		try
		{
			if (Voidstrap.Utility.Platform.IsWindows)
				MoveFileEx(Paths.Process, null, 4);
			else
				File.Delete(Paths.Process);
		}
		catch
		{
		}
	}

	[LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool MoveFileEx(string existingFileName, string? newFileName, uint flags);


	public static async Task HandleUpgradeAsync()
	{
		if (!File.Exists(Paths.Application) || string.Equals(Paths.Process, Paths.Application, StringComparison.OrdinalIgnoreCase) || App.LaunchSettings.WindowAuditFlag.Active)
		{
			return;
		}
		bool flag = App.LaunchSettings.UpgradeFlag.Active || Paths.Process.StartsWith(Path.Combine(Paths.Base, "Updates")) || Paths.Process.StartsWith(Path.Combine(Paths.LocalAppData, "Temp")) || Paths.Process.StartsWith(Paths.TempUpdates);
		string productVersion = FileVersionInfo.GetVersionInfo(Paths.Application).ProductVersion;
		string productVersion2 = FileVersionInfo.GetVersionInfo(Paths.Process).ProductVersion;
		if (MD5Hash.FromFile(Paths.Process) == MD5Hash.FromFile(Paths.Application) || (productVersion2 != null && productVersion != null && Utilities.CompareVersions(productVersion2, productVersion) == VersionComparison.LessThan && Frontend.ShowMessageBox(Strings.InstallChecker_VersionLessThanInstalled, MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) || (!flag && Frontend.ShowMessageBox(Strings.InstallChecker_VersionDifferentThanInstalled, MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes))
		{
			return;
		}
		App.Logger.WriteLine("Installer::HandleUpgrade", "Doing upgrade");
		Filesystem.AssertReadOnly(Paths.Application);
		using InterProcessLock interProcessLock = new("AutoUpdater", TimeSpan.FromSeconds(5L));
		if (!interProcessLock.IsAcquired)
		{
			App.Logger.WriteLine("Installer::HandleUpgrade", "Failed to update! (Could not obtain singleton mutex)");
			return;
		}
		for (int i = 1; i <= 10; i++)
		{
			try
			{
				ReplaceInstalledExecutable();
			}
			catch (Exception ex)
			{
				switch (i)
				{
				case 1:
					App.Logger.WriteLine("Installer::HandleUpgrade", "Waiting for write permissions to update version");
					break;
				case 10:
					App.Logger.WriteLine("Installer::HandleUpgrade", "Failed to update! (Could not get write permissions after 10 tries/5 seconds)");
					App.Logger.WriteException("Installer::HandleUpgrade", ex);
					return;
				}
				await Task.Delay(500);
				continue;
			}
			break;
		}
		if (Voidstrap.Utility.Platform.SupportsRegistry)
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap");
			registryKey.SetValueSafe("DisplayVersion", App.Version);
			registryKey.SetValueSafe("Publisher", "Voidstrap");
			registryKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
			registryKey.SetValueSafe("URLInfoAbout", Voidstrap.Utility.GitHubCache.PreferredRepository + "/issues/new");
			registryKey.SetValueSafe("URLUpdateInfo", Voidstrap.Utility.GitHubCache.PreferredRepository + "/releases");
		}
		else
		{
			Voidstrap.Utility.InstallRecord.MakeExecutable(Paths.Application);
			Voidstrap.Utility.LinuxDesktopEntry.Install(Paths.Application);
		}
		if (productVersion != null)
		{
			if (Utilities.CompareVersions(productVersion, "1.0.3.6") == VersionComparison.LessThan)
			{
				TrySafe("legacy install migration", () => MigrateLegacyInstall(flag));
			}
			App.Settings.Save();
			App.FastFlags.Save();
			App.State.Save();
		}
		RefreshAppIcons();
		if (productVersion2 != null && !flag)
		{
			Frontend.ShowMessageBox(string.Format(Strings.InstallChecker_Updated, productVersion2), MessageBoxImage.Asterisk);
		}
	}

	private static void MigrateLegacyInstall(bool upgradeLaunch)
	{
		if (upgradeLaunch)
		{
			if (App.LaunchSettings.Args.Length == 0)
			{
				App.LaunchSettings.RobloxLaunchMode = LaunchMode.Player;
			}
			string? launchArgument = App.LaunchSettings.Args.FirstOrDefault(x => x.Contains("roblox"));
			if (launchArgument != null)
			{
				App.LaunchSettings.RobloxLaunchMode = LaunchMode.Player;
				App.LaunchSettings.RobloxLaunchArgs = launchArgument;
			}
		}

		TrySafe("legacy desktop shortcut", delegate
		{
			string legacyShortcut = Path.Combine(Paths.Desktop, "Play Roblox.lnk");
			if (File.Exists(legacyShortcut))
			{
				File.Move(legacyShortcut, DesktopShortcut, overwrite: true);
			}
		});

		TrySafe("legacy start menu folder", delegate
		{
			string legacyFolder = Path.Combine(Paths.WindowsStartMenu, "Voidstrap");
			if (!Directory.Exists(legacyFolder))
			{
				return;
			}
			try
			{
				Directory.Delete(legacyFolder, recursive: true);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("Installer::MigrateLegacyInstall", ex);
			}
			Voidstrap.Utility.Shortcut.Create(Paths.Application, "", StartMenuShortcut);
		});

		if (Voidstrap.Utility.Platform.SupportsRegistry)
		{
			TrySafe("legacy registry cleanup", delegate
			{
				Registry.CurrentUser.DeleteSubKeyTree("Software\\Voidstrap", throwOnMissingSubKey: false);
			});
		}

		TrySafe("player registration", WindowsRegistry.RegisterPlayer);
		TrySafe("theme protocol registration", WindowsRegistry.RegisterVoidstrap);
		App.FastFlags.SetValue("FFlagDisableNewIGMinDUA", null);
		App.FastFlags.SetValue("FFlagFixGraphicsQuality", null);
	}

	private static void ReplaceInstalledExecutable()
	{
		string directory = Path.GetDirectoryName(Paths.Application) ?? throw new InvalidOperationException("The install directory is unavailable");
		string stagedPath = Path.Combine(directory, ".Voidstrap.update." + Guid.NewGuid().ToString("N"));
		try
		{
			File.Copy(Paths.Process, stagedPath, overwrite: false);
			Voidstrap.Utility.InstallRecord.MakeExecutable(stagedPath);
			if (Voidstrap.Utility.Platform.IsWindows)
			{
				try
				{
					File.Replace(stagedPath, Paths.Application, null, ignoreMetadataErrors: true);
				}
				catch (Exception ex) when (Voidstrap.Utility.CloudFiles.IsCloudFailure(ex, Paths.Application))
				{
					App.Logger.WriteLine("Installer::ReplaceInstalledExecutable", "Replace failed on a cloud synced file, moving instead: " + ex.Message);
					File.Move(stagedPath, Paths.Application, overwrite: true);
				}
			}
			else
			{
				File.Move(stagedPath, Paths.Application, overwrite: true);
			}
		}
		finally
		{
			if (File.Exists(stagedPath))
			{
				File.Delete(stagedPath);
			}
		}
	}

	[LibraryImport("shell32.dll")]
	private static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

	private static void RefreshAppIcons()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		try
		{
			Voidstrap.Models.SettingTasks.ExtractIconsTask.ExtractAll(overwrite: true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Installer::RefreshAppIcons", ex);
		}
		try
		{
			if (File.Exists(StartMenuShortcut))
			{
				Voidstrap.Utility.Shortcut.Create(Paths.Application, "", StartMenuShortcut);
			}
		}
		catch
		{
		}
		try
		{
			if (File.Exists(DesktopShortcut))
			{
				Voidstrap.Utility.Shortcut.Create(Paths.Application, "", DesktopShortcut);
			}
		}
		catch
		{
		}
		try
		{
			SHChangeNotify(0x08000000, 0u, IntPtr.Zero, IntPtr.Zero);
		}
		catch (Exception ex2)
		{
			App.Logger.WriteException("Installer::RefreshAppIcons", ex2);
		}
	}

	public static bool RelocateInstall(string newLocation)
	{
		if (string.IsNullOrWhiteSpace(newLocation))
		{
			return false;
		}
		string text = Paths.Base;
		string text2 = newLocation.TrimEnd(
		[
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar
		]);
		if (!text2.EndsWith("Voidstrap", StringComparison.InvariantCultureIgnoreCase))
		{
			text2 = Path.Combine(text2, "Voidstrap");
		}
		if (string.Equals(text2, text, StringComparison.InvariantCultureIgnoreCase))
		{
			Frontend.ShowMessageBox("Voidstrap is already installed at that location.", MessageBoxImage.Asterisk);
			return false;
		}
		if (text2.Length <= 3 || text2.StartsWith("\\\\") || text2.StartsWith(Path.GetTempPath(), StringComparison.InvariantCultureIgnoreCase) || text2.Contains("\\Temp\\", StringComparison.InvariantCultureIgnoreCase) || IsCloudSyncedPath(text2) || text2.Contains("Program Files"))
		{
			Frontend.ShowMessageBox(Strings.Menu_InstallLocation_CantInstall, MessageBoxImage.Hand);
			return false;
		}
		string text3 = Path.GetFullPath(text).TrimEnd('\\') + "\\";
		string text4 = Path.GetFullPath(text2).TrimEnd('\\') + "\\";
		if (text4.StartsWith(text3, StringComparison.InvariantCultureIgnoreCase) || text3.StartsWith(text4, StringComparison.InvariantCultureIgnoreCase))
		{
			Frontend.ShowMessageBox(Strings.Menu_InstallLocation_CantInstall, MessageBoxImage.Hand);
			return false;
		}
		string targetParent = Directory.GetParent(text2)?.FullName ?? string.Empty;
		if (string.IsNullOrWhiteSpace(targetParent))
		{
			Frontend.ShowMessageBox(Strings.Menu_InstallLocation_CantInstall, MessageBoxImage.Hand);
			return false;
		}
		try
		{
			Directory.CreateDirectory(targetParent);
			string path = Path.Combine(targetParent, ".Voidstrap.write." + Guid.NewGuid().ToString("N"));
			File.WriteAllText(path, "");
			File.Delete(path);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Installer::RelocateInstall", ex);
			Frontend.ShowMessageBox(Strings.Menu_InstallLocation_NoWritePerms, MessageBoxImage.Hand);
			return false;
		}
		string stagingPath = Path.Combine(targetParent, ".Voidstrap.staging." + Guid.NewGuid().ToString("N"));
		string? replacedTarget = null;
		try
		{
			App.Logger.WriteLine("Installer::RelocateInstall", $"Relocating install from '{text}' to '{text2}'");
			if (Directory.Exists(text2))
			{
				CopyDirectory(text2, stagingPath);
			}
			CopyDirectory(text, stagingPath);
			string stagedApplication = Path.Combine(stagingPath, Path.GetFileName(Paths.Application));
			if (!File.Exists(stagedApplication) || new FileInfo(stagedApplication).Length == 0)
			{
				throw new IOException("The relocated executable could not be verified");
			}
			if (Directory.Exists(text2))
			{
				replacedTarget = Path.Combine(targetParent, ".Voidstrap.previous." + Guid.NewGuid().ToString("N"));
				Directory.Move(text2, replacedTarget);
			}
			try
			{
				Directory.Move(stagingPath, text2);
			}
			catch
			{
				if (replacedTarget != null && Directory.Exists(replacedTarget) && !Directory.Exists(text2))
				{
					Directory.Move(replacedTarget, text2);
				}
				throw;
			}
		}
		catch (Exception ex2)
		{
			TryDeleteRelocationDirectory(stagingPath, targetParent);
			App.Logger.WriteException("Installer::RelocateInstall", ex2);
			Frontend.ShowMessageBox("Failed to move Voidstrap to the new location.", MessageBoxImage.Hand);
			return false;
		}
		Paths.Initialize(text2);
		Paths.EnsureDirectories();
		InstallLocationResolver.Repair(Paths.Base);
		try
		{
			if (App.IsStudioVisible)
			{
				WindowsRegistry.RegisterStudio();
			}
		}
		catch (Exception ex3)
		{
			App.Logger.WriteException("Installer::RelocateInstall", ex3);
		}
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = Paths.Application,
				Arguments = "-settings",
				UseShellExecute = true
			});
			if (process == null)
			{
				throw new InvalidOperationException("The relocated application did not start");
			}
		}
		catch (Exception ex4)
		{
			App.Logger.WriteException("Installer::RelocateInstall", ex4);
			Frontend.ShowMessageBox("Failed to start Voidstrap from the new location.", MessageBoxImage.Hand);
			Paths.Initialize(text);
			InstallLocationResolver.Repair(Paths.Base);
			return false;
		}
		if (replacedTarget != null)
		{
			TryDeleteRelocationDirectory(replacedTarget, targetParent);
		}
		ScheduleOldLocationCleanup(text);
		App.Terminate();
		return true;
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (string text in Directory.GetFiles(source))
		{
			File.Copy(text, Path.Combine(destination, Path.GetFileName(text)), overwrite: true);
		}
		foreach (string text2 in Directory.GetDirectories(source))
		{
			if (new DirectoryInfo(text2).LinkTarget != null)
			{
				continue;
			}
			CopyDirectory(text2, Path.Combine(destination, Path.GetFileName(text2)));
		}
	}

	private static void TryDeleteRelocationDirectory(string path, string expectedParent)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			string parent = Directory.GetParent(fullPath)?.FullName ?? string.Empty;
			if (Directory.Exists(fullPath) && string.Equals(parent, Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fullPath).StartsWith(".Voidstrap.", StringComparison.Ordinal))
			{
				Directory.Delete(fullPath, recursive: true);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("Installer::RelocateInstall", ex);
		}
	}

	private static void ScheduleOldLocationCleanup(string oldBase)
	{
		ScheduleDeferredCleanup(oldBase, true, "Installer::ScheduleOldLocationCleanup");
	}
}
