using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Voidstrap.Models;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed partial class NewsViewModel : ObservableObject, IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/KloBraticc/VoidstrapNews-/contents/feed.json?ref=main";
    private const string RawUrl = "https://raw.githubusercontent.com/KloBraticc/VoidstrapNews-/main/feed.json";
    private const int MaxFeedBytes = 2 * 1024 * 1024;
    private const int FeaturedLimit = 5;
    private const int MaxPosts = 500;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static string? _etag;

    private CancellationTokenSource? _cts;
    private string _shownJson = string.Empty;
    private bool _disposed;

    public ObservableCollection<NewsPost> Posts { get; } = [];

    public ObservableCollection<NewsPost> Featured { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeatured))]
    public partial NewsPost? FeaturedPost { get; set; }

    public bool HasFeatured => FeaturedPost != null;

    public bool HasPosts => Posts.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; set; }

    public bool ShowEmpty => !IsLoading && Featured.Count == 0;

    private static string CachePath => Path.Combine(Paths.Cache, "NewsFeed.json");

    public NewsViewModel()
    {
        _ = InitializeAsync();
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(20));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Voidstrap", "1.0"));
        return client;
    }

    private async Task InitializeAsync()
    {
        string? cached = await ReadCacheAsync();
        if (!string.IsNullOrEmpty(cached) && !_disposed)
            Apply(cached);
        await LoadAsync(true);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(true);

    internal Task CheckForUpdatesAsync() => IsLoading ? Task.CompletedTask : LoadAsync(false);

    [RelayCommand]
    private void SelectFeatured(NewsPost? post)
    {
        if (post != null && Featured.Contains(post))
            SetFeatured(post);
    }

    [RelayCommand]
    private void PreviousFeatured() => StepFeatured(-1);

    [RelayCommand]
    private void NextFeatured() => StepFeatured(1);

    internal void StepFeatured(int step)
    {
        if (Featured.Count == 0)
            return;
        int index = FeaturedPost == null ? 0 : Math.Max(0, Featured.IndexOf(FeaturedPost));
        SetFeatured(Featured[((index + step) % Featured.Count + Featured.Count) % Featured.Count]);
    }

    private void SetFeatured(NewsPost post)
    {
        if (FeaturedPost != null)
            FeaturedPost.IsCurrent = false;
        post.IsCurrent = true;
        FeaturedPost = post;
    }

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (IsHttpsUrl(url))
            Utilities.OpenWebLink(url!);
    }

    private async Task LoadAsync(bool showSpinner)
    {
        const string LOG_IDENT = "NewsViewModel::LoadAsync";
        if (_disposed)
            return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        if (showSpinner)
            IsLoading = true;
        try
        {
            string? json = await FetchAsync(token);
            if (token.IsCancellationRequested || _disposed || json == null)
                return;
            if (Apply(json))
                await WriteCacheAsync(json);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Feed load failed: " + ex.GetType().Name);
        }
        finally
        {
            if (!token.IsCancellationRequested && !_disposed)
                IsLoading = false;
        }
    }

    private static async Task<string?> FetchAsync(CancellationToken token)
    {
        const string LOG_IDENT = "NewsViewModel::FetchAsync";
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

    private bool Apply(string json)
    {
        const string LOG_IDENT = "NewsViewModel::Apply";
        if (string.Equals(json, _shownJson, StringComparison.Ordinal))
            return false;
        List<NewsPost> posts;
        try
        {
            posts = Sanitize(JsonConvert.DeserializeObject<NewsFeed>(json));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Feed is not valid: " + ex.GetType().Name);
            return false;
        }
        if (posts.Count == 0)
            return false;
        string? currentTitle = FeaturedPost?.Title;
        _shownJson = json;
        Posts.Clear();
        Featured.Clear();
        FeaturedPost = null;
        foreach (NewsPost post in posts)
        {
            if (Featured.Count < FeaturedLimit)
                Featured.Add(post);
            else
                Posts.Add(post);
        }
        SetFeatured(Featured.FirstOrDefault(post => post.Title == currentTitle) ?? Featured[0]);
        OnPropertyChanged(nameof(HasPosts));
        OnPropertyChanged(nameof(ShowEmpty));
        return true;
    }

    private static List<NewsPost> Sanitize(NewsFeed? feed)
    {
        List<NewsPost> result = [];
        foreach (NewsPost? post in feed?.Posts ?? [])
        {
            if (post == null || string.IsNullOrWhiteSpace(post.Title))
                continue;
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

    private static bool IsHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static async Task<string?> ReadCacheAsync()
    {
        const string LOG_IDENT = "NewsViewModel::ReadCacheAsync";
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

    private static async Task WriteCacheAsync(string json)
    {
        const string LOG_IDENT = "NewsViewModel::WriteCacheAsync";
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
