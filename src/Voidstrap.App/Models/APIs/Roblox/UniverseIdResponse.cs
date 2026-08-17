using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Roblox;

public class UniverseIdResponse
{
	[JsonPropertyName("universeId")]
	public long UniverseId { get; set; }
}
