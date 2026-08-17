using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Roblox;

public class GameDetailResponse
{
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[JsonPropertyName("rootPlaceId")]
	public long RootPlaceId { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("description")]
	public string Description { get; set; }

	[JsonPropertyName("sourceName")]
	public string SourceName { get; set; }

	[JsonPropertyName("sourceDescription")]
	public string SourceDescription { get; set; }

	[JsonPropertyName("creator")]
	public GameCreator Creator { get; set; }

	[JsonPropertyName("price")]
	public long? Price { get; set; }

	[JsonPropertyName("allowedGearGenres")]
	public IEnumerable<string> AllowedGearGenres { get; set; }

	[JsonPropertyName("allowedGearCategories")]
	public IEnumerable<string> AllowedGearCategories { get; set; }

	[JsonPropertyName("isGenreEnforced")]
	public bool IsGenreEnforced { get; set; }

	[JsonPropertyName("copyingAllowed")]
	public bool CopyingAllowed { get; set; }

	[JsonPropertyName("playing")]
	public long Playing { get; set; }

	[JsonPropertyName("visits")]
	public long Visits { get; set; }

	[JsonPropertyName("maxPlayers")]
	public int MaxPlayers { get; set; }

	[JsonPropertyName("created")]
	public DateTime Created { get; set; }

	[JsonPropertyName("updated")]
	public DateTime Updated { get; set; }

	[JsonPropertyName("studioAccessToApisAllowed")]
	public bool StudioAccessToApisAllowed { get; set; }

	[JsonPropertyName("createVipServersAllowed")]
	public bool CreateVipServersAllowed { get; set; }

	[JsonPropertyName("universeAvatarType")]
	public string UniverseAvatarType { get; set; }

	[JsonPropertyName("genre")]
	public string Genre { get; set; }

	[JsonPropertyName("isAllGenre")]
	public bool IsAllGenre { get; set; }

	[JsonPropertyName("isFavoritedByUser")]
	public bool IsFavoritedByUser { get; set; }

	[JsonPropertyName("favoritedCount")]
	public int FavoritedCount { get; set; }
}
