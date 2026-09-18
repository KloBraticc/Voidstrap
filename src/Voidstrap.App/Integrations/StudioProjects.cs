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
		if (!OperatingSystem.IsWindows())
			return list;
		try
		{
			using RegistryKey? root = Registry.CurrentUser.OpenSubKey(StudioKey);
			if (root == null)
				return list;
			HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
			foreach (string subName in root.GetSubKeyNames().Where(n => n.EndsWith("rbxRecentFiles_v03", StringComparison.Ordinal)))
			{
				using RegistryKey? sub = root.OpenSubKey(subName);
				if (sub == null)
					continue;
				for (int i = 0; sub.GetValue(i + "name") is string path; i++)
				{
					string full = path.Replace('/', Path.DirectorySeparatorChar);
					if (!File.Exists(full) || !seen.Add(full))
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
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Local files read failed: " + ex.Message);
		}
		return list.OrderByDescending(p => p.Updated).ToList();
	}

	public static void Open(StudioProject project)
	{
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
		if (!OperatingSystem.IsWindows())
			return ids;
		try
		{
			using RegistryKey? root = Registry.CurrentUser.OpenSubKey(StudioKey);
			if (root == null)
				return ids;
			IEnumerable<string> keys = root.GetSubKeyNames().Where(n => n.StartsWith("rbxRecentRobloxApiGames_v02_", StringComparison.Ordinal));
			if (userId is long id && keys.Contains("rbxRecentRobloxApiGames_v02_" + id))
				keys = ["rbxRecentRobloxApiGames_v02_" + id];
			foreach (string subName in keys.ToList())
			{
				using RegistryKey? sub = root.OpenSubKey(subName);
				if (sub == null)
					continue;
				for (int i = 0; sub.GetValue("d" + i + "gameId") is string raw; i++)
				{
					if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long universeId) && !ids.Contains(universeId))
						ids.Add(universeId);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Recent games read failed: " + ex.Message);
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
