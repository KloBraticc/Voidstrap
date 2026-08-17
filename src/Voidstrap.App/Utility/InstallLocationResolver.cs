using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using ShellLink;
using Voidstrap.Core.Installation;
using Voidstrap.Extensions;

namespace Voidstrap.Utility;

internal static class InstallLocationResolver
{
	private const string UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap";

	private const string ApiKey = "Software\\Voidstrap";

	private static string DefaultLocation => Path.Combine(Paths.LocalAppData, "Voidstrap");

	private static string DesktopShortcut => Path.Combine(Paths.Desktop, "Voidstrap.lnk");

	private static string StartMenuShortcut => Path.Combine(Paths.WindowsStartMenu, "Voidstrap.lnk");

	private static string ApplicationFileName => Platform.IsWindows ? "Voidstrap.exe" : "Voidstrap";

	public static string? Resolve()
	{
		return InstallLocationSelector.SelectValid(GetCandidates(), IsValid);
	}

	public static string? ResolveExecutable()
	{
		string? location = Resolve();
		return location == null ? null : Path.Combine(location, ApplicationFileName);
	}

	public static void Repair(string installLocation)
	{
		string? normalized = InstallLocationSelector.Normalize(installLocation);
		if (normalized == null || !IsValid(normalized))
		{
			return;
		}

		string executable = Path.Combine(normalized, ApplicationFileName);
		InstallRecord.Write(normalized);
		if (!Platform.SupportsRegistry)
		{
			return;
		}

		try
		{
			using RegistryKey uninstall = Registry.CurrentUser.CreateSubKey(UninstallKey);
			uninstall.SetValueSafe("InstallLocation", normalized);
			uninstall.SetValueSafe("DisplayIcon", executable + ",0");
			uninstall.SetValueSafe("ModifyPath", "\"" + executable + "\" -settings");
			uninstall.SetValueSafe("QuietUninstallString", "\"" + executable + "\" -uninstall -quiet");
			uninstall.SetValueSafe("UninstallString", "\"" + executable + "\" -uninstall");

			using RegistryKey api = Registry.CurrentUser.CreateSubKey(ApiKey);
			api.SetValueSafe("ApplicationPath", executable);
			api.SetValueSafe("InstallationPath", normalized);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("InstallLocationResolver::Repair", "Registry repair failed: " + ex.Message);
		}

		WindowsRegistry.RegisterPlayer(executable, "-player \"%1\"");
		WindowsRegistry.RegisterStudioProtocol(executable, "-studio \"%1\"");
		RepairShortcut(DesktopShortcut, executable);
		RepairShortcut(StartMenuShortcut, executable);
	}

	private static IEnumerable<string?> GetCandidates()
	{
		if (Paths.Initialized)
		{
			yield return Paths.Base;
		}

		string? registryLocation = ReadRegistryValue(UninstallKey, "InstallLocation");
		yield return registryLocation;
		yield return InstallLocationSelector.RebaseUserProfile(registryLocation, Paths.UserProfile);
		string? recordedLocation = InstallRecord.Read();
		yield return recordedLocation;
		yield return InstallLocationSelector.RebaseUserProfile(recordedLocation, Paths.UserProfile);
		string? apiLocation = ReadRegistryValue(ApiKey, "InstallationPath");
		yield return apiLocation;
		yield return InstallLocationSelector.RebaseUserProfile(apiLocation, Paths.UserProfile);
		string? robloxLocation = DirectoryFromExecutable(ReadProtocolExecutable("roblox"));
		yield return robloxLocation;
		yield return InstallLocationSelector.RebaseUserProfile(robloxLocation, Paths.UserProfile);
		string? playerLocation = DirectoryFromExecutable(ReadProtocolExecutable("roblox-player"));
		yield return playerLocation;
		yield return InstallLocationSelector.RebaseUserProfile(playerLocation, Paths.UserProfile);
		string? desktopLocation = DirectoryFromExecutable(ReadShortcutExecutable(DesktopShortcut));
		yield return desktopLocation;
		yield return InstallLocationSelector.RebaseUserProfile(desktopLocation, Paths.UserProfile);
		string? startMenuLocation = DirectoryFromExecutable(ReadShortcutExecutable(StartMenuShortcut));
		yield return startMenuLocation;
		yield return InstallLocationSelector.RebaseUserProfile(startMenuLocation, Paths.UserProfile);
		yield return DefaultLocation;

		string? processDirectory = Path.GetDirectoryName(Paths.Process);
		if (HasLegacyMarkers(processDirectory))
		{
			yield return processDirectory;
		}
	}

	private static bool IsValid(string installLocation)
	{
		try
		{
			return Directory.Exists(installLocation) && File.Exists(Path.Combine(installLocation, ApplicationFileName));
		}
		catch
		{
			return false;
		}
	}

	private static bool HasLegacyMarkers(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory))
		{
			return false;
		}
		try
		{
			bool rootLayout = (File.Exists(Path.Combine(directory, "Settings.json")) || File.Exists(Path.Combine(directory, "AppSettings.json"))) &&
				File.Exists(Path.Combine(directory, "State.json")) &&
				File.Exists(Path.Combine(directory, "DownloadStats.json"));
			bool configLayout = File.Exists(Path.Combine(directory, "Config", "AppSettings.json")) &&
				File.Exists(Path.Combine(directory, "Config", "State.json"));
			return rootLayout || configLayout;
		}
		catch
		{
			return false;
		}
	}

	private static string? ReadRegistryValue(string keyPath, string valueName)
	{
		if (!Platform.SupportsRegistry)
		{
			return null;
		}
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
			return key?.GetValue(valueName) as string;
		}
		catch
		{
			return null;
		}
	}

	private static string? ReadProtocolExecutable(string protocol)
	{
		string? command = ReadRegistryValue("Software\\Classes\\" + protocol + "\\shell\\open\\command", "");
		return InstallLocationSelector.ExtractExecutable(command);
	}

	private static string? ReadShortcutExecutable(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			ShellLink.Shortcut shortcut = ShellLink.Shortcut.ReadFromFile(path);
			return shortcut.ExtraData.EnvironmentVariableDataBlock?.TargetUnicode ??
				shortcut.LinkInfo?.LocalBasePathUnicode ??
				shortcut.LinkInfo?.LocalBasePath;
		}
		catch
		{
			return null;
		}
	}

	private static string? DirectoryFromExecutable(string? executable)
	{
		string? normalized = InstallLocationSelector.Normalize(executable);
		return normalized == null ? null : Path.GetDirectoryName(normalized);
	}

	private static void RepairShortcut(string path, string executable)
	{
		if (!File.Exists(path))
		{
			return;
		}
		string? target = InstallLocationSelector.Normalize(ReadShortcutExecutable(path));
		if (string.Equals(target, InstallLocationSelector.Normalize(executable), StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		try
		{
			File.Delete(path);
			Shortcut.Create(executable, "", path);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("InstallLocationResolver::RepairShortcut", "Shortcut repair failed: " + ex.Message);
		}
	}
}
