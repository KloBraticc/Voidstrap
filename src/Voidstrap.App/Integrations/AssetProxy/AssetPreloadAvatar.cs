using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Entities;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetPreloadAvatar
{
	private const string LOG_IDENT = "AssetPreloadAvatar";

	private const int BatchSize = 50;

	private static readonly HttpClient _client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15L), handler =>
	{
		handler.UseCookies = false;
		handler.AllowAutoRedirect = true;
	});

	private static DateTime _lastRunUtc = DateTime.MinValue;

	private static long _lastUserId;

	private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
	private static readonly SemaphoreSlim Gate = new(1, 1);

	public static async Task PreloadAsync(CancellationToken ct)
	{
		if (!App.Settings.Prop.AssetWarpPreloadAvatar)
		{
			return;
		}
		if (DateTime.UtcNow - _lastRunUtc < Cooldown)
		{
			return;
		}
		await Gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (DateTime.UtcNow - _lastRunUtc < Cooldown)
			{
				return;
			}
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(ct).ConfigureAwait(false);
			if (account == null || account.UserId <= 0)
			{
				return;
			}
			if (account.UserId == _lastUserId && DateTime.UtcNow - _lastRunUtc < Cooldown)
			{
				return;
			}
			List<long> assetIds = await FetchAvatarAssetIdsAsync(account.UserId, ct).ConfigureAwait(false);
			if (assetIds.Count == 0)
			{
				return;
			}
			await ResolveAndCatalogAsync(assetIds, ct).ConfigureAwait(false);
			_lastRunUtc = DateTime.UtcNow;
			_lastUserId = account.UserId;
			App.Logger?.WriteLine(LOG_IDENT, "Preloaded " + assetIds.Count + " avatar assets");
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Preload failed: " + ex.Message);
		}
		finally
		{
			Gate.Release();
		}
	}

	private static async Task<List<long>> FetchAvatarAssetIdsAsync(long userId, CancellationToken ct)
	{
		try
		{
			using HttpRequestMessage req = new(HttpMethod.Get, "https://avatar.roblox.com/v1/users/" + userId + "/avatar");
			AddAuth(req);
			using HttpResponseMessage res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			if (!res.IsSuccessStatusCode)
			{
				return [];
			}
			byte[]? payload = await ReadBytesAsync(res, 4194304, ct).ConfigureAwait(false);
			if (payload == null)
			{
				return [];
			}
			using JsonDocument doc = JsonDocument.Parse(payload);
			if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
			{
				return [];
			}
			List<long> ids = [];
			foreach (JsonElement asset in assets.EnumerateArray())
			{
				if (asset.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				if (asset.TryGetProperty("id", out JsonElement idProp) && idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out long id) && id > 0)
				{
					ids.Add(id);
				}
			}
			return ids;
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

	private static async Task ResolveAndCatalogAsync(List<long> assetIds, CancellationToken ct)
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
				AddAuth(req);
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
					AssetPreloadCache.ObserveBatchAsset(requestId, hash, location);
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

	private static void AddAuth(HttpRequestMessage req)
	{
		try
		{
			string? cookie = RobloxCookie.Get();
			if (!string.IsNullOrEmpty(cookie))
			{
				req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			}
		}
		catch
		{
		}
		req.Headers.TryAddWithoutValidation("Accept", "*/*");
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
