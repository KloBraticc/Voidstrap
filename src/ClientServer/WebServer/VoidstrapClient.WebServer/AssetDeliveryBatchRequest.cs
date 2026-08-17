using System.Text.Json.Serialization;

namespace VoidstrapClient.WebServer;

internal class AssetDeliveryBatchRequest
{
	[JsonPropertyName("assetId")]
	public ulong AssetId { get; set; }

	[JsonPropertyName("requestId")]
	public string? RequestId { get; set; }
}
