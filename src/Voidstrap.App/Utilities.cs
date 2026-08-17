using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Threading;
using Voidstrap.AppData;
using Voidstrap.Enums;
using Voidstrap.UI;

namespace Voidstrap;

public static class Utilities
{
	public static void ShellExecute(string website)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = website,
				UseShellExecute = true
			});
		}
		catch (Win32Exception ex)
		{
			if (ex.NativeErrorCode != -2147221003)
			{
				throw;
			}
			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = "explorer.exe",
					Arguments = "\"" + website + "\"",
					UseShellExecute = false
				});
			}
			catch
			{
			}
		}
	}

	public static Version? GetVersionFromString(string? version)
	{
		if (string.IsNullOrWhiteSpace(version))
		{
			return null;
		}
		version = version.Trim();
		if (version.StartsWith('v') || version.StartsWith('V'))
		{
			version = version.Substring(1);
		}
		int num = version.IndexOf('+');
		if (num != -1)
		{
			version = version.Substring(0, num);
		}
		int num2 = version.IndexOf('-');
		if (num2 != -1)
		{
			version = version.Substring(0, num2);
		}
		version = version.Trim();
		try
		{
			if (Version.TryParse(version, out Version result))
			{
				return result;
			}
			throw new ArgumentException("Invalid version format");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("App::GetVersionFromString", "Invalid version string '" + version + "': " + ex.Message);
			return null;
		}
	}

	public static VersionComparison CompareVersions(string versionStr1, string versionStr2)
	{
		try
		{
			Version? versionFromString = GetVersionFromString(versionStr1);
			Version versionFromString2 = GetVersionFromString(versionStr2);
			return (VersionComparison)versionFromString.CompareTo(versionFromString2);
		}
		catch (Exception)
		{
			App.Logger.WriteLine("Utilities::CompareVersions", "An exception occurred when comparing versions");
			App.Logger.WriteLine("Utilities::CompareVersions", "versionStr1=" + versionStr1 + " versionStr2=" + versionStr2);
			throw;
		}
	}

	public static Version? ParseVersionSafe(string versionStr)
	{
		if (!Version.TryParse(versionStr, out Version result))
		{
			App.Logger.WriteLine("Utilities::ParseVersionSafe", "Failed to convert " + versionStr + " to a valid Version type");
			return result;
		}
		return result;
	}

	public static string GetRobloxVersion(bool studio)
	{
		IAppData appData2;
		if (!studio)
		{
			IAppData appData = new RobloxPlayerData();
			appData2 = appData;
		}
		else
		{
			IAppData appData = new RobloxStudioData();
			appData2 = appData;
		}
		string executablePath = appData2.ExecutablePath;
		if (!File.Exists(executablePath))
		{
			return "";
		}
		FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
		if (versionInfo.ProductVersion == null)
		{
			return "";
		}
		return versionInfo.ProductVersion.Replace(", ", ".");
	}

	public static Process[] GetProcessesSafe()
	{
		try
		{
			return Process.GetProcesses();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Utilities::GetProcessesSafe", "Unable to fetch processes!");
			App.Logger.WriteException("Utilities::GetProcessesSafe", ex);
			return Array.Empty<Process>();
		}
	}

	public static bool IsProcessAlive(int pid)
	{
		Process process = null;
		try
		{
			process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch
		{
			return false;
		}
		finally
		{
			try
			{
				process?.Dispose();
			}
			catch
			{
			}
		}
	}

	public static bool DoesMutexExist(string name)
	{
		try
		{
			Mutex.OpenExisting(name).Close();
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static void KillBackgroundUpdater()
	{
		using EventWaitHandle eventWaitHandle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, "Voidstrap-BackgroundUpdaterKillEvent");
		eventWaitHandle.Set();
	}

	public static void RemoveTeleportFix()
	{
		string identity = Environment.UserDomainName + "\\" + Environment.UserName;
		try
		{
			FileInfo fileInfo = new FileInfo(App.RobloxCookiesFilePath);
			FileSecurity accessControl = fileInfo.GetAccessControl();
			accessControl.RemoveAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Read, AccessControlType.Deny));
			accessControl.RemoveAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Write, AccessControlType.Allow));
			fileInfo.SetAccessControl(accessControl);
			App.Logger.WriteLine("Utilities::RemoveTeleportFix", "Successfully removed teleport fix");
		}
		catch (Exception exception)
		{
			Frontend.ShowExceptionDialog(exception);
		}
	}

	public static string FormatBytes(long bytes)
	{
		string[] array = new string[4] { "GB", "MB", "KB", "B" };
		long num = (long)Math.Pow(1024.0, array.Length - 1);
		string[] array2 = array;
		foreach (string value in array2)
		{
			if (bytes > num)
			{
				return $"{(double)bytes / (double)num:0.##} {value}";
			}
			num /= 1024;
		}
		return "0 B";
	}
}
