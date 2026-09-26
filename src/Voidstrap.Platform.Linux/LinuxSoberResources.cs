using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public readonly record struct LinuxSoberScope(string Unit, string CgroupDirectory);

public static partial class LinuxSoberResources
{
	private const string SoberScopePrefix = "app-flatpak-org.vinegarhq.Sober";
	private const string CgroupRoot = "/sys/fs/cgroup";
	private const int CpuSetBytes = 128;
	private const int CommandTimeoutMilliseconds = 5000;

	private static readonly Lazy<string?> Systemctl = new(() => new Voidstrap.Core.SystemProcessService().FindExecutable("systemctl"));

	public static bool IsSupported => OperatingSystem.IsLinux()
		&& File.Exists(Path.Combine(CgroupRoot, "cgroup.controllers"))
		&& !File.Exists("/.flatpak-info")
		&& !string.IsNullOrWhiteSpace(Systemctl.Value);

	public static bool TryGetScope(out LinuxSoberScope scope)
	{
		scope = default;
		foreach (int processId in LinuxSoberProcessProbe.GetSandboxProcessIds())
		{
			string? path = ReadUnifiedCgroupPath(processId);
			if (path is null)
				continue;
			string unit = Path.GetFileName(path);
			if (!unit.StartsWith(SoberScopePrefix, StringComparison.Ordinal)
				|| !unit.EndsWith(".scope", StringComparison.Ordinal))
				continue;
			scope = new LinuxSoberScope(unit, CgroupRoot + path);
			return Directory.Exists(scope.CgroupDirectory);
		}
		return false;
	}

	public static bool TrySetProperties(LinuxSoberScope scope, IReadOnlyList<string> assignments, out string error)
	{
		error = "";
		string? systemctl = Systemctl.Value;
		if (string.IsNullOrWhiteSpace(systemctl) || assignments.Count == 0)
		{
			error = "systemctl is unavailable";
			return false;
		}

		ProcessStartInfo startInfo = new(systemctl)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("--user");
		startInfo.ArgumentList.Add("set-property");
		startInfo.ArgumentList.Add("--runtime");
		startInfo.ArgumentList.Add(scope.Unit);
		foreach (string assignment in assignments)
			startInfo.ArgumentList.Add(assignment);

		try
		{
			using Process? process = Process.Start(startInfo);
			if (process is null)
			{
				error = "systemctl could not start";
				return false;
			}
			Task<string> errorText = process.StandardError.ReadToEndAsync();
			_ = process.StandardOutput.ReadToEndAsync();
			if (!process.WaitForExit(CommandTimeoutMilliseconds))
			{
				try
				{
					process.Kill();
				}
				catch (InvalidOperationException)
				{
				}
				error = "systemctl did not answer in time";
				return false;
			}
			if (process.ExitCode == 0)
				return true;
			error = errorText.Wait(1000) ? errorText.Result.Trim() : "systemctl failed";
			return false;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			error = ex.Message;
			return false;
		}
	}

	public static byte[]? TryReadAffinity(int processId)
	{
		byte[] mask = new byte[CpuSetBytes];
		return sched_getaffinity(processId, (nuint)mask.Length, mask) == 0 ? mask : null;
	}

	public static byte[] CreateAffinity(int processorCount)
	{
		byte[] mask = new byte[CpuSetBytes];
		int count = Math.Clamp(processorCount, 1, CpuSetBytes * 8);
		for (int cpu = 0; cpu < count; cpu++)
			mask[cpu / 8] |= (byte)(1 << (cpu % 8));
		return mask;
	}

	public static int ApplyAffinity(IReadOnlyList<int> processIds, byte[] mask)
	{
		int applied = 0;
		foreach (int processId in processIds)
		{
			IEnumerable<string> threads;
			try
			{
				threads = Directory.EnumerateDirectories("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/task");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			foreach (string thread in threads)
			{
				if (int.TryParse(Path.GetFileName(thread), NumberStyles.None, CultureInfo.InvariantCulture, out int threadId)
					&& sched_setaffinity(threadId, (nuint)mask.Length, mask) == 0)
					applied++;
			}
		}
		return applied;
	}

	public static long ReadMemoryCurrent(LinuxSoberScope scope)
	{
		try
		{
			return long.TryParse(File.ReadAllText(Path.Combine(scope.CgroupDirectory, "memory.current")).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
				? bytes
				: 0;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}

	public static bool TryReclaimMemory(LinuxSoberScope scope, long bytes)
	{
		if (bytes <= 0)
			return false;
		try
		{
			File.WriteAllText(Path.Combine(scope.CgroupDirectory, "memory.reclaim"), bytes.ToString(CultureInfo.InvariantCulture));
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static string? ReadUnifiedCgroupPath(int processId)
	{
		try
		{
			foreach (string line in File.ReadLines("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/cgroup"))
			{
				if (line.StartsWith("0::", StringComparison.Ordinal))
					return line[3..].Trim();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		return null;
	}

	[LibraryImport("libc", SetLastError = true)]
	private static partial int sched_setaffinity(int pid, nuint cpusetsize, byte[] mask);

	[LibraryImport("libc", SetLastError = true)]
	private static partial int sched_getaffinity(int pid, nuint cpusetsize, byte[] mask);
}
