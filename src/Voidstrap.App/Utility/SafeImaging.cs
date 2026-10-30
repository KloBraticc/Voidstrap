using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;

namespace Voidstrap.Utility;

internal static class SafeImaging
{
    private const long MaxCompressedImageBytes = 64L * 1024 * 1024;
    private const long MaxDecodedImageBytes = 64L * 1024 * 1024;
    private const long MaxDecodedAnimationBytes = 256L * 1024 * 1024;
    private const int MaxAnimationFrames = 180;

    private static readonly Lazy<System.Net.Http.HttpClient> _http = new Lazy<System.Net.Http.HttpClient>(() =>
        VpnHttpClient.Create(TimeSpan.FromSeconds(15))
    );

    public static BitmapSource? FromBytes(byte[]? bytes, int decodeWidth = 0)
    {
        if (bytes == null || bytes.Length == 0 || bytes.LongLength > MaxCompressedImageBytes)
        {
            return null;
        }
        if (TryDecodePortableIcon(bytes, decodeWidth, out BitmapSource? icon))
            return icon;
        if (!HasSafeDimensions(bytes, decodeWidth))
            return DecodeMacOS(bytes, decodeWidth);
        try
        {
            if (!Platform.IsWindows)
            {
                return DecodePortable(bytes, decodeWidth) ?? DecodeMacOS(bytes, decodeWidth);
            }
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0)
            {
                image.DecodePixelWidth = decodeWidth;
            }
            using (MemoryStream compressedStream = new MemoryStream(bytes, writable: false))
            {
                image.StreamSource = compressedStream;
                image.EndInit();
            }
            return Detach(DropSource(image));
        }
        catch (Exception ex)
        {
            if (DecodeMacOS(bytes, decodeWidth) is BitmapSource native)
                return native;
            try
            {
                BitmapSource? portable = DecodePortable(bytes, decodeWidth);
                if (portable != null)
                {
                    return portable;
                }
            }
            catch (Exception fallbackEx)
            {
                App.Logger?.WriteLine("SafeImaging::FromBytes", "Fallback decode failed: " + fallbackEx.Message.Split('\n')[0]);
            }
            App.Logger?.WriteLine("SafeImaging::FromBytes", "Decode failed: " + ex.Message.Split('\n')[0]);
            return null;
        }
    }

    private static BitmapSource? DecodeMacOS(byte[] bytes, int decodeWidth)
    {
        if (!Platform.IsMacOS)
            return null;
        try
        {
            int limit = decodeWidth > 0 ? Math.Min(decodeWidth, 4096) : 4096;
            byte[] pixels = Voidstrap.Platform.MacOS.MacOSImageDecoder.Decode(bytes, limit, 4096, out int width, out int height);
            return Detach(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4)));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDecodePortableIcon(byte[] bytes, int decodeWidth, out BitmapSource? source)
    {
        source = null;
        if (bytes.Length < 22 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] is not (1 or 2) || bytes[3] != 0)
            return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        if (count <= 0 || count > 256 || bytes.Length < 6 + count * 16)
            return true;

        List<(int Width, int Height, int Offset, int Length)> entries = new(count);
        for (int index = 0; index < count; index++)
        {
            int entryOffset = 6 + index * 16;
            int width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
            int height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 8, 4));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 12, 4));
            if (length == 0 || offset > int.MaxValue || length > int.MaxValue || (long)offset + length > bytes.LongLength)
                continue;
            entries.Add((width, height, (int)offset, (int)length));
        }

        entries.Sort((left, right) => IconFrameScore(left.Width, decodeWidth).CompareTo(IconFrameScore(right.Width, decodeWidth)));
        foreach ((int width, int height, int offset, int length) in entries)
        {
            ReadOnlySpan<byte> frame = bytes.AsSpan(offset, length);
            if (
                frame.Length >= 8
                && frame[0] == 0x89
                && frame[1] == 0x50
                && frame[2] == 0x4e
                && frame[3] == 0x47
                && frame[4] == 0x0d
                && frame[5] == 0x0a
                && frame[6] == 0x1a
                && frame[7] == 0x0a
            )
            {
                source = FromBytes(frame.ToArray(), decodeWidth);
                if (source != null)
                    return true;
            }
            else if (TryDecodeIconBitmap(frame, width, height, out source))
            {
                return true;
            }
        }
        return true;
    }

    private static long IconFrameScore(int width, int decodeWidth)
    {
        if (decodeWidth <= 0)
            return -width;
        return width >= decodeWidth ? width - decodeWidth : 100000L + decodeWidth - width;
    }

    private static bool TryDecodeIconBitmap(ReadOnlySpan<byte> frame, int entryWidth, int entryHeight, out BitmapSource? source)
    {
        source = null;
        if (frame.Length < 40)
            return false;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(0, 4));
        int dibWidth = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(4, 4));
        int dibFullHeight = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(8, 4));
        int bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(14, 2));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(16, 4));
        long absoluteWidth = Math.Abs((long)dibWidth);
        long absoluteHeight = Math.Abs((long)dibFullHeight);
        long decodedWidth = absoluteWidth > 0 ? absoluteWidth : entryWidth;
        long decodedHeight = absoluteHeight >= 2 ? absoluteHeight / 2 : entryHeight;
        if (
            headerSize < 40
            || headerSize > frame.Length
            || decodedWidth <= 0
            || decodedHeight <= 0
            || bitsPerPixel is not (1 or 4 or 8 or 24 or 32)
            || compression != 0
            || decodedWidth * decodedHeight > MaxDecodedImageBytes / 4
        )
            return false;
        int width = (int)decodedWidth;
        int height = (int)decodedHeight;
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(32, 4));
        int paletteCount = 0;
        if (bitsPerPixel <= 8)
        {
            int maxColors = 1 << bitsPerPixel;
            if (colorsUsed > maxColors)
                return false;
            paletteCount = colorsUsed > 0 ? (int)colorsUsed : maxColors;
        }

        int sourceStride = checked(((width * bitsPerPixel + 31) / 32) * 4);
        int maskStride = checked(((width + 31) / 32) * 4);
        long paletteEnd = (long)headerSize + paletteCount * 4;
        if (paletteEnd > frame.Length)
            return false;
        int pixelOffset = (int)paletteEnd;
        long pixelBytes = (long)sourceStride * height;
        if (pixelOffset + pixelBytes > frame.Length)
            return false;
        int maskOffset = checked(pixelOffset + (int)pixelBytes);
        bool hasMask = maskOffset + (long)maskStride * height <= frame.Length;
        int targetStride = checked(width * 4);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(targetStride * height));
        bool hasAlpha = false;
        for (int y = 0; y < height; y++)
        {
            int sourceY = dibFullHeight > 0 ? height - 1 - y : y;
            int sourceRow = checked(pixelOffset + sourceY * sourceStride);
            int targetRow = y * targetStride;
            for (int x = 0; x < width; x++)
            {
                int targetPixel = targetRow + x * 4;
                int sourcePixel;
                if (bitsPerPixel <= 8)
                {
                    int bitOffset = x * bitsPerPixel;
                    int paletteIndex = (frame[sourceRow + bitOffset / 8] >> (8 - bitsPerPixel - bitOffset % 8)) & ((1 << bitsPerPixel) - 1);
                    if (paletteIndex >= paletteCount)
                        return false;
                    sourcePixel = headerSize + paletteIndex * 4;
                }
                else
                {
                    sourcePixel = sourceRow + x * (bitsPerPixel / 8);
                }
                pixels[targetPixel] = frame[sourcePixel];
                pixels[targetPixel + 1] = frame[sourcePixel + 1];
                pixels[targetPixel + 2] = frame[sourcePixel + 2];
                byte alpha = bitsPerPixel == 32 ? frame[sourcePixel + 3] : (byte)255;
                pixels[targetPixel + 3] = alpha;
                hasAlpha |= alpha != 0;
            }
        }

        if (!hasAlpha || hasMask)
        {
            for (int y = 0; y < height; y++)
            {
                int maskY = dibFullHeight > 0 ? height - 1 - y : y;
                int maskRow = maskOffset + maskY * maskStride;
                for (int x = 0; x < width; x++)
                {
                    bool transparent = hasMask && (frame[maskRow + x / 8] & (1 << (7 - x % 8))) != 0;
                    int alphaOffset = y * targetStride + x * 4 + 3;
                    if (transparent)
                        pixels[alphaOffset] = 0;
                    else if (!hasAlpha)
                        pixels[alphaOffset] = 255;
                }
            }
        }

        source = Detach(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, targetStride));
        return true;
    }

    public static BitmapSource? FromFile(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan
            );
            if (stream.Length <= 0 || stream.Length > MaxCompressedImageBytes)
            {
                return null;
            }
            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(bytes);
            return FromBytes(bytes, decodeWidth);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("SafeImaging::FromFile", "Could not read " + path + ": " + ex.Message.Split('\n')[0]);
            return null;
        }
    }

    private const int MaxCachedUriImages = 64;

    private static readonly ConcurrentDictionary<string, BitmapSource> _uriCache = new(StringComparer.Ordinal);

    public static void ClearUriCache()
    {
        _uriCache.Clear();
    }

    public static BitmapSource? FromUri(Uri? uri, int decodeWidth = 0)
    {
        if (uri == null)
        {
            return null;
        }
        string key = uri.OriginalString + "|" + decodeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_uriCache.TryGetValue(key, out BitmapSource? cached))
        {
            return cached;
        }
        BitmapSource? loaded = LoadUri(uri, decodeWidth);
        if (loaded == null)
        {
            loaded = FromManifest(uri, decodeWidth);
        }
        if (loaded == null)
        {
            App.Logger?.WriteLine("SafeImaging::FromUri", "Could not load " + uri);
            return null;
        }
        if (loaded.IsFrozen)
        {
            if (_uriCache.Count >= MaxCachedUriImages)
            {
                _uriCache.Clear();
            }
            _uriCache[key] = loaded;
        }
        return loaded;
    }

    public static async System.Threading.Tasks.Task<BitmapSource?> FromHttpAsync(
        string? value,
        int decodeWidth = 0,
        System.Threading.CancellationToken token = default
    )
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        using System.Net.Http.HttpRequestMessage request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
        using System.Net.Http.HttpResponseMessage response = await _http
            .Value.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (
            !response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is long contentLength && contentLength > MaxCompressedImageBytes
        )
        {
            return null;
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[]? bytes = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
        if (bytes == null)
        {
            return null;
        }
        return await System.Threading.Tasks.Task.Run(() => FromBytes(bytes, decodeWidth), token).ConfigureAwait(false);
    }

    private static BitmapSource? FromManifest(Uri uri, int decodeWidth)
    {
        try
        {
            string path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
            path = path.TrimStart('/').Replace('/', '.').Replace('\\', '.');
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            Assembly assembly = Assembly.GetExecutingAssembly();
            string target = assembly.GetName().Name + "." + path;
            using Stream? stream = assembly.GetManifestResourceStream(target);
            if (stream == null)
            {
                return null;
            }
            return FromBytes(ReadBounded(stream), decodeWidth);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadUri(Uri uri, int decodeWidth)
    {
        try
        {
            if (uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using System.Net.Http.HttpRequestMessage request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    uri
                );
                using System.Net.Http.HttpResponseMessage response = _http
                    .Value.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter()
                    .GetResult();
                if (
                    !response.IsSuccessStatusCode
                    || response.Content.Headers.ContentLength is long contentLength && contentLength > MaxCompressedImageBytes
                )
                {
                    return null;
                }
                using Stream responseStream = response.Content.ReadAsStream();
                return FromBytes(ReadBounded(responseStream), decodeWidth);
            }
            if (uri.IsAbsoluteUri && uri.IsFile)
            {
                return FromFile(uri.LocalPath, decodeWidth);
            }
            if (Platform.IsWindows)
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0)
                {
                    image.DecodePixelWidth = decodeWidth;
                }
                image.UriSource = uri;
                image.EndInit();
                return Detach(DropSource(image));
            }
            StreamResourceInfo? info = Application.GetResourceStream(uri);
            if (info?.Stream == null)
            {
                return null;
            }
            using Stream stream = info.Stream;
            return FromBytes(ReadBounded(stream), decodeWidth);
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? FromPack(string packUri, int decodeWidth = 0)
    {
        return Uri.TryCreate(packUri, UriKind.Absolute, out Uri? uri) ? FromUri(uri, decodeWidth) : null;
    }

    public static BitmapSource? FromStream(Stream? stream, int decodeWidth = 0)
    {
        if (stream == null)
        {
            return null;
        }
        try
        {
            return FromBytes(ReadBounded(stream), decodeWidth);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("SafeImaging::FromStream", "Decode failed: " + ex.Message.Split('\n')[0]);
            return null;
        }
    }

    private static byte[]? ReadBounded(Stream stream)
    {
        int capacity = stream.CanSeek ? (int)Math.Min(stream.Length - stream.Position, MaxCompressedImageBytes) : 81920;
        using MemoryStream copy = new MemoryStream(Math.Max(0, capacity));
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return copy.Length == 0 ? null : copy.ToArray();
            }
            if (copy.Length + read > MaxCompressedImageBytes)
            {
                return null;
            }
            copy.Write(buffer, 0, read);
        }
    }

    private static async System.Threading.Tasks.Task<byte[]?> ReadBoundedAsync(Stream stream, System.Threading.CancellationToken token)
    {
        int capacity = stream.CanSeek ? (int)Math.Min(stream.Length - stream.Position, MaxCompressedImageBytes) : 81920;
        using MemoryStream copy = new MemoryStream(Math.Max(0, capacity));
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0)
            {
                return copy.Length == 0 ? null : copy.ToArray();
            }
            if (copy.Length + read > MaxCompressedImageBytes)
            {
                return null;
            }
            await copy.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static bool TryReadNativeDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!Platform.IsWindows)
            return false;
        try
        {
            using MemoryStream stream = new MemoryStream(bytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None
            );
            if (decoder.Frames.Count == 0)
                return false;
            BitmapFrame frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPortableDimensions(byte[] bytes, out long width, out long height)
    {
        width = 0;
        height = 0;
        if (!Raster.TryIdentify(bytes, out int identifiedWidth, out int identifiedHeight))
            return false;
        width = identifiedWidth;
        height = identifiedHeight;
        return true;
    }

    private static WriteableBitmap DropSource(BitmapImage image)
    {
        WriteableBitmap copy = new WriteableBitmap(image);
        copy.Freeze();
        return copy;
    }

    private static bool HasSafeDimensions(byte[] bytes, int decodeWidth)
    {
        try
        {
            long width;
            long height;
            if (TryReadNativeDimensions(bytes, out int nativeWidth, out int nativeHeight))
            {
                width = nativeWidth;
                height = nativeHeight;
            }
            else if (!TryReadPortableDimensions(bytes, out width, out height))
            {
                return false;
            }
            if (decodeWidth > 0 && width > decodeWidth)
            {
                height = Math.Max(1, (height * decodeWidth + width - 1) / width);
                width = decodeWidth;
            }
            return width * height * 4 <= MaxDecodedImageBytes;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? DecodePortable(byte[] bytes, int decodeWidth)
    {
        if (
            NativeImaging.TryDecodeBgra(
                bytes,
                decodeWidth,
                MaxDecodedImageBytes,
                out int nativeWidth,
                out int nativeHeight,
                out byte[] nativePixels
            )
        )
            return Detach(
                BitmapSource.Create(nativeWidth, nativeHeight, 96, 96, PixelFormats.Bgra32, null, nativePixels, checked(nativeWidth * 4))
            );
        return null;
    }

    public static IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> DecodeAnimationPortable(
        string? path,
        int decodeWidth = 0,
        CancellationToken token = default
    )
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];
        try
        {
            FileInfo file = new(path);
            if (file.Length <= 0 || file.Length > MaxCompressedImageBytes)
                return [];
            token.ThrowIfCancellationRequested();
            long budget = Platform.UsesPortableUi ? MaxDecodedAnimationBytes / 4 : MaxDecodedAnimationBytes;
            return DecodeAnimationFrames(File.ReadAllBytes(path), decodeWidth, budget, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("SafeImaging::DecodeAnimationPortable", "Animation decode failed: " + ex.Message.Split('\n')[0]);
            return [];
        }
    }

    public static IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> DecodeAnimationPortable(
        byte[]? bytes,
        int decodeWidth = 0,
        CancellationToken token = default
    )
    {
        if (bytes == null || bytes.Length == 0 || bytes.LongLength > MaxCompressedImageBytes)
            return [];
        try
        {
            return DecodeAnimationFrames(bytes, decodeWidth, MaxDecodedAnimationBytes, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("SafeImaging::DecodeAnimationPortable", "Animation byte decode failed: " + ex.Message.Split('\n')[0]);
            return [];
        }
    }

    private static List<(BitmapSource Frame, int DelayMilliseconds)> DecodeAnimationFrames(
        byte[] bytes,
        int decodeWidth,
        long budget,
        CancellationToken token
    )
    {
        List<(BitmapSource, int)> frames = [];
        token.ThrowIfCancellationRequested();
        if (!Raster.TryIdentify(bytes, out int sourceWidth, out int sourceHeight))
            return frames;
        long sourceFrameBytes = checked((long)sourceWidth * sourceHeight * 4);
        if (sourceFrameBytes > MaxDecodedImageBytes)
            return frames;
        int targetWidth = sourceWidth;
        int targetHeight = sourceHeight;
        if (decodeWidth > 0 && targetWidth > decodeWidth)
        {
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * (double)decodeWidth / targetWidth));
            targetWidth = decodeWidth;
        }
        if (
            NativeImaging.TryDecodeAnimationBgra(
                bytes,
                targetWidth,
                targetHeight,
                MaxAnimationFrames,
                budget,
                out int width,
                out int height,
                out byte[][] pixels,
                out int[] delays
            )
        )
        {
            for (int index = 0; index < pixels.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                BitmapSource source = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels[index],
                    checked(width * 4)
                );
                frames.Add((Detach(source), delays[index] <= 10 ? 100 : delays[index]));
            }
            return frames;
        }
        Raster still = Raster.Decode(bytes, MaxDecodedImageBytes).FitWithin(targetWidth, targetHeight);
        frames.Add(
            (
                Detach(
                    BitmapSource.Create(
                        still.Width,
                        still.Height,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null,
                        still.Pixels,
                        checked(still.Width * 4)
                    )
                ),
                100
            )
        );
        return frames;
    }

    public static bool TryReadDimensions(string? path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            FileInfo file = new(path);
            if (file.Length <= 0 || file.Length > MaxCompressedImageBytes)
                return false;
            return Raster.TryIdentify(File.ReadAllBytes(path), out width, out height);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool SupportsVisualCapture => Platform.IsWindows;

    internal static BitmapSource Detach(BitmapSource source)
    {
        if (source.CanFreeze && !source.IsFrozen)
        {
            source.Freeze();
        }
        return source;
    }
}
