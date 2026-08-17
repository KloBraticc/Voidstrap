public class GithubReleaseAsset
{
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = null!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}