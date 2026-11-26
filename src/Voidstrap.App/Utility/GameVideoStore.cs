using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class GameVideoStore
{
    private const long MaximumVideoBytes = 200L * 1024 * 1024;

    private const int MaximumPlaylistBytes = 256 * 1024;
    private const int MaximumSegments = 96;

    private static readonly HttpClient HttpClient = VpnHttpClient.Create(TimeSpan.FromMinutes(2), handler => handler.AllowAutoRedirect = false);

    private static string Folder => Path.Combine(Paths.Cache, "GameVideos");

    private enum VideoFormat { Unknown, Webm, Mp4, Playlist }

    internal static bool IsPlayableVideo(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => ReadFormat(path) == VideoFormat.Webm,
            ".mp4" => ReadFormat(path) == VideoFormat.Mp4,
            _ => false,
        };
    }

    public static async Task<string?> GetLocalVideoAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "GameVideoStore::GetLocalVideoAsync";
        if (universeId <= 0)
            return null;
        try
        {
            string? videoId = await FindVideoIdAsync(universeId, token).ConfigureAwait(false);
            if (videoId == null)
                return null;
            string path = Path.Combine(Folder, videoId + ".webm");
            bool convert = Platform.IsMacOS || (Platform.IsWindows && GpuInventory.IsVirtualMachine);
            string mp4 = Path.ChangeExtension(path, ".mp4");
            if (!convert && IsPlayableVideo(path))
                return path;
            if (IsPlayableVideo(mp4))
                return mp4;
            Uri? location = await FindLocationAsync(videoId, token).ConfigureAwait(false);
            if (location == null)
                return null;
            Directory.CreateDirectory(Folder);
            await Task.Run(TrimCache, token).ConfigureAwait(false);
            string workspace = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try
            {
                string staged = Path.Combine(workspace, "asset");
                await DownloadAsync(location, staged, token).ConfigureAwait(false);
                switch (ReadFormat(staged))
                {
                    case VideoFormat.Mp4:
                        File.Move(staged, mp4, true);
                        return mp4;
                    case VideoFormat.Webm when !convert:
                        File.Move(staged, path, true);
                        return path;
                    case VideoFormat.Webm:
                        return await ConvertAsync(staged, mp4, workspace, false, token).ConfigureAwait(false) ? mp4 : null;
                    case VideoFormat.Playlist:
                        string playlist = await SaveSegmentsAsync(location, staged, workspace, token).ConfigureAwait(false);
                        string playable = Platform.UsesLinuxHost ? path : mp4;
                        return await ConvertAsync(playlist, playable, workspace, true, token).ConfigureAwait(false) ? playable : null;
                    default:
                        return null;
                }
            }
            finally
            {
                Directory.Delete(workspace, true);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The game trailer for " + universeId + " could not be prepared: " + ex.GetType().Name);
            return null;
        }
    }

    private static void TrimCache()
    {
        const long MaximumCacheBytes = 256L * 1024 * 1024;
        if (!Directory.Exists(Folder))
            return;
        FileInfo[] files = new DirectoryInfo(Folder).GetFiles().OrderByDescending(static file => file.LastAccessTimeUtc).ToArray();
        long total = 0;
        foreach (FileInfo file in files)
        {
            total += file.Length;
            if (total <= MaximumCacheBytes)
                continue;
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<bool> ConvertAsync(string source, string target, string workspace, bool concat, CancellationToken token)
    {
        const string LOG_IDENT = "GameVideoStore::ConvertAsync";
        string? ffmpeg = await Voidstrap.Integrations.Clipper.FfmpegProvisioner.EnsureAvailableAsync(null, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(ffmpeg))
            return false;
        bool webm = Path.GetExtension(target) == ".webm";
        string temporary = Path.Combine(workspace, Path.GetFileName(target));
        ProcessStartInfo info = new(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = false };
        foreach (string argument in new[] { "-nostdin", "-y", "-loglevel", "error", "-protocol_whitelist", "file" })
            info.ArgumentList.Add(argument);
        if (concat)
            foreach (string argument in new[] { "-f", "concat", "-safe", "1" })
                info.ArgumentList.Add(argument);
        foreach (string argument in new[] { "-discard:a", "all", "-i", source, "-an", "-map", "0:v:0" })
            info.ArgumentList.Add(argument);
        string[] codec = webm
            ? ["-c:v", "copy"]
            : ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-vf", "fps=30,scale=trunc(iw/2)*2:trunc(ih/2)*2"];
        foreach (string argument in codec)
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add(temporary);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
        if (process.ExitCode != 0 || !IsPlayableVideo(temporary))
        {
            App.Logger.WriteLine(LOG_IDENT, "ffmpeg could not convert the game video, exit code " + process.ExitCode);
            return false;
        }
        File.Move(temporary, target, true);
        return true;
    }

    private static async Task<string?> FindVideoIdAsync(long universeId, CancellationToken token)
    {
        string url = $"https://games.roblox.com/v2/games/{universeId}/media";
        using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
        foreach (JsonElement item in document.RootElement.GetProperty("data").EnumerateArray())
            if ((!item.TryGetProperty("approved", out JsonElement approved) || approved.ValueKind != JsonValueKind.False)
                && item.TryGetProperty("videoId", out JsonElement id)
                && long.TryParse(id.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0)
                return value.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static async Task<Uri?> FindLocationAsync(string videoId, CancellationToken token)
    {
        string representation = Convert.ToBase64String("""[{"format":"hls","majorVersion":"1","fidelity":"main"}]"""u8);
        string url = $"https://assetdelivery.roblox.com/v2/asset?Id={videoId}&ContentRepresentationPriorityList={Uri.EscapeDataString(representation)}";
        using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("locations", out JsonElement locations) || locations.GetArrayLength() == 0)
            return null;
        return Uri.TryCreate(locations[0].GetProperty("location").GetString(), UriKind.Absolute, out Uri? location) && IsMediaUri(location)
            ? location : null;
    }

    private static bool IsMediaUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host.Equals("rbxcdn.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase));

    private static VideoFormat ReadFormat(string path)
    {
        if (!File.Exists(path))
            return VideoFormat.Unknown;

        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> prefix = stackalloc byte[12];
            int bytes = file.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
            if (bytes < prefix.Length)
                return VideoFormat.Unknown;
            if (prefix[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
                return VideoFormat.Webm;
            if (prefix[4..8].SequenceEqual("ftyp"u8))
                return VideoFormat.Mp4;
            return prefix.StartsWith("#EXTM3U"u8) ? VideoFormat.Playlist : VideoFormat.Unknown;
        }
        catch (IOException)
        {
            return VideoFormat.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return VideoFormat.Unknown;
        }
    }

    private static async Task<string> SaveSegmentsAsync(Uri masterUri, string masterPath, string workspace, CancellationToken token)
    {
        string[] master = ReadPlaylist(masterPath);
        string? variant = master.FirstOrDefault(static line => line.Length > 0 && line[0] != '#');
        if (variant == null || !master.Any(static line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)))
            throw new InvalidDataException("The trailer has no available video stream.");

        Uri playlistUri = ResolvePlaylistUri(masterUri, variant);
        string playlistPath = Path.Combine(workspace, "media.m3u8");
        await DownloadAsync(playlistUri, playlistPath, token, MaximumPlaylistBytes).ConfigureAwait(false);
        string[] media = ReadPlaylist(playlistPath);
        if (!media.Contains("#EXT-X-ENDLIST") || media.Any(static line => line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)))
            throw new InvalidDataException("The trailer playlist does not contain a supported recorded stream.");

        StringBuilder contents = new("ffconcat version 1.0\n");
        int count = 0;
        long remaining = MaximumVideoBytes;
        double duration = 0;
        foreach (string line in media)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line.AsSpan(8);
                int comma = value.IndexOf(',');
                if (!double.TryParse(comma < 0 ? value : value[..comma], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out duration)
                    || duration is <= 0 or > 60)
                    throw new InvalidDataException("The trailer contains an invalid segment duration.");
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                if (++count > MaximumSegments || remaining <= 0 || duration <= 0)
                    throw new InvalidDataException("The trailer exceeds the supported segment limits.");

                string name = count.ToString(CultureInfo.InvariantCulture) + ".webm";
                string file = Path.Combine(workspace, name);
                await DownloadAsync(ResolvePlaylistUri(playlistUri, line), file, token, remaining).ConfigureAwait(false);
                if (ReadFormat(file) != VideoFormat.Webm)
                    throw new InvalidDataException("The trailer segment is not a WebM video.");
                remaining -= new FileInfo(file).Length;
                contents.Append("file ").Append(name).Append('\n');
                contents.Append("duration ").Append(duration.ToString(CultureInfo.InvariantCulture)).Append('\n');
                duration = 0;
            }
        }
        if (count == 0)
            throw new InvalidDataException("The trailer playlist is empty.");

        string list = Path.Combine(workspace, "segments.ffconcat");
        await File.WriteAllTextAsync(list, contents.ToString(), token).ConfigureAwait(false);
        return list;
    }

    private static string[] ReadPlaylist(string file)
    {
        if (new FileInfo(file).Length > MaximumPlaylistBytes)
            throw new InvalidDataException("The trailer playlist is too large.");
        string[] lines = File.ReadAllLines(file);
        if (lines.Length == 0 || lines[0] != "#EXTM3U")
            throw new InvalidDataException("The trailer playlist is invalid.");
        return lines;
    }

    private static Uri ResolvePlaylistUri(Uri playlist, string reference)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        foreach (string pair in playlist.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator > 0)
                query.TryAdd(pair[..separator], pair[(separator + 1)..]);
        }
        string expanded = Regex.Replace(reference, @"\{\$([A-Za-z0-9_-]+)\}", match =>
            query.TryGetValue(match.Groups[1].Value, out string? value) ? value
                : throw new InvalidDataException("The trailer playlist requires a missing URL parameter."));
        if (!Uri.TryCreate(playlist, expanded, out Uri? uri) || !IsMediaUri(uri))
            throw new InvalidDataException("The trailer playlist points outside the Roblox media CDN.");
        return uri;
    }

    private static async Task DownloadAsync(Uri url, string path, CancellationToken token, long maximumBytes = MaximumVideoBytes)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The game video is too large.");
        await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using BufferedStream buffered = new(body);
        int first = buffered.ReadByte();
        int second = buffered.ReadByte();
        if (first < 0 || second < 0)
            throw new InvalidDataException("The game video is empty.");
        await using FileStream raw = File.Create(path + ".raw");
        raw.WriteByte((byte)first);
        raw.WriteByte((byte)second);
        await CopyBoundedAsync(buffered, raw, maximumBytes - 2, token).ConfigureAwait(false);
        raw.Position = 0;
        await using FileStream output = File.Create(path);
        if (first == 0x1F && second == 0x8B)
        {
            await using GZipStream gzip = new(raw, CompressionMode.Decompress);
            await CopyBoundedAsync(gzip, output, maximumBytes, token).ConfigureAwait(false);
        }
        else
        {
            await raw.CopyToAsync(output, token).ConfigureAwait(false);
        }
        await raw.DisposeAsync().ConfigureAwait(false);
        File.Delete(path + ".raw");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long maximumBytes, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("The game video is too large.");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }
}
