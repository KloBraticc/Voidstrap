using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Exceptions;
using Voidstrap.Models.APIs.Roblox;
using Voidstrap.Utility;

namespace Voidstrap.Models.Entities;

public class UniverseDetails
{
	private static readonly ConcurrentDictionary<long, UniverseDetails> _cache = new ConcurrentDictionary<long, UniverseDetails>();

	private static readonly ConcurrentDictionary<long, byte> _notFound = new ConcurrentDictionary<long, byte>();

	private static readonly ConcurrentDictionary<long, long> _placeToUniverse = new ConcurrentDictionary<long, long>();

	private static readonly ConcurrentQueue<long> _cacheOrder = new ConcurrentQueue<long>();

	private const int MaxCacheEntries = 256;

	private const int MaxNotFoundEntries = 512;

	private const int MaxPlaceMappings = 1024;

	public GameDetailResponse Data { get; set; }

	public ThumbnailResponse Thumbnail { get; set; }

	public static UniverseDetails? LoadFromCache(long id)
	{
		if (!_cache.TryGetValue(id, out UniverseDetails value))
		{
			return null;
		}
		return value;
	}

	public static Task FetchSingle(long id, CancellationToken token = default(CancellationToken))
	{
		return FetchBulk(id.ToString(), token);
	}

