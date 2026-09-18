using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voidstrap.Integrations.CommunityMods;

namespace Voidstrap.Utility;

internal readonly record struct LayoutRepairReport(int Remapped, int Stale);

internal static class RobloxLayoutRepair
{
	private const string LogIdent = "RobloxLayoutRepair";

	private static readonly string[] ContentRoots = { "content", "ExtraContent", "PlatformContent", "shaders" };

	private static string _indexedFolder = "";

	private static Dictionary<string, List<string>>? _index;

	private static string _clientFolder = "";

	public static void Forget()
	{
		_index = null;
		_indexedFolder = "";
		_clientFolder = "";
	}

	public static void UseClient(string robloxFolder)
	{
		if (!string.Equals(_clientFolder, robloxFolder, StringComparison.OrdinalIgnoreCase))
		{
			_clientFolder = robloxFolder;
			_index = null;
			_indexedFolder = "";
		}
	}

	public static string FindClientFolder()
	{
		if (_clientFolder.Length > 0 && Directory.Exists(_clientFolder))
		{
			return _clientFolder;
		}
		try
		{
			string? player = RobloxInstallCompression.PlayerFolder();
			if (player != null && Directory.Exists(Path.Combine(player, "content")))
			{
				_clientFolder = player;
				return _clientFolder;
			}
			if (Paths.Initialized && Directory.Exists(Paths.Versions))
			{
				string newest = Directory.EnumerateDirectories(Paths.Versions, "version-*")
					.Where(folder => Directory.Exists(Path.Combine(folder, "content")))
					.OrderByDescending(Directory.GetLastWriteTimeUtc)
					.FirstOrDefault() ?? "";
				_clientFolder = newest;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The installed Roblox folder could not be located: " + ex.Message);
		}
		return _clientFolder;
	}

	public static string? ResolveUniqueName(string fileName)
	{
		string folder = FindClientFolder();
		if (folder.Length == 0)
		{
			return null;
		}
		Dictionary<string, List<string>>? index = BuildIndex(folder);
		if (index is null || !index.TryGetValue(fileName, out List<string>? candidates) || candidates.Count != 1)
		{
			return null;
		}
		return candidates[0];
	}

	public static bool ExistsInClient(string relative)
	{
		string folder = FindClientFolder();
		return folder.Length > 0 && File.Exists(Path.Combine(folder, relative));
	}

	public static LayoutRepairReport Repair(string robloxFolder, IEnumerable<string> modFolders)
	{
		Dictionary<string, List<string>>? index = BuildIndex(robloxFolder);
		if (index is null || index.Count == 0)
		{
			return new LayoutRepairReport(0, 0);
		}
		int remapped = 0;
		int stale = 0;
		foreach (string modFolder in modFolders)
		{
			if (!Directory.Exists(modFolder))
			{
				continue;
			}
			foreach ((string relative, string? corrected) in Plan(robloxFolder, modFolder, index))
			{
				if (corrected is null)
				{
					stale++;
					App.Logger?.WriteLine(LogIdent, "Roblox no longer ships " + relative + ", the mod file has no target");
					continue;
				}
				if (TryMove(modFolder, relative, corrected))
				{
					remapped++;
					App.Logger?.WriteLine(LogIdent, "Roblox moved this file, remapped " + relative + " to " + corrected);
				}
			}
		}
		if (remapped > 0 || stale > 0)
		{
			App.Logger?.WriteLine(LogIdent, "Remapped " + remapped + " mod files to the current Roblox layout, " + stale + " have no target left");
		}
		return new LayoutRepairReport(remapped, stale);
	}

	public static IEnumerable<(string Relative, string? Corrected)> Plan(string robloxFolder, string modFolder, Dictionary<string, List<string>> index)
	{
		List<(string, string?)> plan = [];
		IEnumerable<string> files;
		try
		{
			files = Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not scan " + modFolder + ": " + ex.Message);
			return plan;
		}
		foreach (string file in files)
		{
			string relative = Path.GetRelativePath(modFolder, file);
			if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(relative, "README.txt", StringComparison.OrdinalIgnoreCase)
				|| !IsUnderContentRoot(relative))
			{
				continue;
			}
			if (File.Exists(Path.Combine(robloxFolder, relative)))
			{
				continue;
			}
			if (!index.TryGetValue(Path.GetFileName(relative), out List<string>? candidates) || candidates.Count == 0)
			{
				plan.Add((relative, null));
				continue;
			}
			string best = candidates[0];
			int bestScore = -1;
			foreach (string candidate in candidates)
			{
				int score = SharedTrailingSegments(relative, candidate);
				if (score > bestScore)
				{
					bestScore = score;
					best = candidate;
				}
			}
			if (bestScore <= 0 || string.Equals(best, relative, StringComparison.OrdinalIgnoreCase))
			{
				plan.Add((relative, null));
				continue;
			}
			plan.Add((relative, best));
		}
		return plan;
	}

