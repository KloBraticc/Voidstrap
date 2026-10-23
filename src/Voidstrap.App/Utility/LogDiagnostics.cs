using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Voidstrap.Utility;

internal static class LogDiagnostics
{
	private const string LOG_IDENT = "App::Diagnostics";

	private const string MarkerExtension = ".running";

	private static string? _markerPath;

	public static void WriteEnvironment()
	{
		foreach (string line in EnvironmentLines())
			Write(line);
	}

	public static List<string> EnvironmentLines()
	{
		List<string> lines =
		[
			"System: " + RuntimeInformation.OSDescription.Trim() + WindowsBuild() + ", " + RuntimeInformation.OSArchitecture + " OS, " + RuntimeInformation.ProcessArchitecture + " process",
			".NET " + Environment.Version + ", " + Environment.ProcessorCount + " logical cores, " + TotalMemory(),
			"Interface language " + CultureInfo.CurrentUICulture.Name,
			"Started " + (Paths.StartedByLauncher ? "through the launcher" : "directly") + ", " + (IsElevated() ? "elevated" : "not elevated")
		];
		string platform = PlatformDetails();
		if (platform.Length > 0)
			lines.Add(platform);
		return lines;
	}

	private static string PlatformDetails()
	{
		if (OperatingSystem.IsLinux())
		{
			string session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
			string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown";
			string packaging = Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed ? "Flatpak"
				: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")) ? "AppImage"
				: "native package or folder";
			return "Linux distribution " + LinuxDistribution() + ", " + Sanitize(session) + " session, desktop " + Sanitize(desktop) + ", running as " + packaging;
		}
		if (OperatingSystem.IsMacOS())
			return "macOS " + Environment.OSVersion.Version;
		return "";
	}

	private static string LinuxDistribution()
	{
		try
		{
			foreach (string line in File.ReadLines("/etc/os-release"))
			{
				if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
					return Sanitize(line["PRETTY_NAME=".Length..].Trim('"'));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		return "unknown";
	}

	private static string Sanitize(string value)
	{
		string trimmed = value.Length > 80 ? value[..80] : value;
		return trimmed.Replace('\n', ' ').Replace('\r', ' ');
	}

	public static void MarkSessionStarted(string? logFile)
	{
		if (string.IsNullOrEmpty(logFile))
			return;
		try
		{
			string directory = Path.Combine(Path.GetDirectoryName(logFile)!, ".sessions");
			Directory.CreateDirectory(directory);
			ReportUncleanSessions(directory);
			using Process current = Process.GetCurrentProcess();
			string marker = Path.Combine(directory, Environment.ProcessId + MarkerExtension);
			File.WriteAllText(marker, current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + Path.GetFileName(logFile));
			_markerPath = marker;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			Write("The session marker could not be written: " + ex.Message);
		}
	}

	public static void MarkSessionEnded()
	{
		string? marker = _markerPath;
		_markerPath = null;
		if (marker == null)
			return;
		try
		{
			File.Delete(marker);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void ReportUncleanSessions(string directory)
	{
		foreach (string marker in Directory.EnumerateFiles(directory, "*" + MarkerExtension))
		{
			try
			{
				if (!int.TryParse(Path.GetFileNameWithoutExtension(marker), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
				{
					File.Delete(marker);
					continue;
				}
				string[] lines = File.ReadAllLines(marker);
				long startTicks = lines.Length > 0 && long.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? ticks : 0;
				string logName = lines.Length > 1 ? lines[1] : "an unknown log";
				if (IsStillRunning(pid, startTicks))
					continue;
				DateTime started = startTicks > 0 ? new DateTime(startTicks, DateTimeKind.Utc) : File.GetCreationTimeUtc(marker);
				Write("The previous session (process " + pid + ", started " + started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC, log " + logName + ") ended without shutting down cleanly, it most likely crashed or was killed");
				File.Delete(marker);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool IsStillRunning(int pid, long startTicks)
	{
		try
		{
			using Process process = Process.GetProcessById(pid);
			return startTicks == 0 || process.StartTime.ToUniversalTime().Ticks == startTicks;
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private static string WindowsBuild()
	{
		if (!OperatingSystem.IsWindows())
			return "";
		try
		{
			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
			if (key == null)
				return "";
			string edition = key.GetValue("EditionID") as string ?? "";
			string release = key.GetValue("DisplayVersion") as string ?? "";
			string build = key.GetValue("CurrentBuild") as string ?? "";
			string revision = key.GetValue("UBR") is int ubr ? "." + ubr.ToString(CultureInfo.InvariantCulture) : "";
			return " (" + string.Join(" ", new[] { edition, release, "build " + build + revision }) + ")";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return "";
		}
	}

	private static string TotalMemory()
	{
		long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
		return bytes > 0 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB memory" : "unknown memory";
	}

	private static bool IsElevated()
	{
		try
		{
			return OperatingSystem.IsWindows() ? ProcessElevation.IsAdministrator() : Environment.UserName == "root";
		}
		catch
		{
			return false;
		}
	}

	private static void Write(string message)
	{
		App.Logger.WriteLine(LOG_IDENT, message);
	}
}
