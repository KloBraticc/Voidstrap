using System;
using System.Collections.Generic;
using System.IO;

namespace Voidstrap.Core.Installation;

public static class InstallLocationSelector
{
	public static string? SelectValid(IEnumerable<string?> candidates, Func<string, bool> validator)
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string? candidate in candidates)
		{
			string? normalized = Normalize(candidate);
			if (normalized != null && seen.Add(normalized) && validator(normalized))
			{
				return normalized;
			}
		}
		return null;
	}

	public static string? Normalize(string? candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return null;
		}
		try
		{
			string expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
			if (!Path.IsPathFullyQualified(expanded))
			{
				return null;
			}
			return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch
		{
			return null;
		}
	}

	public static string? ExtractExecutable(string? command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}
		string value = Environment.ExpandEnvironmentVariables(command.Trim());
		if (value.StartsWith('"'))
		{
			int closing = value.IndexOf('"', 1);
			return closing > 1 ? value.Substring(1, closing - 1) : null;
		}
		int executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
		return executableEnd >= 0 ? value.Substring(0, executableEnd + 4).Trim() : null;
	}

	public static string? RebaseUserProfile(string? candidate, string userProfile)
	{
		string? normalized = Normalize(candidate);
		string? normalizedProfile = Normalize(userProfile);
		if (normalized == null || normalizedProfile == null)
		{
			return null;
		}
		string marker = Path.DirectorySeparatorChar + "Users" + Path.DirectorySeparatorChar;
		int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (markerIndex < 1)
		{
			return null;
		}
		int relativeStart = normalized.IndexOf(Path.DirectorySeparatorChar, markerIndex + marker.Length);
		if (relativeStart < 0)
		{
			return normalizedProfile;
		}
		return normalizedProfile + normalized.Substring(relativeStart);
	}
}