	public static int SharedTrailingSegments(string left, string right)
	{
		string[] a = left.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
		string[] b = right.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
		int shared = 0;
		while (shared < a.Length && shared < b.Length
			&& string.Equals(a[a.Length - 1 - shared], b[b.Length - 1 - shared], StringComparison.OrdinalIgnoreCase))
		{
			shared++;
		}
		return shared;
	}

	private static bool IsUnderContentRoot(string relative)
	{
		int separator = relative.IndexOf(Path.DirectorySeparatorChar);
		if (separator <= 0)
		{
			return false;
		}
		string root = relative[..separator];
		return ContentRoots.Contains(root, StringComparer.OrdinalIgnoreCase);
	}

	private static bool TryMove(string modFolder, string relative, string corrected)
	{
		try
		{
			CommunityModVerdict verdict = CommunityModGuard.InspectRelativePath(corrected);
			if (!verdict.Allowed || !IsUnderContentRoot(corrected))
			{
				return false;
			}
			string root = Path.GetFullPath(modFolder);
			string source = Path.GetFullPath(Path.Combine(root, relative));
			string destination = Path.GetFullPath(Path.Combine(root, corrected));
			if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (File.Exists(destination))
			{
				return false;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Move(source, destination);
			PruneEmptyFolders(Path.GetDirectoryName(source)!, root);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not remap " + relative + ": " + ex.Message);
			return false;
		}
	}

	private static void PruneEmptyFolders(string folder, string root)
	{
		try
		{
			string current = Path.GetFullPath(folder);
			while (current.Length > root.Length
				&& current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& Directory.Exists(current)
				&& !Directory.EnumerateFileSystemEntries(current).Any())
			{
				Directory.Delete(current);
				current = Path.GetDirectoryName(current) ?? root;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not tidy an empty mod folder: " + ex.Message);
		}
	}

	public static Dictionary<string, List<string>>? BuildIndex(string robloxFolder)
	{
		if (_index is not null && string.Equals(_indexedFolder, robloxFolder, StringComparison.OrdinalIgnoreCase))
		{
			return _index;
		}
		if (!Directory.Exists(robloxFolder))
		{
			return null;
		}
		Dictionary<string, List<string>> index = new(StringComparer.OrdinalIgnoreCase);
		foreach (string relativeRoot in ContentRoots)
		{
			string root = Path.Combine(robloxFolder, relativeRoot);
			if (!Directory.Exists(root))
			{
				continue;
			}
			try
			{
				foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				{
					string relative = Path.GetRelativePath(robloxFolder, file);
					string name = Path.GetFileName(relative);
					if (!index.TryGetValue(name, out List<string>? list))
					{
						list = [];
						index[name] = list;
					}
					list.Add(relative);
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not index " + relativeRoot + ": " + ex.Message);
			}
		}
		_index = index;
		_indexedFolder = robloxFolder;
		return index;
	}
}
