using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class RobloxCatalog
{
	private const string NewestUrl = "https://catalog.roblox.com/v1/search/items/details?Category=1&Limit=10&SortType=3";

	private const string ThumbnailUrl = "https://thumbnails.roblox.com/v1/assets?assetIds=";

	private const string ThumbnailOptions = "&size=150x150&format=Png&isCircular=false";

	private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(10);

	private static readonly Dictionary<int, string> AssetTypes = new()
	{
		[1] = "Image",
		[2] = "T-Shirt",
		[3] = "Audio",
		[4] = "Mesh",
		[8] = "Hat",
		[9] = "Place",
		[10] = "Model",
		[11] = "Shirt",
		[12] = "Pants",
		[13] = "Decal",
		[17] = "Head",
		[18] = "Face",
		[19] = "Gear",
		[21] = "Badge",
		[24] = "Animation",
		[27] = "Torso",
		[28] = "Right Arm",
		[29] = "Left Arm",
		[30] = "Left Leg",
		[31] = "Right Leg",
		[32] = "Package",
		[34] = "Game Pass",
		[38] = "Plugin",
		[40] = "MeshPart",
		[41] = "Hair Accessory",
		[42] = "Face Accessory",
		[43] = "Neck Accessory",
		[44] = "Shoulder Accessory",
		[45] = "Front Accessory",
		[46] = "Back Accessory",
		[47] = "Waist Accessory",
		[48] = "Climb Animation",
		[49] = "Death Animation",
		[50] = "Fall Animation",
		[51] = "Idle Animation",
		[52] = "Jump Animation",
		[53] = "Run Animation",
		[54] = "Swim Animation",
		[55] = "Walk Animation",
		[56] = "Pose Animation",
		[61] = "Emote",
		[62] = "Video",
		[64] = "T-Shirt Accessory",
		[65] = "Shirt Accessory",
		[66] = "Pants Accessory",
		[67] = "Jacket Accessory",
		[68] = "Sweater Accessory",
		[69] = "Shorts Accessory",
		[70] = "Left Shoe Accessory",
		[71] = "Right Shoe Accessory",
		[72] = "Dress Skirt Accessory",
		[76] = "Eyebrow Accessory",
		[77] = "Eyelash Accessory",
		[78] = "Mood Animation",
		[79] = "Dynamic Head"
	};

	private static readonly string[] LimitedRestrictions = ["Limited", "LimitedUnique", "Collectible"];

	public sealed class NewItem
	{
		public long Id { get; init; }

		public string Name { get; init; } = string.Empty;

		public int? Price { get; init; }

		public string Creator { get; init; } = string.Empty;

		public string TypeName { get; init; } = string.Empty;

		public bool Limited { get; init; }

		public int Favorites { get; init; }

		public string Image { get; init; } = string.Empty;
	}

	public static async Task<List<NewItem>> GetNewestAsync(CancellationToken token)
	{
		SearchResponse? response = await GitHubCache.GetJsonAsync<SearchResponse>(NewestUrl, CacheAge, token).ConfigureAwait(false);
		List<SearchItem> source = response?.Data ?? [];
		if (source.Count == 0)
			return [];

		List<long> ids = source.Where(item => item.Id > 0).Select(item => item.Id).Distinct().ToList();
		Dictionary<long, string> images = await LoadThumbnailsAsync(ids, token).ConfigureAwait(false);
		List<NewItem> items = new(source.Count);
		foreach (SearchItem item in source)
		{
			items.Add(new NewItem
			{
				Id = item.Id,
				Name = item.Name ?? string.Empty,
				Price = item.Price ?? item.LowestPrice,
				Creator = item.CreatorName ?? string.Empty,
				TypeName = ResolveTypeName(item.AssetType),
				Limited = item.ItemRestrictions?.Any(restriction => LimitedRestrictions.Contains(restriction, StringComparer.OrdinalIgnoreCase)) == true,
				Favorites = item.FavoriteCount ?? 0,
				Image = images.TryGetValue(item.Id, out string? image) ? image : string.Empty
			});
		}

		return items;
	}

	private static async Task<Dictionary<long, string>> LoadThumbnailsAsync(List<long> ids, CancellationToken token)
	{
		Dictionary<long, string> images = new();
		if (ids.Count == 0)
			return images;

		try
		{
			string url = ThumbnailUrl + string.Join(',', ids) + ThumbnailOptions;
			ThumbnailResponse? response = await GitHubCache.GetJsonAsync<ThumbnailResponse>(url, CacheAge, token).ConfigureAwait(false);
			foreach (ThumbnailItem item in response?.Data ?? [])
			{
				if (item.TargetId > 0 && !string.IsNullOrEmpty(item.ImageUrl))
					images[item.TargetId] = item.ImageUrl;
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxCatalog", "The item thumbnails could not be loaded: " + ex.Message);
		}

		return images;
	}

	private static string ResolveTypeName(int? assetType)
	{
		if (assetType is not int value || value <= 0)
			return string.Empty;
		return AssetTypes.TryGetValue(value, out string? name) ? name : "Asset";
	}

	private sealed class SearchResponse
	{
		[JsonPropertyName("data")]
		public List<SearchItem> Data { get; set; } = [];
	}

	private sealed class SearchItem
	{
		[JsonPropertyName("id")]
		public long Id { get; set; }

		[JsonPropertyName("name")]
		public string? Name { get; set; }

		[JsonPropertyName("price")]
		public int? Price { get; set; }

		[JsonPropertyName("lowestPrice")]
		public int? LowestPrice { get; set; }

		[JsonPropertyName("creatorName")]
		public string? CreatorName { get; set; }

		[JsonPropertyName("assetType")]
		public int? AssetType { get; set; }

		[JsonPropertyName("itemRestrictions")]
		public List<string>? ItemRestrictions { get; set; }

		[JsonPropertyName("favoriteCount")]
		public int? FavoriteCount { get; set; }
	}

	private sealed class ThumbnailResponse
	{
		[JsonPropertyName("data")]
		public List<ThumbnailItem> Data { get; set; } = [];
	}

	private sealed class ThumbnailItem
	{
		[JsonPropertyName("targetId")]
		public long TargetId { get; set; }

		[JsonPropertyName("imageUrl")]
		public string? ImageUrl { get; set; }
	}
}
