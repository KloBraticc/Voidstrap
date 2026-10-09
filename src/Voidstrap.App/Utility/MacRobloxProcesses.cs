using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Voidstrap.Utility;

internal static class MacRobloxProcesses
{
	internal readonly record struct Snapshot(IReadOnlyList<int> GamePids, IReadOnlyList<int> MenuBarHelpers)
	{
		public bool GameRunning => GamePids.Count > 0;
	}

	internal static bool IsGameRunning() => Scan().GameRunning;

	internal static void CloseIdleMenuBarHelpers()
	{
		Snapshot snapshot = Scan();
		if (snapshot.GameRunning || snapshot.MenuBarHelpers.Count == 0)
			return;
		foreach (int pid in snapshot.MenuBarHelpers)
		{
			try
			{
				using Process helper = Process.GetProcessById(pid);
				helper.Kill();
				helper.WaitForExit(3000);
				App.Logger?.WriteLine("MacRobloxProcesses", $"Closed the idle Roblox menu bar helper {pid} so the launch starts a fresh Roblox");
			}
			catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
			{
			}
		}
	}

	internal static Snapshot Scan()
	{
		List<int> helpers = [];
		List<int> games = [];
		if (!OperatingSystem.IsMacOS())
			return new Snapshot(games, helpers);
		try
		{
			using Process ps = new()
			{
				StartInfo = new ProcessStartInfo("/bin/ps", "-axo pid=,stat=,args=")
				{
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			ps.Start();
			string output = ps.StandardOutput.ReadToEnd();
			ps.WaitForExit(3000);
			string executable = "/Contents/MacOS/" + Platform.RobloxPlayerProcessName;
			foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = raw.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 3 || !int.TryParse(parts[0], out int pid) || parts[1].StartsWith('Z'))
					continue;
				string args = parts[2];
				int end = args.IndexOf(executable, StringComparison.Ordinal);
				if (end < 0 || (end + executable.Length < args.Length && args[end + executable.Length] != ' '))
					continue;
				if (args.Contains("-launchToTray", StringComparison.Ordinal))
					helpers.Add(pid);
				else
					games.Add(pid);
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
		{
			App.Logger?.WriteLine("MacRobloxProcesses", "The Roblox processes could not be listed: " + ex.Message);
			foreach (Process process in Process.GetProcessesByName(Platform.RobloxPlayerProcessName))
			{
				games.Add(process.Id);
				process.Dispose();
			}
		}
		return new Snapshot(games, helpers);
	}
}
