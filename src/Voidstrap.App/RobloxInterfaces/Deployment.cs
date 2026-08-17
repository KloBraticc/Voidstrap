using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Exceptions;
using Voidstrap.Models.APIs.Roblox;

namespace Voidstrap.RobloxInterfaces;

public static class Deployment
{
	public const string DefaultChannel = "production";

	private const string VersionStudioHash = "version-012732894899482c";

	public static readonly HashSet<HttpStatusCode?> BadChannelCodes;

	private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

	private static readonly ConcurrentDictionary<string, ClientVersion> ClientVersionCache;

	private static readonly ConcurrentQueue<string> ClientVersionCacheOrder;

	private const int MaxCachedVersions = 16;

	private const int MaxVersionResponseBytes = 1048576;

	private static readonly Dictionary<string, int> BaseUrls;

	private static readonly string[] ClientSettingsHosts =
	[
		"https://clientsettingscdn.roblox.com",
		"https://clientsettings.roblox.com"
	];

	private static readonly HttpClient SharedHttp;

	public static string BaseUrl { get; private set; }

	public static string PreferredBaseUrl { get; set; } = string.Empty;

	public static IReadOnlyList<string> Mirrors => [.. BaseUrls.OrderBy(entry => entry.Value).Select(entry => entry.Key)];

