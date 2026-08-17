using System.Text.Json.Serialization;

namespace VoidstrapClient.WebServer;

internal class AssetLocation
{
	[JsonPropertyName("location")]
	public string? Location { get; set; }
}
