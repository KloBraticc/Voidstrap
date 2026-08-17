using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Voidstrap.Utility;

internal static class CpuInfo
{
	public static string? GetModelName()
	{
		try
		{
			if (OperatingSystem.IsLinux())
			{
				foreach (string line in File.ReadLines("/proc/cpuinfo"))
				{
					if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
					{
						int separator = line.IndexOf(':');
						if (separator >= 0)
						{
							string value = line[(separator + 1)..].Trim();
							if (!string.IsNullOrEmpty(value))
							{
								return value;
							}
						}
					}
				}
				return null;
			}
			if (OperatingSystem.IsMacOS())
			{
				string result = RunCommand("sysctl", "-n machdep.cpu.brand_string");
				return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
			}
		}
		catch
		{
		}
		return null;
	}

	public static int GetPhysicalCoreCount()
	{
		try
		{
			if (OperatingSystem.IsLinux())
			{
				HashSet<string> seen = new HashSet<string>();
				string? physicalId = null;
				string? coreId = null;
				foreach (string line in File.ReadLines("/proc/cpuinfo"))
				{
					if (line.StartsWith("physical id", StringComparison.OrdinalIgnoreCase))
					{
						physicalId = ValueOf(line);
					}
					else if (line.StartsWith("core id", StringComparison.OrdinalIgnoreCase))
					{
						coreId = ValueOf(line);
					}
					if (physicalId != null && coreId != null)
					{
						seen.Add(physicalId + ":" + coreId);
						physicalId = null;
						coreId = null;
					}
				}
				if (seen.Count > 0)
				{
					return seen.Count;
				}
				foreach (string line in File.ReadLines("/proc/cpuinfo"))
				{
					if (line.StartsWith("cpu cores", StringComparison.OrdinalIgnoreCase)
						&& int.TryParse(ValueOf(line), out int cores) && cores > 0)
					{
						return cores;
					}
				}
			}
			else if (OperatingSystem.IsMacOS())
			{
				string result = RunCommand("sysctl", "-n hw.physicalcpu");
				if (int.TryParse(result.Trim(), out int physical) && physical > 0)
				{
					return physical;
				}
			}
		}
		catch
		{
		}
		return 0;
	}

	private static string? ValueOf(string line)
	{
		int separator = line.IndexOf(':');
		return separator >= 0 ? line[(separator + 1)..].Trim() : null;
	}

	private static string RunCommand(string fileName, string arguments)
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
			{
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process == null)
			{
				return string.Empty;
			}
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(3000);
			return output;
		}
		catch
		{
			return string.Empty;
		}
	}
}
