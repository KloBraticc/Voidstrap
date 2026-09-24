using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Voidstrap.Integrations;

public sealed class StudioProject
{
	public long UniverseId { get; init; }

	public long PlaceId { get; init; }

	public string Name { get; init; } = "";

	public string CreatorName { get; init; } = "";

	public DateTime? Updated { get; set; }

	public bool IsPublic { get; init; }

	public string? IconUrl { get; set; }

	public string? FilePath { get; init; }

	public bool IsLocalFile => FilePath != null;

	public string PrivacyLabel => IsPublic ? "Public" : "Private";

	public Wpf.Ui.Common.SymbolRegular PrivacyIcon => IsPublic ? Wpf.Ui.Common.SymbolRegular.Globe24 : Wpf.Ui.Common.SymbolRegular.LockClosed24;

	public string ModifiedLabel => Updated is DateTime d ? "Modified " + d.ToLocalTime().ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture) : "";

	public string StudioUri => "roblox-studio:1+launchmode:edit+task:EditPlace+placeId:" + PlaceId + "+universeId:" + UniverseId;
}

public static class StudioProjects
{
	private const string LOG_IDENT = "StudioProjects";

	private const string StudioKey = "Software\\Roblox\\RobloxStudio";

	private static readonly long[] TemplateUniverseIds = [28220420, 2464612126, 28223770, 6314775459, 6106389365, 6680068955];

	public static async Task<List<StudioProject>> GetRecentAsync(CancellationToken ct)
	{
		RobloxAccount? account = await RobloxCookie.GetAccountAsync(ct).ConfigureAwait(false);
		return await GetDetailsAsync(ReadRecentUniverseIds(account?.UserId), ct).ConfigureAwait(false);
	}

	public static async Task<List<StudioProject>> GetOwnedAsync(CancellationToken ct)
	{
		string? json = await RobloxCookie.GetAuthenticatedStringAsync("https://develop.roblox.com/v1/user/universes?isArchived=false&limit=50&sortOrder=Desc", ct).ConfigureAwait(false);
		if (json == null)
			return [];
		List<StudioProject> list = ParseUniverses(json);
		await AttachIconsAsync(list, ct).ConfigureAwait(false);
		return list.OrderByDescending(p => p.Updated).ToList();
	}

	public static Task<List<StudioProject>> GetTemplatesAsync(CancellationToken ct) => GetDetailsAsync(TemplateUniverseIds, ct);

	public static List<StudioProject> GetLocalFiles()
	{
		List<StudioProject> list = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (StudioHistoryKey key in ReadStudioKeys(name => name.EndsWith("rbxRecentFiles_v03", StringComparison.Ordinal)))
		{
			for (int i = 0; key.Values.TryGetValue(i + "name", out string? path); i++)
			{
				string? full = ToLocalPath(path, key.Prefix);
				if (full == null || !File.Exists(full) || !seen.Add(full))
					continue;
				list.Add(new StudioProject
				{
					Name = Path.GetFileNameWithoutExtension(full),
					CreatorName = Path.GetDirectoryName(full) ?? "",
					Updated = File.GetLastWriteTimeUtc(full),
					FilePath = full
				});
			}
		}
		return list.OrderByDescending(p => p.Updated).ToList();
	}

	private sealed record StudioHistoryKey(string Name, Dictionary<string, string> Values, string? Prefix);

	private static List<StudioHistoryKey> ReadStudioKeys(Func<string, bool> filter)
	{
		List<StudioHistoryKey> keys = [];
		try
		{
			if (OperatingSystem.IsWindows())
			{
				using RegistryKey? root = Registry.CurrentUser.OpenSubKey(StudioKey);
				if (root == null)
					return keys;
				foreach (string subName in root.GetSubKeyNames().Where(filter))
				{
					using RegistryKey? sub = root.OpenSubKey(subName);
					if (sub == null)
						continue;
					Dictionary<string, string> values = new(StringComparer.Ordinal);
					foreach (string valueName in sub.GetValueNames())
					{
						if (sub.GetValue(valueName) is string value)
							values[valueName] = value;
					}
					keys.Add(new StudioHistoryKey(subName, values, null));
				}
			}
			else if (OperatingSystem.IsLinux())
			{
				foreach (string prefix in VinegarPrefixes())
					keys.AddRange(ReadWineStudioKeys(prefix, filter));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Studio history read failed: " + ex.Message);
		}
		return keys;
	}

	private static IEnumerable<string> VinegarPrefixes()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		string[] roots =
		[
			Path.Combine(home, ".var", "app", "org.vinegarhq.Vinegar", "data", "vinegar", "prefixes"),
			Path.Combine(string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(home, ".local", "share") : dataHome, "vinegar", "prefixes")
		];
		foreach (string root in roots.Distinct(StringComparer.Ordinal))
		{
			if (!Directory.Exists(root))
				continue;
			foreach (string prefix in Directory.EnumerateDirectories(root))
			{
				if (File.Exists(Path.Combine(prefix, "user.reg")))
					yield return prefix;
			}
		}
	}

