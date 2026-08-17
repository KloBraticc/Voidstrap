using System.Collections.Generic;
using System.Text.Json.Serialization;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.WebServer.Models;

internal class AvatarItem
{
	[JsonIgnore]
	public ulong Id { get; set; }

	[JsonPropertyName("type")]
	public AvatarAssetType Type { get; set; }

	[JsonPropertyName("items")]
	public List<ulong> Items { get; set; } = new List<ulong>();

	[JsonPropertyName("assetVersion")]
	public int AssetVersion { get; set; }
}
