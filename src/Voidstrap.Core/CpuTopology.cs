using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Voidstrap.Core;

public static class CpuTopology
{
	private const string CpuRoot = "/sys/devices/system/cpu/";

	private static readonly Lazy<int[]> Online = new(ReadOnline);

	private static readonly Lazy<int[]> Isolated = new(ReadIsolated);

	public static IReadOnlyList<int> OnlineCpus => Online.Value;

	public static IReadOnlyList<int> IsolatedCpus => Isolated.Value;

	public static IReadOnlyList<int> Select(int count)
	{
		int[] online = Online.Value;
		int[] isolated = Isolated.Value;
		return isolated
			.Concat(online.Except(isolated).Reverse())
			.Take(Math.Clamp(count, 1, online.Length))
			.Order()
			.ToArray();
	}

	public static int[] ParseList(string? text)
	{
		SortedSet<int> cpus = [];
		foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			string[] range = part.Split('-');
			if (range.Length > 2
				|| !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first))
				return [];
			int last = first;
			if (range.Length == 2 && !int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out last))
				return [];
			if (last < first || last - first > 8192)
				return [];
			for (int cpu = first; cpu <= last; cpu++)
				cpus.Add(cpu);
		}
		return [.. cpus];
	}

	private static int[] ReadOnline()
	{
		int[] online = ReadList("online");
		return online.Length > 0 ? online : Enumerable.Range(0, Environment.ProcessorCount).ToArray();
	}

	private static int[] ReadIsolated()
	{
		int[] online = Online.Value;
		return ReadList("isolated").Union(ReadList("nohz_full")).Where(online.Contains).Order().ToArray();
	}

	private static int[] ReadList(string name)
	{
		if (!OperatingSystem.IsLinux())
			return [];
		try
		{
			return ParseList(File.ReadAllText(CpuRoot + name));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
