using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public readonly record struct LinuxSoberScope(string Unit, string CgroupDirectory);

public static partial class LinuxSoberResources
{
	private const string SoberScopePrefix = "app-flatpak-org.vinegarhq.Sober";
	private const string CgroupRoot = "/sys/fs/cgroup";
	private const int CpuSetBytes = 128;
	private const int CommandTimeoutMilliseconds = 5000;

	private const long HostScopeCacheMilliseconds = 5000;

	private static readonly Lazy<string?> Systemctl = new(() => new Voidstrap.Core.SystemProcessService().FindExecutable("systemctl"));

	private static readonly Lazy<string?> FlatpakSpawn = new(() => new Voidstrap.Core.SystemProcessService().FindExecutable("flatpak-spawn"));

	private static readonly Lazy<bool> HostSupport = new(() => RunHost(["sh", "-c", "test -f /sys/fs/cgroup/cgroup.controllers && command -v systemctl >/dev/null && command -v taskset >/dev/null"], out _, 3000));

	private static readonly object HostScopeSync = new();

	private static LinuxSoberScope? _hostScope;

	private static long _hostScopeCheckedAt = long.MinValue;

	public static bool IsSupported => OperatingSystem.IsLinux()
		&& (LinuxFlatpakHost.IsSandboxed
			? HostSupport.Value
			: File.Exists(Path.Combine(CgroupRoot, "cgroup.controllers")) && !string.IsNullOrWhiteSpace(Systemctl.Value));

	public static bool TryGetScope(out LinuxSoberScope scope)
	{
		if (LinuxFlatpakHost.IsSandboxed)
			return TryGetHostScope(out scope);

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

	private static bool TryGetHostScope(out LinuxSoberScope scope)
	{
		lock (HostScopeSync)
		{
			long now = Environment.TickCount64;
			if (_hostScopeCheckedAt != long.MinValue && now - _hostScopeCheckedAt < HostScopeCacheMilliseconds)
			{
				scope = _hostScope ?? default;
				return _hostScope.HasValue;
			}

			_hostScopeCheckedAt = now;
			_hostScope = null;
			scope = default;
			if (!RunHost(["systemctl", "--user", "list-units", "--type=scope", "--state=running", "--plain", "--no-legend", "--no-pager", SoberScopePrefix + "*"], out string units))
				return false;

			foreach (string line in units.Split('\n'))
			{
				string unit = line.Trim().Split(' ', 2)[0];
				if (!unit.StartsWith(SoberScopePrefix, StringComparison.Ordinal) || !unit.EndsWith(".scope", StringComparison.Ordinal))
					continue;
				if (!RunHost(["systemctl", "--user", "show", "--property=ControlGroup", "--value", unit], out string group))
					continue;
				group = group.Trim();
				if (!group.StartsWith('/'))
					continue;
				scope = new LinuxSoberScope(unit, CgroupRoot + group);
				_hostScope = scope;
				return true;
			}

			return false;
		}
	}

	private static bool RunHost(IReadOnlyList<string> arguments, out string output, int timeoutMilliseconds = CommandTimeoutMilliseconds)
	{
		output = "";
		string? spawn = FlatpakSpawn.Value;
		if (string.IsNullOrWhiteSpace(spawn))
			return false;

		ProcessStartInfo startInfo = new(spawn)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add("--host");
		startInfo.ArgumentList.Add("--directory=/");
		foreach (string argument in arguments)
			startInfo.ArgumentList.Add(argument);

		try
		{
			using Process? process = Process.Start(startInfo);
			if (process is null)
				return false;
			Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
			Task<string> standardError = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeoutMilliseconds))
			{
				try
				{
					process.Kill();
				}
				catch (InvalidOperationException)
				{
				}
				output = "the host command did not answer in time";
				return false;
			}
			if (process.ExitCode == 0)
			{
				output = standardOutput.Wait(1000) ? standardOutput.Result : "";
				return true;
			}
			output = standardError.Wait(1000) ? standardError.Result.Trim() : "";
			return false;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			output = ex.Message;
			return false;
		}
	}

	public static IReadOnlyList<int> GetProcessIds(LinuxSoberScope scope)
	{
		if (!LinuxFlatpakHost.IsSandboxed)
			return LinuxSoberProcessProbe.GetSandboxProcessIds();

		List<int> processIds = [];
		if (RunHost(["cat", Path.Combine(scope.CgroupDirectory, "cgroup.procs")], out string output))
		{
			foreach (string line in output.Split('\n'))
			{
				if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
					processIds.Add(processId);
			}
		}
		return processIds;
	}

	private static string FormatMask(byte[] mask)
	{
		StringBuilder builder = new();
		for (int index = mask.Length - 1; index >= 0; index--)
			builder.Append(mask[index].ToString("x2", CultureInfo.InvariantCulture));
		string hex = builder.ToString().TrimStart('0');
		return hex.Length == 0 ? "0" : hex;
	}

	private static byte[]? ParseMask(string hex)
	{
		hex = hex.Replace(",", "", StringComparison.Ordinal).Trim();
		if (hex.Length == 0 || hex.Length > CpuSetBytes * 2)
			return null;
		if (hex.Length % 2 == 1)
			hex = "0" + hex;
		byte[] mask = new byte[CpuSetBytes];
		for (int index = 0; index < hex.Length / 2; index++)
		{
			if (!byte.TryParse(hex.AsSpan(hex.Length - (index + 1) * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
				return null;
			mask[index] = value;
		}
		return mask;
	}

	public static bool TrySetProperties(LinuxSoberScope scope, IReadOnlyList<string> assignments, out string error)
	{
		error = "";
		if (LinuxFlatpakHost.IsSandboxed)
		{
			if (assignments.Count == 0)
			{
				error = "no properties were requested";
				return false;
			}
			List<string> hostArguments = ["systemctl", "--user", "set-property", "--runtime", scope.Unit, .. assignments];
			if (RunHost(hostArguments, out string output))
				return true;
			error = output.Length == 0 ? "systemctl failed on the host" : output;
			return false;
		}

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
		if (LinuxFlatpakHost.IsSandboxed)
		{
			if (!RunHost(["taskset", "-p", processId.ToString(CultureInfo.InvariantCulture)], out string output))
				return null;
			int separator = output.LastIndexOf(':');
			return separator < 0 ? null : ParseMask(output[(separator + 1)..]);
		}

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
		if (LinuxFlatpakHost.IsSandboxed)
		{
			if (processIds.Count == 0)
				return 0;
			List<string> hostArguments = ["sh", "-c", "m=$1; shift; n=0; for p in \"$@\"; do if taskset -a -p \"$m\" \"$p\" >/dev/null 2>&1; then n=$((n + $(ls /proc/$p/task | wc -l))); fi; done; echo $n", "sh", FormatMask(mask)];
			foreach (int processId in processIds)
				hostArguments.Add(processId.ToString(CultureInfo.InvariantCulture));
			return RunHost(hostArguments, out string output)
				&& int.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int threads)
				? threads
				: 0;
		}

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
		if (LinuxFlatpakHost.IsSandboxed)
		{
			return RunHost(["cat", Path.Combine(scope.CgroupDirectory, "memory.current")], out string output)
				&& long.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long hostBytes)
				? hostBytes
				: 0;
		}

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
		if (LinuxFlatpakHost.IsSandboxed)
			return RunHost(["sh", "-c", "printf '%s' \"$1\" > \"$2\"", "sh", bytes.ToString(CultureInfo.InvariantCulture), Path.Combine(scope.CgroupDirectory, "memory.reclaim")], out _);
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
