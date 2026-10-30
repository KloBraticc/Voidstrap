using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Voidstrap.Integrations;

namespace Voidstrap.Utility;

public static class RobloxBadges
{
	public sealed record Badge(long Id, string Name, string Description, double WinRate, long AwardedCount);

	public sealed record Progress(IReadOnlyList<Badge> Badges, IReadOnlyDictionary<long, DateTime> Awarded, bool SignedIn)
	{
		public int Total => Badges.Count;

		public int Earned => Badges.Count(badge => Awarded.ContainsKey(badge.Id));
	}

	private const int MaximumPages = 20;

	private static readonly HttpClient Client = CreateClient();

	private sealed class AwardedRefusedException : Exception { }

	private static readonly TimeSpan BadgeListLifetime = TimeSpan.FromMinutes(30);

	private static readonly TimeSpan AwardedLifetime = TimeSpan.FromMinutes(3);

	private static readonly ConcurrentDictionary<long, (DateTime At, IReadOnlyList<Badge> Badges)> BadgeLists = new();

	private static readonly ConcurrentDictionary<
		(long User, long Universe),
		(DateTime At, IReadOnlyDictionary<long, DateTime> Awarded)
	> AwardedCache = new();

	private static readonly ConcurrentDictionary<long, string> IconCache = new();

	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static readonly TimeSpan StoredLifetime = TimeSpan.FromHours(6);

	private static readonly object StoreLock = new();

	private static Dictionary<string, StoredSummary>? _store;

	private static long _storeSavedTicks;

	private static long _cooldownUntilTicks;

	private static int _foreground;

	public sealed class StoredSummary
	{
		public int Earned { get; set; }

		public int Total { get; set; }

		public DateTime At { get; set; }
	}

	private static readonly SemaphoreSlim AccountGate = new(1, 1);

	private static string? _accountCookie;

	private static long _accountUserId;

