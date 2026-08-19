using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class ShellQuery
{
	public static string Run(string fileName, string arguments, int timeoutMilliseconds = 2500)
	{
		Process? process = null;
		try
		{
			process = Process.Start(new ProcessStartInfo(fileName, arguments)
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

			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> error = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeoutMilliseconds))
			{
				try
				{
					process.Kill(true);
				}
				catch
				{
				}
			}

			try
			{
				Task.WaitAll([output, error], timeoutMilliseconds);
			}
			catch
			{
			}

			return output.Status == TaskStatus.RanToCompletion ? output.Result : string.Empty;
		}
		catch
		{
			return string.Empty;
		}
		finally
		{
			process?.Dispose();
		}
	}
}
