using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility
{
    public sealed class BorderRender
    {
        public ImageSource Image { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public Thickness Margin { get; set; }
        public int ZIndex { get; set; }
    }

    public sealed class WebsiteBorderData
    {
        public BorderRender ImageBorder { get; set; }
        public string GradientBorderKey { get; set; }
        public string AvatarUrl { get; set; }
    }

    public static class WebsiteBorderRenderer
    {
        private const int MaxProfileBytes = 1048576;

        private const int MaxImageBytes = 4000000;

        public static async Task<WebsiteBorderData> FetchActiveAsync(double avatarSize, double containerSize)
        {
            try
            {
                string token = WebsiteAuth.GetToken();
                if (string.IsNullOrEmpty(token))
                    return null;

                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, App.WebsiteBaseUrl + "/api/me");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                byte[]? payload = await ReadBoundedAsync(response.Content, MaxProfileBytes).ConfigureAwait(false);
                if (payload == null)
                    return null;
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                    return null;

                var data = new WebsiteBorderData();
                if (user.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String)
                    data.AvatarUrl = WebsiteUrl.Absolute(av.GetString());
                if (user.TryGetProperty("avatarBorder", out var ab) && ab.ValueKind == JsonValueKind.String)
                    data.GradientBorderKey = ab.GetString();
                if (user.TryGetProperty("equippedBorder", out var eqb) && eqb.ValueKind == JsonValueKind.Object)
                    data.ImageBorder = Build(eqb, avatarSize, containerSize);
                return data;
            }
            catch
            {
                return null;
            }
        }

        public static BorderRender Build(JsonElement border, double avatarSize, double containerSize)
        {
            try
            {
                if (!border.TryGetProperty("image", out var imgEl) || imgEl.ValueKind != JsonValueKind.String)
                    return null;
                var source = LoadSecureImage(imgEl.GetString() ?? "");
                if (source == null)
                    return null;

                double scale = Math.Min(3.0, Math.Max(0.5, ReadNum(border, "scale", 1.2)));
                double ox = Math.Min(60.0, Math.Max(-60.0, ReadNum(border, "ox", 0)));
                double oy = Math.Min(60.0, Math.Max(-60.0, ReadNum(border, "oy", 0)));
                double iw = ReadNum(border, "imgWidth", 0);
                double ih = ReadNum(border, "imgHeight", 0);
                bool behind = border.TryGetProperty("behind", out var bh) && bh.ValueKind == JsonValueKind.True;

                double sizePct = scale * 100.0;
                double widthPct = (iw >= 50 && iw <= 300) ? iw : sizePct;
                double heightPct = (ih >= 50 && ih <= 300) ? ih : sizePct;
                double leftPct = (100.0 - widthPct) / 2.0 + ox;
                double topPct = (100.0 - heightPct) / 2.0 + oy;

                double offset = (containerSize - avatarSize) / 2.0;
                double rawWidth = widthPct / 100.0 * avatarSize;
                double rawHeight = heightPct / 100.0 * avatarSize;
                double fit = Math.Min(1.0, Math.Min(containerSize / rawWidth, containerSize / rawHeight));
                double width = rawWidth * fit;
                double height = rawHeight * fit;
                double left = leftPct / 100.0 * avatarSize + offset + (rawWidth - width) / 2.0;
                double top = topPct / 100.0 * avatarSize + offset + (rawHeight - height) / 2.0;
                left = Math.Clamp(left, 0.0, containerSize - width);
                top = Math.Clamp(top, 0.0, containerSize - height);
                return new BorderRender
                {
                    Image = source,
                    Width = width,
                    Height = height,
                    Margin = new Thickness(left, top, 0, 0),
                    ZIndex = behind ? -1 : 10
                };
            }
            catch
            {
                return null;
            }
        }

        private static string? _imageCacheDir;

        private static readonly object CacheDirLock = new object();

        private static string? ResolveImageCacheDirectory()
        {
            lock (CacheDirLock)
            {
                if (_imageCacheDir != null)
                {
                    return _imageCacheDir.Length == 0 ? null : _imageCacheDir;
                }

                foreach (string candidate in EnumerateCacheCandidates())
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.CreateDirectory(candidate);
                        string probe = Path.Combine(candidate, ".writetest");
                        File.WriteAllBytes(probe, Array.Empty<byte>());
                        File.Delete(probe);
                        _imageCacheDir = candidate;
                        return candidate;
                    }
                    catch
                    {
                    }
                }

                _imageCacheDir = string.Empty;
                return null;
            }
        }

        private static IEnumerable<string> EnumerateCacheCandidates()
        {
            string documents = "";
            try
            {
                documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(documents))
            {
                yield return Path.Combine(documents, "Voidstrap", "WebImages");
            }

            string cache = "";
            try
            {
                cache = Paths.Cache ?? "";
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(cache))
            {
                yield return Path.Combine(cache, "WebImages");
            }

            string temp = "";
            try
            {
                temp = Path.GetTempPath();
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(temp))
            {
                yield return Path.Combine(temp, "Voidstrap", "WebImages");
            }
        }

        public static double ReadNum(JsonElement obj, string name, double fallback)
        {
            if (obj.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
                    return d;
                if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ds))
                    return ds;
            }
            return fallback;
        }

        private static byte[] FetchImageCached(Uri uri, string resolved)
        {
            string cacheKey = resolved;
            try
            {
                UriBuilder builder = new UriBuilder(uri);
                if (!string.IsNullOrEmpty(builder.Query))
                {
                    string[] kept = builder.Query.TrimStart('?').Split('&').Where((string p) => !p.StartsWith("r=", StringComparison.OrdinalIgnoreCase)).ToArray();
                    builder.Query = string.Join("&", kept);
                    cacheKey = builder.Uri.ToString();
                }
            }
            catch
            {
            }
            string? dir = ResolveImageCacheDirectory();
            if (string.IsNullOrEmpty(dir))
                return null;
            string file = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))) + ".bin");
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length <= MaxImageBytes && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(24.0))
                    return File.ReadAllBytes(file);
            }
            catch
            {
            }
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, resolved);
                using HttpResponseMessage response = App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                    return null;
                byte[]? fresh = ReadBounded(response.Content, MaxImageBytes);
                if (fresh == null)
                    return null;
                try
                {
                    Directory.CreateDirectory(dir);
                    string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temporary, fresh);
                        File.Move(temporary, file, true);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                }
                catch
                {
                }
                return fresh;
            }
            catch
            {
                try
                {
                    if (File.Exists(file) && new FileInfo(file).Length <= MaxImageBytes)
                        return File.ReadAllBytes(file);
                }
                catch
                {
                }
                return null;
            }
        }

        private static byte[]? ReadBounded(HttpContent content, int maxBytes)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maxBytes))
                return null;
            using Stream input = content.ReadAsStream();
            using MemoryStream output = new MemoryStream(content.Headers.ContentLength is long length ? (int)length : 81920);
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > maxBytes)
                    return null;
                output.Write(buffer, 0, read);
            }
        }

        private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maxBytes))
                return null;
            await using Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using MemoryStream output = new MemoryStream(content.Headers.ContentLength is long length ? (int)length : 81920);
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > maxBytes)
                    return null;
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }

        public static ImageSource LoadSecureImage(string image)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(image))
                    return null;

                byte[] bytes;
                if (image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = image.IndexOf(',');
                    if (comma < 0)
                        return null;
                    string meta = image.Substring(0, comma);
                    if (meta.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0)
                        return null;
                    if (!(meta.Contains("png") || meta.Contains("jpeg") || meta.Contains("jpg") || meta.Contains("webp") || meta.Contains("gif")))
                        return null;
                    string b64 = image.Substring(comma + 1);
                    if (b64.Length > 6_000_000)
                        return null;
                    bytes = Convert.FromBase64String(b64);
                }
                else
                {
                    string resolved = image;
                    if (image.StartsWith("/", StringComparison.Ordinal))
                        resolved = App.WebsiteBaseUrl.TrimEnd('/') + image;
                    if (!Uri.TryCreate(resolved, UriKind.Absolute, out Uri uri))
                        return null;
                    bool isHttps = uri.Scheme == Uri.UriSchemeHttps;
                    bool isLocalHttp = uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "127.0.0.1" || uri.Host == "localhost");
                    if (!isHttps && !isLocalHttp)
                        return null;
                    bytes = FetchImageCached(uri, resolved);
                    if (bytes == null)
                        return null;
                }

                if (bytes == null || bytes.Length == 0 || bytes.Length > 4_000_000)
                    return null;

                var bmp = Voidstrap.Utility.SafeImaging.FromStream(new System.IO.MemoryStream(bytes), 256);
                if (bmp.CanFreeze)
                    bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
