using System.Text.Json.Serialization;

namespace Voidstrap.Models.VoidstrapRPC;

internal class RichPresenceImage
{
	[JsonPropertyName("assetId")]
	public ulong? AssetId { get; set; }

	[JsonPropertyName("hoverText")]
	public string? HoverText { get; set; }

	[JsonPropertyName("clear")]
	public bool Clear { get; set; }

	[JsonPropertyName("reset")]
	public bool Reset { get; set; }

	public string? CustomKey { get; set; }
}
