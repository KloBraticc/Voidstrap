using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginMedia
{
    private const int MaxEncodedBytes = 16 * 1024 * 1024;
    private const long MaxDecodedBytes = 48L * 1024 * 1024;
    private const int MaxFrames = 180;
    private static readonly HttpClient Http = new(
        new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static readonly Dictionary<string, Entry> Cache = [];

    private sealed record Media(IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> Frames, long Bytes);

    private sealed record Entry(Task<Media?> Task, DateTime Created);

    internal static void Apply(
        Image image,
        string source,
        string? directory,
        int frameWidth,
        int frameHeight,
        int frameIndex,
        double fps,
        bool animate
    )
    {
        try
        {
            string identity = source;
            string? path = null;
            if (
                !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            )
            {
                path = ResolveLocalPath(directory, source);
                if (path == null)
                    return;
                FileInfo file = new(path);
                if (!file.Exists || file.Length > MaxEncodedBytes)
                    return;
                identity = path + "\0" + file.Length + "\0" + file.LastWriteTimeUtc.Ticks;
            }
            if (source.Length > MaxEncodedBytes * 4L / 3 + 128)
                return;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\0" + frameWidth + "\0" + frameHeight)));
            Task<Media?> task;
            lock (Cache)
            {
                if (
                    Cache.TryGetValue(key, out Entry? found)
                    && (
                        !found.Task.IsCompleted
                        || (
                            found.Task.IsCompletedSuccessfully
                            && found.Task.Result != null
                            && (
                                !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                || DateTime.UtcNow - found.Created < TimeSpan.FromMinutes(5)
                            )
                        )
                        || DateTime.UtcNow - found.Created < TimeSpan.FromMinutes(1)
                    )
                )
                    task = found.Task;
                else
                {
                    if (Cache.Count >= 128)
                    {
                        foreach (string expired in Cache.Where(item => item.Value.Task.IsCompleted).Select(item => item.Key).ToArray())
                            Cache.Remove(expired);
                        if (Cache.Count >= 128)
                            return;
                    }
                    task = Task.Run(() => LoadAsync(source, path, frameWidth, frameHeight));
                    Cache[key] = new Entry(task, DateTime.UtcNow);
                }
            }
            if (task.IsCompletedSuccessfully)
                Display(image, task.Result, frameIndex, fps, animate);
            else
                _ = DisplayAsync(image, task, frameIndex, fps, animate);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("PluginMedia", "Media was hidden: " + ex.GetType().Name);
        }
    }

    internal static string? ResolveLocalPath(string? directory, string source)
    {
        if (string.IsNullOrEmpty(directory) || Path.IsPathRooted(source))
            return null;
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, source));
        StringComparison comparison = Utility.Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison))
            return null;
        for (
            FileSystemInfo? current = new FileInfo(full);
            current != null;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent
        )
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
        }
        return full;
    }

    private static async Task DisplayAsync(Image image, Task<Media?> task, int index, double fps, bool animate)
    {
        try
        {
            Media? media = await task.ConfigureAwait(false);
            if (!image.Dispatcher.HasShutdownStarted)
                await image.Dispatcher.InvokeAsync(
                    () => Display(image, media, index, fps, animate),
                    System.Windows.Threading.DispatcherPriority.Background
                );
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("PluginMedia", "Media display failed: " + ex.GetType().Name);
        }
    }

    private static void Display(Image image, Media? media, int index, double fps, bool animate)
    {
        lock (Cache)
        {
            long total = 0;
            foreach (KeyValuePair<string, Entry> item in Cache.OrderByDescending(item => item.Value.Created).ToArray())
            {
                if (!item.Value.Task.IsCompletedSuccessfully || item.Value.Task.Result == null)
                    continue;
                total += item.Value.Task.Result.Bytes;
                if (total > MaxDecodedBytes)
                    Cache.Remove(item.Key);
            }
        }
        if (media == null || media.Frames.Count == 0)
            return;
        if (index >= 0)
        {
            if (index < media.Frames.Count)
                image.Source = media.Frames[index].Frame;
            return;
        }
        if (!animate || media.Frames.Count == 1)
            image.Source = media.Frames[0].Frame;
        else
            UI.GifImageBehavior.SetFrames(
                image,
                fps > 0
                    ? media.Frames.Select(frame => (frame.Frame, (int)Math.Round(1000 / Math.Clamp(fps, 1, 60)))).ToArray()
                    : media.Frames
            );
    }

    private static async Task<Media?> LoadAsync(string source, string? path, int frameWidth, int frameHeight)
    {
        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] bytes;
            if (path != null)
            {
                using FileStream stream = File.OpenRead(path);
                bytes = await ReadBoundedAsync(stream).ConfigureAwait(false);
            }
            else if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = source.IndexOf(',');
                if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return null;
                bytes = Convert.FromBase64String(source[(comma + 1)..]);
            }
            else
            {
                using HttpResponseMessage response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                bytes = await ReadBoundedAsync(stream).ConfigureAwait(false);
            }
            if (
                bytes.Length == 0
                || bytes.Length > MaxEncodedBytes
                || !Raster.TryIdentify(bytes, out int width, out int height)
                || (long)width * height * 4 > MaxDecodedBytes
            )
                return null;
            List<(BitmapSource Frame, int DelayMilliseconds)> frames = [];
            bool gif = bytes.Length >= 6 && Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a";
            if (gif)
            {
                int count = CountGifFrames(bytes);
                if (count < 1 || (long)width * height * 4 * count > MaxDecodedBytes || frameWidth != 0 || frameHeight != 0)
                    return null;
                frames.AddRange(SafeImaging.DecodeAnimationPortable(bytes));
                if (frames.Count != count)
                    return null;
            }
            else
            {
                Raster raster = Raster.Decode(bytes, MaxDecodedBytes);
                if (frameWidth != 0 || frameHeight != 0)
                {
                    if (
                        frameWidth <= 0
                        || frameHeight <= 0
                        || width % frameWidth != 0
                        || height % frameHeight != 0
                        || (long)(width / frameWidth) * (height / frameHeight) > MaxFrames
                    )
                        return null;
                    for (int y = 0; y < height; y += frameHeight)
                        for (int x = 0; x < width; x += frameWidth)
                            frames.Add((ToBitmap(raster.Crop(x, y, frameWidth, frameHeight)), 100));
                }
                else
                    frames.Add((ToBitmap(raster), 100));
            }
            foreach ((BitmapSource frame, _) in frames)
            {
                if (!await MediaContentFilter.IsAllowedAsync(frame).ConfigureAwait(false))
                {
                    App.Logger?.WriteLine("PluginMedia", "Media was hidden by local content checking");
                    return null;
                }
            }
            return new Media(frames, frames.Sum(frame => (long)frame.Frame.PixelWidth * frame.Frame.PixelHeight * 4));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("PluginMedia", "Media was hidden because loading failed: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static BitmapSource ToBitmap(Raster raster) =>
        SafeImaging.Detach(
            BitmapSource.Create(raster.Width, raster.Height, 96, 96, PixelFormats.Bgra32, null, raster.Pixels, checked(raster.Width * 4))
        );

    private static async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaxEncodedBytes)
                throw new InvalidDataException("Media exceeds its size limit");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static int CountGifFrames(byte[] bytes)
    {
        if (bytes.Length < 13 || Encoding.ASCII.GetString(bytes, 0, 6) is not ("GIF87a" or "GIF89a"))
            return 0;
        int offset = 13 + ((bytes[10] & 128) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
        int frames = 0;
        while (offset < bytes.Length)
        {
            int marker = bytes[offset++];
            if (marker == 0x3b)
                return frames;
            if (marker == 0x2c)
            {
                if (offset + 9 > bytes.Length || ++frames > MaxFrames)
                    return 0;
                int packed = bytes[offset + 8];
                offset += 9 + ((packed & 128) != 0 ? 3 * (1 << ((packed & 7) + 1)) : 0) + 1;
            }
            else if (marker == 0x21)
                offset++;
            else
                return 0;
            int length;
            do
            {
                if (offset >= bytes.Length)
                    return 0;
                length = bytes[offset++];
                offset += length;
            } while (length != 0);
        }
        return 0;
    }
}