	public static async Task FetchBulk(string ids, CancellationToken token = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(ids))
		{
			return;
		}
		List<long> requestedIds = (from s in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			select (!long.TryParse(s, out var result)) ? 0 : result into v
			where v > 0 && !_cache.ContainsKey(v) && !_notFound.ContainsKey(v)
			select v).Distinct().ToList();
		if (requestedIds.Count == 0)
		{
			return;
		}
		for (int offset = 0; offset < requestedIds.Count; offset += MaxIdsPerRequest)
		{
			List<long> batch = requestedIds.GetRange(offset, Math.Min(MaxIdsPerRequest, requestedIds.Count - offset));
			await FetchBatch(batch, token).ConfigureAwait(false);
		}
	}

	private const int MaxIdsPerRequest = 50;

	private static async Task FetchBatch(List<long> requestedIds, CancellationToken token)
	{
		string queryIds = string.Join(',', requestedIds);
		ApiArrayResponse<GameDetailResponse> gameDetailResponse;
		try
		{
			gameDetailResponse = await Http.GetJson<ApiArrayResponse<GameDetailResponse>>("https://games.roblox.com/v1/games?universeIds=" + queryIds, token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("UniverseDetails::FetchBulk(Games)", ex);
			throw new InvalidHTTPResponseException("Roblox API for Game Details did not respond. This is normally a transient issue; try again in a moment.");
		}
		if (gameDetailResponse?.Data == null || !gameDetailResponse.Data.Any())
		{
			MarkNotFound(requestedIds);
			return;
		}
		ApiArrayResponse<ThumbnailResponse> universeThumbnailResponse = null;
		try
		{
			universeThumbnailResponse = await Http.GetJson<ApiArrayResponse<ThumbnailResponse>>("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + queryIds + "&returnPolicy=PlaceHolder&size=128x128&format=Png&isCircular=false", token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			App.Logger.WriteException("UniverseDetails::FetchBulk(Thumbnails)", ex2);
		}
		IEnumerable<ThumbnailResponse> source = universeThumbnailResponse?.Data ?? Enumerable.Empty<ThumbnailResponse>();
		Dictionary<long, GameDetailResponse> detailsById = new();
		foreach (GameDetailResponse detail in gameDetailResponse.Data)
		{
			if (detail != null)
				detailsById.TryAdd(detail.Id, detail);
		}
		Dictionary<long, ThumbnailResponse> thumbnailsById = new();
		foreach (ThumbnailResponse entry in source)
		{
			if (entry != null)
				thumbnailsById.TryAdd(entry.TargetId, entry);
		}
		HashSet<long> storedIds = new HashSet<long>();
		foreach (long id in requestedIds)
		{
			detailsById.TryGetValue(id, out GameDetailResponse gameDetailResponse2);
			if (gameDetailResponse2 != null)
			{
				ThumbnailResponse thumbnail = (thumbnailsById.TryGetValue(id, out ThumbnailResponse existingThumbnail) ? existingThumbnail : null) ?? new ThumbnailResponse
				{
					TargetId = id,
					State = "Unavailable",
					ImageUrl = null
				};
				Store(id, new UniverseDetails
				{
					Data = gameDetailResponse2,
					Thumbnail = thumbnail
				});
				storedIds.Add(id);
			}
		}
		List<long> notFoundIds = requestedIds.Where((long id) => !storedIds.Contains(id)).ToList();
		if (notFoundIds.Count > 0)
		{
			MarkNotFound(notFoundIds);
		}
	}

	private static void MarkNotFound(List<long> ids)
	{
		foreach (long id in ids)
		{
			_notFound[id] = 0;
		}
		if (_notFound.Count > MaxNotFoundEntries)
		{
			_notFound.Clear();
		}
	}

	public static async Task ResolvePlacesToUniversesAsync(IEnumerable<long> placeIds, CancellationToken token = default(CancellationToken))
	{
		if (_placeToUniverse.Count > MaxPlaceMappings)
		{
			_placeToUniverse.Clear();
		}
		List<long> requested = (placeIds ?? Enumerable.Empty<long>()).Where((long p) => p > 0 && !_placeToUniverse.ContainsKey(p)).Distinct().ToList();
		if (requested.Count == 0)
		{
			return;
		}
		for (int offset = 0; offset < requested.Count; offset += MaxIdsPerRequest)
		{
			List<long> batch = requested.GetRange(offset, Math.Min(MaxIdsPerRequest, requested.Count - offset));
			await ResolvePlaceBatch(batch, token).ConfigureAwait(false);
		}
	}

	public static bool TryGetUniverseForPlace(long placeId, out long universeId)
	{
		return _placeToUniverse.TryGetValue(placeId, out universeId);
	}

	public static async Task FetchForEntriesAsync(IEnumerable<ActivityData> entries, CancellationToken token = default(CancellationToken))
	{
		List<ActivityData> list = (entries ?? Enumerable.Empty<ActivityData>()).Where((ActivityData x) => x != null).ToList();
		if (list.Count == 0)
		{
			return;
		}
		List<long> missing = list.Where((ActivityData x) => x.UniverseDetails == null && x.UniverseId != 0).Select((ActivityData x) => x.UniverseId).Distinct().ToList();
		if (missing.Count > 0)
		{
			await FetchBulk(string.Join(',', missing), token).ConfigureAwait(false);
		}
		List<ActivityData> unresolved = list.Where((ActivityData x) => x.UniverseDetails == null && x.UniverseId == 0 && x.PlaceId != 0).ToList();
		if (unresolved.Count > 0)
		{
			await ResolvePlacesToUniversesAsync(unresolved.Select((ActivityData x) => x.PlaceId), token).ConfigureAwait(false);
			List<long> resolvedIds = new List<long>();
			foreach (ActivityData entry in unresolved)
			{
				if (TryGetUniverseForPlace(entry.PlaceId, out long universeId))
				{
					entry.UniverseId = universeId;
					resolvedIds.Add(universeId);
				}
			}
			if (resolvedIds.Count > 0)
			{
				await FetchBulk(string.Join(',', resolvedIds.Distinct()), token).ConfigureAwait(false);
			}
		}
		foreach (ActivityData entry in list)
		{
			if (entry.UniverseDetails == null && entry.UniverseId != 0)
			{
				entry.UniverseDetails = LoadFromCache(entry.UniverseId);
			}
		}
	}

	private static async Task ResolvePlaceBatch(List<long> placeIds, CancellationToken token)
	{
		string query = string.Join(',', placeIds);
		ApiArrayResponse<PlaceDetailResponse>? response = null;
		try
		{
			response = await Http.GetJson<ApiArrayResponse<PlaceDetailResponse>>("https://games.roblox.com/v1/games/multiget-place-details?placeIds=" + query, token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("UniverseDetails::ResolvePlaces", "Bulk place lookup unavailable, using individual universe lookups: " + ex.Message);
		}
		foreach (PlaceDetailResponse detail in response?.Data ?? Enumerable.Empty<PlaceDetailResponse>())
		{
			if (detail.PlaceId > 0 && detail.UniverseId > 0)
			{
				_placeToUniverse[detail.PlaceId] = detail.UniverseId;
			}
		}
		List<long> unresolved = placeIds.Where(placeId => !_placeToUniverse.ContainsKey(placeId)).ToList();
		if (unresolved.Count == 0)
			return;
		using SemaphoreSlim gate = new SemaphoreSlim(6, 6);
		Task[] lookups = unresolved.Select(async placeId =>
		{
			await gate.WaitAsync(token).ConfigureAwait(false);
			try
			{
				PlaceUniverseResponse? resolved = await Http.GetJson<PlaceUniverseResponse>("https://apis.roblox.com/universes/v1/places/" + placeId + "/universe", token).ConfigureAwait(false);
				if (resolved?.UniverseId > 0)
					_placeToUniverse[placeId] = resolved.UniverseId;
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("UniverseDetails::ResolvePlaces", "Universe lookup failed for place " + placeId + ": " + ex.Message);
			}
			finally
			{
				gate.Release();
			}
		}).ToArray();
		await Task.WhenAll(lookups).ConfigureAwait(false);
	}

	private sealed class PlaceDetailResponse
	{
		[JsonPropertyName("placeId")]
		public long PlaceId { get; set; }

		[JsonPropertyName("universeId")]
		public long UniverseId { get; set; }
	}

	private sealed class PlaceUniverseResponse
	{
		[JsonPropertyName("universeId")]
		public long UniverseId { get; set; }
	}

	private static void Store(long id, UniverseDetails details)
	{
		if (_cache.TryAdd(id, details))
		{
			_cacheOrder.Enqueue(id);
		}
		else
		{
			_cache[id] = details;
		}
		while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out long oldest))
		{
			_cache.TryRemove(oldest, out _);
		}
	}
}
