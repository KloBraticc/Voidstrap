using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public static class VoidstrapMatchmaker
{
	private const string LOG_IDENT = "VoidstrapMatchmaker";

	public const int MinCandidateCount = 8;

	public const int MaxCandidateCount = 64;

	private const int FilteredCandidateCeiling = 80;

	private const int ProbeConcurrency = 8;

	private const int JoinTimeoutMs = 2500;

	private const int MaxJoinResponseBytes = 1048576;

	private const int MaxServerListPages = 5;

	private const int MaxIpLookupEntries = 1024;

	private const int MaxResolvedServerEntries = 2048;

	private const double EmptyPreferenceMs = 60.0;

	private const double FullnessTiebreakMs = 8.0;

	private const double GoodEnoughMarginMs = 4.0;

	private const double ClosestDatacenterBandMs = 12.0;

	private const double HandoffPingMs = 120.0;

	private const double HandoffFloorMultiplier = 4.0;

	private const int EarlyExitMinResults = 12;

	private const int EarlyExitClosestMatches = 6;

	private static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(25.0);

	private static readonly TimeSpan GeoCacheTtl = TimeSpan.FromHours(6.0);

	private static readonly TimeSpan IpLookupFailCooldown = TimeSpan.FromMinutes(10.0);

	private static readonly TimeSpan ResolvedServerCacheTtl = TimeSpan.FromMinutes(2.0);

	private static readonly HttpClient _geoClient = CreateClient("Voidstrap/1.0", TimeSpan.FromSeconds(8.0), acceptJson: true);

	private static readonly HttpClient _serverListClient = CreateClient("Voidstrap/1.0", TimeSpan.FromSeconds(10.0), acceptJson: true);

	private static readonly HttpClient _joinClient = CreateClient("Roblox/WinInet", TimeSpan.FromSeconds(8.0), acceptJson: true, allowAutoRedirect: false);

	private static readonly object _geoLock = new object();

	private static readonly SemaphoreSlim _geoRefreshLock = new SemaphoreSlim(1, 1);

	private static UserGeo? _cachedGeo;

	private static DateTime _cachedGeoUtc = DateTime.MinValue;

	private static int _cachedGeoNetworkVersion = -1;

	private static readonly ConcurrentDictionary<string, RobloxDatacenter> _ipLookupCache = new ConcurrentDictionary<string, RobloxDatacenter>(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentQueue<string> _ipLookupOrder = new ConcurrentQueue<string>();

	private static readonly ConcurrentDictionary<string, DateTime> _ipLookupFailUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentQueue<string> _ipLookupFailOrder = new ConcurrentQueue<string>();

	private static readonly ConcurrentDictionary<string, ResolvedServerCacheEntry> _resolvedServerCache = new ConcurrentDictionary<string, ResolvedServerCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentQueue<string> _resolvedServerOrder = new ConcurrentQueue<string>();

	private static readonly SemaphoreSlim _ipLookupLock = new SemaphoreSlim(6, 6);

	private static long _joinBackoffUntilTicks;

	private static string? _csrfToken;

	private static readonly SemaphoreSlim _csrfLock = new SemaphoreSlim(1, 1);

	private readonly record struct ResolvedServerCacheEntry(string Ip, int Port, DateTime ResolvedUtc);

	private static readonly Dictionary<string, string> _countryNormalizationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ "US", "USA" }, { "USA", "USA" }, { "United States", "USA" }, { "United States of America", "USA" },
		{ "GB", "UK" }, { "UK", "UK" }, { "United Kingdom", "UK" }, { "Great Britain", "UK" }, { "England", "UK" },
		{ "NL", "Netherlands" }, { "Netherlands", "Netherlands" }, { "Holland", "Netherlands" },
		{ "FR", "France" }, { "France", "France" },
		{ "DE", "Germany" }, { "Germany", "Germany" },
		{ "PL", "Poland" }, { "Poland", "Poland" },
		{ "IN", "India" }, { "India", "India" },
		{ "JP", "Japan" }, { "Japan", "Japan" },
		{ "SG", "Singapore" }, { "Singapore", "Singapore" },
		{ "AU", "Australia" }, { "Australia", "Australia" },
		{ "CN", "China" }, { "China", "China" }, { "HK", "China" }, { "Hong Kong", "China" },
		{ "CA", "Canada" }, { "Canada", "Canada" },
		{ "BR", "Brazil" }, { "Brazil", "Brazil" },
		{ "KR", "South Korea" }, { "South Korea", "South Korea" }, { "Korea", "South Korea" },
		{ "TW", "Taiwan" }, { "Taiwan", "Taiwan" },
		{ "ZA", "South Africa" }, { "South Africa", "South Africa" },
		{ "AE", "UAE" }, { "United Arab Emirates", "UAE" },
		{ "RU", "Russia" }, { "Russia", "Russia" },
		{ "MX", "Mexico" }, { "Mexico", "Mexico" },
		{ "CL", "Chile" }, { "Chile", "Chile" },
		{ "AR", "Argentina" }, { "Argentina", "Argentina" }
	};

	private static HttpClient CreateClient(string userAgent, TimeSpan timeout, bool acceptJson = false, bool allowAutoRedirect = true)
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(timeout, handler =>
		{
			handler.UseCookies = false;
			handler.AllowAutoRedirect = allowAutoRedirect;
		});
		client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
		if (acceptJson)
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		return client;
	}

	public static int EstimatePingMs(double distanceKm)
	{
		if (double.IsNaN(distanceKm) || distanceKm < 0.0)
			return -1;
		return Math.Clamp((int)Math.Round(5.0 + distanceKm / 75.0), 1, 999);
	}

	private static double EstimateRttMs(double distanceKm)
	{
		if (double.IsNaN(distanceKm) || distanceKm < 0.0)
			return 999.0;
		return 5.0 + distanceKm / 75.0;
	}

	public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
	{
		double dLat = ToRad(lat2 - lat1);
		double dLon = ToRad(lon2 - lon1);
		double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
		return 6371.0 * (2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a)));
	}

	private static double ToRad(double d) => d * Math.PI / 180.0;

	public static string NormalizeCountryCode(string? country)
	{
		if (string.IsNullOrWhiteSpace(country))
			return country ?? "";
		return _countryNormalizationMap.TryGetValue(country.Trim(), out string? mapped) ? mapped : country;
	}

	public static string CountryToIso2(string? country) => Voidstrap.Utility.CountryFlag.ToIso2(country);

	public static string CountryToDisplayName(string? country) => Voidstrap.Utility.CountryFlag.ToDisplayName(country);

	public static string DatacenterKey(RobloxDatacenter? dc) => dc == null ? "" : dc.City + "|" + NormalizeCountryCode(dc.Country);

	public static bool MatchesPreferredDc(RobloxDatacenter? dc, string? preferredKey)
	{
		if (dc == null || string.IsNullOrWhiteSpace(preferredKey))
			return false;
		int sep = preferredKey.IndexOf('|');
		string city = sep < 0 ? preferredKey : preferredKey.Substring(0, sep);
		string country = sep < 0 ? "" : preferredKey.Substring(sep + 1);
		if (!string.Equals(dc.City, city, StringComparison.OrdinalIgnoreCase))
			return false;
		if (string.IsNullOrEmpty(country) || string.IsNullOrEmpty(dc.Country))
			return true;
		return string.Equals(NormalizeCountryCode(country), NormalizeCountryCode(dc.Country), StringComparison.OrdinalIgnoreCase);
	}

	public static HashSet<string> GetBlockedDatacenters()
	{
		HashSet<string> blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string key in App.Settings.Prop.VoidstrapMatchmakerDisabledDatacenters ?? new List<string>())
		{
			if (!string.IsNullOrWhiteSpace(key))
				blocked.Add(key.Trim());
		}
		return blocked;
	}

	public static int ResolveEffectiveCandidateCount()
	{
		if (!App.Settings.Prop.VoidstrapMatchmakerAutoCandidates)
			return Math.Clamp(App.Settings.Prop.VoidstrapMatchmakerMaxCandidates, MinCandidateCount, MaxCandidateCount);
		int blocked = GetBlockedDatacenters().Count;
		return Math.Clamp(40 + blocked * 4, 40, MaxCandidateCount);
	}

	public static async Task<UserGeo?> GetUserGeoAsync(CancellationToken token = default)
	{
		int networkVersion = Voidstrap.Utility.VpnHttpClient.NetworkVersion;
		lock (_geoLock)
		{
			if (_cachedGeo != null && _cachedGeoNetworkVersion == networkVersion && DateTime.UtcNow - _cachedGeoUtc < GeoCacheTtl)
				return _cachedGeo;
		}
		await _geoRefreshLock.WaitAsync(token).ConfigureAwait(false);
		try
		{
			networkVersion = Voidstrap.Utility.VpnHttpClient.NetworkVersion;
			lock (_geoLock)
			{
				if (_cachedGeo != null && _cachedGeoNetworkVersion == networkVersion && DateTime.UtcNow - _cachedGeoUtc < GeoCacheTtl)
					return _cachedGeo;
			}
			UserGeo? result = await TryGeoProviderAsync("https://ipinfo.io/json", ParseIpInfo, token).ConfigureAwait(false)
				?? await TryGeoProviderAsync("https://ipwho.is/", ParseIpWhoIs, token).ConfigureAwait(false)
				?? await TryGeoProviderAsync("https://ipapi.co/json/", ParseIpApiCo, token).ConfigureAwait(false);
			if (result == null || !IsValidCoordinate(result.Lat, result.Lon))
			{
				App.Logger.WriteLine(LOG_IDENT, "All geo providers failed, cannot match by location");
				return null;
			}
			lock (_geoLock)
			{
				_cachedGeo = result;
				_cachedGeoUtc = DateTime.UtcNow;
				_cachedGeoNetworkVersion = networkVersion;
			}
			App.Logger.WriteLine(LOG_IDENT, $"User geo: {result.City}, {result.Region}, {result.Country} ({result.Lat:F2}, {result.Lon:F2})");
			return result;
		}
		finally
		{
			_geoRefreshLock.Release();
		}
	}

	private static bool IsValidCoordinate(double lat, double lon)
	{
		return double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90.0 and <= 90.0 && lon is >= -180.0 and <= 180.0;
	}

	private static async Task<UserGeo?> TryGeoProviderAsync(string url, Func<JsonElement, UserGeo?> parser, CancellationToken token)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(await Utility.Http.GetStringBoundedAsync(_geoClient, url, token, 262144).ConfigureAwait(false));
			return parser(doc.RootElement);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Geo provider failed (" + url + "): " + ex.Message);
			return null;
		}
	}

	private static UserGeo? ParseIpInfo(JsonElement root)
	{
		if (!root.TryGetProperty("loc", out JsonElement locEl))
			return null;
		string[] parts = (locEl.GetString() ?? "").Split(',');
		if (parts.Length != 2
			|| !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
			|| !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
			return null;
		return new UserGeo
		{
			Lat = lat,
			Lon = lon,
			City = ReadString(root, "city"),
			Region = ReadString(root, "region"),
			Country = ReadString(root, "country")
		};
	}

	private static UserGeo? ParseIpWhoIs(JsonElement root)
	{
		if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
			return null;
		if (!TryReadDouble(root, "latitude", out double lat) || !TryReadDouble(root, "longitude", out double lon))
			return null;
		return new UserGeo
		{
			Lat = lat,
			Lon = lon,
			City = ReadString(root, "city"),
			Region = ReadString(root, "region"),
			Country = ReadString(root, "country_code")
		};
	}

	private static UserGeo? ParseIpApiCo(JsonElement root)
	{
		if (root.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.True)
			return null;
		if (!TryReadDouble(root, "latitude", out double lat) || !TryReadDouble(root, "longitude", out double lon))
			return null;
		return new UserGeo
		{
			Lat = lat,
			Lon = lon,
			City = ReadString(root, "city"),
			Region = ReadString(root, "region"),
			Country = ReadString(root, "country")
		};
	}

	private static string ReadString(JsonElement root, string prop)
	{
		return root.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
	}

	private static bool TryReadDouble(JsonElement root, string prop, out double value)
	{
		value = 0.0;
		return root.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value);
	}

	public static async Task<RobloxDatacenter?> LookupUnknownIpAsync(string? ip, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(ip) || IsPrivateIp(ip))
			return null;
		RobloxDatacenter? mapped = RobloxDatacenterMap.Map(ip);
		if (mapped != null)
			return mapped;
		LearnedServerEntry? learned = ServerFetchStore.Lookup(ip);
		if (learned != null && (learned.Lat != 0.0 || learned.Lon != 0.0))
		{
			return new RobloxDatacenter
			{
				City = learned.City,
				Region = learned.Region,
				Country = NormalizeCountryCode(learned.Country),
				Lat = learned.Lat,
				Lon = learned.Lon
			};
		}
		if (_ipLookupCache.TryGetValue(ip, out RobloxDatacenter? cached))
			return cached;
		if (_ipLookupFailUtc.TryGetValue(ip, out DateTime failedUtc) && DateTime.UtcNow - failedUtc < IpLookupFailCooldown)
			return null;
		_ipLookupFailUtc.TryRemove(ip, out _);

		UserGeo? geo;
		await _ipLookupLock.WaitAsync(token).ConfigureAwait(false);
		try
		{
			if (_ipLookupCache.TryGetValue(ip, out RobloxDatacenter? raced))
				return raced;
			geo = await TryGeoProviderAsync("https://ipinfo.io/" + ip + "/json", ParseIpInfo, token).ConfigureAwait(false)
				?? await TryGeoProviderAsync("https://ipwho.is/" + ip, ParseIpWhoIs, token).ConfigureAwait(false)
				?? await TryGeoProviderAsync("https://ipapi.co/" + ip + "/json/", ParseIpApiCo, token).ConfigureAwait(false);
		}
		finally
		{
			_ipLookupLock.Release();
		}

		if (geo == null)
		{
			StoreIpLookupFailure(ip);
			return null;
		}
		RobloxDatacenter dc = new RobloxDatacenter
		{
			City = geo.City,
			Region = geo.Region,
			Country = NormalizeCountryCode(geo.Country),
			Lat = geo.Lat,
			Lon = geo.Lon
		};
		StoreIpLookup(ip, dc);
		App.Logger.WriteLine(LOG_IDENT, $"Resolved unknown IP {ip}: {dc.City}, {dc.Country} ({dc.Lat:F2}, {dc.Lon:F2})");
		return dc;
	}

	private static void StoreIpLookup(string ip, RobloxDatacenter datacenter)
	{
		if (_ipLookupCache.TryAdd(ip, datacenter))
			_ipLookupOrder.Enqueue(ip);
		else
			_ipLookupCache[ip] = datacenter;
		while (_ipLookupCache.Count > MaxIpLookupEntries && _ipLookupOrder.TryDequeue(out string? oldest))
			_ipLookupCache.TryRemove(oldest, out _);
	}

	private static void StoreIpLookupFailure(string ip)
	{
		if (_ipLookupFailUtc.TryAdd(ip, DateTime.UtcNow))
			_ipLookupFailOrder.Enqueue(ip);
		else
			_ipLookupFailUtc[ip] = DateTime.UtcNow;
		while (_ipLookupFailUtc.Count > MaxIpLookupEntries && _ipLookupFailOrder.TryDequeue(out string? oldest))
			_ipLookupFailUtc.TryRemove(oldest, out _);
	}

	public static double NearestDatacenterKm(UserGeo geo)
	{
		double best = double.PositiveInfinity;
		foreach (RobloxDatacenter dc in RobloxDatacenterMap.AllDatacenters())
		{
			if (dc.Lat == 0.0 && dc.Lon == 0.0)
				continue;
			double km = HaversineKm(geo.Lat, geo.Lon, dc.Lat, dc.Lon);
			if (km < best)
				best = km;
		}
		return double.IsPositiveInfinity(best) ? 0.0 : best;
	}

	public static async Task<MatchmakerCandidate?> PickBestJobIdAsync(long placeId, IEnumerable<string>? exclude = null, int maxCandidates = 40, CancellationToken token = default, string? preferredOverride = null)
	{
		try
		{
			return await PickBestCoreAsync(placeId, exclude, maxCandidates, token, preferredOverride).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			App.Logger.WriteLine(LOG_IDENT, $"Matchmaking hit the {OverallDeadline.TotalSeconds:F0}s deadline, letting Roblox pick the server");
			return null;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Matchmaking failed: " + ex.Message);
			App.Logger.WriteException(LOG_IDENT, ex);
			return null;
		}
	}

	private static async Task<MatchmakerCandidate?> PickBestCoreAsync(long placeId, IEnumerable<string>? exclude, int maxCandidates, CancellationToken token, string? preferredOverride)
	{
		System.Diagnostics.Stopwatch stageClock = System.Diagnostics.Stopwatch.StartNew();
		using CancellationTokenSource deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		deadlineCts.CancelAfter(OverallDeadline);
		CancellationToken outerToken = token;
		token = deadlineCts.Token;

		string? cookie = RobloxAuthLauncher.TryGetRobloxSecurityCookie();
		if (string.IsNullOrEmpty(cookie))
		{
			App.Logger.WriteLine(LOG_IDENT, "No Roblox cookie found, sign in to use the matchmaker");
			return null;
		}

		string preferred = (preferredOverride ?? App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter ?? "").Trim();
		HashSet<string> blocked = GetBlockedDatacenters();
		bool filtering = preferred.Length > 0 || blocked.Count > 0;
		int probeBudget = Math.Clamp(maxCandidates, MinCandidateCount, MaxCandidateCount);
		if (filtering)
			probeBudget = Math.Min(FilteredCandidateCeiling, probeBudget * 2);

		HashSet<string> excludeSet = exclude == null
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);

		bool preferEmpty = App.Settings.Prop.VoidstrapMatchmakerPreferEmpty;
		Task<UserGeo?> geoTask = GetUserGeoAsync(token);
		Task<List<ServerListItem>> poolTask = ListPublicServersAsync(placeId, cookie, MaxServerListPages, preferEmpty, token);
		Task csrfTask = PrimeCsrfAsync(placeId, cookie, token);
		try
		{
			await Task.WhenAll(geoTask, poolTask, csrfTask).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			App.Logger.WriteLine(LOG_IDENT, "Initial server search timed out");
			return null;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Initial server search failed: " + ex.Message);
			return null;
		}

		UserGeo? geo = await geoTask.ConfigureAwait(false);
		if (geo == null)
		{
			App.Logger.WriteLine(LOG_IDENT, "Cannot match without user location");
			return null;
		}
		List<ServerListItem> pool = await poolTask.ConfigureAwait(false);

		pool = pool.Where(x => !excludeSet.Contains(x.JobId)).ToList();
		if (pool.Count == 0)
		{
			App.Logger.WriteLine(LOG_IDENT, "No untried public servers available for this place");
			return null;
		}

		List<ServerListItem> probeList = pool.Count > probeBudget ? Stratify(pool, probeBudget) : pool;
		App.Logger.WriteLine(LOG_IDENT, $"Server list ready in {stageClock.ElapsedMilliseconds}ms, probing {probeList.Count} of {pool.Count} servers for place {placeId} at {ProbeConcurrency} at a time");
		long listReadyMs = stageClock.ElapsedMilliseconds;

		double nearestDcKm = NearestDatacenterKm(geo);
		double floorMs = EstimateRttMs(nearestDcKm);
		List<MatchmakerCandidate> probed;
		try
		{
			probed = await ProbeAsync(placeId, probeList, cookie, geo, preferred, blocked, preferEmpty, floorMs, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
		{
			App.Logger.WriteLine(LOG_IDENT, $"Matchmaking hit the {OverallDeadline.TotalSeconds:F0}s deadline while probing");
			return null;
		}

		if (probed.Count == 0)
		{
			App.Logger.WriteLine(LOG_IDENT, "No probed server could be resolved to a datacenter");
			return null;
		}

		App.Logger.WriteLine(LOG_IDENT, $"Probed {probed.Count} of {probeList.Count} servers in {stageClock.ElapsedMilliseconds - listReadyMs}ms, {stageClock.ElapsedMilliseconds}ms total so far");

		MatchmakerCandidate? closestOverall = probed.OrderBy(c => c.DistanceKm).FirstOrDefault();
		App.Logger.WriteLine(LOG_IDENT, "Datacenters seen: " + string.Join(", ", probed
			.GroupBy(c => DatacenterKey(c.Datacenter))
			.Select(g => $"{g.First().Datacenter?.City}({EstimatePingMs(g.Min(c => c.DistanceKm))}ms x{g.Count()})")));

		List<MatchmakerCandidate> allowed = probed.Where(c => !blocked.Contains(DatacenterKey(c.Datacenter))).ToList();
		if (allowed.Count == 0)
		{
			App.Logger.WriteLine(LOG_IDENT, "Every probed server was in a blocked datacenter, nothing to pick");
			return null;
		}

		string? blockedClosestCity = null;
		double blockedClosestKm = 0.0;
		if (closestOverall?.Datacenter != null && blocked.Contains(DatacenterKey(closestOverall.Datacenter)))
		{
			blockedClosestCity = closestOverall.Datacenter.City;
			blockedClosestKm = closestOverall.DistanceKm;
			App.Logger.WriteLine(LOG_IDENT, $"Closest datacenter {blockedClosestCity} ({EstimatePingMs(blockedClosestKm)}ms) is blocked, skipping it");
		}

		if (preferred.Length > 0)
		{
			List<MatchmakerCandidate> inPreferred = allowed.Where(c => MatchesPreferredDc(c.Datacenter, preferred)).ToList();
			if (inPreferred.Count > 0)
			{
				App.Logger.WriteLine(LOG_IDENT, $"Preferred datacenter matched {inPreferred.Count} servers");
				allowed = inPreferred;
			}
			else
			{
				App.Logger.WriteLine(LOG_IDENT, "Preferred datacenter had no servers in this list, falling back to the closest allowed one");
			}
		}
		else
		{
			double closestRtt = allowed.Min(c => (double)c.EstimatedPingMs);
			List<MatchmakerCandidate> close = allowed
				.Where(c => c.EstimatedPingMs <= closestRtt + ClosestDatacenterBandMs)
				.ToList();
			if (close.Count > 0)
				allowed = close;
			if (!preferEmpty)
			{
				List<MatchmakerCandidate> active = allowed.Where(c => c.Playing >= 4).ToList();
				if (active.Count > 0)
					allowed = active;
				List<MatchmakerCandidate> headroom = allowed.Where(HasSafeJoinHeadroom).ToList();
				if (headroom.Count > 0)
					allowed = headroom;
			}
		}

		MatchmakerCandidate winner = allowed.OrderBy(c => c.Score).First();
		winner = new MatchmakerCandidate
		{
			JobId = winner.JobId,
			MachineAddress = winner.MachineAddress,
			Port = winner.Port,
			Datacenter = winner.Datacenter,
			DistanceKm = winner.DistanceKm,
			Playing = winner.Playing,
			MaxPlayers = winner.MaxPlayers,
			Ping = winner.Ping,
			EstimatedPingMs = winner.EstimatedPingMs,
			Score = winner.Score,
			BlockedClosestCity = blockedClosestCity,
			BlockedClosestDistanceKm = blockedClosestKm
		};

		string players = winner.MaxPlayers > 0 ? $"{winner.Playing}/{winner.MaxPlayers} players" : "player count unknown";
		App.Logger.WriteLine(LOG_IDENT, $"Winner: {winner.DatacenterName}, about {winner.EstimatedPingMs}ms, {players}, JobId {winner.JobId}");

		bool winnerIsPreferred = preferred.Length > 0 && MatchesPreferredDc(winner.Datacenter, preferred);
		if (ShouldHandOff(winner.EstimatedPingMs, floorMs, winnerIsPreferred))
		{
			App.Logger.WriteLine(LOG_IDENT, $"Every server found is far away, the best is about {winner.EstimatedPingMs}ms, handing off to Roblox matchmaking so it can try a fresh nearby server");
			return null;
		}
		return winner;
	}

	internal static bool ShouldHandOff(double winnerPingMs, double floorMs, bool winnerIsPreferred)
	{
		if (winnerIsPreferred)
			return false;
		return winnerPingMs > HandoffPingMs && winnerPingMs > floorMs * HandoffFloorMultiplier;
	}

	private static bool HasSafeJoinHeadroom(MatchmakerCandidate candidate)
	{
		if (candidate.MaxPlayers <= 0)
			return true;
		int open = candidate.MaxPlayers - candidate.Playing;
		return open >= Math.Max(2, (int)Math.Ceiling(candidate.MaxPlayers * 0.08));
	}

	private static List<ServerListItem> Stratify(List<ServerListItem> items, int target)
	{
		List<ServerListItem> picked = new List<ServerListItem>(target);
		double step = (double)items.Count / target;
		for (int i = 0; i < target; i++)
		{
			int idx = Math.Min(items.Count - 1, (int)Math.Floor(i * step));
			picked.Add(items[idx]);
		}
		return picked;
	}

	private static double PopulationPenaltyMs(int playing, int maxPlayers, bool preferEmpty)
	{
		double fullness = maxPlayers > 0 ? Math.Clamp((double)playing / maxPlayers, 0.0, 1.0) : 0.5;
		if (preferEmpty)
			return fullness * EmptyPreferenceMs + JoinHeadroomPenaltyMs(playing, maxPlayers);
		double sparsePenalty = playing switch
		{
			<= 0 => 100.0,
			1 => 80.0,
			2 => 60.0,
			3 => 45.0,
			_ => fullness < 0.15 ? (0.15 - fullness) * 80.0 : 0.0
		};
		double crowdedPenalty = fullness > 0.85 ? (fullness - 0.85) * 120.0 : 0.0;
		double fullnessTiebreak = Math.Abs(fullness - 0.65) * FullnessTiebreakMs;
		return sparsePenalty + crowdedPenalty + fullnessTiebreak + JoinHeadroomPenaltyMs(playing, maxPlayers);
	}

	private static double JoinHeadroomPenaltyMs(int playing, int maxPlayers)
	{
		if (maxPlayers <= 0)
			return 0.0;
		int open = maxPlayers - playing;
		if (open <= 1)
			return 120.0;
		if (open == 2)
			return 35.0;
		return 0.0;
	}

	private static async Task<List<MatchmakerCandidate>> ProbeAsync(long placeId, List<ServerListItem> servers, string cookie, UserGeo geo, string preferred, HashSet<string> blocked, bool preferEmpty, double floorMs, CancellationToken token)
	{
		ConcurrentBag<MatchmakerCandidate> results = new ConcurrentBag<MatchmakerCandidate>();
		int goodEnough = 0;
		int resultCount = 0;
		using CancellationTokenSource earlyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		CancellationToken localToken = earlyCts.Token;
		ConcurrentQueue<ServerListItem> queue = new ConcurrentQueue<ServerListItem>(servers);

		async Task WorkerAsync()
		{
			while (!localToken.IsCancellationRequested && queue.TryDequeue(out ServerListItem? sv))
			{
				(string Ip, int Port)? resolved;
				try
				{
					resolved = await ResolveServerAsync(placeId, sv.JobId, cookie, localToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				if (!resolved.HasValue)
					continue;

				RobloxDatacenter? dc;
				try
				{
					dc = await LookupUnknownIpAsync(resolved.Value.Ip, localToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				if (dc == null)
				{
					ServerFetchStore.RecordSighting(resolved.Value.Ip);
					continue;
				}

				string normalized = NormalizeCountryCode(dc.Country);
				if (!string.Equals(normalized, dc.Country, StringComparison.Ordinal))
				{
					dc = new RobloxDatacenter
					{
						City = dc.City,
						Region = dc.Region,
						Country = normalized,
						Lat = dc.Lat,
						Lon = dc.Lon
					};
				}
				ServerFetchStore.RecordSighting(resolved.Value.Ip, dc.City, dc.Region, dc.Country, dc.Lat, dc.Lon);

				double km = HaversineKm(geo.Lat, geo.Lon, dc.Lat, dc.Lon);
				double geographicPing = EstimateRttMs(km);
				double learnedPing = ServerFetchStore.GetMedianPing(resolved.Value.Ip, out int learnedSamples);
				double learnedWeight = learnedSamples <= 0 || learnedPing < 1.0 || learnedPing > 999.0 ? 0.0 : Math.Min(0.75, 0.2 + learnedSamples * 0.05);
				double effectivePing = learnedWeight > 0.0 ? geographicPing * (1.0 - learnedWeight) + learnedPing * learnedWeight : geographicPing;
				results.Add(new MatchmakerCandidate
				{
					JobId = sv.JobId,
					MachineAddress = resolved.Value.Ip,
					Port = resolved.Value.Port,
					Datacenter = dc,
					DistanceKm = km,
					Playing = sv.Playing,
					MaxPlayers = sv.MaxPlayers,
					Ping = sv.Ping,
					EstimatedPingMs = Math.Clamp((int)Math.Round(effectivePing), 1, 999),
					Score = effectivePing + PopulationPenaltyMs(sv.Playing, sv.MaxPlayers, preferEmpty)
				});
				Interlocked.Increment(ref resultCount);

				bool usable = !blocked.Contains(DatacenterKey(dc));
				bool onTarget = preferred.Length > 0 ? MatchesPreferredDc(dc, preferred) : EstimateRttMs(km) <= floorMs + ClosestDatacenterBandMs;
				bool populated = preferEmpty || sv.Playing >= 4 || sv.MaxPlayers > 0 && sv.Playing >= Math.Ceiling(sv.MaxPlayers * 0.15);
				int requiredMatches = preferred.Length > 0 ? 3 : EarlyExitClosestMatches;
				if (usable && onTarget && populated && Interlocked.Increment(ref goodEnough) >= requiredMatches && Volatile.Read(ref resultCount) >= EarlyExitMinResults)
				{
					earlyCts.Cancel();
					return;
				}
			}
		}

		Task[] workers = Enumerable.Range(0, Math.Min(ProbeConcurrency, Math.Max(1, servers.Count))).Select(_ => WorkerAsync()).ToArray();
		try
		{
			await Task.WhenAll(workers).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (earlyCts.IsCancellationRequested && !token.IsCancellationRequested)
		{
		}

		if (earlyCts.IsCancellationRequested && !token.IsCancellationRequested)
			App.Logger.WriteLine(LOG_IDENT, $"Found enough good servers after {results.Count} probes, stopping early");

		return results.ToList();
	}

	private static bool UseV2()
	{
		return App.Settings.Prop.VoidstrapMatchmakerGamejoinApiVersion >= 2;
	}

	private static async Task PrimeCsrfAsync(long placeId, string cookie, CancellationToken token)
	{
		if (!string.IsNullOrEmpty(_csrfToken))
			return;
		await _csrfLock.WaitAsync(token).ConfigureAwait(false);
		try
		{
			if (!string.IsNullOrEmpty(_csrfToken))
				return;
			using HttpRequestMessage req = BuildJoinRequest(UseV2(), placeId, Guid.Empty.ToString(), cookie, null);
			using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeoutCts.CancelAfter(JoinTimeoutMs);
			using HttpResponseMessage res = await _joinClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
			if (res.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values))
				_csrfToken = values.FirstOrDefault();
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "CSRF prime failed, probes will fetch it on demand: " + ex.Message);
		}
		finally
		{
			_csrfLock.Release();
		}
	}

	private static HttpRequestMessage BuildJoinRequest(bool useV2, long placeId, string jobId, string cookie, string? csrf)
	{
		string url = useV2 ? "https://gamejoin.roblox.com/v2/join-game-instance" : "https://gamejoin.roblox.com/v1/join-game-instance";
		string body = useV2
			? JsonSerializer.Serialize(new { placeId, gameId = jobId, gameJoinAttemptId = Guid.NewGuid().ToString(), joinOrigin = "VoidstrapFetchInfo" })
			: JsonSerializer.Serialize(new { placeId, gameId = jobId, gameJoinAttemptId = Guid.NewGuid().ToString() });
		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
		req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
		req.Headers.Referrer = new Uri("https://www.roblox.com/");
		if (!string.IsNullOrEmpty(csrf))
			req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
		req.Content = new StringContent(body, Encoding.UTF8, "application/json");
		return req;
	}

	private static async Task WaitForJoinBackoffAsync(CancellationToken token)
	{
		long until = Interlocked.Read(ref _joinBackoffUntilTicks);
		long now = DateTime.UtcNow.Ticks;
		if (until > now)
			await Task.Delay(TimeSpan.FromTicks(until - now) + TimeSpan.FromMilliseconds(Random.Shared.Next(25, 201)), token).ConfigureAwait(false);
	}

	private static void SetJoinBackoff(TimeSpan duration)
	{
		if (duration > TimeSpan.FromSeconds(8.0))
			duration = TimeSpan.FromSeconds(8.0);
		long until = DateTime.UtcNow.Ticks + duration.Ticks;
		while (true)
		{
			long existing = Interlocked.Read(ref _joinBackoffUntilTicks);
			if (until <= existing || Interlocked.CompareExchange(ref _joinBackoffUntilTicks, until, existing) == existing)
				return;
		}
	}

	private static async Task<(string Ip, int Port)?> ResolveServerAsync(long placeId, string jobId, string cookie, CancellationToken token)
	{
		string cacheKey = placeId.ToString(CultureInfo.InvariantCulture) + ":" + jobId;
		if (_resolvedServerCache.TryGetValue(cacheKey, out ResolvedServerCacheEntry cached))
		{
			if (DateTime.UtcNow - cached.ResolvedUtc < ResolvedServerCacheTtl)
				return (cached.Ip, cached.Port);
			_resolvedServerCache.TryRemove(cacheKey, out _);
		}
		bool primaryV2 = UseV2();
		ResolveAttempt result = await AttemptResolveAsync(primaryV2, placeId, jobId, cookie, token).ConfigureAwait(false);
		if (result.HasValue)
		{
			StoreResolvedServer(cacheKey, result.Ip, result.Port);
			return (result.Ip, result.Port);
		}
		if (!result.AllowAlternate || token.IsCancellationRequested)
			return null;
		ResolveAttempt alternate = await AttemptResolveAsync(!primaryV2, placeId, jobId, cookie, token).ConfigureAwait(false);
		if (!alternate.HasValue)
			return null;
		StoreResolvedServer(cacheKey, alternate.Ip, alternate.Port);
		return (alternate.Ip, alternate.Port);
	}

	public static async Task<(string Ip, int Port)?> ResolveJobServerAsync(long placeId, string? jobId, CancellationToken token = default)
	{
		if (placeId <= 0 || string.IsNullOrWhiteSpace(jobId))
			return null;
		string? cookie = RobloxAuthLauncher.TryGetRobloxSecurityCookie();
		if (string.IsNullOrEmpty(cookie))
			return null;
		try
		{
			await PrimeCsrfAsync(placeId, cookie, token).ConfigureAwait(false);
			return await ResolveServerAsync(placeId, jobId, cookie, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Current server resolution failed: " + ex.Message);
			return null;
		}
	}

	private static void StoreResolvedServer(string cacheKey, string ip, int port)
	{
		if (_resolvedServerCache.TryAdd(cacheKey, new ResolvedServerCacheEntry(ip, port, DateTime.UtcNow)))
			_resolvedServerOrder.Enqueue(cacheKey);
		else
			_resolvedServerCache[cacheKey] = new ResolvedServerCacheEntry(ip, port, DateTime.UtcNow);
		while (_resolvedServerCache.Count > MaxResolvedServerEntries && _resolvedServerOrder.TryDequeue(out string? oldest))
			_resolvedServerCache.TryRemove(oldest, out _);
	}

	private readonly struct ResolveAttempt
	{
		public string Ip { get; }
		public int Port { get; }
		public bool HasValue { get; }
		public bool AllowAlternate { get; }

		public ResolveAttempt(string ip, int port, bool hasValue, bool allowAlternate)
		{
			Ip = ip;
			Port = port;
			HasValue = hasValue;
			AllowAlternate = allowAlternate;
		}
	}

	private static long _lastProbeFailureLogTicks;

	private static void LogProbeFailure(HttpStatusCode status)
	{
		long now = DateTime.UtcNow.Ticks;
		long last = Interlocked.Read(ref _lastProbeFailureLogTicks);
		if (now - last < TimeSpan.TicksPerSecond * 5)
			return;
		if (Interlocked.CompareExchange(ref _lastProbeFailureLogTicks, now, last) != last)
			return;
		string hint = status switch
		{
			HttpStatusCode.Unauthorized => "your Roblox sign in is missing or expired, sign in again through Settings",
			HttpStatusCode.Forbidden => "Roblox rejected the request token",
			HttpStatusCode.TooManyRequests => "Roblox is rate limiting the probes",
			_ => "Roblox refused the probe"
		};
		App.Logger.WriteLine(LOG_IDENT, "Server probe returned HTTP " + (int)status + ", " + hint);
	}

	private static async Task<ResolveAttempt> AttemptResolveAsync(bool useV2, long placeId, string jobId, string cookie, CancellationToken token)
	{
		for (int attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				await WaitForJoinBackoffAsync(token).ConfigureAwait(false);
				using HttpRequestMessage req = BuildJoinRequest(useV2, placeId, jobId, cookie, _csrfToken);
				using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
				timeoutCts.CancelAfter(JoinTimeoutMs);
				using HttpResponseMessage res = await _joinClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

				if (res.StatusCode == HttpStatusCode.Forbidden && res.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values))
				{
					_csrfToken = values.FirstOrDefault();
					continue;
				}
				if (res.StatusCode == HttpStatusCode.TooManyRequests)
				{
					SetJoinBackoff(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1.5));
					return new ResolveAttempt("", 0, false, false);
				}
				if (!res.IsSuccessStatusCode)
				{
					LogProbeFailure(res.StatusCode);
					bool alternate = res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
						or HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
						or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
					return new ResolveAttempt("", 0, false, alternate);
				}

				using JsonDocument? doc = await ReadJoinResponseAsync(res, timeoutCts.Token).ConfigureAwait(false);
				if (doc == null || !HasJoinScript(doc.RootElement))
					return new ResolveAttempt("", 0, false, true);
				(string ip, int port) = ParseJoinResponse(doc.RootElement);
				if (string.IsNullOrEmpty(ip) || IsPrivateIp(ip))
					return new ResolveAttempt("", 0, false, true);
				return new ResolveAttempt(ip, port, true, false);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				return new ResolveAttempt("", 0, false, true);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Probe of " + jobId + " failed: " + ex.Message);
				return new ResolveAttempt("", 0, false, true);
			}
		}
		return new ResolveAttempt("", 0, false, false);
	}

	private static async Task<JsonDocument?> ReadJoinResponseAsync(HttpResponseMessage res, CancellationToken token)
	{
		string contentType = res.Content.Headers.ContentType?.MediaType ?? "";
		if (contentType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
			return await ReadJoinEventStreamAsync(res, token).ConfigureAwait(false);
		if (res.Content.Headers.ContentLength is long length && length > MaxJoinResponseBytes)
			return null;
		using Stream stream = await res.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		using MemoryStream buffer = new MemoryStream();
		byte[] chunk = new byte[16384];
		while (true)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
			if (read == 0)
				break;
			if (buffer.Length + read > MaxJoinResponseBytes)
				return null;
			buffer.Write(chunk, 0, read);
		}
		string payload = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
		if (string.IsNullOrWhiteSpace(payload))
			return null;
		try
		{
			return JsonDocument.Parse(payload);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static async Task<JsonDocument?> ReadJoinEventStreamAsync(HttpResponseMessage res, CancellationToken token)
	{
		using Stream stream = await res.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		using StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
		StringBuilder block = new StringBuilder();
		int total = 0;
		while (true)
		{
			string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
			if (line == null)
				return TryParseJoinEventBlock(block.ToString());
			total += line.Length + 1;
			if (total > MaxJoinResponseBytes)
				return null;
			if (line.Length == 0)
			{
				JsonDocument? parsed = TryParseJoinEventBlock(block.ToString());
				if (parsed != null && HasJoinScript(parsed.RootElement))
					return parsed;
				parsed?.Dispose();
				block.Clear();
				continue;
			}
			block.AppendLine(line);
		}
	}

	private static JsonDocument? TryParseJoinEventBlock(string block)
	{
		string payload = ExtractServerSentEventJson(block);
		if (string.IsNullOrWhiteSpace(payload))
			return null;
		try
		{
			return JsonDocument.Parse(payload);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string ExtractServerSentEventJson(string text)
	{
		if (string.IsNullOrEmpty(text))
			return string.Empty;
		string firstData = string.Empty;
		foreach (string block in text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None))
		{
			if (string.IsNullOrWhiteSpace(block))
				continue;
			string eventName = "message";
			List<string> dataLines = new List<string>();
			foreach (string rawLine in block.Split('\n'))
			{
				string line = rawLine.TrimEnd('\r');
				if (line.Length == 0 || line[0] == ':')
					continue;
				int sep = line.IndexOf(':');
				string field = sep < 0 ? line : line.Substring(0, sep);
				string val = sep < 0 ? string.Empty : line.Substring(sep + 1).TrimStart(' ');
				if (field == "event")
					eventName = val;
				else if (field == "data")
					dataLines.Add(val);
			}
			string data = string.Join("\n", dataLines);
			if (string.IsNullOrWhiteSpace(data))
				continue;
			if (firstData.Length == 0)
				firstData = data;
			if (eventName == "ResponseReady")
				return data;
		}
		return firstData;
	}

	private static bool HasJoinScript(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
			return false;
		if (TryGetObject(root, "joinScript").HasValue)
			return true;
		return TryGetArray(root, "UdmuxEndpoints").HasValue || !string.IsNullOrEmpty(TryGetString(root, "MachineAddress"));
	}

	private static (string Ip, int Port) ParseJoinResponse(JsonElement root)
	{
		string? ip = null;
		int port = 0;
		JsonElement? joinScript = TryGetObject(root, "joinScript");
		JsonElement? endpoints = TryGetArray(root, "UdmuxEndpoints") ?? (joinScript.HasValue ? TryGetArray(joinScript.Value, "UdmuxEndpoints") : null);
		if (endpoints.HasValue && endpoints.Value.GetArrayLength() > 0)
		{
			JsonElement first = endpoints.Value[0];
			if (first.ValueKind == JsonValueKind.Object)
			{
				ip = TryGetString(first, "Address");
				int? p = TryGetInt(first, "Port");
				if (p.HasValue && p.Value > 0)
					port = p.Value;
			}
		}
		if (string.IsNullOrEmpty(ip))
		{
			ip = TryGetString(root, "MachineAddress");
			if (string.IsNullOrEmpty(ip) && joinScript.HasValue)
				ip = TryGetString(joinScript.Value, "MachineAddress");
		}
		if (port == 0)
		{
			int? p = TryGetInt(root, "ServerPort") ?? (joinScript.HasValue ? TryGetInt(joinScript.Value, "ServerPort") : null);
			if (p.HasValue)
				port = p.Value;
		}
		return (ip ?? "", port);
	}

	private static JsonElement? TryGetObject(JsonElement el, string prop)
	{
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Object)
			return v;
		return null;
	}

	private static JsonElement? TryGetArray(JsonElement el, string prop)
	{
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Array)
			return v;
		return null;
	}

	private static string? TryGetString(JsonElement el, string prop)
	{
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String)
			return v.GetString();
		return null;
	}

	private static int? TryGetInt(JsonElement el, string prop)
	{
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
			return i;
		return null;
	}

	private static async Task<List<ServerListItem>> ListPublicServersAsync(long placeId, string cookie, int maxPages, bool preferEmpty, CancellationToken token)
	{
		List<ServerListItem> items = new List<ServerListItem>();
		HashSet<string> seenJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string? cursor = null;
		int[] backoffMs = { 0, 750, 2000 };

		for (int page = 0; page < maxPages; page++)
		{
			string sortOrder = preferEmpty ? "Asc" : "Desc";
			string url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?excludeFullGames=true&limit=100&sortOrder={sortOrder}";
			if (!string.IsNullOrEmpty(cursor))
				url += "&cursor=" + Uri.EscapeDataString(cursor);

			string? nextCursor = null;
			bool pageOk = false;

			for (int attempt = 0; attempt < backoffMs.Length && !pageOk; attempt++)
			{
				if (backoffMs[attempt] > 0)
				{
					App.Logger.WriteLine(LOG_IDENT, $"Server list rate limited, waiting {backoffMs[attempt]}ms");
					await Task.Delay(backoffMs[attempt], token).ConfigureAwait(false);
				}
				try
				{
					using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
					if (!string.IsNullOrEmpty(cookie))
						req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
					using HttpResponseMessage res = await _serverListClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

					if (res.StatusCode == HttpStatusCode.TooManyRequests)
					{
						TimeSpan retryAfter = res.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
						if (retryAfter > TimeSpan.Zero && retryAfter <= TimeSpan.FromSeconds(5.0))
							await Task.Delay(retryAfter, token).ConfigureAwait(false);
						if (attempt == backoffMs.Length - 1)
						{
							App.Logger.WriteLine(LOG_IDENT, $"Server list page {page + 1} still HTTP {(int)res.StatusCode} after retries");
							return SortServers(items, preferEmpty);
						}
						continue;
					}
					if (res.StatusCode == HttpStatusCode.Forbidden)
					{
						App.Logger.WriteLine(LOG_IDENT, $"Server list page {page + 1} refused access, stopping the server scan");
						return SortServers(items, preferEmpty);
					}
					if (!res.IsSuccessStatusCode)
					{
						App.Logger.WriteLine(LOG_IDENT, $"Server list page {page + 1} returned HTTP {(int)res.StatusCode}");
						return SortServers(items, preferEmpty);
					}

					using JsonDocument doc = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, 4 * 1024 * 1024, token).ConfigureAwait(false));
					if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement el in data.EnumerateArray())
						{
							string? jobId = TryGetString(el, "id");
							if (string.IsNullOrEmpty(jobId) || !seenJobIds.Add(jobId))
								continue;
							int playing = TryGetInt(el, "playing") ?? 0;
							int maxPlayers = TryGetInt(el, "maxPlayers") ?? 0;
							int ping = TryGetInt(el, "ping") ?? -1;
							if (maxPlayers > 0 && playing >= maxPlayers)
								continue;
							items.Add(new ServerListItem
							{
								JobId = jobId,
								Playing = playing,
								MaxPlayers = maxPlayers,
								Ping = ping
							});
						}
					}
					nextCursor = TryGetString(doc.RootElement, "nextPageCursor");
					pageOk = true;
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (OperationCanceledException)
				{
					return SortServers(items, preferEmpty);
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine(LOG_IDENT, $"Server list page {page + 1} threw: {ex.Message}");
					if (attempt == backoffMs.Length - 1)
						return SortServers(items, preferEmpty);
				}
			}

			if (string.IsNullOrEmpty(nextCursor))
				break;
			cursor = nextCursor;
		}
		return SortServers(items, preferEmpty);
	}

	private static List<ServerListItem> SortServers(List<ServerListItem> items, bool preferEmpty)
	{
		return preferEmpty
			? items.OrderBy(x => x.Playing).ToList()
			: items.OrderByDescending(x => x.MaxPlayers > 0 ? (double)x.Playing / x.MaxPlayers : x.Playing > 0 ? 0.5 : 0.0).ThenByDescending(x => x.Playing).ToList();
	}

	internal static bool IsPrivateIp(string? ip)
	{
		if (string.IsNullOrWhiteSpace(ip))
			return true;
		if (!IPAddress.TryParse(ip, out IPAddress? address))
			return false;
		if (IPAddress.IsLoopback(address))
			return true;
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			byte[] v6 = address.GetAddressBytes();
			return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (v6[0] & 0xFE) == 0xFC;
		}
		if (address.AddressFamily != AddressFamily.InterNetwork)
			return true;
		byte[] b = address.GetAddressBytes();
		if (b[0] == 0 || b[0] == 10 || b[0] == 127)
			return true;
		if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
			return true;
		if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
			return true;
		if (b[0] == 192 && b[1] == 168)
			return true;
		if (b[0] == 169 && b[1] == 254)
			return true;
		if (b[0] >= 224)
			return true;
		return false;
	}
}
