using System;
using System.Collections.Generic;
using System.IO;

namespace Voidstrap.Integrations;

internal static class DiscordIpc
{
	internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

	internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

	private static readonly string[] SandboxDirectories =
	{
		"app/com.discordapp.Discord",
		"snap.discord"
	};

	internal static bool TryFindPipe(out int pipe)
	{
		if (Voidstrap.Utility.Platform.IsWindows)
			return TryFindPipeIn(@"\\.\pipe\", out pipe);

		List<string> directories = new List<string>();
		HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
		AddRoot(directories, unique, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));
		AddRoot(directories, unique, Environment.GetEnvironmentVariable("TMPDIR"));
		AddRoot(directories, unique, Environment.GetEnvironmentVariable("TMP"));
		AddRoot(directories, unique, Environment.GetEnvironmentVariable("TEMP"));
		AddRoot(directories, unique, Path.GetTempPath());
		AddRoot(directories, unique, "/tmp");

		foreach (string directory in directories)
		{
			if (TryFindPipeIn(directory, out pipe))
				return true;
		}

		pipe = -1;
		return false;
	}

	private static bool TryFindPipeIn(string directory, out int pipe)
	{
		pipe = -1;
		try
		{
			foreach (string path in Directory.EnumerateFileSystemEntries(directory, "discord-ipc-*", SearchOption.TopDirectoryOnly))
			{
				string name = Path.GetFileName(path);
				if (name.Length == 13 &&
					name.StartsWith("discord-ipc-", StringComparison.Ordinal) &&
					int.TryParse(name.AsSpan(12), out int candidate) &&
					candidate is >= 0 and <= 9)
				{
					pipe = candidate;
					return true;
				}
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}

		return false;
	}

	private static void AddRoot(List<string> directories, HashSet<string> unique, string? root)
	{
		if (string.IsNullOrWhiteSpace(root))
			return;

		AddDirectory(directories, unique, root);
		foreach (string child in SandboxDirectories)
			AddDirectory(directories, unique, Path.Combine(root, child));
	}

	private static void AddDirectory(List<string> directories, HashSet<string> unique, string directory)
	{
		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(directory);
		}
		catch
		{
			return;
		}

		if (unique.Add(fullPath) && Directory.Exists(fullPath))
			directories.Add(fullPath);
	}
}
