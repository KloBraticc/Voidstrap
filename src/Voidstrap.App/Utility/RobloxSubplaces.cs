using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class RobloxSubplaces
{
	public sealed record Place(long Id, string Name)
	{
		public string IconUrl { get; init; } = "";
		public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id.ToString(CultureInfo.InvariantCulture) : Name + " (" + Id.ToString(CultureInfo.InvariantCulture) + ")";
	}

	public static async Task<IReadOnlyList<Place>> GetAsync(HttpClient client, long placeId, CancellationToken token, long universeId = 0)
	{
		if (universeId <= 0)
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placeId);
			using JsonDocument universe = await GetDocumentAsync(client, "https://apis.roblox.com/universes/v1/places/" + placeId.ToString(CultureInfo.InvariantCulture) + "/universe", token).ConfigureAwait(false);
			if (!TryGetPositiveId(universe.RootElement, "universeId", out universeId))
				throw new InvalidDataException("The place does not belong to an available experience.");
		}
		using JsonDocument game = await GetDocumentAsync(client, "https://games.roblox.com/v1/games?universeIds=" + universeId.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
		if (game.RootElement.ValueKind != JsonValueKind.Object || !game.RootElement.TryGetProperty("data", out JsonElement games) || games.ValueKind != JsonValueKind.Array || games.GetArrayLength() != 1 || !TryGetPositiveId(games[0], "rootPlaceId", out long rootPlaceId))
			throw new InvalidDataException("The experience is unavailable.");
		List<Place> places = new();
		HashSet<long> ids = new();
		HashSet<string> cursors = new(StringComparer.Ordinal);
		string? cursor = null;
		do
		{
			token.ThrowIfCancellationRequested();
			string url = "https://develop.roblox.com/v1/universes/" + universeId.ToString(CultureInfo.InvariantCulture) + "/places?limit=100&sortOrder=Asc";
			if (cursor != null)
				url += "&cursor=" + Uri.EscapeDataString(cursor);
			using JsonDocument page = await GetDocumentAsync(client, url, token).ConfigureAwait(false);
			if (page.RootElement.ValueKind != JsonValueKind.Object || !page.RootElement.TryGetProperty("data", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException("Roblox returned an invalid place list.");
			foreach (JsonElement item in items.EnumerateArray())
			{
				if (!TryGetPositiveId(item, "id", out long id) || id == rootPlaceId || !ids.Add(id))
					continue;
				string? name = item.TryGetProperty("name", out JsonElement nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
				places.Add(new Place(id, name ?? ""));
			}
			if (!page.RootElement.TryGetProperty("nextPageCursor", out JsonElement cursorValue) || (cursorValue.ValueKind != JsonValueKind.String && cursorValue.ValueKind != JsonValueKind.Null))
				throw new InvalidDataException("Roblox returned an invalid page cursor.");
			cursor = cursorValue.ValueKind == JsonValueKind.String ? cursorValue.GetString() : null;
			if (string.IsNullOrEmpty(cursor))
				cursor = null;
			else if (cursor.Length > 4096 || !cursors.Add(cursor))
				throw new InvalidDataException("Roblox returned an invalid page cursor.");
		} while (cursor != null);
		await LoadIconsAsync(client, places, token).ConfigureAwait(false);
		return places;
	}

	private static async Task LoadIconsAsync(HttpClient client, List<Place> places, CancellationToken token)
	{
		using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
		request.CancelAfter(TimeSpan.FromSeconds(8));
		for (int offset = 0; offset < places.Count; offset += 50)
		{
			token.ThrowIfCancellationRequested();
			if (request.IsCancellationRequested)
				break;
			Dictionary<long, int> indices = new();
			for (int i = offset; i < Math.Min(offset + 50, places.Count); i++)
				indices.Add(places[i].Id, i);
			string ids = string.Join(',', indices.Keys.Select(id => id.ToString(CultureInfo.InvariantCulture)));
			try
			{
				using JsonDocument response = await GetDocumentAsync(client, "https://thumbnails.roblox.com/v1/places/gameicons?placeIds=" + ids + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", request.Token).ConfigureAwait(false);
				if (response.RootElement.ValueKind != JsonValueKind.Object || !response.RootElement.TryGetProperty("data", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
					continue;
				foreach (JsonElement image in images.EnumerateArray())
				{
					if (!TryGetPositiveId(image, "targetId", out long id) || !indices.TryGetValue(id, out int index)
						|| !image.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.String || state.GetString() != "Completed"
						|| !image.TryGetProperty("imageUrl", out JsonElement imageUrl) || imageUrl.ValueKind != JsonValueKind.String
						|| !Uri.TryCreate(imageUrl.GetString(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
						continue;
					places[index] = places[index] with { IconUrl = uri.AbsoluteUri };
				}
			}
			catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException or Http.RateLimitedException)
			{
				token.ThrowIfCancellationRequested();
			}
		}
	}

	private static bool TryGetPositiveId(JsonElement item, string propertyName, out long id)
	{
		id = 0;
		return item.ValueKind == JsonValueKind.Object
			&& item.TryGetProperty(propertyName, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt64(out id)
			&& id > 0;
	}

	private static async Task<JsonDocument> GetDocumentAsync(HttpClient client, string url, CancellationToken token)
	{
		return JsonDocument.Parse(await Http.GetStringBoundedAsync(client, url, 1024 * 1024, token).ConfigureAwait(false));
	}
}
