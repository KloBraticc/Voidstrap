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

        private const long MaxCachedBytes = 4L * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> Downloads = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, byte[]> ByteCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentQueue<string> ByteCacheOrder = new();

        private static readonly SemaphoreSlim LinuxDownloadGate = new(8, 8);

        private static readonly SemaphoreSlim LinuxDecodeGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

        private static long _cachedBytes;

        private const string EmbeddedRoot = "pack://application:,,,/Resources/AppImages/";

        private static readonly Dictionary<string, string> EmbeddedAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://www.pngall.com/wp-content/uploads/17/Roblox-Cash-Icon-Illustration-PNG.png"] = "robux-cash.png",
            ["https://avatars.githubusercontent.com/u/195697851?v=4"] = "about-1.png",
            ["https://avatars.githubusercontent.com/u/168036205?v=4"] = "about-2.png",
            ["https://avatars.githubusercontent.com/u/193777251?v=4"] = "about-3.png",
            ["https://avatars.githubusercontent.com/u/213444273?v=4"] = "about-4.png",
            ["https://avatars.githubusercontent.com/u/105540464?v=4"] = "about-5.png",
            ["https://avatars.githubusercontent.com/u/194078013?v=4"] = "about-6.png",
            ["https://avatars.githubusercontent.com/u/186699266"] = "ext-fleasion.png",
            ["https://raw.githubusercontent.com/KloBraticc/RiShade/main/Images/RiShde.png"] = "ext-rishade.png",
            ["https://raw.githubusercontent.com/MaximumADHD/Roblox-API-Dump-Tool/master/Resources/AppLogo.png"] = "ext-apidump.png",
            ["https://github.com/rojo-rbx.png"] = "ext-rojo.png",
            ["https://images.rbxcdn.com/905bd722ee0a6ceda3caacde54c0b081.png"] = "studio.png",
            ["https://static.wikia.nocookie.net/roblox/images/3/3d/2005_Icon.png/revision/latest?cb=20231105222123"] = "client-2007.png",
            ["https://static.wikia.nocookie.net/roblox/images/1/11/Roblox_2007.PNG_%281%29.png/revision/latest?cb=20221020205633"] = "client-2010.png",
            ["https://static.wikia.nocookie.net/roblox/images/1/15/2011_Icon.png/revision/latest?cb=20250329002829"] = "client-2013.png",
        };

        public static string? EmbeddedAsset(string? url)
        {
            return url != null && EmbeddedAssets.TryGetValue(url, out string? name) ? EmbeddedRoot + name : null;
        }

        public static bool IsRemote(string url)
        {
            return !string.IsNullOrEmpty(url)
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
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
            string? embedded = EmbeddedAsset(url);
            if (embedded != null)
                return new[] { embedded };
            if (IsRemote(url))
                return new[] { url, WsrvUrl(url, size) };
            return new[] { url };
        }

        public static BitmapSource? LoadSync(string url, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            foreach (string candidate in GetCandidates(url, decodeWidth))
            {
                try
                {
                    if (TryReadDataUri(candidate, out byte[]? inlineBytes))
                    {
                        BitmapSource? inline = SafeImaging.FromBytes(inlineBytes, decodeWidth);
                        if (inline != null)
                            return inline;
                        continue;
                    }
                    if (!IsRemote(candidate))
                    {
                        BitmapSource? local = Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                            ? SafeImaging.FromUri(parsed, decodeWidth)
                            : SafeImaging.FromFile(candidate, decodeWidth);
                        if (local != null)
                            return local;
                        continue;
                    }
                    byte[]? bytes = DownloadBytesAsync(candidate).GetAwaiter().GetResult();
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
            IReadOnlyList<string> candidates = GetCandidates(url, decodeWidth);
            foreach (string candidate in candidates)
            {
                if (ct.IsCancellationRequested)
                    return null;
                if (TryReadDataUri(candidate, out byte[]? inlineBytes))
                {
                    BitmapSource? inline = inlineBytes == null ? null : await DecodeAsync(inlineBytes, decodeWidth, ct).ConfigureAwait(false);
                    if (inline != null)
                        return inline;
                    continue;
                }
                if (!IsRemote(candidate))
                {
                    BitmapSource? local = await DecodeAsync(() =>
                    {
                        try
                        {
                            return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                                ? SafeImaging.FromUri(parsed, decodeWidth)
                                : SafeImaging.FromFile(candidate, decodeWidth);
                        }
                        catch
                        {
                            return null;
                        }
                    }, ct).ConfigureAwait(false);
                    if (local != null)
                        return local;
                    continue;
                }
                byte[]? bytes = await DownloadBytesAsync(candidate, ct).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                    continue;
                BitmapSource? bmp = await DecodeAsync(bytes, decodeWidth, ct).ConfigureAwait(false);
                if (bmp != null)
                    return bmp;
            }
            return null;
        }

		public static async Task<byte[]?> LoadBytesAsync(string url, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(url))
				return null;
			foreach (string candidate in GetCandidates(url))
			{
				ct.ThrowIfCancellationRequested();
				if (TryReadDataUri(candidate, out byte[]? inlineBytes))
					return inlineBytes;
				if (candidate.StartsWith(EmbeddedRoot, StringComparison.OrdinalIgnoreCase))
				{
					System.Windows.Resources.StreamResourceInfo? resource = System.Windows.Application.GetResourceStream(new Uri(candidate, UriKind.Absolute));
					if (resource?.Stream == null)
						continue;
					await using Stream resourceStream = resource.Stream;
					using MemoryStream resourceBytes = new();
					await resourceStream.CopyToAsync(resourceBytes, ct).ConfigureAwait(false);
					return resourceBytes.ToArray();
				}
				if (!IsRemote(candidate))
				{
					try
					{
						string path = Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed) && parsed.IsFile
							? parsed.LocalPath
							: candidate;
						FileInfo file = new(path);
						if (!file.Exists || file.Length <= 0 || file.Length > MaxDownloadBytes)
							continue;
						return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
					}
					catch (Exception) when (!ct.IsCancellationRequested)
					{
						continue;
					}
				}
				byte[]? downloaded = await DownloadBytesAsync(candidate, ct).ConfigureAwait(false);
				if (downloaded is { Length: > 0 })
					return downloaded;
			}
			return null;
		}

        private static Task<BitmapSource?> DecodeAsync(byte[] bytes, int decodeWidth, CancellationToken ct)
        {
            if (!Platform.IsLinux)
            {
                return Task.Run(delegate
                {
                    try
                    {
                        return SafeImaging.FromBytes(bytes, decodeWidth);
                    }
                    catch
                    {
                        return null;
                    }
                }, ct);
            }

            return DecodeAsync(() => SafeImaging.FromBytes(bytes, decodeWidth), ct);
        }

        internal static Task<BitmapSource?> DecodeBytesAsync(byte[] bytes, int decodeWidth, CancellationToken ct = default)
        {
            return DecodeAsync(bytes, decodeWidth, ct);
        }

        private static async Task<BitmapSource?> DecodeAsync(Func<BitmapSource?> decode, CancellationToken ct)
        {
            await LinuxDecodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        return decode();
                    }
                    catch
                    {
                        return null;
                    }
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                LinuxDecodeGate.Release();
            }
        }

        private static bool TryReadDataUri(string value, out byte[]? bytes)
        {
            bytes = null;
            if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return false;
            int comma = value.IndexOf(',');
            if (comma <= 5 || comma == value.Length - 1)
                return true;
            try
            {
                string metadata = value.Substring(5, comma - 5);
                string payload = value.Substring(comma + 1);
                bytes = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.Latin1.GetBytes(Uri.UnescapeDataString(payload));
            }
            catch
            {
                bytes = null;
            }
            return true;
        }

        public static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            if (ByteCache.TryGetValue(url, out byte[]? cached))
                return cached;
            byte[]? stored = await ReadDiskCacheAsync(url, ct).ConfigureAwait(false);
            if (stored != null)
            {
                CacheBytes(url, stored);
                return stored;
            }
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
            bool entered = false;
            try
            {
                if (Platform.IsLinux)
                {
                    await LinuxDownloadGate.WaitAsync().ConfigureAwait(false);
                    entered = true;
                }
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
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
                await WriteDiskCacheAsync(url, bytes).ConfigureAwait(false);
                return bytes;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (entered)
                    LinuxDownloadGate.Release();
            }
        }

        private const int MaxDiskCachedBytes = 512 * 1024;

        private static readonly TimeSpan DiskCacheAge = TimeSpan.FromHours(24);

        private static readonly TimeSpan DiskCachePruneAge = TimeSpan.FromDays(7);

        private static int _diskCachePruned;

        private static string DiskCacheDirectory => Path.Combine(Paths.Cache, "Images");

        private static string DiskCachePath(string url)
        {
            return Path.Combine(DiskCacheDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant()))) + ".img");
        }

        private static async Task<byte[]?> ReadDiskCacheAsync(string url, CancellationToken ct)
        {
            try
            {
                if (!IsRemote(url))
                    return null;
                string path = DiskCachePath(url);
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > MaxDiskCachedBytes || DateTime.UtcNow - info.LastWriteTimeUtc > DiskCacheAge)
                    return null;
                return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteDiskCacheAsync(string url, byte[] bytes)
        {
            if (bytes.Length > MaxDiskCachedBytes || !IsRemote(url))
                return;
            try
            {
                string directory = DiskCacheDirectory;
                Directory.CreateDirectory(directory);
                if (Interlocked.Exchange(ref _diskCachePruned, 1) == 0)
                    PruneDiskCache(directory);
                string path = DiskCachePath(url);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("AppImage::DiskCache", ex.Message);
            }
        }

        private static void PruneDiskCache(string directory)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - DiskCachePruneAge;
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("AppImage::DiskCache", "Prune failed: " + ex.Message);
            }
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
