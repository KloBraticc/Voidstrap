using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Voidstrap.Utility;

public static class InstanceDirectory
{
	private const string LogIdent = "InstanceDirectory";

	private const int MaxSlots = 8;

	public static string? Prepare(string versionDirectory, string executableName)
	{
		if (!Platform.IsWindows || string.IsNullOrEmpty(versionDirectory) || !Directory.Exists(versionDirectory))
		{
			return null;
		}
		try
		{
			string root = Path.Combine(Paths.Base, "MultiInstance");
			Directory.CreateDirectory(root);
			HashSet<string> busy = GetDirectoriesInUse();
			for (int slot = 1; slot <= MaxSlots; slot++)
			{
				string candidate = Path.Combine(root, "slot" + slot);
				if (busy.Contains(candidate))
				{
					continue;
				}
				if (!EnsureLink(candidate, versionDirectory))
				{
					continue;
				}
				if (File.Exists(Path.Combine(candidate, executableName)))
				{
					App.Logger?.WriteLine(LogIdent, "Instance slot " + slot + " is ready at " + candidate);
					return candidate;
				}
			}
			App.Logger?.WriteLine(LogIdent, "Every instance slot is already in use");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "An instance slot could not be prepared: " + ex.Message);
		}
		return null;
	}

	private static HashSet<string> GetDirectoriesInUse()
	{
		HashSet<string> busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in new string[] { "RobloxPlayerBeta", "RobloxStudioBeta" })
		{
			Process[] processes;
			try
			{
				processes = Process.GetProcessesByName(name);
			}
			catch
			{
				continue;
			}
			foreach (Process process in processes)
			{
				using (process)
				{
					try
					{
						string? path = process.MainModule?.FileName;
						if (!string.IsNullOrEmpty(path))
						{
							busy.Add(Path.GetDirectoryName(path)!);
						}
					}
					catch
					{
					}
				}
			}
		}
		return busy;
	}

	private static bool EnsureLink(string link, string target)
	{
		try
		{
			if (Directory.Exists(link))
			{
				string? current = new DirectoryInfo(link).LinkTarget;
				if (string.Equals(current?.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (current == null)
				{
					return false;
				}
				Directory.Delete(link);
			}
			return CreateJunction(link, target);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The slot link at " + link + " could not be prepared: " + ex.Message);
			return false;
		}
	}

	private static bool CreateJunction(string link, string target)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("mklink");
		startInfo.ArgumentList.Add("/J");
		startInfo.ArgumentList.Add(link);
		startInfo.ArgumentList.Add(target);
		try
		{
			using Process? process = Process.Start(startInfo);
			if (process == null)
			{
				return false;
			}
			process.WaitForExit(10000);
			return Directory.Exists(link);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "A slot link could not be created: " + ex.Message);
			return false;
		}
	}
}
