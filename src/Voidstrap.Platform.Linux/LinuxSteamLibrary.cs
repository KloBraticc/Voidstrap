using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Voidstrap.Platform.Linux;

public sealed record SteamShortcutRequest(
	string AppName,
	string Executable,
	string StartDirectory,
	string LaunchOptions,
	string FlatpakAppId,
	IReadOnlyDictionary<string, byte[]> Artwork);

public sealed record SteamShortcutResult(bool Success, IReadOnlyList<string> UpdatedUsers, string Message);

public static partial class LinuxSteamLibrary
{
	private const long SteamId64Base = 76561197960265728L;
	private static readonly uint[] Crc32Table = BuildCrc32Table();

	public static IReadOnlyList<string> FindSteamRoots()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string[] candidates =
		[
			Path.Combine(home, ".steam", "root"),
			Path.Combine(home, ".steam", "steam"),
			Path.Combine(home, ".local", "share", "Steam"),
			Path.Combine(home, ".steam", "debian-installation"),
			Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
			Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam")
		];

		List<string> roots = [];
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (string candidate in candidates)
		{
			string resolved = ResolveDirectory(candidate);
			if (resolved.Length == 0 || !Directory.Exists(Path.Combine(resolved, "userdata")))
				continue;
			if (seen.Add(resolved))
				roots.Add(resolved);
		}
		return roots;
	}

	public static bool IsFlatpakSteam(string root)
	{
		return root.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);
	}

	public static IReadOnlyList<string> FindUserConfigDirectories(string root)
	{
		string userdata = Path.Combine(root, "userdata");
		List<string> users = [];
		try
		{
			foreach (string directory in Directory.EnumerateDirectories(userdata))
			{
				string name = Path.GetFileName(directory);
				if (name == "0" || !ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
					continue;
				if (Directory.Exists(Path.Combine(directory, "config")))
					users.Add(directory);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}

		string? recent = FindMostRecentAccountId(root);
		if (recent is not null)
		{
			string preferred = Path.Combine(userdata, recent);
			if (users.Contains(preferred, StringComparer.Ordinal))
				return [preferred];
		}
		return users;
	}

	public static string? FindMostRecentAccountId(string root)
	{
		try
		{
			string path = Path.Combine(root, "config", "loginusers.vdf");
			if (!File.Exists(path))
				return null;
			string text = File.ReadAllText(path);
			foreach (Match match in LoginUserPattern().Matches(text))
			{
				if (!MostRecentPattern().IsMatch(match.Groups[2].Value))
					continue;
				if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long steamId) && steamId > SteamId64Base)
					return (steamId - SteamId64Base).ToString(CultureInfo.InvariantCulture);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		return null;
	}

	public static uint ComputeShortcutId(string executable, string appName)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(executable + appName);
		uint crc = 0xFFFFFFFFu;
		foreach (byte value in bytes)
			crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
		return ~crc | 0x80000000u;
	}

	public static bool IsSteamRunning()
	{
		try
		{
			foreach (string directory in Directory.EnumerateDirectories("/proc"))
			{
				string name = Path.GetFileName(directory);
				if (name.Length == 0 || !char.IsAsciiDigit(name[0]))
					continue;
				try
				{
					string command = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
					if (command is "steam" or "steamwebhelper")
						return true;
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
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

	public static SteamShortcutResult AddOrUpdate(string root, IReadOnlyList<SteamShortcutRequest> requests)
	{
		IReadOnlyList<string> users = FindUserConfigDirectories(root);
		if (users.Count == 0)
			return new SteamShortcutResult(false, [], "No signed in Steam user was found, open Steam and sign in once first");

		List<string> updated = [];
		foreach (string user in users)
		{
			string config = Path.Combine(user, "config");
			string shortcutsPath = Path.Combine(config, "shortcuts.vdf");
			SteamVdfNode document;
			if (File.Exists(shortcutsPath))
			{
				byte[] existing = File.ReadAllBytes(shortcutsPath);
				document = existing.Length == 0 ? new SteamVdfNode() : SteamBinaryVdf.Read(existing);
				string backup = shortcutsPath + ".voidstrap.bak";
				if (!File.Exists(backup))
					File.WriteAllBytes(backup, existing);
			}
			else
			{
				document = new SteamVdfNode();
			}

			SteamVdfNode shortcuts = document.GetNode("shortcuts") ?? new SteamVdfNode();
			document.Set("shortcuts", shortcuts);

			string grid = Path.Combine(config, "grid");
			foreach (SteamShortcutRequest request in requests)
			{
				uint id = ComputeShortcutId(request.Executable, request.AppName);
				string iconPath = string.Empty;
				if (request.Artwork.Count > 0)
				{
					Directory.CreateDirectory(grid);
					foreach (KeyValuePair<string, byte[]> art in request.Artwork)
					{
						string file = Path.Combine(grid, id.ToString(CultureInfo.InvariantCulture) + art.Key);
						WriteAtomic(file, art.Value);
						if (art.Key == "_icon.png")
							iconPath = file;
					}
				}
				UpsertShortcut(shortcuts, id, request, iconPath);
			}

			WriteAtomic(shortcutsPath, SteamBinaryVdf.Write(document));
			updated.Add(Path.GetFileName(user));
		}

		return new SteamShortcutResult(true, updated, "Added to Steam for " + updated.Count + " account(s)");
	}

	private static void UpsertShortcut(SteamVdfNode shortcuts, uint id, SteamShortcutRequest request, string iconPath)
	{
		int signedId = unchecked((int)id);
		SteamVdfNode? entry = null;
		int highestIndex = -1;
		foreach (KeyValuePair<string, object> item in shortcuts.Entries)
		{
			if (int.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
				highestIndex = Math.Max(highestIndex, index);
			if (item.Value is not SteamVdfNode candidate)
				continue;
			bool sameId = candidate.GetInt("appid") == signedId;
			bool sameTarget = string.Equals(candidate.GetString("AppName"), request.AppName, StringComparison.Ordinal)
				&& string.Equals(candidate.GetString("Exe"), request.Executable, StringComparison.Ordinal);
			if (sameId || sameTarget)
			{
				entry = candidate;
				break;
			}
		}

		if (entry is null)
		{
			entry = new SteamVdfNode();
			shortcuts.Entries.Add(new KeyValuePair<string, object>((highestIndex + 1).ToString(CultureInfo.InvariantCulture), entry));
			entry.Set("appid", signedId);
			entry.Set("AppName", request.AppName);
			entry.Set("Exe", request.Executable);
			entry.Set("StartDir", request.StartDirectory);
			entry.Set("icon", iconPath);
			entry.Set("ShortcutPath", string.Empty);
			entry.Set("LaunchOptions", request.LaunchOptions);
			entry.Set("IsHidden", 0);
			entry.Set("AllowDesktopConfig", 1);
			entry.Set("AllowOverlay", 1);
			entry.Set("OpenVR", 0);
			entry.Set("Devkit", 0);
			entry.Set("DevkitGameID", string.Empty);
			entry.Set("DevkitOverrideAppID", 0);
			entry.Set("LastPlayTime", 0);
			entry.Set("FlatpakAppID", request.FlatpakAppId);
			entry.Set("tags", new SteamVdfNode());
			return;
		}

		entry.Set("appid", signedId);
		entry.Set("AppName", request.AppName);
		entry.Set("Exe", request.Executable);
		entry.Set("StartDir", request.StartDirectory);
		if (iconPath.Length > 0)
			entry.Set("icon", iconPath);
		entry.Set("LaunchOptions", request.LaunchOptions);
		entry.Set("FlatpakAppID", request.FlatpakAppId);
	}

	private static void WriteAtomic(string path, byte[] data)
	{
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllBytes(temporary, data);
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static string ResolveDirectory(string path)
	{
		try
		{
			DirectoryInfo info = new(path);
			if (!info.Exists)
				return string.Empty;
			FileSystemInfo? target = info.ResolveLinkTarget(true);
			return Path.GetFullPath(target?.FullName ?? info.FullName).TrimEnd('/');
		}
		catch (IOException)
		{
			return string.Empty;
		}
		catch (UnauthorizedAccessException)
		{
			return string.Empty;
		}
	}

	private static uint[] BuildCrc32Table()
	{
		uint[] table = new uint[256];
		for (uint index = 0; index < 256; index++)
		{
			uint value = index;
			for (int bit = 0; bit < 8; bit++)
				value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
			table[index] = value;
		}
		return table;
	}

	[GeneratedRegex("\"(\\d{17})\"\\s*\\{([^}]*)\\}", RegexOptions.CultureInvariant)]
	private static partial Regex LoginUserPattern();

	[GeneratedRegex("\"MostRecent\"\\s+\"1\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex MostRecentPattern();
}
