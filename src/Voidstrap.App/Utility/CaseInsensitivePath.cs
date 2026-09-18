using System;
using System.IO;

namespace Voidstrap.Utility;

internal static class CaseInsensitivePath
{
	public static string Resolve(string? path)
	{
		if (string.IsNullOrEmpty(path))
			return path ?? string.Empty;

		if (!Platform.IsLinux)
			return path;

		if (File.Exists(path) || Directory.Exists(path))
			return path;

		try
		{
			string full = Path.GetFullPath(path);
			string? root = Path.GetPathRoot(full);
			if (string.IsNullOrEmpty(root))
				return path;

			string remainder = full.Substring(root.Length);
			if (remainder.Length == 0)
				return path;

			string current = root;
			string[] segments = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

			for (int i = 0; i < segments.Length; i++)
			{
				string candidate = Path.Combine(current, segments[i]);
				bool last = i == segments.Length - 1;

				if (last ? File.Exists(candidate) || Directory.Exists(candidate) : Directory.Exists(candidate))
				{
					current = candidate;
					continue;
				}

				string? match = FindEntry(current, segments[i], last);
				if (match == null)
					return path;

				current = match;
			}

			return current;
		}
		catch (Exception)
		{
			return path;
		}
	}

	public static bool Exists(string? path)
	{
		if (string.IsNullOrEmpty(path))
			return false;

		string resolved = Resolve(path);
		return File.Exists(resolved) || Directory.Exists(resolved);
	}

	private static string? FindEntry(string directory, string name, bool allowFile)
	{
		if (!Directory.Exists(directory))
			return null;

		foreach (string entry in Directory.EnumerateDirectories(directory))
		{
			if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
				return entry;
		}

		if (!allowFile)
			return null;

		foreach (string entry in Directory.EnumerateFiles(directory))
		{
			if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
				return entry;
		}

		return null;
	}
}
