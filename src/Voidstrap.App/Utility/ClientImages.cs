using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility
{
    public static class ClientImages
    {
        public const string Default = "pack://application:,,,/Resources/RobloxPlayerIcon.png";

        public static readonly Dictionary<string, string> Images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["2007E-FakeFeb"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2007E"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2007L"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2007M"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2008E"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2008L"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2008M"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2009E"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2009L"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2009M"] = "https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123",
            ["2010E"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2010L"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2010M"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2011E"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2011L"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2011M"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2012E"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2012L"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2012M"] = "https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633",
            ["2013E"] = "https://static.wikia.nocookie.net/roblox/images/1/15/2011_Icon.png/revision/latest?cb=20250329002829",
            ["2013L"] = "https://static.wikia.nocookie.net/roblox/images/1/15/2011_Icon.png/revision/latest?cb=20250329002829",
            ["2013M"] = "https://static.wikia.nocookie.net/roblox/images/1/15/2011_Icon.png/revision/latest?cb=20250329002829",
        };

        public const int LowRes = 48;
        public const int FullRes = 256;

        private static readonly Dictionary<string, ImageSource> _cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> _cacheOrder = new Queue<string>();
        private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
        private static ImageSource _defaultImage;
        private static long _cacheBytes;

        private const int MaxMemoryCacheEntries = 64;
        private const long MaxMemoryCacheBytes = 32L * 1024 * 1024;
        private const int MaxRemoteImageBytes = 8 * 1024 * 1024;

        private static string CacheDir => Paths.ClientImageCache;

        public static string GetUri(string code)
        {
            if (!string.IsNullOrEmpty(code) && Images.TryGetValue(code, out string uri) && !string.IsNullOrWhiteSpace(uri))
                return uri;
            return Default;
        }

        public static ImageSource Get(string code)
        {
            string uri = GetUri(code);

            if (IsLocal(uri))
            {
                string key = uri + "@0";
                lock (_cache)
                {
                    if (_cache.TryGetValue(key, out ImageSource cached) && cached != null)
                        return cached;
                }
                ImageSource local = Load(uri, 0) ?? DefaultImage();
                StoreCached(key, local);
                return local;
            }

            lock (_cache)
            {
                if (_cache.TryGetValue(uri + "@" + FullRes, out ImageSource full) && full != null)
                    return full;
                if (_cache.TryGetValue(uri + "@" + LowRes, out ImageSource low) && low != null)
                    return low;
            }
            return DefaultImage();
        }

        public static Task<ImageSource> LoadAsync(string code)
        {
            return LoadAsync(code, FullRes);
        }

        public static Task<ImageSource> LoadAsync(string code, int size)
        {
            string uri = GetUri(code);
            string key = uri + "@" + (IsLocal(uri) ? 0 : size);
            Lazy<Task<ImageSource>> candidate = new(() => Task.Run(() => LoadCached(uri, size)), LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<ImageSource>> selected = _inflight.GetOrAdd(key, candidate);
            return AwaitInflightAsync(key, selected);
        }

        private static async Task<ImageSource> AwaitInflightAsync(string key, Lazy<Task<ImageSource>> operation)
        {
            try
            {
                return await operation.Value.ConfigureAwait(false);
            }
            finally
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<ImageSource>>>>)_inflight).Remove(new KeyValuePair<string, Lazy<Task<ImageSource>>>(key, operation));
            }
        }

        private static ImageSource LoadCached(string uri, int size)
        {
            string key = uri + "@" + (IsLocal(uri) ? 0 : size);
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out ImageSource cached) && cached != null)
                    return cached;
            }
            ImageSource image = (IsLocal(uri) ? Load(uri, 0) : LoadRemote(uri, size)) ?? DefaultImage();
            StoreCached(key, image);
            return image;
        }

        private static void StoreCached(string key, ImageSource image)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out ImageSource existing) && existing != null)
                    _cacheBytes -= EstimateBytes(existing);
                if (!_cache.ContainsKey(key))
                    _cacheOrder.Enqueue(key);
                _cache[key] = image;
                _cacheBytes += EstimateBytes(image);
                while ((_cache.Count > MaxMemoryCacheEntries || _cacheBytes > MaxMemoryCacheBytes) && _cacheOrder.Count > 0)
                {
                    string oldest = _cacheOrder.Dequeue();
                    if (_cache.Remove(oldest, out ImageSource removed) && removed != null)
                        _cacheBytes -= EstimateBytes(removed);
                }
            }
        }

        private static long EstimateBytes(ImageSource image)
        {
            if (image is not BitmapSource bitmap)
                return 0;
            int bitsPerPixel = bitmap.Format.BitsPerPixel > 0 ? bitmap.Format.BitsPerPixel : 32;
            return ((long)Math.Max(1, bitmap.PixelWidth) * Math.Max(1, bitmap.PixelHeight) * bitsPerPixel + 7) / 8;
        }

        private static ImageSource DefaultImage()
        {
            return _defaultImage ??= Load(Default, 0);
        }

        private static bool IsLocal(string uri)
        {
            return !uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static ImageSource Load(string uri, int size)
        {
            try
            {
                var bmp = Voidstrap.Utility.SafeImaging.FromUri(new Uri(uri, UriKind.Absolute), size);
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource LoadRemote(string uri, int size)
        {
            try
            {
                foreach (string candidate in AppImage.GetCandidates(uri, size))
                {
                    ImageSource image = TryLoadCandidate(candidate, uri, size);
                    if (image != null)
                        return image;
                }
            }
            catch
            {
            }
            return null;
        }

        private static ImageSource TryLoadCandidate(string candidate, string uri, int size)
        {
            try
            {
                string cacheFile = DiskCachePath(uri, size);
                byte[] bytes = TryReadFile(cacheFile);
                if (bytes == null)
                {
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, candidate);
                    using HttpResponseMessage response = App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is long contentLength && contentLength > MaxRemoteImageBytes)
                        return null;
                    using Stream stream = response.Content.ReadAsStream();
                    bytes = ReadBounded(stream);
                    if (bytes == null)
                        return null;
                    TryWriteFile(cacheFile, bytes);
                }
                return SafeImaging.FromBytes(bytes, size);
            }
            catch
            {
                return null;
            }
        }

        private static string DiskCachePath(string uri, int size)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(uri + "@" + size));
            return Path.Combine(CacheDir, Convert.ToHexString(hash) + ".png");
        }

        private static byte[] TryReadFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    long length = new FileInfo(path).Length;
                    if (length <= 0 || length > MaxRemoteImageBytes)
                        return null;
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.Length > 0)
                        return bytes;
                }
            }
            catch
            {
            }
            return null;
        }

        private static byte[] ReadBounded(Stream stream)
        {
            using MemoryStream output = new MemoryStream();
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > MaxRemoteImageBytes)
                    return null;
                output.Write(buffer, 0, read);
            }
        }

        private static void TryWriteFile(string path, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxRemoteImageBytes)
                return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, path, true);
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
        }
    }
}
