using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class GameVideoStore
{
    private const long MaximumVideoBytes = 200L * 1024 * 1024;

    private static readonly HttpClient HttpClient = VpnHttpClient.Create(TimeSpan.FromMinutes(2));

    private static string Folder => Path.Combine(Paths.Cache, "GameVideos");

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
            string playable = Platform.IsMacOS ? Path.ChangeExtension(path, ".mp4") : path;
            if (File.Exists(playable))
                return playable;
            string? location = await FindLocationAsync(videoId, token).ConfigureAwait(false);
            if (location == null)
                return null;
            Directory.CreateDirectory(Folder);
            string temporary = path + ".tmp";
            await DownloadAsync(location, temporary, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
            if (!Platform.IsMacOS)
                return path;
            return await ConvertAsync(path, playable, token).ConfigureAwait(false) ? playable : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            return null;
        }
    }

    private static async Task<bool> ConvertAsync(string source, string target, CancellationToken token)
    {
        const string LOG_IDENT = "GameVideoStore::ConvertAsync";
        string? ffmpeg = await Voidstrap.Integrations.Clipper.FfmpegProvisioner.EnsureAvailableAsync(null, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(ffmpeg))
            return false;
        string temporary = target + ".tmp.mp4";
        ProcessStartInfo info = new(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = false };
        foreach (string argument in new[] { "-y", "-loglevel", "error", "-i", source, "-an", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", temporary })
            info.ArgumentList.Add(argument);
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
        if (process.ExitCode != 0)
        {
            App.Logger.WriteLine(LOG_IDENT, "ffmpeg could not convert the game video, exit code " + process.ExitCode);
            File.Delete(temporary);
            return false;
        }
        File.Move(temporary, target, true);
        File.Delete(source);
        return true;
    }

    private static async Task<string?> FindVideoIdAsync(long universeId, CancellationToken token)
    {
        string url = $"https://games.roblox.com/v2/games/{universeId}/media";
        using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
        foreach (JsonElement item in document.RootElement.GetProperty("data").EnumerateArray())
            if (item.TryGetProperty("videoId", out JsonElement id) && id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out _))
                return id.GetString();
        return null;
    }

    private static async Task<string?> FindLocationAsync(string videoId, CancellationToken token)
    {
        string url = $"https://assetdelivery.roblox.com/v2/assetId/{videoId}";
        using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url, token: token).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("locations", out JsonElement locations) || locations.GetArrayLength() == 0)
            return null;
        return locations[0].GetProperty("location").GetString();
    }

    private static async Task DownloadAsync(string url, string path, CancellationToken token)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumVideoBytes)
            throw new InvalidDataException("The game video is too large.");
        await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using BufferedStream buffered = new(body);
        int first = buffered.ReadByte();
        int second = buffered.ReadByte();
        await using FileStream raw = File.Create(path + ".raw");
        raw.WriteByte((byte)first);
        raw.WriteByte((byte)second);
        await CopyBoundedAsync(buffered, raw, token).ConfigureAwait(false);
        raw.Position = 0;
        await using FileStream output = File.Create(path);
        if (first == 0x1F && second == 0x8B)
        {
            await using GZipStream gzip = new(raw, CompressionMode.Decompress);
            await CopyBoundedAsync(gzip, output, token).ConfigureAwait(false);
        }
        else
        {
            await raw.CopyToAsync(output, token).ConfigureAwait(false);
        }
        await raw.DisposeAsync().ConfigureAwait(false);
        File.Delete(path + ".raw");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaximumVideoBytes)
                throw new InvalidDataException("The game video is too large.");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }
}
