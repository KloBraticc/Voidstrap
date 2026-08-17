using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetPreloadProactive
{
	private const string LOG_IDENT = "AssetPreloadProactive";

	private const int BatchSize = 50;

	private const int MaxSubplaces = 20;

	private static readonly HttpClient _client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15L), handler =>
	{
		handler.UseCookies = false;
		handler.AllowAutoRedirect = true;
	});

	private static DateTime _lastRunUtc = DateTime.MinValue;

	private static long _lastPlaceId;

	private static long _lastUniverseId;

	private static readonly SemaphoreSlim DiscoveryGate = new(1, 1);

	private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

	public static async Task DiscoverAsync(long placeId, CancellationToken ct)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || placeId <= 0)
		{
			return;
		}
		await DiscoveryGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (placeId == _lastPlaceId && DateTime.UtcNow - _lastRunUtc < Cooldown)
			{
				return;
			}
			long universeId = await ResolveUniverseIdAsync(placeId, ct).ConfigureAwait(false);
			if (universeId <= 0)
			{
				return;
			}
			List<long> assetIds = [];
			assetIds.Add(placeId);
			List<long> subplaces = await FetchSubplaceIdsAsync(universeId, ct).ConfigureAwait(false);
			assetIds.AddRange(subplaces.Take(MaxSubplaces));
			if (assetIds.Count == 0)
			{
				return;
			}
			await ResolveAndCatalogAsync(placeId, assetIds, ct).ConfigureAwait(false);
			_lastRunUtc = DateTime.UtcNow;
			_lastPlaceId = placeId;
			_lastUniverseId = universeId;
			App.Logger?.WriteLine(LOG_IDENT, "Discovered " + assetIds.Count + " assets for place " + placeId);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Discovery failed: " + ex.Message);
		}
		finally
		{
			DiscoveryGate.Release();
		}
	}

	public static async Task DiscoverCrossGameAsync(CancellationToken ct)
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled || !App.Settings.Prop.AssetWarpPreloadCrossGame)
		{
			return;
		}
		try
		{
			long currentUniverseId = Interlocked.Read(ref _lastUniverseId);
			PlayTimeStore.StoreData store = PlayTimeStore.Read();
			List<long> recentUniverses = [.. store.Universes
				.OrderByDescending(kv => kv.Value.LastPlayed ?? DateTime.MinValue)
				.Where(kv => kv.Key > 0 && kv.Key != currentUniverseId)
				.Take(5)
				.Select(kv => kv.Key)];
			if (recentUniverses.Count == 0)
			{
				return;
			}
			int totalPreloaded = 0;
			foreach (long universeId in recentUniverses)
			{
				if (ct.IsCancellationRequested)
				{
					break;
				}
				try
				{
					long rootPlaceId = await ResolveRootPlaceIdAsync(universeId, ct).ConfigureAwait(false);
					if (rootPlaceId <= 0)
					{
						continue;
					}
					List<long> assetIds = [rootPlaceId];
					List<long> subplaces = await FetchSubplaceIdsAsync(universeId, ct).ConfigureAwait(false);
					assetIds.AddRange(subplaces.Take(10));
					await ResolveAndCatalogAsync(rootPlaceId, assetIds, ct).ConfigureAwait(false);
					await AssetPreloadCache.WarmCatalogAsync(rootPlaceId, 128, ct).ConfigureAwait(false);
					totalPreloaded += assetIds.Count;
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch
				{
				}
			}
			if (totalPreloaded > 0)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Cross-game preloaded " + totalPreloaded + " assets from " + recentUniverses.Count + " games");
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Cross-game discovery failed: " + ex.Message);
		}
	}

	private static async Task<long> ResolveUniverseIdAsync(long placeId, CancellationToken ct)
	{
		try
		{
			using HttpRequestMessage req = new(HttpMethod.Get, "https://apis.roblox.com/universes/v1/places/" + placeId + "/universe");
			using HttpResponseMessage res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			if (!res.IsSuccessStatusCode)
			{
				return 0;
			}
			byte[]? payload = await ReadBytesAsync(res, 4096, ct).ConfigureAwait(false);
			if (payload == null)
			{
				return 0;
			}
			using JsonDocument doc = JsonDocument.Parse(payload);
			if (doc.RootElement.TryGetProperty("universeId", out JsonElement uid) && uid.ValueKind == JsonValueKind.Number && uid.TryGetInt64(out long id) && id > 0)
			{
				return id;
			}
			return 0;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return 0;
		}
	}

	private static async Task<long> ResolveRootPlaceIdAsync(long universeId, CancellationToken ct)
	{
		try
		{
			using HttpRequestMessage req = new(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=" + universeId);
			using HttpResponseMessage res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			if (!res.IsSuccessStatusCode)
			{
				return 0;
			}
			byte[]? payload = await ReadBytesAsync(res, 4096, ct).ConfigureAwait(false);
			if (payload == null)
			{
				return 0;
			}
			using JsonDocument doc = JsonDocument.Parse(payload);
			if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
			{
				JsonElement first = data[0];
				if (first.TryGetProperty("rootPlaceId", out JsonElement rp) && rp.ValueKind == JsonValueKind.Number && rp.TryGetInt64(out long id) && id > 0)
				{
					return id;
				}
			}
			return 0;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return 0;
		}
	}

	private static async Task<List<long>> FetchSubplaceIdsAsync(long universeId, CancellationToken ct)
	{
		try
		{
			List<long> ids = [];
			string? cursor = null;
			while (ids.Count < MaxSubplaces)
			{
				string url = "https://develop.roblox.com/v1/universes/" + universeId + "/places?isUniverseCreation=false&limit=100&sortOrder=Asc";
				if (!string.IsNullOrEmpty(cursor))
				{
					url += "&cursor=" + Uri.EscapeDataString(cursor);
				}
				using HttpRequestMessage req = new(HttpMethod.Get, url);
				using HttpResponseMessage res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				if (!res.IsSuccessStatusCode)
				{
					break;
				}
				byte[]? payload = await ReadBytesAsync(res, 4194304, ct).ConfigureAwait(false);
				if (payload == null)
				{
					break;
				}
				using JsonDocument doc = JsonDocument.Parse(payload);
				if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				{
					break;
				}
				foreach (JsonElement place in data.EnumerateArray())
				{
					JsonElement pid = default;
					bool hasId = place.ValueKind == JsonValueKind.Object && (place.TryGetProperty("id", out pid) || place.TryGetProperty("placeId", out pid));
					if (hasId && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt64(out long id) && id > 0)
					{
						ids.Add(id);
						if (ids.Count >= MaxSubplaces)
						{
							break;
						}
					}
				}
				cursor = doc.RootElement.TryGetProperty("nextPageCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
				if (string.IsNullOrEmpty(cursor))
				{
					break;
				}
			}
			return ids.Distinct().ToList();
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return [];
		}
	}

	private static async Task ResolveAndCatalogAsync(long catalogPlaceId, List<long> assetIds, CancellationToken ct)
	{
		for (int i = 0; i < assetIds.Count; i += BatchSize)
		{
			if (ct.IsCancellationRequested)
			{
				break;
			}
			List<long> chunk = [.. assetIds.Skip(i).Take(BatchSize)];
			try
			{
				string body = "[" + string.Join(",", chunk.Select(id => "{\"assetId\":\"" + id + "\",\"requestId\":\"" + id + "\"}")) + "]";
				using HttpRequestMessage req = new(HttpMethod.Post, "https://assetdelivery.roblox.com/v1/assets/batch");
				req.Content = new StringContent(body, Encoding.UTF8, "application/json");
				using HttpResponseMessage res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				if (!res.IsSuccessStatusCode)
				{
					continue;
				}
				byte[]? payload = await ReadBytesAsync(res, 4194304, ct).ConfigureAwait(false);
				if (payload == null)
				{
					continue;
				}
				using JsonDocument doc = JsonDocument.Parse(payload);
				if (doc.RootElement.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				foreach (JsonElement item in doc.RootElement.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object)
					{
						continue;
					}
					string requestId = item.TryGetProperty("requestId", out JsonElement rid) ? ReadStr(rid) : "";
					string location = item.TryGetProperty("location", out JsonElement loc) ? ReadStr(loc) : "";
					if (location.Length == 0 || requestId.Length == 0)
					{
						continue;
					}
					string? hash = ExtractHash(location);
					AssetPreloadCache.ObserveBatchAssetForPlace(catalogPlaceId, requestId, hash, location);
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
			}
		}
	}

	private static async Task<byte[]?> ReadBytesAsync(HttpResponseMessage res, int maxBytes, CancellationToken ct)
	{
		long? contentLength = res.Content.Headers.ContentLength;
		if (contentLength <= 0 || contentLength > maxBytes)
		{
			return null;
		}
		await using System.IO.Stream input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		int capacity = contentLength.HasValue ? (int)contentLength.Value : 0;
		using System.IO.MemoryStream output = capacity > 0 ? new System.IO.MemoryStream(capacity) : new System.IO.MemoryStream();
		byte[] buffer = new byte[65536];
		while (true)
		{
			int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}
			if (output.Length + read > maxBytes)
			{
				return null;
			}
			await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
		}
		return output.Length == 0 ? null : output.ToArray();
	}

	private static string ReadStr(JsonElement element)
	{
		return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText().Trim('"');
	}

	private static string? ExtractHash(string location)
	{
		int query = location.IndexOf('?');
		string value = query >= 0 ? location[..query] : location;
		string candidate = value[(value.LastIndexOf('/') + 1)..];
		return candidate.Length == 32 && candidate.All(Uri.IsHexDigit) ? candidate.ToLowerInvariant() : null;
	}
}
