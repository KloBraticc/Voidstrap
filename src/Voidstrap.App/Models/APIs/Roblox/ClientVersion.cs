using System;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Roblox;

public class ClientVersion
{
	[JsonPropertyName("version")]
	public string Version { get; set; }

	[JsonPropertyName("clientVersionUpload")]
	public string VersionGuid { get; set; }

	[JsonPropertyName("bootstrapperVersion")]
	public string BootstrapperVersion { get; set; }

	public DateTime? Timestamp { get; set; }

	public bool IsBehindDefaultChannel { get; set; }
}
