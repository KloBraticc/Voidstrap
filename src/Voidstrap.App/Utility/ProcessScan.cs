using System.Diagnostics;

namespace Voidstrap.Utility;

public static class ProcessScan
{
	public static int Count(string name)
	{
		Process[] processes;
		try
		{
			processes = Process.GetProcessesByName(name);
		}
		catch
		{
			return 0;
		}
		foreach (Process process in processes)
			process.Dispose();
		return processes.Length;
	}

	public static bool Any(string name) => Count(name) > 0;
}
