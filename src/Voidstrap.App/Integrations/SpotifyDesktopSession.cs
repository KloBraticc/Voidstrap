using System.Linq;
using System.Threading.Tasks;
#if !CROSSPLAT
using Windows.Media.Control;
using Windows.Storage.Streams;
#endif

namespace Voidstrap.Integrations;

public sealed record SpotifyDesktopTrack(string Title, string Artist, bool Playing, byte[]? Cover, TimeSpan Position = default, TimeSpan Duration = default)
{
    public DateTime SampledAt { get; init; } = DateTime.UtcNow;

    public TimeSpan PositionNow
    {
        get
        {
            TimeSpan position = Playing ? Position + (DateTime.UtcNow - SampledAt) : Position;
            if (position < TimeSpan.Zero)
                return TimeSpan.Zero;
            return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
        }
    }
}

public static class SpotifyDesktopSession
{
#if CROSSPLAT
    private const string MprisName = "org.mpris.MediaPlayer2.spotify";

    private const string MprisPath = "/org/mpris/MediaPlayer2";

    private const string MprisPlayer = "org.mpris.MediaPlayer2.Player";

    private const string MacScript =
        "if application \"Spotify\" is running then tell application \"Spotify\" to return (player state as string) & linefeed & (name of current track) & linefeed & (artist of current track) & linefeed & (player position as string) & linefeed & (duration of current track as string)";