	private static List<StudioHistoryKey> ReadWineStudioKeys(string prefix, Func<string, bool> filter)
	{
		const string rootName = "Software\\Roblox\\RobloxStudio\\";
		List<StudioHistoryKey> keys = [];
		Dictionary<string, string>? current = null;
		foreach (string raw in File.ReadLines(Path.Combine(prefix, "user.reg")))
		{
			string line = raw.Trim();
			if (line.StartsWith('['))
			{
				current = null;
				int close = line.IndexOf(']');
				if (close < 1)
					continue;
				string name = UnescapeWine(line[1..close]);
				if (!name.StartsWith(rootName, StringComparison.OrdinalIgnoreCase))
					continue;
				string subName = name[rootName.Length..];
				if (subName.Contains('\\') || !filter(subName))
					continue;
				current = new Dictionary<string, string>(StringComparer.Ordinal);
				keys.Add(new StudioHistoryKey(subName, current, prefix));
				continue;
			}
			if (current == null || !line.StartsWith('"'))
				continue;
			int split = line.IndexOf("\"=\"", StringComparison.Ordinal);
			if (split < 1 || !line.EndsWith('"'))
				continue;
			current[UnescapeWine(line[1..split])] = UnescapeWine(line[(split + 3)..^1]);
		}
		return keys;
	}

	private static string UnescapeWine(string value)
	{
		return value.Replace("\\\\", "\u0001", StringComparison.Ordinal)
			.Replace("\\\"", "\"", StringComparison.Ordinal)
			.Replace("\u0001", "\\", StringComparison.Ordinal);
	}

	private static string? ToLocalPath(string path, string? prefix)
	{
		if (prefix == null)
			return path.Replace('/', Path.DirectorySeparatorChar);
		string windows = path.Replace('/', '\\');
		if (windows.Length < 3 || windows[1] != ':' || windows[2] != '\\')
			return null;
		string drive = Path.Combine(prefix, "dosdevices", char.ToLowerInvariant(windows[0]) + ":");
		return Path.Combine(drive, windows[3..].Replace('\\', '/'));
	}

	public static void Open(StudioProject project)
	{
		if (OperatingSystem.IsLinux() && project.FilePath is string localFile)
		{
			App.Logger.WriteLine(LOG_IDENT, "Opening a local file in Vinegar: " + project.Name);
			if (!Voidstrap.Platform.Linux.LinuxVinegarStudioRuntimeProvider.TryOpenFile(localFile, out string error))
				Frontend.ShowMessageBox("Studio could not open this file through Vinegar: " + error, System.Windows.MessageBoxImage.Warning);
			return;
		}
		string target = project.FilePath ?? project.StudioUri;
		App.Logger.WriteLine(LOG_IDENT, "Opening in Studio: " + (project.IsLocalFile ? project.Name : project.PlaceId.ToString(CultureInfo.InvariantCulture)));
		Launch("-studio \"" + target + "\"");
	}

	public static void LaunchStudio() => Launch("-studio");

