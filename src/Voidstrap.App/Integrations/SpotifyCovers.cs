namespace Voidstrap.Integrations;

internal static class SpotifyCovers
{
    private const string LOG_IDENT = "SpotifyCovers";

    private const int MaximumBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Client = VpnHttpClient.Create(TimeSpan.FromSeconds(8));

    private sealed record Entry(string Url, byte[]? Bytes, DateTime At);

    private static Entry? _last;

    public static async Task<byte[]?> ReadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        Entry? last = _last;
        if (last != null && last.Url == url && (last.Bytes != null || DateTime.UtcNow - last.At < RetryDelay))
            return last.Bytes;
        byte[]? bytes = await DownloadAsync(url).ConfigureAwait(false);
        _last = new Entry(url, bytes, DateTime.UtcNow);
        return bytes;
    }

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;
        if (uri.IsFile)
            return ReadFile(uri.LocalPath);
        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/image/", StringComparison.Ordinal))
            uri = new Uri("https://i.scdn.co" + uri.AbsolutePath);
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
                return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return bytes.Length is > 0 and <= MaximumBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The cover could not be downloaded: " + ex.GetType().Name);
            return null;
        }
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists && info.Length is > 0 and <= MaximumBytes ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The cover file could not be read: " + ex.GetType().Name);
            return null;
        }
    }
}