	public static async Task<long> GetSignedInUserIdAsync(CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return 0;
		await AccountGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			if (_accountUserId > 0 && string.Equals(_accountCookie, cookie, StringComparison.Ordinal))
				return _accountUserId;
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, token).ConfigureAwait(false);
			_accountCookie = cookie;
			_accountUserId = account?.UserId ?? 0;
			return _accountUserId;
		}
		finally
		{
			AccountGate.Release();
		}
	}

	public static bool TryGetCached(long universeId, long userId, out Progress? progress)
	{
		progress = null;
		if (!BadgeLists.TryGetValue(universeId, out var list) || DateTime.UtcNow - list.At > BadgeListLifetime)
			return false;
		if (userId <= 0)
		{
			progress = new Progress(list.Badges, new Dictionary<long, DateTime>(), false);
			return true;
		}
		if (!AwardedCache.TryGetValue((userId, universeId), out var awarded) || DateTime.UtcNow - awarded.At > AwardedLifetime)
			return false;
		progress = new Progress(list.Badges, awarded.Awarded, true);
		return true;
	}

	public static async Task WaitForForegroundAsync(CancellationToken token)
	{
		while (Volatile.Read(ref _foreground) > 0)
			await Task.Delay(500, token).ConfigureAwait(false);
	}

	public static bool TryGetStoredSummary(long universeId, long userId, out int earned, out int total, out bool fresh)
	{
		earned = -1;
		total = -1;
		fresh = false;
		lock (StoreLock)
		{
			Dictionary<string, StoredSummary> store = LoadStore();
			if (!store.TryGetValue(StoreKey(userId, universeId), out StoredSummary? summary))
				return false;
			earned = userId > 0 ? summary.Earned : -1;
			total = summary.Total;
			fresh = DateTime.UtcNow - summary.At < StoredLifetime;
			return true;
		}
	}

	private static string StoreKey(long userId, long universeId) =>
		userId.ToString(System.Globalization.CultureInfo.InvariantCulture)
		+ ":"
		+ universeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

	private static string StorePath => Path.Combine(Paths.Cache, "BadgeProgress.json");

	private static Dictionary<string, StoredSummary> LoadStore()
	{
		if (_store != null)
			return _store;
		try
		{
			if (File.Exists(StorePath) && new FileInfo(StorePath).Length < 4 * 1024 * 1024)
				_store = JsonSerializer.Deserialize<Dictionary<string, StoredSummary>>(File.ReadAllText(StorePath));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxBadges", "Stored badge progress could not be read: " + ex.Message);
		}
		_store ??= new Dictionary<string, StoredSummary>();
		return _store;
	}

	private static void Remember(long userId, long universeId, Progress progress)
	{
		lock (StoreLock)
		{
			Dictionary<string, StoredSummary> store = LoadStore();
			store[StoreKey(progress.SignedIn ? userId : 0, universeId)] = new StoredSummary
			{
				Earned = progress.SignedIn ? progress.Earned : 0,
				Total = progress.Total,
				At = DateTime.UtcNow,
			};
			if (Environment.TickCount64 - _storeSavedTicks >= 5000)
				SaveStoreLocked();
		}
	}

	public static void SaveNow()
	{
		lock (StoreLock)
		{
			if (_store != null)
				SaveStoreLocked();
		}
	}

	private static void SaveStoreLocked()
	{
		_storeSavedTicks = Environment.TickCount64;
		try
		{
			Directory.CreateDirectory(Paths.Cache);
			string temp = StorePath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(_store));
			File.Move(temp, StorePath, true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxBadges", "Badge progress could not be saved: " + ex.Message);
		}
	}

	public static async Task<Progress> GetProgressAsync(
		long universeId,
		bool refreshAwarded,
		CancellationToken token,
		bool foreground = false
	)
	{
		if (foreground)
			Interlocked.Increment(ref _foreground);
		try
		{
			long userId = await GetSignedInUserIdAsync(token).ConfigureAwait(false);
			if (!refreshAwarded && TryGetCached(universeId, userId, out Progress? cached) && cached != null)
				return cached;
			Progress progress = await FetchProgressAsync(universeId, userId, token).ConfigureAwait(false);
			Remember(userId, universeId, progress);
			return progress;
		}
		finally
		{
			if (foreground)
				Interlocked.Decrement(ref _foreground);
		}
	}

	private static async Task<Progress> FetchProgressAsync(long universeId, long userId, CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			IReadOnlyList<Badge> badges = await GetBadgesAsync(universeId, token).ConfigureAwait(false);
			if (userId <= 0)
				return new Progress(badges, new Dictionary<long, DateTime>(), false);
			try
			{
				IReadOnlyDictionary<long, DateTime> awarded = await GetAwardedAsync(userId, universeId, badges, token)
					.ConfigureAwait(false);
				return new Progress(badges, awarded, true);
			}
			catch (AwardedRefusedException)
			{
				App.Logger.WriteLine(
					"RobloxBadges",
					"Roblox refused the earned badge list for universe " + universeId + ", showing the badges without progress"
				);
				return new Progress(badges, new Dictionary<long, DateTime>(), false);
			}
		}
		finally
		{
			Gate.Release();
		}
	}

	public static async Task<IReadOnlyDictionary<long, string>> GetIconsAsync(IReadOnlyList<long> badgeIds, CancellationToken token)
	{
		Dictionary<long, string> result = new();
		List<long> missing = new();
		foreach (long id in badgeIds)
		{
			if (IconCache.TryGetValue(id, out string? url))
				result[id] = url;
			else
				missing.Add(id);
		}
		foreach (long[] chunk in missing.Chunk(100))
		{
			string url =
				"https://thumbnails.roblox.com/v1/badges/icons?badgeIds="
				+ string.Join(',', chunk)
				+ "&size=150x150&format=Png&isCircular=false";
			using JsonDocument document = JsonDocument.Parse(await GetWithRetryAsync(url, token).ConfigureAwait(false));
			if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				continue;
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (!item.TryGetProperty("targetId", out JsonElement idElement) || !idElement.TryGetInt64(out long id))
					continue;
				if (!item.TryGetProperty("imageUrl", out JsonElement imageElement) || imageElement.ValueKind != JsonValueKind.String)
					continue;
				string? image = imageElement.GetString();
				if (string.IsNullOrEmpty(image))
					continue;
				IconCache[id] = image;
				result[id] = image;
			}
		}
		return result;
	}

	private static async Task<IReadOnlyList<Badge>> GetBadgesAsync(long universeId, CancellationToken token)
	{
		if (BadgeLists.TryGetValue(universeId, out var cached) && DateTime.UtcNow - cached.At <= BadgeListLifetime)
			return cached.Badges;
		List<Badge> badges = new();
		HashSet<long> seen = new();
		string? cursor = null;
		for (int page = 0; page < MaximumPages; page++)
		{
			string url = $"https://badges.roblox.com/v1/universes/{universeId}/badges?limit=100&sortOrder=Asc";
			if (!string.IsNullOrEmpty(cursor))
				url += "&cursor=" + Uri.EscapeDataString(cursor);
			using JsonDocument document = JsonDocument.Parse(await GetWithRetryAsync(url, token).ConfigureAwait(false));
			JsonElement root = document.RootElement;
			if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in data.EnumerateArray())
				{
					if (
						!item.TryGetProperty("id", out JsonElement idElement)
						|| !idElement.TryGetInt64(out long id)
						|| id <= 0
						|| !seen.Add(id)
					)
						continue;
					if (item.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.False)
						continue;
					string name = ReadString(item, "displayName");
					if (name.Length == 0)
						name = ReadString(item, "name");
					string description = ReadString(item, "displayDescription");
					if (description.Length == 0)
						description = ReadString(item, "description");
					double winRate = 0;
					long awardedCount = 0;
					if (item.TryGetProperty("statistics", out JsonElement statistics) && statistics.ValueKind == JsonValueKind.Object)
					{
						if (statistics.TryGetProperty("winRatePercentage", out JsonElement rate) && rate.ValueKind == JsonValueKind.Number)
							winRate = rate.GetDouble();
						if (statistics.TryGetProperty("awardedCount", out JsonElement count) && count.ValueKind == JsonValueKind.Number)
							awardedCount = count.GetInt64();
					}
					badges.Add(new Badge(id, name, description, winRate, awardedCount));
				}
			}
			cursor =
				root.TryGetProperty("nextPageCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String
					? next.GetString()
					: null;
			if (string.IsNullOrEmpty(cursor))
				break;
		}
		BadgeLists[universeId] = (DateTime.UtcNow, badges);
		return badges;
	}

	private static async Task<IReadOnlyDictionary<long, DateTime>> GetAwardedAsync(
		long userId,
		long universeId,
		IReadOnlyList<Badge> badges,
		CancellationToken token
	)
	{
		Dictionary<long, DateTime> awarded = new();
		foreach (Badge[] chunk in badges.Chunk(100))
		{
			string url =
				$"https://badges.roblox.com/v1/users/{userId}/badges/awarded-dates?badgeIds="
				+ string.Join(',', chunk.Select(badge => badge.Id));
			using JsonDocument document = JsonDocument.Parse(await GetWithRetryAsync(url, token, signedIn: true).ConfigureAwait(false));
			if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				continue;
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (!item.TryGetProperty("badgeId", out JsonElement idElement) || !idElement.TryGetInt64(out long id))
					continue;
				DateTime when =
					item.TryGetProperty("awardedDate", out JsonElement date)
					&& date.ValueKind == JsonValueKind.String
					&& date.TryGetDateTime(out DateTime parsed)
						? parsed.ToUniversalTime()
						: DateTime.MinValue;
				awarded[id] = when;
			}
		}
		AwardedCache[(userId, universeId)] = (DateTime.UtcNow, awarded);
		return awarded;
	}

	private static async Task<string> GetWithRetryAsync(string url, CancellationToken token, bool signedIn = false)
	{
		for (int attempt = 0; ; attempt++)
		{
			long waitTicks = Interlocked.Read(ref _cooldownUntilTicks) - Environment.TickCount64;
			if (waitTicks > 0)
				await Task.Delay(TimeSpan.FromMilliseconds(waitTicks), token).ConfigureAwait(false);
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			if (signedIn && RobloxCookie.Get() is { Length: > 0 } cookie)
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			HttpResponseMessage response;
			try
			{
				response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			}
			catch (HttpRequestException) when (attempt < 2)
			{
				await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), token).ConfigureAwait(false);
				continue;
			}
			using (response)
			{
				int status = (int)response.StatusCode;
				if (signedIn && status is 401 or 403)
					throw new AwardedRefusedException();
				if (status == 429 && attempt < 6)
				{
					TimeSpan wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(20 * (attempt + 1));
					if (wait < TimeSpan.FromSeconds(10))
						wait = TimeSpan.FromSeconds(10);
					if (wait > TimeSpan.FromSeconds(90))
						wait = TimeSpan.FromSeconds(90);
					Interlocked.Exchange(ref _cooldownUntilTicks, Environment.TickCount64 + (long)wait.TotalMilliseconds);
					App.Logger.WriteLine(
						"RobloxBadges",
						"Roblox asked to slow down, waiting " + (int)wait.TotalSeconds + " seconds before asking again"
					);
					continue;
				}
				if (status >= 500 && attempt < 3)
				{
					await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), token).ConfigureAwait(false);
					continue;
				}
				response.EnsureSuccessStatusCode();
				return await Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, token).ConfigureAwait(false);
			}
		}
	}

	private static HttpClient CreateClient()
	{
		HttpClient client = VpnHttpClient.Create(
			TimeSpan.FromSeconds(20),
			handler =>
			{
				handler.UseCookies = false;
				handler.AllowAutoRedirect = false;
				handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
			}
		);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/1.0");
		return client;
	}

	private static string ReadString(JsonElement item, string property)
	{
		return item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? (value.GetString() ?? string.Empty).Trim()
			: string.Empty;
	}
}
