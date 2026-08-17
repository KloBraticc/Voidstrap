using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Voidstrap.Utility;

internal static class ScreenMetrics
{
	private static (double Width, double Height)? _cached;

	public static (double Width, double Height) GetPrimary()
	{
		if (_cached.HasValue)
		{
			return _cached.Value;
		}
		(double, double) result = (0.0, 0.0);
		try
		{
			if (OperatingSystem.IsLinux())
			{
				result = QueryLinux();
			}
			else if (OperatingSystem.IsMacOS())
			{
				result = QueryMacOS();
			}
		}
		catch
		{
		}
		if (result.Item1 < 320.0 || result.Item2 < 240.0)
		{
			try
			{
				result = (System.Windows.SystemParameters.PrimaryScreenWidth, System.Windows.SystemParameters.PrimaryScreenHeight);
			}
			catch
			{
			}
		}
		if (result.Item1 < 320.0 || result.Item2 < 240.0)
		{
			result = (1280.0, 800.0);
		}
		_cached = result;
		return result;
	}

	private static (double, double) QueryLinux()
	{
		string info = Run("xdpyinfo", "");
		Match match = Regex.Match(info, @"dimensions:\s+(\d+)x(\d+)");
		if (match.Success && int.TryParse(match.Groups[1].Value, out int w1) && int.TryParse(match.Groups[2].Value, out int h1))
		{
			return (w1, h1);
		}
		string randr = Run("xrandr", "--current");
		match = Regex.Match(randr, @"current\s+(\d+)\s*x\s*(\d+)");
		if (match.Success && int.TryParse(match.Groups[1].Value, out int w2) && int.TryParse(match.Groups[2].Value, out int h2))
		{
			return (w2, h2);
		}
		match = Regex.Match(randr, @"\bconnected\b.*?\b(\d+)x(\d+)\+");
		if (match.Success && int.TryParse(match.Groups[1].Value, out int w3) && int.TryParse(match.Groups[2].Value, out int h3))
		{
			return (w3, h3);
		}
		return (0.0, 0.0);
	}

	private static (double, double) QueryMacOS()
	{
		string info = Run("system_profiler", "SPDisplaysDataType");
		Match match = Regex.Match(info, @"Resolution:\s+(\d+)\s*x\s*(\d+)");
		if (match.Success && int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
		{
			return (w, h);
		}
		return (0.0, 0.0);
	}

	private static string Run(string fileName, string arguments)
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process == null)
			{
				return string.Empty;
			}
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(2500);
			return output;
		}
		catch
		{
			return string.Empty;
		}
	}
}