	private static void Launch(string arguments)
	{
		try
		{
			string exe = Paths.LaunchExecutable;
			Process.Start(new ProcessStartInfo
			{
				FileName = exe,
				Arguments = arguments,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LOG_IDENT, ex);
		}
	}

	private static List<long> ReadRecentUniverseIds(long? userId)
	{
		List<long> ids = [];
		List<StudioHistoryKey> keys = ReadStudioKeys(n => n.StartsWith("rbxRecentRobloxApiGames_v02_", StringComparison.Ordinal));
		if (userId is long id && keys.Any(k => k.Name == "rbxRecentRobloxApiGames_v02_" + id))
			keys = keys.Where(k => k.Name == "rbxRecentRobloxApiGames_v02_" + id).ToList();
		foreach (StudioHistoryKey key in keys)
		{
			for (int i = 0; key.Values.TryGetValue("d" + i + "gameId", out string? raw); i++)
			{
				if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long universeId) && !ids.Contains(universeId))
					ids.Add(universeId);
			}
		}
		return ids;
	}

	private static async Task<List<StudioProject>> GetDetailsAsync(IReadOnlyList<long> universeIds, CancellationToken ct)
	{
		List<StudioProject> list = [];
		foreach (long[] chunk in universeIds.Chunk(50))
		{
			string url = "https://develop.roblox.com/v1/universes/multiget?" + string.Join("&", chunk.Select(id => "ids=" + id));
			try
			{
				string json = await RobloxCookie.GetAuthenticatedStringAsync(url, ct).ConfigureAwait(false)
					?? await Utility.Http.GetString(url, ct).ConfigureAwait(false);
				list.AddRange(ParseUniverses(json));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.WriteLine(LOG_IDENT, "Universe details failed: " + ex.Message);
			}
		}
		list = list.OrderBy(p => Array.IndexOf(universeIds.ToArray(), p.UniverseId)).ToList();
		await AttachIconsAsync(list, ct).ConfigureAwait(false);
		return list;
	}

	private static List<StudioProject> ParseUniverses(string json)
	{
		List<StudioProject> list = [];
		using JsonDocument doc = JsonDocument.Parse(json);
		if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
			return list;
		foreach (JsonElement u in data.EnumerateArray())
		{
			list.Add(new StudioProject
			{
				UniverseId = u.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0,
				PlaceId = u.TryGetProperty("rootPlaceId", out JsonElement place) && place.ValueKind == JsonValueKind.Number ? place.GetInt64() : 0,
				Name = u.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
				CreatorName = u.TryGetProperty("creatorName", out JsonElement creator) ? creator.GetString() ?? "" : "",
				Updated = u.TryGetProperty("updated", out JsonElement updated) && updated.TryGetDateTime(out DateTime dt) ? dt : null,
				IsPublic = u.TryGetProperty("isActive", out JsonElement active) && active.ValueKind == JsonValueKind.True
			});
		}
		return list;
	}

	private static async Task AttachIconsAsync(List<StudioProject> projects, CancellationToken ct)
	{
		await AttachModifiedAsync(projects, ct).ConfigureAwait(false);
		foreach (StudioProject[] chunk in projects.Chunk(50))
		{
			string url = "https://thumbnails.roblox.com/v1/games/icons?size=256x256&format=Png&isCircular=false&returnPolicy=PlaceHolder&universeIds=" + string.Join(",", chunk.Select(p => p.UniverseId));
			try
			{
				using JsonDocument doc = JsonDocument.Parse(await Utility.Http.GetString(url, ct).ConfigureAwait(false));
				if (!doc.RootElement.TryGetProperty("data", out JsonElement data))
					continue;
				foreach (JsonElement t in data.EnumerateArray())
				{
					long target = t.TryGetProperty("targetId", out JsonElement id) ? id.GetInt64() : 0;
					string? image = t.TryGetProperty("imageUrl", out JsonElement img) ? img.GetString() : null;
					foreach (StudioProject p in chunk.Where(p => p.UniverseId == target))
						p.IconUrl = image;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.WriteLine(LOG_IDENT, "Icon fetch failed: " + ex.Message);
			}
		}
	}

	private static async Task AttachModifiedAsync(List<StudioProject> projects, CancellationToken ct)
	{
		foreach (StudioProject[] chunk in projects.Chunk(50))
		{
			string url = "https://games.roblox.com/v1/games?universeIds=" + string.Join(",", chunk.Select(p => p.UniverseId));
			try
			{
				using JsonDocument doc = JsonDocument.Parse(await Utility.Http.GetString(url, ct).ConfigureAwait(false));
				if (!doc.RootElement.TryGetProperty("data", out JsonElement data))
					continue;
				foreach (JsonElement g in data.EnumerateArray())
				{
					long target = g.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0;
					if (!g.TryGetProperty("updated", out JsonElement updated) || !updated.TryGetDateTime(out DateTime dt))
						continue;
					foreach (StudioProject p in chunk.Where(p => p.UniverseId == target))
						p.Updated = dt;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.WriteLine(LOG_IDENT, "Modified dates failed: " + ex.Message);
			}
		}
	}
}
