using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility
{
    public static class AppImage
    {
        private const long MaxDownloadBytes = 8L * 1024 * 1024;

        private const long MaxCachedBytes = 32L * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> Downloads = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, byte[]> ByteCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentQueue<string> ByteCacheOrder = new();

        private static long _cachedBytes;

        private static long _proxyBlockedUntil;

        private static readonly Dictionary<string, string> StaticAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://www.pngall.com/wp-content/uploads/17/Roblox-Cash-Icon-Illustration-PNG.png"] = "/assets/img/app/robux-cash.png",
            ["https://avatars.githubusercontent.com/u/195697851?v=4"] = "/assets/img/app/about-1.png",
            ["https://avatars.githubusercontent.com/u/168036205?v=4"] = "/assets/img/app/about-2.png",
            ["https://avatars.githubusercontent.com/u/193777251?v=4"] = "/assets/img/app/about-3.png",
            ["https://avatars.githubusercontent.com/u/213444273?v=4"] = "/assets/img/app/about-4.png",
            ["https://avatars.githubusercontent.com/u/105540464?v=4"] = "/assets/img/app/about-5.png",
            ["https://avatars.githubusercontent.com/u/194078013?v=4"] = "/assets/img/app/about-6.png",
            ["https://avatars.githubusercontent.com/u/186699266"] = "/assets/img/app/ext-fleasion.png",
            ["https://raw.githubusercontent.com/KloBraticc/RiShade/main/Images/RiShde.png"] = "/assets/img/app/ext-rishade.png",
            ["https://raw.githubusercontent.com/MaximumADHD/Roblox-API-Dump-Tool/master/Resources/AppLogo.png"] = "/assets/img/app/ext-apidump.png",
            ["https://github.com/rojo-rbx.png"] = "/assets/img/app/ext-rojo.png",
            ["https://images.rbxcdn.com/905bd722ee0a6ceda3caacde54c0b081.png"] = "/assets/img/app/studio.png",
            ["https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123"] = "/assets/img/app/client-2007.png",
            ["https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633"] = "/assets/img/app/client-2010.png",
            ["https://static.wikia.nocookie.net/roblox/images/1/15/2011_Icon.png/revision/latest?cb=20250329002829"] = "/assets/img/app/client-2013.png",
        };

        public static string BaseUrl => App.WebsiteBaseUrl;

        public static bool IsWebsiteUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            string baseUrl = BaseUrl;
            return url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase) || url.StartsWith(App.WebsiteLocalUrl, StringComparison.OrdinalIgnoreCase) || url.StartsWith(App.WebsiteProductionUrl, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsRemote(string url)
        {
            return !string.IsNullOrEmpty(url)
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        public static string? ResolveAsset(string url)
        {
            if (StaticAssets.TryGetValue(url ?? string.Empty, out string asset))
                return BaseUrl.TrimEnd('/') + asset;
            return null;
        }

        public static string ProxyUrl(string url)
        {
            return BaseUrl.TrimEnd('/') + "/api/img?url=" + Uri.EscapeDataString(url);
        }

        private static string WsrvUrl(string url, int size)
        {
            int s = size > 0 ? size : 256;
            return "https://wsrv.nl/?url=" + Uri.EscapeDataString(url) + "&output=png&w=" + s + "&h=" + s + "&fit=inside&we";
        }

        public static IReadOnlyList<string> GetCandidates(string url, int size = 0)
        {
            if (string.IsNullOrEmpty(url))
                return Array.Empty<string>();
            string? asset = ResolveAsset(url);
            if (asset != null)
                return new[] { asset, WsrvUrl(url, size), url };
            if (IsWebsiteUrl(url))
                return new[] { url };
            if (IsRemote(url))
                return new[] { ProxyUrl(url), url };
            return new[] { url };
        }

        public static string ResolveUrl(string url, int size = 0)
        {
            IReadOnlyList<string> candidates = GetCandidates(url, size);
            return candidates.Count > 0 ? candidates[0] : url;
        }

        public static BitmapSource? LoadSync(string url, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            foreach (string candidate in GetCandidates(url, decodeWidth))
            {
                try
                {
                    if (!IsRemote(candidate))
                    {
                        BitmapSource? local = Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                            ? SafeImaging.FromUri(parsed, decodeWidth)
                            : SafeImaging.FromFile(candidate, decodeWidth);
                        if (local != null)
                            return local;
                        continue;
                    }
                    byte[] bytes = DownloadBytesAsync(candidate).GetAwaiter().GetResult();
                    if (bytes == null || bytes.Length == 0)
                        continue;
                    BitmapSource? bmp = SafeImaging.FromBytes(bytes, decodeWidth);
                    if (bmp != null)
                        return bmp;
                }
                catch
                {
                }
            }
            return null;
        }

        public static async Task<BitmapSource?> LoadAsync(string url, int decodeWidth = 0, CancellationToken ct = default)
        {
            foreach (string candidate in GetCandidates(url, decodeWidth))
            {
                if (ct.IsCancellationRequested)
                    return null;
                byte[] bytes = await DownloadBytesAsync(candidate, ct).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                    continue;
                BitmapSource? bmp = await Task.Run(delegate
                {
                    try
                    {
                        return SafeImaging.FromBytes(bytes, decodeWidth);
                    }
                    catch
                    {
                        return null;
                    }
                }, ct).ConfigureAwait(false);
                if (bmp != null)
                    return bmp;
            }
            return null;
        }

        public static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            if (ByteCache.TryGetValue(url, out byte[]? cached))
                return cached;
            if (IsImageProxyUrl(url) && Environment.TickCount64 < Interlocked.Read(ref _proxyBlockedUntil))
                return null;
            Lazy<Task<byte[]?>> candidate = new(() => DownloadBytesCoreAsync(url), LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<byte[]?>> active = Downloads.GetOrAdd(url, candidate);
            try
            {
                return await active.Value.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (active.IsValueCreated && active.Value.IsCompleted && Downloads.TryGetValue(url, out Lazy<Task<byte[]?>>? current) && ReferenceEquals(current, active))
                    Downloads.TryRemove(url, out _);
            }
        }

        private static async Task<byte[]?> DownloadBytesCoreAsync(string url)
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && IsImageProxyUrl(url))
                {
                    TimeSpan delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
                        delay = retryDate - DateTimeOffset.UtcNow;
                    long milliseconds = (long)Math.Clamp(delay.TotalMilliseconds, 5000, 120000);
                    Interlocked.Exchange(ref _proxyBlockedUntil, Environment.TickCount64 + milliseconds);
                    return null;
                }
                long declared = response.Content.Headers.ContentLength ?? 0;
                if (!response.IsSuccessStatusCode || declared > MaxDownloadBytes)
                    return null;
                await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using MemoryStream output = new(declared > 0 && declared < MaxDownloadBytes ? (int)declared : 81920);
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (output.Length + read > MaxDownloadBytes)
                        return null;
                    output.Write(buffer, 0, read);
                }
                if (output.Length == 0)
                    return null;
                byte[] bytes = output.ToArray();
                CacheBytes(url, bytes);
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsImageProxyUrl(string url)
        {
            return url.StartsWith(BaseUrl.TrimEnd('/') + "/api/img?", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith(App.WebsiteProductionUrl.TrimEnd('/') + "/api/img?", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith(App.WebsiteLocalUrl.TrimEnd('/') + "/api/img?", StringComparison.OrdinalIgnoreCase);
        }

        private static void CacheBytes(string url, byte[] bytes)
        {
            if (bytes.Length > MaxCachedBytes / 4 || !ByteCache.TryAdd(url, bytes))
                return;
            ByteCacheOrder.Enqueue(url);
            Interlocked.Add(ref _cachedBytes, bytes.Length);
            while (Interlocked.Read(ref _cachedBytes) > MaxCachedBytes && ByteCacheOrder.TryDequeue(out string? oldest))
            {
                if (ByteCache.TryRemove(oldest, out byte[]? removed))
                    Interlocked.Add(ref _cachedBytes, -removed.Length);
            }
        }
    }
}
