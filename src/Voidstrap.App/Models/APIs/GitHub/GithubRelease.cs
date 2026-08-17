using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.GitHub;

public class GithubRelease
{
	[JsonPropertyName("tag_name")]
	public string TagName { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("body")]
	public string Body { get; set; }

	[JsonPropertyName("created_at")]
	public string CreatedAt { get; set; }

	[JsonPropertyName("prerelease")]
	public bool Prerelease { get; set; }

	[JsonPropertyName("draft")]
	public bool Draft { get; set; }

	[JsonPropertyName("assets")]
	public List<GithubReleaseAsset>? Assets { get; set; }
}
