using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public sealed class RobloxPlace
{
	public long Id { get; init; }

	public string Name { get; init; } = string.Empty;

	public bool IsRoot { get; init; }
}

// Lists the places of a universe (the start place and its subplaces) and their icons
public static class RobloxPlaces
{
	private const string LOG_IDENT = "RobloxPlaces";
	private const int MaxResponseBytes = 2 * 1024 * 1024;
	private const int MaxPages = 5;

	private static readonly HttpClient Client = CreateClient();
	private static readonly object CacheLock = new();
	private static readonly Dictionary<long, (List<RobloxPlace> Places, DateTime FetchedUtc)> PlaceCache = new();
	private static readonly Dictionary<long, string> IconCache = new();
	private static readonly TimeSpan PlaceLifetime = TimeSpan.FromMinutes(10);

	private static HttpClient CreateClient()
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(10), handler =>
		{
			handler.UseCookies = false;
			handler.AllowAutoRedirect = false;
			handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
		});
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/1.0");
		return client;
	}

	public static async Task<List<RobloxPlace>> GetUniversePlacesAsync(long universeId, long rootPlaceId, CancellationToken token)
	{
		if (universeId <= 0)
			return new List<RobloxPlace>();
		lock (CacheLock)
		{
			if (PlaceCache.TryGetValue(universeId, out var cached) && DateTime.UtcNow - cached.FetchedUtc < PlaceLifetime)
				return cached.Places.ToList();
		}

		List<RobloxPlace> places = new();
		HashSet<long> seen = new();
		string? cursor = null;
		for (int page = 0; page < MaxPages; page++)
		{
			string url = $"https://develop.roblox.com/v1/universes/{universeId}/places?isUniverseCreation=false&limit=100&sortOrder=Asc";
			if (!string.IsNullOrEmpty(cursor))
				url += "&cursor=" + Uri.EscapeDataString(cursor);
			using JsonDocument? document = await GetAsync(url, token).ConfigureAwait(false);
			if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				break;
			foreach (JsonElement item in data.EnumerateArray())
			{
				long id = item.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt64(out long value) ? value : 0;
				if (id <= 0 || !seen.Add(id))
					continue;
				string name = item.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() ?? "" : "";
				places.Add(new RobloxPlace { Id = id, Name = name.Length > 0 ? name : "Place " + id.ToString(CultureInfo.InvariantCulture), IsRoot = id == rootPlaceId });
			}
			cursor = document.RootElement.TryGetProperty("nextPageCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
			if (string.IsNullOrEmpty(cursor))
				break;
		}

		// The start place first, then the subplaces in the order Roblox lists them
		places = places.OrderByDescending(place => place.IsRoot).ToList();
		lock (CacheLock)
			PlaceCache[universeId] = (places, DateTime.UtcNow);
		return places.ToList();
	}

	public static async Task<Dictionary<long, string>> GetPlaceIconUrlsAsync(IEnumerable<long> placeIds, CancellationToken token)
	{
		Dictionary<long, string> urls = new();
		List<long> missing = new();
		lock (CacheLock)
		{
			foreach (long id in placeIds.Where(id => id > 0).Distinct())
			{
				if (IconCache.TryGetValue(id, out string? url))
					urls[id] = url;
				else
					missing.Add(id);
			}
		}
		for (int index = 0; index < missing.Count; index += 50)
		{
			string url = "https://thumbnails.roblox.com/v1/places/gameicons?placeIds=" + string.Join(",", missing.Skip(index).Take(50))
				+ "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false";
			using JsonDocument? document = await GetAsync(url, token).ConfigureAwait(false);
			if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				continue;
			foreach (JsonElement item in data.EnumerateArray())
			{
				long id = item.TryGetProperty("targetId", out JsonElement idElement) && idElement.TryGetInt64(out long value) ? value : 0;
				string image = item.TryGetProperty("imageUrl", out JsonElement imageElement) && imageElement.ValueKind == JsonValueKind.String ? imageElement.GetString() ?? "" : "";
				if (id <= 0 || !image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
					continue;
				urls[id] = image;
				lock (CacheLock)
					IconCache[id] = image;
			}
		}
		return urls;
	}

	private static async Task<JsonDocument?> GetAsync(string url, CancellationToken token)
	{
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			string? cookie = RobloxCookie.Get();
			if (!string.IsNullOrEmpty(cookie))
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				App.Logger.WriteLine(LOG_IDENT, $"{new Uri(url).Host} answered {(int)response.StatusCode} {response.StatusCode}");
				return null;
			}
			return JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false));
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Request failed: " + ex.Message);
			return null;
		}
	}
}
