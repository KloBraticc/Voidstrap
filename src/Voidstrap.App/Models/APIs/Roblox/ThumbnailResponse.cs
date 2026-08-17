using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Roblox;

public class ThumbnailResponse
{
	[JsonPropertyName("requestId")]
	public string RequestId { get; set; }

	[JsonPropertyName("errorCode")]
	public int ErrorCode { get; set; }

	[JsonPropertyName("errorMessage")]
	public string? ErrorMessage { get; set; }

	[JsonPropertyName("targetId")]
	public long TargetId { get; set; }

	[JsonPropertyName("state")]
	public string State { get; set; }

	[JsonPropertyName("imageUrl")]
	public string? ImageUrl { get; set; }
}
