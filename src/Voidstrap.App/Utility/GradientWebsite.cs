using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility
{
    internal static class GradientWebsite
    {
        private const int MaxBannerBytes = 12000000;
        private const int MaxCssLength = 2048;
        private const int MaxStops = 32;

        private static readonly TimeSpan FreshCacheAge = TimeSpan.FromHours(1);

        private static readonly TimeSpan StaleCacheAge = TimeSpan.FromDays(30);

        private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> BannerLoads = new(StringComparer.OrdinalIgnoreCase);

        private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

        public static Brush? Parse(string? css)
        {
            if (string.IsNullOrWhiteSpace(css) || css!.Length > MaxCssLength)
                return null;

            string text = css.Trim();
            int open = text.IndexOf('(');
            if (open <= 0 || !text.EndsWith(")", StringComparison.Ordinal))
                return null;

            if (!text.AsSpan(0, open).Trim().Equals("linear-gradient", StringComparison.OrdinalIgnoreCase))
                return null;

            List<string> parts = SplitTopLevel(text.Substring(open + 1, text.Length - open - 2));
            if (parts.Count < 2 || parts.Count > MaxStops + 1)
                return null;

            double angle = 180;
            int start = 0;
            if (TryParseAngle(parts[0], out double parsedAngle))
            {
                angle = parsedAngle;
                start = 1;
            }

            var colors = new List<Color>(parts.Count);
            var offsets = new List<double>(parts.Count);

            for (int i = start; i < parts.Count; i++)
            {
                if (!TryParseStop(parts[i], out Color color, out double offset))
                    return null;

                colors.Add(color);
                offsets.Add(offset);
            }

            if (colors.Count < 2)
                return null;

            FillOffsets(offsets);

            var stops = new GradientStopCollection(colors.Count);
            for (int i = 0; i < colors.Count; i++)
                stops.Add(new GradientStop(colors[i], offsets[i]));

            double radians = angle * Math.PI / 180.0;
            double dx = Math.Sin(radians);
            double dy = -Math.Cos(radians);

            var brush = new LinearGradientBrush(stops)
            {
                StartPoint = new Point(0.5 - dx * 0.5, 0.5 - dy * 0.5),
                EndPoint = new Point(0.5 + dx * 0.5, 0.5 + dy * 0.5)
            };
            brush.Freeze();
            return brush;
        }

        private static List<string> SplitTopLevel(string value)
        {
            var parts = new List<string>();
            int depth = 0;
            int begin = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth > 0)
                        depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    parts.Add(value.Substring(begin, i - begin).Trim());
                    begin = i + 1;
                }
            }

            parts.Add(value.Substring(begin).Trim());
            return parts;
        }

        private static bool TryParseAngle(string value, out double degrees)
        {
            degrees = 0;
            string text = value.Trim();
            if (text.Length == 0)
                return false;

            if (text.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
            {
                bool top = false, bottom = false, left = false, right = false;
                foreach (string word in text.Substring(3).Split(Whitespace, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.Equals("top", StringComparison.OrdinalIgnoreCase)) top = true;
                    else if (word.Equals("bottom", StringComparison.OrdinalIgnoreCase)) bottom = true;
                    else if (word.Equals("left", StringComparison.OrdinalIgnoreCase)) left = true;
                    else if (word.Equals("right", StringComparison.OrdinalIgnoreCase)) right = true;
                    else return false;
                }

                if (top && right) degrees = 45;
                else if (bottom && right) degrees = 135;
                else if (bottom && left) degrees = 225;
                else if (top && left) degrees = 315;
                else if (top) degrees = 0;
                else if (right) degrees = 90;
                else if (bottom) degrees = 180;
                else if (left) degrees = 270;
                else return false;

                return true;
            }

            double scale;
            if (text.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) { text = text[..^3]; scale = 1; }
            else if (text.EndsWith("grad", StringComparison.OrdinalIgnoreCase)) { text = text[..^4]; scale = 0.9; }
            else if (text.EndsWith("turn", StringComparison.OrdinalIgnoreCase)) { text = text[..^4]; scale = 360; }
            else if (text.EndsWith("rad", StringComparison.OrdinalIgnoreCase)) { text = text[..^3]; scale = 180.0 / Math.PI; }
            else return false;

            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
                return false;

            degrees = raw * scale;
            return true;
        }

        private static bool TryParseStop(string value, out Color color, out double offset)
        {
            color = default;
            offset = double.NaN;

            string text = value.Trim();
            if (text.Length == 0)
                return false;

            for (int strip = 0; strip < 2; strip++)
            {
                int space = text.LastIndexOfAny(Whitespace);
                if (space <= 0)
                    break;

                string tail = text.Substring(space + 1).Trim();
                if (!tail.EndsWith("%", StringComparison.Ordinal))
                    break;

                if (!double.TryParse(tail[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                    break;

                offset = Math.Clamp(percent / 100.0, 0, 1);
                text = text.Substring(0, space).Trim();
            }

            return TryParseColor(text, out color);
        }

        public static bool TryParseColor(string value, out Color color)
        {
            color = default;
            string text = value.Trim();
            if (text.Length == 0)
                return false;

            if (text[0] == '#')
            {
                string hex = text.Substring(1);
                foreach (char c in hex)
                {
                    if (!Uri.IsHexDigit(c))
                        return false;
                }

                if (hex.Length == 3 || hex.Length == 4)
                {
                    byte r = Expand(hex[0]);
                    byte g = Expand(hex[1]);
                    byte b = Expand(hex[2]);
                    byte a = hex.Length == 4 ? Expand(hex[3]) : (byte)255;
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }

                if (hex.Length == 6 || hex.Length == 8)
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }

                return false;
            }

            bool isRgba = text.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);
            if (isRgba || text.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
            {
                if (!text.EndsWith(")", StringComparison.Ordinal))
                    return false;

                int prefix = isRgba ? 5 : 4;
                string[] fields = text
                    .Substring(prefix, text.Length - prefix - 1)
                    .Split(new[] { ',', '/', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                if (fields.Length < 3 || fields.Length > 4)
                    return false;

                byte[] channels = new byte[3];
                for (int i = 0; i < 3; i++)
                {
                    if (!TryParseChannel(fields[i], out channels[i]))
                        return false;
                }

                byte alpha = 255;
                if (fields.Length == 4)
                {
                    string field = fields[3].Trim();
                    bool percent = field.EndsWith("%", StringComparison.Ordinal);
                    if (percent)
                        field = field[..^1];

                    if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value2))
                        return false;

                    alpha = (byte)Math.Round(Math.Clamp(percent ? value2 / 100.0 : value2, 0, 1) * 255.0);
                }

                color = Color.FromArgb(alpha, channels[0], channels[1], channels[2]);
                return true;
            }

            try
            {
                object? parsed = ColorConverter.ConvertFromString(text);
                if (parsed is Color named)
                {
                    color = named;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryParseChannel(string field, out byte channel)
        {
            channel = 0;
            string text = field.Trim();
            bool percent = text.EndsWith("%", StringComparison.Ordinal);
            if (percent)
                text = text[..^1];

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return false;

            channel = (byte)Math.Round(Math.Clamp(percent ? value * 2.55 : value, 0, 255));
            return true;
        }

        private static byte Expand(char digit)
        {
            int v = Convert.ToInt32(digit.ToString(), 16);
            return (byte)(v * 17);
        }

        private static void FillOffsets(List<double> offsets)
        {
            if (double.IsNaN(offsets[0]))
                offsets[0] = 0;

            int last = offsets.Count - 1;
            if (double.IsNaN(offsets[last]))
                offsets[last] = 1;

            for (int i = 1; i < last; i++)
            {
                if (!double.IsNaN(offsets[i]))
                    continue;

                int next = i + 1;
                while (next < last && double.IsNaN(offsets[next]))
                    next++;

                double from = offsets[i - 1];
                double to = offsets[next];
                int steps = next - i + 1;
                for (int k = 0; k < steps - 1; k++)
                    offsets[i + k] = from + (to - from) * (k + 1) / steps;
            }

            for (int i = 1; i < offsets.Count; i++)
            {
                if (offsets[i] < offsets[i - 1])
                    offsets[i] = offsets[i - 1];
            }
        }

        public static async Task<Brush?> CreateBannerBrushAsync(string? bannerUrl, string? gradientCss, CancellationToken token = default)
        {
            if (!string.IsNullOrWhiteSpace(bannerUrl))
            {
                try
                {
                    BitmapSource? image = await LoadBannerImageAsync(bannerUrl!, token).ConfigureAwait(false);
                    if (image != null)
                    {
                        var brush = new ImageBrush(image)
                        {
                            Stretch = Stretch.UniformToFill,
                            AlignmentX = AlignmentX.Center,
                            AlignmentY = AlignmentY.Center
                        };
                        brush.Freeze();
                        return brush;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("GradientWebsite::CreateBannerBrushAsync", ex);
                }
            }

            Brush? gradient = Parse(gradientCss);
            if (gradient != null)
                return gradient;
            return string.IsNullOrWhiteSpace(bannerUrl) ? null : CreateFallbackBrush();
        }

        public static async Task<BitmapSource?> LoadBannerImageAsync(string bannerUrl, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(bannerUrl))
                return null;

            Lazy<Task<BitmapSource?>> pending = new(() => LoadBannerImageCoreAsync(bannerUrl), LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<BitmapSource?>> active = BannerLoads.GetOrAdd(bannerUrl, pending);
            try
            {
                return await active.Value.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                if (active.IsValueCreated && active.Value.IsCompleted && BannerLoads.TryGetValue(bannerUrl, out Lazy<Task<BitmapSource?>>? current) && ReferenceEquals(current, active))
                    BannerLoads.TryRemove(bannerUrl, out _);
            }
        }

        private static async Task<BitmapSource?> LoadBannerImageCoreAsync(string bannerUrl)
        {
            byte[]? embedded = DecodeDataUrl(bannerUrl);
            if (embedded != null)
                return DecodeBanner(embedded);
            if (bannerUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Uri.TryCreate(bannerUrl, UriKind.Absolute, out Uri? original) || original.Scheme != Uri.UriSchemeHttp && original.Scheme != Uri.UriSchemeHttps)
                return null;

            string? cachePath = ResolveCachePath(original);
            BitmapSource? fresh = TryReadCache(cachePath, bannerUrl, FreshCacheAge, true);
            if (fresh != null)
                return fresh;

            foreach (Uri candidate in EnumerateCandidates(original))
            {
                byte[]? bytes = await DownloadBannerAsync(candidate).ConfigureAwait(false);
                BitmapSource? image = DecodeBanner(bytes);
                if (image == null)
                    continue;
                WriteCache(cachePath, bannerUrl, bytes!);
                App.Logger.WriteLine("GradientWebsite", "Banner image loaded from network");
                return image;
            }

            BitmapSource? stale = TryReadCache(cachePath, bannerUrl, StaleCacheAge, false);
            if (stale != null)
            {
                App.Logger.WriteLine("GradientWebsite", "Banner image loaded from cached fallback");
                return stale;
            }

            App.Logger.WriteLine("GradientWebsite", "Banner image unavailable, using visual fallback");
            return null;
        }

        private static byte[]? DecodeDataUrl(string value)
        {
            if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return null;
            int comma = value.IndexOf(',');
            if (comma <= 0)
                return null;
            string header = value.Substring(0, comma);
            string payload = value.Substring(comma + 1);
            if (payload.Length == 0 || payload.Length > 16_000_000)
                return null;
            try
            {
                byte[] bytes = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                return bytes.Length <= MaxBannerBytes ? bytes : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]?> DownloadBannerAsync(Uri uri)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                return await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken token)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > MaxBannerBytes))
                return null;
            await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream output = new(content.Headers.ContentLength is long length ? (int)length : 81920);
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > MaxBannerBytes)
                    return null;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }

        private static BitmapSource? DecodeBanner(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBannerBytes)
                return null;
            BitmapSource? image = SafeImaging.FromBytes(bytes, 1600);
            if (image?.CanFreeze == true)
                image.Freeze();
            return image;
        }

        private static IEnumerable<Uri> EnumerateCandidates(Uri original)
        {
            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            foreach (string value in EnumerateCandidateStrings(original))
            {
                if (emitted.Add(value) && Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate))
                    yield return candidate;
            }
        }

        private static IEnumerable<string> EnumerateCandidateStrings(Uri original)
        {
            yield return original.ToString();
            UriBuilder withoutFragment = new(original) { Fragment = "" };
            yield return withoutFragment.Uri.ToString();

            foreach (string baseUrl in new[] { App.WebsiteBaseUrl, App.WebsiteProductionUrl, App.WebsiteLocalUrl })
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? site) || !IsWebsiteHost(original.Host))
                    continue;
                UriBuilder alternate = new(original)
                {
                    Scheme = site.Scheme,
                    Host = site.Host,
                    Port = site.IsDefaultPort ? -1 : site.Port,
                    Fragment = ""
                };
                yield return alternate.Uri.ToString();
            }

            if (!string.IsNullOrEmpty(original.Query))
            {
                UriBuilder withoutQuery = new(original) { Query = "", Fragment = "" };
                yield return withoutQuery.Uri.ToString();
            }
        }

        private static bool IsWebsiteHost(string host)
        {
            foreach (string value in new[] { App.WebsiteBaseUrl, App.WebsiteProductionUrl, App.WebsiteLocalUrl })
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out Uri? site) && host.Equals(site.Host, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string? ResolveCachePath(Uri uri)
        {
            try
            {
                UriBuilder canonical = new(uri) { Fragment = "", Query = "" };
                string root = Paths.Initialized ? Paths.Cache : Paths.Temp;
                if (string.IsNullOrWhiteSpace(root))
                    return null;
                string directory = Path.Combine(root, "Banners");
                Directory.CreateDirectory(directory);
                string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.Uri.ToString())));
                return Path.Combine(directory, name + ".bin");
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource? TryReadCache(string? path, string source, TimeSpan maximumAge, bool requireSourceMatch)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                FileInfo file = new(path);
                if (!file.Exists || file.Length <= 0 || file.Length > MaxBannerBytes || DateTime.UtcNow - file.LastWriteTimeUtc > maximumAge)
                    return null;
                if (requireSourceMatch)
                {
                    string sourcePath = path + ".source";
                    if (!File.Exists(sourcePath) || !string.Equals(File.ReadAllText(sourcePath), source, StringComparison.Ordinal))
                        return null;
                }
                byte[] bytes = File.ReadAllBytes(path);
                BitmapSource? image = DecodeBanner(bytes);
                if (image == null)
                {
                    File.Delete(path);
                    TryDelete(path + ".source");
                }
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(string? path, string source, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path))
                return;
            string dataTemporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string sourcePath = path + ".source";
            string sourceTemporary = sourcePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(dataTemporary, bytes);
                File.WriteAllText(sourceTemporary, source);
                File.Move(dataTemporary, path, true);
                File.Move(sourceTemporary, sourcePath, true);
            }
            catch
            {
                TryDelete(dataTemporary);
                TryDelete(sourceTemporary);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }

        private static Brush CreateFallbackBrush()
        {
            LinearGradientBrush brush = new(
                Color.FromRgb(44, 48, 60),
                Color.FromRgb(26, 29, 38),
                new Point(0, 0),
                new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        public static bool HasGradient(string? gradientKey, string? gradient)
        {
            return !string.IsNullOrWhiteSpace(gradient)
                && !string.Equals(gradientKey, "none", StringComparison.OrdinalIgnoreCase)
                && Parse(gradient) is not null;
        }
    }
}