	static Deployment()
	{
		PreferredBaseUrl = App.Settings.Prop.PreferredMirror ?? string.Empty;
		BaseUrl = string.Empty;
		BadChannelCodes =
        [
            HttpStatusCode.Unauthorized,
			HttpStatusCode.Forbidden,
			HttpStatusCode.NotFound
		];
		ClientVersionCache = new ConcurrentDictionary<string, ClientVersion>();
		ClientVersionCacheOrder = new ConcurrentQueue<string>();
		BaseUrls = new Dictionary<string, int>
		{
			{ "https://setup.rbxcdn.com", 0 },
			{ "https://setup-aws.rbxcdn.com", 2 },
			{ "https://setup-ak.rbxcdn.com", 2 },
			{ "https://roblox-setup.cachefly.net", 2 },
			{ "https://s3.amazonaws.com/setup.roblox.com", 4 }
		};
		SharedHttp = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(30L));
		SharedHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VoidstrapUpdater/2.0");
		SharedHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
	}

	public static string GetInfoUrl(string channel, string binaryType = "WindowsPlayer")
	{
		ValidateBinaryType(binaryType);
		string text = string.Equals(channel, "production", StringComparison.OrdinalIgnoreCase)
			? "/v2/client-version/" + binaryType
			: "/v2/client-version/" + binaryType + "/channel/" + Uri.EscapeDataString(channel);
		return "https://clientsettingscdn.roblox.com" + text;
	}

	private static async Task<T> SafeGetJson<T>(string channel, string binaryType, CancellationToken cancellationToken)
	{
		ValidateBinaryType(binaryType);
		Exception? lastError = null;
		foreach (string host in ClientSettingsHosts)
		{
			string path = string.Equals(channel, "production", StringComparison.OrdinalIgnoreCase)
				? "/v2/client-version/" + binaryType
				: "/v2/client-version/" + binaryType + "/channel/" + Uri.EscapeDataString(channel);
			string url = host + path;
			try
			{
				using HttpResponseMessage response = await SharedHttp.GetAsync(url, cancellationToken).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
				{
					lastError = new HttpRequestException($"Request failed: {(int)response.StatusCode}", null, response.StatusCode);
					continue;
				}
				string text = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, MaxVersionResponseBytes, cancellationToken).ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('<'))
				{
					lastError = new InvalidHTTPResponseException("Invalid JSON response from " + url);
					continue;
				}
				T value = JsonSerializer.Deserialize<T>(text, CaseInsensitiveJson) ?? throw new InvalidHTTPResponseException("Failed to deserialize JSON from " + url);
				return value;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex) when (ex is HttpRequestException or InvalidHTTPResponseException or JsonException or TaskCanceledException)
			{
				lastError = ex;
				App.Logger.WriteLine("Deployment::GetInfo", "Deployment source failed: " + host + ", " + ex.Message);
			}
		}
		throw lastError ?? new HttpRequestException("Roblox deployment services are unavailable");
	}

	public static async Task<ClientVersion> GetInfo(string? inputChannel = null, IEnumerable<string>? cycleChannels = null, CancellationToken cancellationToken = default, string binaryType = "WindowsPlayer", Action<string>? resolvedChannel = null)
	{
		ValidateBinaryType(binaryType);
		string channel = string.IsNullOrEmpty(inputChannel) ? App.Settings.Prop.Channel : inputChannel;
		bool isDefault = string.Equals(channel, "production", StringComparison.OrdinalIgnoreCase);
		App.Logger.WriteLine("Deployment::GetInfo", "Fetching deploy info for channel " + channel);
		string cacheKey = channel + "|" + binaryType;
		if (ClientVersionCache.TryGetValue(cacheKey, out ClientVersion value))
		{
			App.Logger.WriteLine("Deployment::GetInfo", "Using cached deploy info");
			resolvedChannel?.Invoke(channel);
			return value;
		}
		try
		{
			ClientVersion clientVersion = await SafeGetJson<ClientVersion>(channel, binaryType, cancellationToken);
			CacheVersion(cacheKey, clientVersion);
			resolvedChannel?.Invoke(channel);
			return clientVersion;
		}
		catch (HttpRequestException ex) when (!isDefault && BadChannelCodes.Contains(ex.StatusCode))
		{
			App.Logger.WriteLine("Deployment::GetInfo", $"Channel {channel} failed ({ex.StatusCode}).");
			if (cycleChannels != null)
			{
				foreach (string next in cycleChannels.Distinct<string>(StringComparer.OrdinalIgnoreCase))
				{
					try
					{
						App.Logger.WriteLine("Deployment::GetInfo", "Trying next channel: " + next);
						ClientVersion clientVersion2 = await SafeGetJson<ClientVersion>(next, binaryType, cancellationToken);
						if (!string.IsNullOrWhiteSpace(clientVersion2.Version))
						{
							CacheVersion(next + "|" + binaryType, clientVersion2);
							App.Logger.WriteLine("Deployment::GetInfo", "Found working channel: " + next);
							resolvedChannel?.Invoke(next);
							return clientVersion2;
						}
					}
					catch (HttpRequestException ex2) when (BadChannelCodes.Contains(ex2.StatusCode))
					{
					}
					catch
					{
					}
				}
			}
			App.Logger.WriteLine("Deployment::GetInfo", "All cycle channels failed. Falling back to production.");
			ClientVersion clientVersion3 = await SafeGetJson<ClientVersion>("production", binaryType, cancellationToken);
			CacheVersion("production|" + binaryType, clientVersion3);
			resolvedChannel?.Invoke("production");
			return clientVersion3;
		}
	}

	private static void ValidateBinaryType(string binaryType)
	{
		if (!string.Equals(binaryType, "WindowsPlayer", StringComparison.Ordinal) && !string.Equals(binaryType, "WindowsStudio64", StringComparison.Ordinal))
			throw new ArgumentException("The Roblox deployment binary type is invalid", nameof(binaryType));
	}

	private static void CacheVersion(string key, ClientVersion version)
	{
		if (ClientVersionCache.TryAdd(key, version))
		{
			ClientVersionCacheOrder.Enqueue(key);
		}
		else
		{
			ClientVersionCache[key] = version;
		}
		while (ClientVersionCache.Count > MaxCachedVersions && ClientVersionCacheOrder.TryDequeue(out string? oldest))
		{
			ClientVersionCache.TryRemove(oldest, out _);
		}
	}

	public static async Task<Exception?> InitializeConnectivity()
	{
		using CancellationTokenSource cancellation = new();
		string preferred = PreferredBaseUrl;
		if (!string.IsNullOrWhiteSpace(preferred) && BaseUrls.ContainsKey(preferred))
		{
			using CancellationTokenSource preferredTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
			preferredTimeout.CancelAfter(TimeSpan.FromSeconds(3));
			string? chosen = await ProbeAsync(preferred, preferredTimeout.Token);
			if (chosen != null)
			{
				BaseUrl = chosen;
				App.Logger.WriteLine("Deployment::InitializeConnectivity", "Using preferred base URL: " + BaseUrl);
				return null;
			}
			App.Logger.WriteLine("Deployment::InitializeConnectivity", "Preferred mirror did not respond, falling back to automatic: " + preferred);
		}
		List<Task<string>> probes = [.. (from entry in BaseUrls
			orderby entry.Value
			select ProbeAsync(entry.Key, cancellation.Token))];
		while (probes.Count > 0)
		{
			Task<string> task = await Task.WhenAny(probes);
			probes.Remove(task);
			string text = await task;
			if (text != null)
			{
				cancellation.Cancel();
				BaseUrl = text;
				App.Logger.WriteLine("Deployment::InitializeConnectivity", "Using base URL: " + BaseUrl);
				return null;
			}
		}
		return new Exception("Failed to connect to any setup mirrors.");
	}

	private static async Task<string?> ProbeAsync(string url, CancellationToken token)
	{
		try
		{
			using HttpResponseMessage resp = await SharedHttp.GetAsync(url + "/versionStudio", token);
			if (!resp.IsSuccessStatusCode)
			{
				return null;
			}
			return ((await Voidstrap.Utility.Http.ReadStringBoundedAsync(resp.Content, 1024, token).ConfigureAwait(false)).Trim() == VersionStudioHash) ? url : null;
		}
		catch
		{
			return null;
		}
	}

	public static string GetLocation(string resource, string? channel = null)
	{
		string effectiveChannel = string.IsNullOrWhiteSpace(channel) ? App.Settings.Prop.Channel : channel;
		string text = BaseUrl;
		if (!string.Equals(effectiveChannel, DefaultChannel, StringComparison.OrdinalIgnoreCase))
		{
			string text2 = ApplicationSettings.GetSettings("PCClientBootstrapper", effectiveChannel).Get<bool>("FFlagReplaceChannelNameForDownload") ? "common" : effectiveChannel.ToLowerInvariant();
			text = text + "/channel/" + Uri.EscapeDataString(text2);
		}
		return text + resource;
	}

	public static IReadOnlyList<string> GetLocations(string resource, string? channel = null)
	{
		string effectiveChannel = string.IsNullOrWhiteSpace(channel) ? App.Settings.Prop.Channel : channel;
		List<string> hosts = [];
		if (!string.IsNullOrEmpty(BaseUrl))
		{
			hosts.Add(BaseUrl);
		}
		foreach (string key in BaseUrls.Keys)
		{
			if (!hosts.Contains(key))
			{
				hosts.Add(key);
			}
		}
		List<string> urls = [];
		if (!string.Equals(effectiveChannel, DefaultChannel, StringComparison.OrdinalIgnoreCase))
		{
			string channelName;
			try
			{
				channelName = ApplicationSettings.GetSettings("PCClientBootstrapper", effectiveChannel).Get<bool>("FFlagReplaceChannelNameForDownload") ? "common" : effectiveChannel.ToLowerInvariant();
			}
			catch
			{
				channelName = effectiveChannel.ToLowerInvariant();
			}
			string alternate = channelName == "common" ? effectiveChannel.ToLowerInvariant() : "common";
			channelName = Uri.EscapeDataString(channelName);
			alternate = Uri.EscapeDataString(alternate);
			foreach (string host in hosts)
			{
				urls.Add(host + "/channel/" + channelName + resource);
			}
			foreach (string host in hosts)
			{
				urls.Add(host + "/channel/" + alternate + resource);
			}
			foreach (string host in hosts)
			{
				urls.Add(host + resource);
			}
		}
		else
		{
			foreach (string host in hosts)
			{
				urls.Add(host + resource);
			}
		}
		return urls;
	}
}
