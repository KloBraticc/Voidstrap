using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public static partial class UnixSystem
{
	private const int CtlKern = 1;
	private const int KernProc = 14;
	private const int KernProcArgs = 7;
	private const int KernProcPathname = 12;
	private const int PermissionDenied = 1;
	private const int CurrentDirectory = -100;

	public static int OpenNonBlocking { get; } = HostSystem.IsFreeBSD ? 0x4 : 0x800;

	public static int OpenCloseOnExec { get; } = HostSystem.IsFreeBSD ? 0x100000 : 0x80000;

	public static int OpenCreate { get; } = HostSystem.IsFreeBSD ? 0x200 : 0x40;

	public static IReadOnlyList<int> ProcessIds()
	{
		List<int> found = [];
		if (HostSystem.IsFreeBSD)
		{
			Process[] processes;
			try
			{
				processes = Process.GetProcesses();
			}
			catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
			{
				return found;
			}
			foreach (Process process in processes)
			{
				found.Add(process.Id);
				process.Dispose();
			}
			return found;
		}

		try
		{
			foreach (string directory in Directory.EnumerateDirectories("/proc"))
			{
				if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
					found.Add(processId);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
		return found;
	}

	public static string? CommandLine(int processId)
	{
		if (processId <= 0)
			return null;
		if (HostSystem.IsFreeBSD)
		{
			byte[]? raw = ReadProcessSysctl(processId, KernProcArgs);
			return raw == null ? null : Encoding.UTF8.GetString(raw);
		}
		return ReadProcFile(processId, "cmdline");
	}

	public static string? ExecutablePath(int processId)
	{
		if (processId <= 0)
			return null;
		if (HostSystem.IsFreeBSD)
		{
			byte[]? raw = ReadProcessSysctl(processId, KernProcPathname);
			return raw == null ? null : Encoding.UTF8.GetString(raw).TrimEnd('\0');
		}
		try
		{
			return new FileInfo(ProcPath(processId, "exe")).ResolveLinkTarget(false)?.FullName;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static string? ProcessName(int processId)
	{
		if (processId <= 0)
			return null;
		if (!HostSystem.IsFreeBSD)
			return ReadProcFile(processId, "comm")?.Trim();
		try
		{
			using Process process = Process.GetProcessById(processId);
			return process.ProcessName;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	public static long ResidentBytes(int processId)
	{
		if (processId <= 0)
			return 0;
		if (HostSystem.IsFreeBSD)
		{
			try
			{
				using Process process = Process.GetProcessById(processId);
				return Math.Max(0, process.WorkingSet64);
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				return 0;
			}
		}

		long value = ReadKilobytes(ProcPath(processId, "smaps_rollup"), "Pss:");
		return value > 0 ? value : ReadKilobytes(ProcPath(processId, "status"), "VmRSS:");
	}

	public static string? StartTag(int processId)
	{
		if (processId <= 0)
			return null;
		if (HostSystem.IsFreeBSD)
		{
			try
			{
				using Process process = Process.GetProcessById(processId);
				return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				return null;
			}
		}

		string? stat = ReadProcFile(processId, "stat");
		if (stat is null)
			return null;
		int close = stat.LastIndexOf(')');
		if (close < 0 || close + 2 > stat.Length)
			return null;
		string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length < 20 || fields[0] == "Z" || fields[0] == "X")
			return null;
		return fields[19];
	}

	public static bool IsAlive(int processId)
	{
		if (processId <= 0)
			return false;
		if (Kill(processId, 0) == 0)
			return true;
		return Marshal.GetLastPInvokeError() == PermissionDenied;
	}

	public static long? TotalMemoryBytes()
	{
		if (HostSystem.IsFreeBSD)
			return SysctlNumber("hw.physmem");
		long value = ReadKilobytes("/proc/meminfo", "MemTotal:");
		return value > 0 ? value : null;
	}

	public static long? AvailableMemoryBytes()
	{
		if (HostSystem.IsFreeBSD)
		{
			long? pageSize = SysctlNumber("hw.pagesize");
			if (pageSize is not > 0)
				return null;
			long pages = 0;
			foreach (string name in (string[])["vm.stats.vm.v_free_count", "vm.stats.vm.v_inactive_count", "vm.stats.vm.v_cache_count"])
				pages += SysctlNumber(name) ?? 0;
			return pages > 0 ? pages * pageSize.Value : null;
		}
		long value = ReadKilobytes("/proc/meminfo", "MemAvailable:");
		return value > 0 ? value : null;
	}

	private static string ProcPath(int processId, string name) =>
		"/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/" + name;

	private static string? ReadProcFile(int processId, string name)
	{
		try
		{
			return File.ReadAllText(ProcPath(processId, name));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static long ReadKilobytes(string path, string field)
	{
		try
		{
			foreach (string line in File.ReadLines(path))
			{
				if (!line.StartsWith(field, StringComparison.Ordinal))
					continue;
				string[] parts = line[field.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
				return parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long kilobytes) ? kilobytes * 1024 : 0;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
		return 0;
	}

	private static byte[]? ReadProcessSysctl(int processId, int kind)
	{
		int[] name = [CtlKern, KernProc, kind, processId];
		nuint length = 0;
		if (Sysctl(name, (uint)name.Length, null, ref length, 0, 0) != 0 || length == 0)
			return null;
		byte[] buffer = new byte[(int)length + 256];
		length = (nuint)buffer.Length;
		if (Sysctl(name, (uint)name.Length, buffer, ref length, 0, 0) != 0)
			return null;
		return buffer.AsSpan(0, (int)length).ToArray();
	}

	public static long? SysctlNumber(string name)
	{
		if (!HostSystem.IsFreeBSD)
			return null;
		byte[] buffer = new byte[8];
		nuint length = (nuint)buffer.Length;
		if (SysctlByName(name, buffer, ref length, 0, 0) != 0)
			return null;
		return length switch
		{
			4 => BitConverter.ToInt32(buffer, 0),
			8 => BitConverter.ToInt64(buffer, 0),
			_ => null
		};
	}

	public static string? SysctlString(string name)
	{
		if (!HostSystem.IsFreeBSD)
			return null;
		byte[] buffer = new byte[512];
		nuint length = (nuint)buffer.Length;
		if (SysctlByName(name, buffer, ref length, 0, 0) != 0)
			return null;
		string value = Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(length, (nuint)buffer.Length)).TrimEnd('\0').Trim();
		return value.Length == 0 ? null : value;
	}

	public static bool TryGetFileStatus(string path, bool followLinks, out uint mode, out uint owner)
	{
		mode = 0;
		owner = 0;
		byte[] status = new byte[256];
		try
		{
			if (HostSystem.IsFreeBSD)
			{
				if (FreeBSDFileStatus(CurrentDirectory, path, followLinks ? 0 : 0x200, status) != 0)
					return false;
				mode = BitConverter.ToUInt16(status, 24);
				owner = BitConverter.ToUInt32(status, 28);
				return true;
			}
			if (LinuxFileStatus(CurrentDirectory, path, followLinks ? 0 : 0x100, 0xB, status) != 0)
				return false;
			mode = BitConverter.ToUInt16(status, 28);
			owner = BitConverter.ToUInt32(status, 20);
			return true;
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static bool IsRegularFile(uint mode) => (mode & 0xF000) == 0x8000;

	[LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int LinuxFileStatus(int directoryFileDescriptor, string path, int flags, uint mask, byte[] status);

	[LibraryImport("libc", EntryPoint = "fstatat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int FreeBSDFileStatus(int directoryFileDescriptor, string path, int flags, byte[] status);

	[LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
	private static partial int Kill(int processId, int signal);

	[LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
	private static partial int Sysctl(int[] name, uint nameLength, byte[]? oldValue, ref nuint oldLength, nint newValue, nuint newLength);

	[LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int SysctlByName(string name, byte[] oldValue, ref nuint oldLength, nint newValue, nuint newLength);
}
