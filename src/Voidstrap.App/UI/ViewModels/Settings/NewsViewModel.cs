using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.UI.ViewModels.Settings
{
    internal sealed class NewsItemDto
    {
        public string? Title { get; set; }
        public string? Date { get; set; }
        public string? Content { get; set; }
        public string? ImageUrl { get; set; }
    }

    public sealed partial class NewsViewModel : ObservableObject, IDisposable
    {
        private const int MaxNewsJsonBytes = 2 * 1024 * 1024;
        private const int MaxNewsImageBytes = 8 * 1024 * 1024;
        private const int MaxNewsItems = 100;
        private const long MaxRetainedImageBytes = 32L * 1024 * 1024;
        private static readonly HttpClient _http = CreateHttpClient();
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private CancellationTokenSource _cts = new();
        private bool _disposed;

        public const string FeedUrl =
            "https://raw.githubusercontent.com/KloBraticc/VoidstrapNews-/main/news.json";

        private static string BasePath => Paths.Cache;
        private string CachePath => Path.Combine(BasePath, "news_cache.json");
        private string ETagPath => Path.Combine(BasePath, "news_cache.etag");

        [ObservableProperty] private ObservableCollection<NewsItem> newsItems = new();
        [ObservableProperty] private string lastUpdatedText = "Loading...";
        [ObservableProperty] private bool isLoading = true;

        private static HttpClient CreateHttpClient()
        {
            var http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15), handler =>
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Voidstrap", "1.0"));
            return http;
        }

        public NewsViewModel()
        {
            Directory.CreateDirectory(BasePath);
            _ = SafeRefreshAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            await SafeRefreshAsync();
        }

        [RelayCommand]
        private void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenUrl ERROR] {ex}");
            }
        }

        private async Task SafeRefreshAsync()
        {
            if (_disposed)
                return;
            bool lockHeld = false;
            try
            {
                if (!await _loadLock.WaitAsync(0, _cts.Token))
                    return;
                lockHeld = true;
                await LoadNewsAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (lockHeld)
                    _loadLock.Release();
            }
        }

        private async Task LoadNewsAsync(CancellationToken ct)
        {
            try
            {
                await SetLoadingAsync(true, "Loading...");

                string? json = null;
                bool fromCache = false;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);

                    if (File.Exists(ETagPath))
                    {
                            var etag = await SafeReadAllTextAsync(ETagPath, 4096, ct);
                        if (!string.IsNullOrWhiteSpace(etag))
                            req.Headers.TryAddWithoutValidation("If-None-Match", etag.Trim());
                    }

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (resp.StatusCode == HttpStatusCode.NotModified && File.Exists(CachePath))
                    {
                        json = await SafeReadAllTextAsync(CachePath, MaxNewsJsonBytes, ct);
                        fromCache = true;
                        Debug.WriteLine("[News] Using cached JSON (304 Not Modified).");
                    }
                    else
                    {
                        resp.EnsureSuccessStatusCode();
                        json = await Voidstrap.Utility.Http.ReadStringBoundedAsync(resp.Content, MaxNewsJsonBytes, ct);
                        await SafeWriteAllTextAsync(CachePath, json, ct);

                        if (resp.Headers.ETag is not null)
                            await SafeWriteAllTextAsync(ETagPath, resp.Headers.ETag.ToString(), ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[News] Online fetch failed: {ex.Message}");
                    if (File.Exists(CachePath))
                    {
                        json = await SafeReadAllTextAsync(CachePath, MaxNewsJsonBytes, ct);
                        fromCache = true;
                    }
                }

                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("No news data available (network and cache unavailable).");

                var items = ParseNews(json);
                bool hasNewContent = !NewsItems.Select(n => n.Title).SequenceEqual(items.Select(i => i.Title));

                if (!hasNewContent && fromCache)
                {
                    await SetLoadingAsync(false, $"Last updated: {DateTime.Now:G} (no new items)");
                    return;
                }

                using SemaphoreSlim imageGate = new SemaphoreSlim(4, 4);
                long retainedImageBytes = 0;
                var imageTasks = items.Select(async item =>
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(item.ImageUrl))
                        return;

                    if (!Uri.TryCreate(item.ImageUrl, UriKind.Absolute, out var uri))
                        return;

                    var fileName = SanitizeFileName(Path.GetFileName(uri.LocalPath) ?? $"img_{Guid.NewGuid():N}.bin");
                    var localPath = Path.Combine(BasePath, fileName);

                    await imageGate.WaitAsync(ct);
                    try
                    {
                        if (Volatile.Read(ref retainedImageBytes) >= MaxRetainedImageBytes)
                            return;
                        if (!File.Exists(localPath) || !fromCache)
                        {
                            using HttpRequestMessage imageRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                            using HttpResponseMessage imageResponse = await _http.SendAsync(imageRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                            imageResponse.EnsureSuccessStatusCode();
                            var data = await Voidstrap.Utility.Http.ReadBytesBoundedAsync(imageResponse.Content, MaxNewsImageBytes, ct);
                            await SafeWriteAllBytesAsync(localPath, data, ct);
                        }

                        var bmp = await LoadLocalImageAsync(localPath, ct);
                        if (bmp != null)
                        {
                            int bitsPerPixel = bmp.Format.BitsPerPixel > 0 ? bmp.Format.BitsPerPixel : 32;
                            long imageBytes = ((long)Math.Max(1, bmp.PixelWidth) * Math.Max(1, bmp.PixelHeight) * bitsPerPixel + 7) / 8;
                            long totalBytes = Interlocked.Add(ref retainedImageBytes, imageBytes);
                            if (totalBytes <= MaxRetainedImageBytes)
                                item.Image = bmp;
                            else
                                Interlocked.Add(ref retainedImageBytes, -imageBytes);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ImageLoad ERROR] {ex}");
                    }
                    finally
                    {
                        imageGate.Release();
                    }
                });

                await Task.WhenAll(imageTasks);

                await OnUiAsync(() =>
                {
                    NewsItems.Clear();
                    foreach (var n in items.OrderByDescending(i => i.Date))
                        NewsItems.Add(n);

                    LastUpdatedText = $"Last updated: {DateTime.Now:G}";
                    IsLoading = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadNewsAsync ERROR] {ex}");
                await OnUiAsync(() =>
                {
                    NewsItems.Clear();
                    NewsItems.Add(new NewsItem
                    {
                        Title = "Failed to load news",
                        Date = DateTime.Now,
                        Content = ex.Message
                    });
                    LastUpdatedText = $"Last checked: {DateTime.Now:G} (failed)";
                    IsLoading = false;
                });
            }
        }

        private static List<NewsItem> ParseNews(string json)
        {
            try
            {
                var token = JToken.Parse(json);
                JArray arr = token switch
                {
                    JArray a => a,
                    JObject o when o.TryGetValue("items", StringComparison.OrdinalIgnoreCase, out var t) && t is JArray ja => ja,
                    _ => throw new FormatException("Unexpected news format.")
                };

                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                };

                var dtos = arr.ToObject<NewsItemDto[]>(Newtonsoft.Json.JsonSerializer.Create(settings)) ?? Array.Empty<NewsItemDto>();

                static DateTime ParseDate(string? s) =>
                    DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
                        ? d.ToLocalTime()
                        : DateTime.MinValue;

                return dtos.Take(MaxNewsItems).Select(d => new NewsItem
                {
                    Title = d.Title?.Trim() ?? "Untitled",
                    Content = d.Content?.Trim() ?? string.Empty,
                    Date = ParseDate(d.Date),
                    ImageUrl = d.ImageUrl?.Trim() ?? string.Empty
                }).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ParseNews ERROR] {ex}");
                throw;
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
        }

        private static async Task<string> SafeReadAllTextAsync(string path, int maxBytes, CancellationToken ct)
        {
            byte[] data = await SafeReadAllBytesAsync(path, maxBytes, ct);
            return System.Text.Encoding.UTF8.GetString(data);
        }

        private static async Task SafeWriteAllTextAsync(string path, string content, CancellationToken ct)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            using var sw = new StreamWriter(fs);
            ct.ThrowIfCancellationRequested();
            await sw.WriteAsync(content.AsMemory(), ct);
        }

        private static async Task SafeWriteAllBytesAsync(string path, byte[] content, CancellationToken ct)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            ct.ThrowIfCancellationRequested();
            await fs.WriteAsync(content.AsMemory(0, content.Length), ct);
        }

        private static async Task<byte[]> SafeReadAllBytesAsync(string path, int maxBytes, CancellationToken ct)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            if (fs.Length < 0 || fs.Length > maxBytes)
                throw new InvalidDataException("Cached news file is too large");
            var buffer = new byte[checked((int)fs.Length)];
            int bytesRead = 0;
            while (bytesRead < buffer.Length)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(buffer.AsMemory(bytesRead, buffer.Length - bytesRead), ct);
                if (read == 0)
                    throw new EndOfStreamException();
                bytesRead += read;
            }
            return buffer;
        }

        private static async Task<BitmapSource?> LoadLocalImageAsync(string path, CancellationToken ct)
        {
            try
            {
                var data = await SafeReadAllBytesAsync(path, MaxNewsImageBytes, ct);
                return await Task.Run(() =>
                {
                    return Voidstrap.Utility.SafeImaging.FromBytes(data, 900);
                }, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadLocalImageAsync ERROR] {ex}");
                return null;
            }
        }

        private static Task OnUiAsync(Action action)
        {
            if (Application.Current?.Dispatcher is Dispatcher d)
                return d.InvokeAsync(action).Task;

            action();
            return Task.CompletedTask;
        }

        private Task SetLoadingAsync(bool value, string? text = null) =>
            OnUiAsync(() =>
            {
                IsLoading = value;
                if (!string.IsNullOrWhiteSpace(text))
                    LastUpdatedText = text!;
            });

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
            NewsItems.Clear();
            GC.SuppressFinalize(this);
        }
    }
}
