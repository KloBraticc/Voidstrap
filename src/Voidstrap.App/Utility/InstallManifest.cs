using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class InstallManifest
{
	public const string FileName = ".voidstrap-files";

	public sealed record Entry(string Path, long Size, uint? Crc);

	public sealed record Result(int Checked, int Skipped, IReadOnlyList<string> Missing, IReadOnlyList<string> Damaged, bool HasChecksums)
	{
		public int ProblemCount => Missing.Count + Damaged.Count;
	}

	private static readonly uint[] Table = BuildTable();

	private static readonly int Workers = Math.Clamp(Environment.ProcessorCount, 2, 8);

	private static uint[] BuildTable()
	{
		uint[] table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			uint value = i;
			for (int bit = 0; bit < 8; bit++)
				value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
			table[i] = value;
		}
		return table;
	}

	public static uint ComputeCrc(string path, CancellationToken token)
	{
		uint crc = 0xFFFFFFFFu;
		byte[] buffer = new byte[1 << 17];
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan);
		int read;
		while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			token.ThrowIfCancellationRequested();
			for (int i = 0; i < read; i++)
				crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
		}
		return ~crc;
	}

	public static string Format(Entry entry)
	{
		return entry.Crc is uint crc
			? entry.Path + "\t" + entry.Size.ToString(CultureInfo.InvariantCulture) + "\t" + crc.ToString("x8", CultureInfo.InvariantCulture)
			: entry.Path;
	}

	public static string PathOf(string line)
	{
		int tab = line.IndexOf('\t');
		return tab < 0 ? line : line[..tab];
	}

	public static void Write(string root, IEnumerable<Entry> entries)
	{
		File.WriteAllLines(System.IO.Path.Combine(root, FileName), entries.Select(Format), new UTF8Encoding(false));
	}

	public static List<Entry>? Read(string root)
	{
		string path = System.IO.Path.Combine(root, FileName);
		if (!File.Exists(path))
			return null;
		List<Entry> entries = [];
		foreach (string line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;
			string[] parts = line.Split('\t');
			if (parts.Length >= 3
				&& long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
				&& uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc))
				entries.Add(new Entry(parts[0], size, crc));
			else
				entries.Add(new Entry(parts[0], -1, null));
		}
		return entries;
	}

	public static List<Entry> Capture(string root, CancellationToken token)
	{
		string fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
		ConcurrentBag<Entry> entries = [];
		Parallel.ForEach(Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories), new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, file =>
		{
			string relative = file[fullRoot.Length..];
			if (string.Equals(relative, FileName, StringComparison.OrdinalIgnoreCase))
				return;
			entries.Add(new Entry(relative, new FileInfo(file).Length, ComputeCrc(file, token)));
		});
		return [.. entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)];
	}

	public static Result Verify(string root, IReadOnlyList<Entry> entries, ISet<string> modified, Action<int, int>? progress, CancellationToken token)
	{
		string fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
		ConcurrentBag<string> missing = [];
		ConcurrentBag<string> damaged = [];
		int done = 0;
		int skipped = 0;
		Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, entry =>
		{
			string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, entry.Path));
			if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
			{
				Interlocked.Increment(ref skipped);
			}
			else if (!File.Exists(full))
			{
				missing.Add(entry.Path);
			}
			else if (modified.Contains(entry.Path))
			{
				Interlocked.Increment(ref skipped);
			}
			else if (entry.Crc is uint expected)
			{
				try
				{
					if (new FileInfo(full).Length != entry.Size || ComputeCrc(full, token) != expected)
						damaged.Add(entry.Path);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					damaged.Add(entry.Path);
				}
			}
			int count = Interlocked.Increment(ref done);
			if (count % 64 == 0 || count == entries.Count)
				progress?.Invoke(count, entries.Count);
		});
		return new Result(
			entries.Count - skipped - missing.Count,
			skipped,
			[.. missing.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
			[.. damaged.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
			entries.Any(entry => entry.Crc != null));
	}
}