    public static async Task<SpotifyDesktopTrack?> ReadAsync(string? knownTitle)
    {
        try
        {
            if (Voidstrap.Utility.Platform.IsMacOS)
                return await ReadMacAsync().ConfigureAwait(false);
            if (Voidstrap.Utility.Platform.IsLinux)
                return await ReadLinuxAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        {
        }
        return null;
    }

    private static async Task<SpotifyDesktopTrack?> ReadMacAsync()
    {
        string? output = await RunAsync("osascript", "-e", MacScript).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            return null;
        string[] lines = output.Split((char)10);
        if (lines.Length < 3 || string.IsNullOrWhiteSpace(lines[1]))
            return null;
        TimeSpan position = lines.Length > 3 && double.TryParse(lines[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        TimeSpan duration = lines.Length > 4 && double.TryParse(lines[4].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double millis) ? TimeSpan.FromMilliseconds(millis) : TimeSpan.Zero;
        return new SpotifyDesktopTrack(lines[1].Trim(), lines[2].Trim(), lines[0].Trim() == "playing", null, position, duration);
    }

    private static async Task<SpotifyDesktopTrack?> ReadLinuxAsync()
    {
        string? status = await RunAsync("busctl", "--user", "--json=short", "get-property", MprisName, MprisPath, MprisPlayer, "PlaybackStatus").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status))
            return null;
        string? metadata = await RunAsync("busctl", "--user", "--json=short", "get-property", MprisName, MprisPath, MprisPlayer, "Metadata").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(metadata))
            return null;
        using System.Text.Json.JsonDocument statusDoc = System.Text.Json.JsonDocument.Parse(status);
        using System.Text.Json.JsonDocument metaDoc = System.Text.Json.JsonDocument.Parse(metadata);
        if (!metaDoc.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement data))
            return null;
        string title = data.TryGetProperty("xesam:title", out System.Text.Json.JsonElement t) && t.TryGetProperty("data", out System.Text.Json.JsonElement tv)
            ? tv.GetString() ?? string.Empty
            : string.Empty;
        if (title.Length == 0)
            return null;
        string artist = string.Empty;
        if (data.TryGetProperty("xesam:artist", out System.Text.Json.JsonElement a) && a.TryGetProperty("data", out System.Text.Json.JsonElement av) && av.ValueKind == System.Text.Json.JsonValueKind.Array)
            artist = string.Join(", ", av.EnumerateArray().Select(static item => item.GetString()).Where(static item => !string.IsNullOrEmpty(item)));
        bool playing = statusDoc.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement s) && s.GetString() == "Playing";
        TimeSpan duration = data.TryGetProperty("mpris:length", out System.Text.Json.JsonElement l) && l.TryGetProperty("data", out System.Text.Json.JsonElement lv) && lv.TryGetInt64(out long micros)
            ? TimeSpan.FromTicks(micros * 10)
            : TimeSpan.Zero;
        TimeSpan position = TimeSpan.Zero;
        string? positionJson = await RunAsync("busctl", "--user", "--json=short", "get-property", MprisName, MprisPath, MprisPlayer, "Position").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(positionJson))
        {
            using System.Text.Json.JsonDocument positionDoc = System.Text.Json.JsonDocument.Parse(positionJson);
            if (positionDoc.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement pv) && pv.TryGetInt64(out long at))
                position = TimeSpan.FromTicks(at * 10);
        }
        return new SpotifyDesktopTrack(title, artist, playing, null, position, duration);
    }

    private static async Task<string?> RunAsync(string file, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo info = new(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
        if (process == null)
            return null;
        using System.Threading.CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        Task<string> read = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            return null;
        }
        string output = await read.ConfigureAwait(false);
        return process.ExitCode == 0 ? output : null;
    }

    public static Task ToggleAsync() => ControlAsync("PlayPause", "playpause");

    public static Task SkipAsync(bool next) => next ? ControlAsync("Next", "next track") : ControlAsync("Previous", "previous track");

    private static async Task ControlAsync(string mprisMethod, string macCommand)
    {
        try
        {
            if (Voidstrap.Utility.Platform.IsMacOS)
                await RunAsync("osascript", "-e", "if application \"Spotify\" is running then tell application \"Spotify\" to " + macCommand).ConfigureAwait(false);
            else if (Voidstrap.Utility.Platform.IsLinux)
                await RunAsync("busctl", "--user", "call", MprisName, MprisPath, MprisPlayer, mprisMethod).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
#else
    private const string LOG_IDENT = "SpotifyDesktopSession";

    private const uint MaximumCoverBytes = 2 * 1024 * 1024;

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    private static async Task<GlobalSystemMediaTransportControlsSession?> FindAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
        return _manager.GetSessions().FirstOrDefault(static session => session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<SpotifyDesktopTrack?> ReadAsync(string? knownTitle)
    {
        try
        {
            GlobalSystemMediaTransportControlsSession? session = await FindAsync().ConfigureAwait(false);
            if (session == null)
                return null;
            GlobalSystemMediaTransportControlsSessionMediaProperties media = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            if (string.IsNullOrEmpty(media.Title))
                return null;
            bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            byte[]? cover = media.Title == knownTitle ? null : await ReadCoverAsync(media).ConfigureAwait(false);
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            TimeSpan duration = timeline.EndTime - timeline.StartTime;
            TimeSpan position = timeline.Position - timeline.StartTime;
            DateTime sampled = timeline.LastUpdatedTime.UtcDateTime;
            if (sampled.Year < 2000 || sampled > DateTime.UtcNow)
                sampled = DateTime.UtcNow;
            return new SpotifyDesktopTrack(media.Title, media.Artist, playing, cover, position, duration) { SampledAt = sampled };
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The desktop player could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    private static async Task<byte[]?> ReadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties media)
    {
        if (media.Thumbnail == null)
            return null;
        using IRandomAccessStreamWithContentType stream = await media.Thumbnail.OpenReadAsync().AsTask().ConfigureAwait(false);
        if (stream.Size == 0 || stream.Size > MaximumCoverBytes)
            return null;
        using DataReader reader = new(stream.GetInputStreamAt(0));
        uint size = (uint)stream.Size;
        await reader.LoadAsync(size).AsTask().ConfigureAwait(false);
        byte[] bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public static async Task SkipAsync(bool next)
    {
        try
        {
            GlobalSystemMediaTransportControlsSession? session = await FindAsync().ConfigureAwait(false);
            if (session != null)
                await (next ? session.TrySkipNextAsync() : session.TrySkipPreviousAsync()).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The desktop player could not skip: " + ex.GetType().Name);
        }
    }

    public static async Task ToggleAsync()
    {
        try
        {
            GlobalSystemMediaTransportControlsSession? session = await FindAsync().ConfigureAwait(false);
            if (session != null)
                await session.TryTogglePlayPauseAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The desktop player could not be toggled: " + ex.GetType().Name);
        }
    }
#endif
}
