using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public static class ServerFetchStore
{
	private const string LOG_IDENT = "ServerFetchStore";

	public const string OfficialPresetUrl = "https://github.com/KloBraticc/Voidstrap/blob/main/assets/Datacenters/ServerFetch.json";
	public const string FallbackPresetUrl = "https://github.com/KloBraticc/Voidstrap/blob/main/assets/Datacenters/ServerFetch.json";

	private const int MaxPingSamples = 25;

	private const int MaxIpsPerEntry = 100;

	private const int MaxServerEntries = 4096;

	private const int MaxRemoteBytes = 4194304;

	private const int MaxStoreBytes = 16777216;

	private static readonly HttpClient RemoteClient = CreateRemoteClient();

	private static readonly object _lock = new object();

	private static ServerFetchData _data = new ServerFetchData();

	private static bool _loaded;

	private static Timer? _saveTimer;
	private static readonly JsonSerializerOptions StoreJsonOptions = new(Voidstrap.Utility.JsonOptions.Tolerant)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static string FolderPath => Paths.ServerFetch;

	public static string FilePath => Path.Combine(FolderPath, "Data.json");

	private static HttpClient CreateRemoteClient()
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15L));
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/1.0");
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
		return client;
	}

	public static void EnsureLoaded()
	{
		if (_loaded)
		{
			return;
		}
		lock (_lock)
		{
			if (!_loaded)
			{
				Load();
				PrunePrivateCidrs();
				PruneStalePreSeeded();
				RepairCountries();
				RobloxDatacenterMap.AddCidrEntries(_data.Servers.Select(item => new SeedCidrEntry
				{
					Cidr = string.IsNullOrWhiteSpace(item.Value.Cidr) ? item.Key : item.Value.Cidr,
					City = item.Value.City,
					Region = item.Value.Region,
					Country = item.Value.Country,
					Lat = item.Value.Lat,
					Lon = item.Value.Lon
				}));
				_loaded = true;
			}
		}
	}

	private static void RepairCountries()
	{
		int repaired = 0;
		foreach (KeyValuePair<string, LearnedServerEntry> server in _data.Servers)
		{
			string corrected = RobloxDatacenterMap.ResolveCountry(server.Value.City, server.Value.Country);
			if (string.Equals(corrected, server.Value.Country ?? "", StringComparison.Ordinal))
				continue;
			App.Logger.WriteLine("ServerFetchStore", $"Corrected country for {server.Key}: '{server.Value.Country}' is now '{corrected}'");
			server.Value.Country = corrected;
			repaired++;
		}
		if (repaired > 0)
			SaveThrottled();
	}

	private static void PruneStalePreSeeded()
	{
		try
		{
			HashSet<string> hashSet = new HashSet<string>(from s in RobloxDatacenterMap.AllSeedEntries()
				select s.Cidr, StringComparer.OrdinalIgnoreCase);
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, LearnedServerEntry> server in _data.Servers)
			{
				if (server.Value.SeenCount <= 0 && (server.Value.IPs == null || server.Value.IPs.Count == 0) && hashSet.Contains(server.Key))
				{
					list.Add(server.Key);
				}
			}
			if (list.Count <= 0)
			{
				return;
			}
			foreach (string item in list)
			{
				_data.Servers.Remove(item);
			}
			App.Logger.WriteLine("ServerFetchStore", $"Pruned {list.Count} unseen preseeded entries on load");
			SaveNow();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerFetchStore", "PruneStalePreSeeded failed: " + ex.Message);
		}
	}

	private static void PrunePrivateCidrs()
	{
		try
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, LearnedServerEntry> server in _data.Servers)
			{
				if (VoidstrapMatchmaker.IsPrivateIp(server.Key.Split('/')[0]))
				{
					list.Add(server.Key);
				}
			}
			if (list.Count <= 0)
			{
				return;
			}
			foreach (string item in list)
			{
				_data.Servers.Remove(item);
			}
			App.Logger.WriteLine("ServerFetchStore", $"Pruned {list.Count} private/internal CIDR(s) from learned datacenters");
			SaveThrottled();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerFetchStore", "PrunePrivateCidrs failed: " + ex.Message);
		}
	}

	private static void Load()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				Directory.CreateDirectory(FolderPath);
				_data = new ServerFetchData();
				return;
			}
			if (!Voidstrap.Utility.JsonFile.TryLoad<ServerFetchData>(FilePath, StoreJsonOptions, out ServerFetchData? serverFetchData, out bool recovered, out Exception? failure, MaxStoreBytes) || serverFetchData == null)
				throw failure ?? new InvalidDataException("Server fetch store is invalid");
			_data = NormalizeData(serverFetchData);
			if (recovered)
				App.Logger.WriteLine("ServerFetchStore", "Recovered the last valid server fetch store backup");
			App.Logger.WriteLine("ServerFetchStore", $"Loaded {_data.Servers.Count} learned datacenter");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerFetchStore", "Load failed: " + ex.Message + " starting fresh");
			_data = new ServerFetchData();
		}
	}

	public static int PruneUnseenSeedEntries()
	{
		int num = 0;
		lock (_lock)
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, LearnedServerEntry> server in _data.Servers)
			{
				if (server.Value.SeenCount <= 0 && (server.Value.IPs == null || server.Value.IPs.Count == 0))
				{
					list.Add(server.Key);
				}
			}
			foreach (string item in list)
			{
				_data.Servers.Remove(item);
				num++;
			}
		}
		if (num > 0)
		{
			App.Logger.WriteLine("ServerFetchStore", $"Pruned {num} unseen pre-seeded entries from learned datacenters");
			SaveNow();
		}
		return num;
	}

	public static async Task<int> RefreshFromRemoteAsync(string? url, CancellationToken token = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return 0;
		}
		string normalizedUrl = NormalizeRemoteUrl(url.Trim());
		int added = 0;
		int updated = 0;
		try
		{
			string json = await ReadRemoteTextAsync(normalizedUrl, token).ConfigureAwait(continueOnCapturedContext: false);
			ServerFetchData serverFetchData = null;
			try
			{
				serverFetchData = JsonSerializer.Deserialize<ServerFetchData>(json);
			}
			catch
			{
			}
			if (serverFetchData == null || serverFetchData.Servers == null || serverFetchData.Servers.Count == 0)
			{
				Dictionary<string, LearnedServerEntry> dictionary = null;
				try
				{
					dictionary = JsonSerializer.Deserialize<Dictionary<string, LearnedServerEntry>>(json);
				}
				catch
				{
				}
				if (dictionary != null && dictionary.Count > 0)
				{
					serverFetchData = new ServerFetchData
					{
						Servers = new Dictionary<string, LearnedServerEntry>(dictionary, StringComparer.OrdinalIgnoreCase)
					};
				}
			}
			if (serverFetchData == null || serverFetchData.Servers == null || serverFetchData.Servers.Count == 0)
			{
				App.Logger.WriteLine("ServerFetchStore", "Remote preset returned no usable entries");
				return 0;
			}
			serverFetchData = NormalizeData(serverFetchData);
			EnsureLoaded();
			List<SeedCidrEntry> list = new List<SeedCidrEntry>();
			lock (_lock)
			{
				foreach (KeyValuePair<string, LearnedServerEntry> server in serverFetchData.Servers)
				{
					string text = (string.IsNullOrWhiteSpace(server.Value.Cidr) ? server.Key : server.Value.Cidr);
					if (string.IsNullOrWhiteSpace(text) || VoidstrapMatchmaker.IsPrivateIp(text.Split('/')[0]))
					{
						continue;
					}
					string text2 = server.Value.City ?? "";
					string text3 = server.Value.Region ?? "";
					string text4 = VoidstrapMatchmaker.NormalizeCountryCode(server.Value.Country ?? "");
					double lat = server.Value.Lat;
					double lon = server.Value.Lon;
					bool hasLocation = double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90.0 and <= 90.0 && lon is >= -180.0 and <= 180.0 && (lat != 0.0 || lon != 0.0);
					List<string> list2 = ((server.Value.IPs != null && server.Value.IPs.Count > 0) ? new List<string>(server.Value.IPs) : null);
					list.Add(new SeedCidrEntry
					{
						Cidr = text,
						City = text2,
						Region = text3,
						Country = text4,
						Lat = lat,
						Lon = lon
					});
					if (!_data.Servers.TryGetValue(text, out LearnedServerEntry value))
					{
						_data.Servers[text] = new LearnedServerEntry
						{
							Cidr = text,
							City = text2,
							Region = text3,
							Country = text4,
							Lat = lat,
							Lon = lon,
							FirstSeenUtc = DateTime.UtcNow,
							LastSeenUtc = DateTime.UtcNow,
							SeenCount = 0,
							IPs = list2
						};
						added++;
						continue;
					}
					bool num = value.SeenCount <= 0 && (value.IPs == null || value.IPs.Count == 0);
					bool flag = false;
					if (num)
					{
						if (!string.IsNullOrEmpty(text2) && value.City != text2)
						{
							value.City = text2;
							flag = true;
						}
						if (!string.IsNullOrEmpty(text3) && value.Region != text3)
						{
							value.Region = text3;
							flag = true;
						}
						if (!string.IsNullOrEmpty(text4) && value.Country != text4)
						{
							value.Country = text4;
							flag = true;
						}
						if (lat != 0.0 && value.Lat != lat)
						{
							value.Lat = lat;
							flag = true;
						}
						if (lon != 0.0 && value.Lon != lon)
						{
							value.Lon = lon;
							flag = true;
						}
						if (list2 != null && (value.IPs == null || value.IPs.Count == 0))
						{
							value.IPs = list2;
							flag = true;
						}
					}
					else
					{
						if (!string.IsNullOrEmpty(text2) && value.City != text2)
						{
							value.City = text2;
							flag = true;
						}
						if (!string.IsNullOrEmpty(text3) && value.Region != text3)
						{
							value.Region = text3;
							flag = true;
						}
						if (!string.IsNullOrEmpty(text4) && value.Country != text4)
						{
							value.Country = text4;
							flag = true;
						}
						if (hasLocation && value.Lat != lat)
						{
							value.Lat = lat;
							flag = true;
						}
						if (hasLocation && value.Lon != lon)
						{
							value.Lon = lon;
							flag = true;
						}
						if (list2 != null)
						{
							LearnedServerEntry learnedServerEntry = value;
							if (learnedServerEntry.IPs == null)
							{
								learnedServerEntry.IPs = new List<string>();
							}
							foreach (string item in list2)
							{
								if (!value.IPs.Contains(item))
								{
									value.IPs.Add(item);
									flag = true;
								}
							}
						}
					}
					if (flag)
					{
						updated++;
					}
				}
				_data = NormalizeData(_data);
			}
			if (list.Count > 0)
			{
				try
				{
					RobloxDatacenterMap.AddCidrEntries(list);
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("ServerFetchStore", $"Failed to inject {list.Count} CIDR into map: {ex.Message}");
				}
			}
			if (added > 0 || updated > 0)
			{
				SaveNow();
			}
			App.Logger.WriteLine("ServerFetchStore", $"Remote preset fetched from {DescribeRemote(normalizedUrl)}: +{added} new, {updated} updated entries");
			return added + updated;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			App.Logger.WriteLine("ServerFetchStore", "RefreshFromRemoteAsync('" + normalizedUrl + "') failed: " + ex2.Message);
			return 0;
		}
	}

	private static async Task<string> ReadRemoteTextAsync(string url, CancellationToken token)
	{
		try
		{
			return await ReadRemoteTextCoreAsync(url, token).ConfigureAwait(false);
		}
		catch when (string.Equals(url, NormalizeRemoteUrl(OfficialPresetUrl), StringComparison.OrdinalIgnoreCase))
		{
			return await ReadRemoteTextCoreAsync(NormalizeRemoteUrl(FallbackPresetUrl), token).ConfigureAwait(false);
		}
	}

	private static async Task<string> ReadRemoteTextCoreAsync(string url, CancellationToken token)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
		using HttpResponseMessage response = await RemoteClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaxRemoteBytes))
		{
			throw new InvalidDataException("Remote server fetch data is too large");
		}
		await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		using MemoryStream output = new MemoryStream(response.Content.Headers.ContentLength is long contentLength ? (int)contentLength : 0);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
		try
		{
			while (true)
			{
				int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(continueOnCapturedContext: false);
				if (read == 0)
				{
					break;
				}
				if (output.Length + read > MaxRemoteBytes)
				{
					throw new InvalidDataException("Remote server fetch data is too large");
				}
				await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(continueOnCapturedContext: false);
			}
			return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static ServerFetchData NormalizeData(ServerFetchData data)
	{
		Dictionary<string, LearnedServerEntry> normalized = new Dictionary<string, LearnedServerEntry>(StringComparer.OrdinalIgnoreCase);
		IEnumerable<KeyValuePair<string, LearnedServerEntry>> entries = (data.Servers ?? new Dictionary<string, LearnedServerEntry>())
			.Where((KeyValuePair<string, LearnedServerEntry> item) => item.Value != null)
			.OrderByDescending((KeyValuePair<string, LearnedServerEntry> item) => item.Value.LastSeenUtc)
			.Take(MaxServerEntries);
		foreach (KeyValuePair<string, LearnedServerEntry> item in entries)
		{
			LearnedServerEntry entry = item.Value;
			if (entry.IPs != null)
			{
				entry.IPs = entry.IPs.Where((string ip) => !string.IsNullOrWhiteSpace(ip)).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(MaxIpsPerEntry).ToList();
			}
			if (entry.PingSamplesMs != null)
			{
				entry.PingSamplesMs = entry.PingSamplesMs.TakeLast(MaxPingSamples).ToList();
			}
			normalized[item.Key] = entry;
		}
		data.Servers = normalized;
		return data;
	}

	private static string DescribeRemote(string url)
	{
		try
		{
			return new Uri(url).Host;
		}
		catch
		{
			return url;
		}
	}

	private static string NormalizeRemoteUrl(string url)
	{
		if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
		{
			string[] array = url.Substring("https://github.com/".Length).Split('/');
			if (array.Length >= 5 && string.Equals(array[2], "blob", StringComparison.OrdinalIgnoreCase))
			{
				string value = array[0];
				string value2 = array[1];
				string value3 = array[3];
				string value4 = string.Join('/', array, 4, array.Length - 4);
				return $"https://raw.githubusercontent.com/{value}/{value2}/{value3}/{value4}";
			}
		}
		return url;
	}

	public static List<LearnedServerEntry> AllEntries()
	{
		EnsureLoaded();
		lock (_lock)
		{
			return _data.Servers.Values.ToList();
		}
	}

	public static LearnedServerEntry? Lookup(string? ip)
	{
		if (string.IsNullOrWhiteSpace(ip))
		{
			return null;
		}
		EnsureLoaded();
		string slash24Cidr = GetSlash24Cidr(ip);
		if (slash24Cidr == null)
		{
			return null;
		}
		lock (_lock)
		{
			_data.Servers.TryGetValue(slash24Cidr, out LearnedServerEntry value);
			return value;
		}
	}

	public static void RecordSighting(string? ip, string? city = null, string? region = null, string? country = null, double? lat = null, double? lon = null)
	{
		if (string.IsNullOrWhiteSpace(ip) || VoidstrapMatchmaker.IsPrivateIp(ip))
		{
			return;
		}
		EnsureLoaded();
		string slash24Cidr = GetSlash24Cidr(ip);
		if (slash24Cidr == null)
		{
			return;
		}
		lock (_lock)
		{
			if (!_data.Servers.TryGetValue(slash24Cidr, out LearnedServerEntry value))
			{
				value = new LearnedServerEntry
				{
					Cidr = slash24Cidr,
					City = (city ?? ""),
					Region = (region ?? ""),
					Country = RobloxDatacenterMap.ResolveCountry(city, country),
					Lat = lat.GetValueOrDefault(),
					Lon = lon.GetValueOrDefault(),
					FirstSeenUtc = DateTime.UtcNow
				};
				_data.Servers[slash24Cidr] = value;
				App.Logger.WriteLine("ServerFetchStore", $"New CIDR learned: {slash24Cidr} (initial label: {city ?? "?"}, {country ?? "?"})");
			}
			else
			{
				if (string.IsNullOrEmpty(value.City) && !string.IsNullOrEmpty(city))
				{
					value.City = city;
				}
				if (string.IsNullOrEmpty(value.Region) && !string.IsNullOrEmpty(region))
				{
					value.Region = region;
				}
				if (string.IsNullOrEmpty(value.Country) && !string.IsNullOrEmpty(country))
				{
					value.Country = RobloxDatacenterMap.ResolveCountry(city ?? value.City, country);
				}
				if (value.Lat == 0.0 && lat.HasValue)
				{
					value.Lat = lat.Value;
				}
				if (value.Lon == 0.0 && lon.HasValue)
				{
					value.Lon = lon.Value;
				}
			}
			value.SeenCount++;
			value.LastSeenUtc = DateTime.UtcNow;
			LearnedServerEntry learnedServerEntry = value;
			if (learnedServerEntry.IPs == null)
			{
				List<string> list = (learnedServerEntry.IPs = new List<string>());
			}
			if (!value.IPs.Contains(ip))
			{
				value.IPs.Add(ip);
				if (value.IPs.Count > 100)
				{
					value.IPs.RemoveRange(0, value.IPs.Count - 100);
				}
			}
		}
		SaveThrottled();
		Voidstrap.Utility.WebsiteGeoSync.PushSoon();
	}

	public static void RecordPing(string? ip, int pingMs)
	{
		if (string.IsNullOrWhiteSpace(ip) || pingMs < 0)
		{
			return;
		}
		EnsureLoaded();
		string slash24Cidr = GetSlash24Cidr(ip);
		if (slash24Cidr == null)
		{
			return;
		}
		lock (_lock)
		{
			if (!_data.Servers.TryGetValue(slash24Cidr, out LearnedServerEntry value))
			{
				return;
			}
			LearnedServerEntry learnedServerEntry = value;
			if (learnedServerEntry.PingSamplesMs == null)
			{
				List<int> list = (learnedServerEntry.PingSamplesMs = new List<int>());
			}
			value.PingSamplesMs.Add(pingMs);
			if (value.PingSamplesMs.Count > 25)
			{
				value.PingSamplesMs.RemoveRange(0, value.PingSamplesMs.Count - 25);
			}
		}
		SaveThrottled();
	}

	public static double GetMedianPing(string? ip, out int sampleCount)
	{
		sampleCount = 0;
		if (string.IsNullOrWhiteSpace(ip))
			return -1.0;
		EnsureLoaded();
		string? slash24Cidr = GetSlash24Cidr(ip);
		if (slash24Cidr == null)
			return -1.0;
		lock (_lock)
		{
			if (!_data.Servers.TryGetValue(slash24Cidr, out LearnedServerEntry? entry) || entry.PingSamplesMs == null || entry.PingSamplesMs.Count == 0)
				return -1.0;
			int[] samples = entry.PingSamplesMs.OrderBy(value => value).ToArray();
			sampleCount = samples.Length;
			int middle = samples.Length / 2;
			return samples.Length % 2 == 0 ? (samples[middle - 1] + samples[middle]) / 2.0 : samples[middle];
		}
	}

	public static List<LearnedServerEntry> SortedByDistance(double lat, double lon, int max = 10)
	{
		EnsureLoaded();
		lock (_lock)
		{
			return (from e in _data.Servers.Values
				where e.Lat != 0.0 || e.Lon != 0.0
				orderby VoidstrapMatchmaker.HaversineKm(lat, lon, e.Lat, e.Lon)
				select e).Take(max).ToList();
		}
	}

	public static (int Datacenters, int Servers, int TotalSightings, int PingedDatacenters) GetStats()
	{
		EnsureLoaded();
		lock (_lock)
		{
			int count = _data.Servers.Count;
			int item = _data.Servers.Values.Sum((LearnedServerEntry e) => e.IPs?.Count ?? 0);
			int item2 = _data.Servers.Values.Sum((LearnedServerEntry e) => e.SeenCount);
			int item3 = _data.Servers.Values.Count(delegate(LearnedServerEntry e)
			{
				List<int>? pingSamplesMs = e.PingSamplesMs;
				return pingSamplesMs != null && pingSamplesMs.Count > 0;
			});
			return (Datacenters: count, Servers: item, TotalSightings: item2, PingedDatacenters: item3);
		}
	}

	public static void SaveThrottled()
	{
		lock (_lock)
		{
			_saveTimer ??= new Timer(OnSaveTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			_saveTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
		}
	}

	private static void OnSaveTimer(object? state)
	{
		try
		{
			SaveNow();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerFetchStore::OnSaveTimer", ex);
		}
	}

	public static void SaveNow()
	{
		string contents;
		try
		{
			lock (_lock)
			{
				_saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
				_data = NormalizeData(_data);
				_data.UpdatedUtc = DateTime.UtcNow;
				foreach (LearnedServerEntry value in _data.Servers.Values)
				{
					if (value.IPs != null && value.IPs.Count == 0)
					{
						value.IPs = null;
					}
					if (value.PingSamplesMs != null && value.PingSamplesMs.Count == 0)
					{
						value.PingSamplesMs = null;
					}
				}
				contents = JsonSerializer.Serialize(_data, StoreJsonOptions);
			}
			Directory.CreateDirectory(FolderPath);
			Voidstrap.Utility.JsonFile.WriteAtomicText(FilePath, contents);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerFetchStore", "Save failed: " + ex.Message);
		}
	}

	public static void Shutdown()
	{
		Timer? timer;
		lock (_lock)
		{
			timer = _saveTimer;
			_saveTimer = null;
		}
		timer?.Dispose();
		if (_loaded)
			SaveNow();
	}

	private static string? GetSlash24Cidr(string ip)
	{
		if (!IPAddress.TryParse(ip, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork)
		{
			return null;
		}
		byte[] addressBytes = address.GetAddressBytes();
		return $"{addressBytes[0]}.{addressBytes[1]}.{addressBytes[2]}.0/24";
	}
}
