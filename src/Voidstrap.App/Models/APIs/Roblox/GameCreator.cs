using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Roblox;

public class GameCreator
{
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("isRNVAccount")]
	public bool IsRNVAccount { get; set; }

	[JsonPropertyName("hasVerifiedBadge")]
	public bool HasVerifiedBadge { get; set; }
}
