using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Voidstrap.Utility;

internal static class RobloxProcesses
{
	private static readonly string[] SoberNames = ["sober", "Sober", "org.vinegarhq.Sober"];

	public static List<Process> Player()
	{
		if (Platform.IsMacOS)
		{
			List<Process> all = Process.GetProcessesByName(Platform.RobloxPlayerProcessName).ToList();
			if (all.Count == 0 || MacRobloxProcesses.IsGameRunning())
				return all;
			all.ForEach(process => process.Dispose());
			return [];
		}
		IEnumerable<string> names = Platform.IsLinux ? SoberNames.Append(Platform.RobloxPlayerProcessName) : [Platform.RobloxPlayerProcessName];
		return names.SelectMany(Process.GetProcessesByName).ToList();
	}

	public static List<Process> Studio() => Process.GetProcessesByName(Platform.RobloxStudioProcessName).ToList();

	public static bool PlayerRunning() => AnyAndDispose(Player());

	public static bool StudioRunning() => AnyAndDispose(Studio());

	private static bool AnyAndDispose(List<Process> processes)
	{
		bool running = processes.Count > 0;
		processes.ForEach(process => process.Dispose());
		return running;
	}
}
