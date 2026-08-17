using System.Text.Json.Serialization;

namespace VoidstrapClient.WebServer;

internal class AssetInformation
{
	[JsonPropertyName("locations")]
	public AssetLocation[]? Locations { get; set; }

	[JsonPropertyName("requestId")]
	public string? RequestId { get; set; }

	[JsonPropertyName("assetTypeId")]
	public int AssetTypeId { get; set; }
}
