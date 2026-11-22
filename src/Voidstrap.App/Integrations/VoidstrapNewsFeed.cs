using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace Voidstrap.Integrations;

internal static class VoidstrapNewsFeed
{
    private const string ApiUrl = "https://api.github.com/repos/KloBraticc/VoidstrapNews-/contents/feed.json?ref=main";
    private const string RawUrl = "https://raw.githubusercontent.com/KloBraticc/VoidstrapNews-/main/feed.json";
    private const int MaxFeedBytes = 2 * 1024 * 1024;
    private const int MaxPosts = 500;
    private const int MaxSeenPosts = 600;
    private const int MaxNotificationsPerCheck = 3;
    private const int FreshDays = 3;
    private const int MaxNotificationText = 300;
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(20);
    private static readonly Regex PostId = new("^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly object NotifyLock = new();
    private static string? _etag;
    private static int _watching;

    private static string CachePath => Path.Combine(Paths.Cache, "NewsFeed.json");

    private static string SeenPath => Path.Combine(Paths.Data, "SeenNews.json");

    private static HttpClient CreateClient()
    {
        HttpClient client = VpnHttpClient.Create(TimeSpan.FromSeconds(20));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Voidstrap", "1.0"));
        return client;
    }

    internal static async Task WatchAsync(CancellationToken token)
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::WatchAsync";
        if (Interlocked.Exchange(ref _watching, 1) != 0)
            return;
        try
        {
            await Task.Delay(FirstCheckDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string? json = await FetchAsync(token).ConfigureAwait(false);
                    if (json != null && Parse(json) is { Count: > 0 } posts)
                    {
                        NotifyNewPosts(posts);
                        await WriteCacheAsync(json).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "News check failed: " + ex.GetType().Name);
                }
                await Task.Delay(CheckInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref _watching, 0);
        }
    }

    internal static async Task<string?> FetchAsync(CancellationToken token)
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::FetchAsync";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
            if (_etag != null && EntityTagHeaderValue.TryParse(_etag, out EntityTagHeaderValue? tag))
                request.Headers.IfNoneMatch.Add(tag);
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return null;
            if (response.IsSuccessStatusCode)
            {
                _etag = response.Headers.ETag?.ToString();
                return await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, MaxFeedBytes, token);
            }
            App.Logger.WriteLine(LOG_IDENT, "GitHub API returned " + (int)response.StatusCode + ", using the raw file");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "GitHub API failed: " + ex.GetType().Name + ", using the raw file");
        }
        using HttpRequestMessage rawRequest = new(HttpMethod.Get, RawUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        rawRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using HttpResponseMessage rawResponse = await Http.SendAsync(rawRequest, HttpCompletionOption.ResponseHeadersRead, token);
        rawResponse.EnsureSuccessStatusCode();
        return await Voidstrap.Utility.Http.ReadStringBoundedAsync(rawResponse.Content, MaxFeedBytes, token);
    }

    internal static List<NewsPost>? Parse(string json)
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::Parse";
        try
        {
            return Sanitize(JsonConvert.DeserializeObject<NewsFeed>(json));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Feed is not valid: " + ex.GetType().Name);
            return null;
        }
    }

    private static List<NewsPost> Sanitize(NewsFeed? feed)
    {
        List<NewsPost> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (NewsPost? post in feed?.Posts ?? [])
        {
            if (post == null || string.IsNullOrWhiteSpace(post.Title))
                continue;
            post.Id = post.Id != null && PostId.IsMatch(post.Id) && seen.Add(post.Id) ? post.Id : null;
            post.Title = post.Title.Trim();
            post.Body = post.Body?.Trim() ?? string.Empty;
            post.Date = post.Date?.Trim() ?? string.Empty;
            post.Image = IsHttpsUrl(post.Image) ? post.Image : null;
            post.Links = (post.Links ?? []).Where(link => link != null && !string.IsNullOrWhiteSpace(link.Label) && IsHttpsUrl(link.Url)).ToList();
            result.Add(post);
            if (result.Count >= MaxPosts)
                break;
        }
        return result;
    }

    internal static bool IsHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    internal static bool IsNewsNotification(string key) => key.StartsWith("info:news:", StringComparison.Ordinal);

    internal static void NotifyNewPosts(IReadOnlyList<NewsPost> posts)
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::NotifyNewPosts";
        if (posts.Count == 0 || !Paths.Initialized)
            return;
        DateTime today = DateTime.Now.Date;
        int notified = 0;
        int added = 0;
        lock (NotifyLock)
        {
            List<string> seen = ReadSeen();
            HashSet<string> known = new(seen, StringComparer.Ordinal);
            foreach (NewsPost post in posts)
            {
                string key = KeyOf(post);
                if (!known.Add(key))
                    continue;
                seen.Add(key);
                added++;
                if (notified >= MaxNotificationsPerCheck || !IsFresh(post, today))
                    continue;
                AppNotifications.RecordInfo("news:" + key, post.Title, Shorten(post.Body));
                notified++;
            }
            if (added == 0)
                return;
            if (seen.Count > MaxSeenPosts)
                seen.RemoveRange(0, seen.Count - MaxSeenPosts);
            WriteSeen(seen);
        }
        App.Logger.WriteLine(LOG_IDENT, $"Found {added} unseen news posts, {notified} recent enough to notify");
    }

    private static List<string> ReadSeen()
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::ReadSeen";
        try
        {
            if (File.Exists(SeenPath))
                return JsonFile.Deserialize<List<string>>(SeenPath, JsonOptions.Tolerant, 256 * 1024)?.Where(key => !string.IsNullOrEmpty(key)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Seen news list could not be read: " + ex.GetType().Name);
        }
        return [];
    }

    private static void WriteSeen(List<string> seen)
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::WriteSeen";
        try
        {
            Directory.CreateDirectory(Paths.Data);
            JsonFile.SerializeAtomic(SeenPath, seen, createBackup: false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Seen news list could not be saved: " + ex.GetType().Name);
        }
    }

    private static string KeyOf(NewsPost post)
    {
        if (!string.IsNullOrEmpty(post.Id))
            return post.Id;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(post.Date + "\n" + post.Title));
        return "h" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private static bool IsFresh(NewsPost post, DateTime today)
    {
        if (!DateTime.TryParseExact(post.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime posted))
            return false;
        double age = (today - posted.Date).TotalDays;
        return age >= -1 && age <= FreshDays;
    }

    private static string Shorten(string text)
    {
        string flat = Regex.Replace(text, "\\s+", " ").Trim();
        return flat.Length <= MaxNotificationText ? flat : flat[..MaxNotificationText].TrimEnd() + "...";
    }

    internal static async Task<string?> ReadCacheAsync()
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::ReadCacheAsync";
        await CacheLock.WaitAsync();
        try
        {
            FileInfo file = new(CachePath);
            if (!file.Exists || file.Length > MaxFeedBytes)
                return null;
            return await File.ReadAllTextAsync(file.FullName);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Cache read failed: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    internal static async Task WriteCacheAsync(string json)
    {
        const string LOG_IDENT = "VoidstrapNewsFeed::WriteCacheAsync";
        await CacheLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Paths.Cache);
            string temp = CachePath + ".tmp";
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, CachePath, true);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Cache write failed: " + ex.GetType().Name);
        }
        finally
        {
            CacheLock.Release();
        }
    }
}
