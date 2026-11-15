#if CROSSPLAT
using Tmds.DBus;
#else
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
    private const string LOG_IDENT = "SpotifyDesktopSession";

#if CROSSPLAT
    private const string MprisPrefix = "org.mpris.MediaPlayer2.";

    private static readonly ObjectPath MprisPath = new("/org/mpris/MediaPlayer2");

    private static readonly string[] SpotifyClients = ["ncspot", "psst", "spot", "spotube", "librespot"];

    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MacDeniedPause = TimeSpan.FromSeconds(30);

    private static readonly string[] MacScript =
    [
        "if application \"Spotify\" is running then",
        "tell application \"Spotify\"",
        "set s to player state as string",
        "if s is \"stopped\" then return s",
        "return s & linefeed & (name of current track) & linefeed & (artist of current track) & linefeed & (player position as string) & linefeed & (duration of current track as string) & linefeed & (artwork url of current track)",
        "end tell",
        "end if",
    ];

    private static readonly SemaphoreSlim BusGate = new(1, 1);

    private static Connection? _bus;

    private static string? _player;

    private static bool _busFailureLogged;

    private static DateTime _macPausedUntil;

    private static bool _macDeniedLogged;

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    public static async Task<SpotifyDesktopTrack?> ReadAsync(string? knownTitle)
    {
        SpotifyDesktopTrack? track = null;
        try
        {
            if (Voidstrap.Utility.Platform.IsMacOS)
                track = await ReadMacAsync().ConfigureAwait(false);
            else if (Voidstrap.Utility.Platform.UsesLinuxHost)
                track = await ReadLinuxAsync().ConfigureAwait(false);
        }
        catch (DBusException ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The desktop player could not be read: " + ex.ErrorName);
        }
        catch (Exception ex)
        {
            if (Voidstrap.Utility.Platform.UsesLinuxHost)
                ResetBus(ex);
            else
                App.Logger.WriteLine(LOG_IDENT, "The desktop player could not be read: " + ex.GetType().Name);
        }
        return track != null && track.Title == knownTitle ? track with { Cover = null } : track;
    }

    public static Task ToggleAsync() => ControlAsync(static player => player.PlayPauseAsync(), "playpause");

    public static Task SkipAsync(bool next) =>
        next ? ControlAsync(static player => player.NextAsync(), "next track") : ControlAsync(static player => player.PreviousAsync(), "previous track");

    private static async Task ControlAsync(Func<IMprisPlayer, Task> linux, string macCommand)
    {
        try
        {
            if (Voidstrap.Utility.Platform.IsMacOS)
            {
                if (IsMacSpotifyRunning())
                    await RunAsync("osascript", "-e", "tell application \"Spotify\" to " + macCommand).ConfigureAwait(false);
                return;
            }
            string? player = _player;
            Connection? bus = Voidstrap.Utility.Platform.UsesLinuxHost && player != null ? await GetBusAsync().ConfigureAwait(false) : null;
            if (bus != null && player != null)
                await WithTimeout(linux(bus.CreateProxy<IMprisPlayer>(player, MprisPath))).ConfigureAwait(false);
        }
        catch (DBusException ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The desktop player did not take the command: " + ex.ErrorName);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The desktop player could not be controlled: " + ex.GetType().Name);
        }
    }

    private static bool IsMacSpotifyRunning()
    {
        Process[] processes = Process.GetProcessesByName("Spotify");
        foreach (Process process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    private static async Task<SpotifyDesktopTrack?> ReadMacAsync()
    {
        if (DateTime.UtcNow < _macPausedUntil || !IsMacSpotifyRunning())
            return null;
        string[] args = new string[MacScript.Length * 2];
        for (int i = 0; i < MacScript.Length; i++)
        {
            args[i * 2] = "-e";
            args[i * 2 + 1] = MacScript[i];
        }
        ProcessResult? result = await RunAsync("osascript", args).ConfigureAwait(false);
        if (result == null)
            return null;
        if (result.ExitCode != 0)
        {
            if (result.Error.Contains("-1743", StringComparison.Ordinal) || result.Error.Contains("Not authorized", StringComparison.OrdinalIgnoreCase))
            {
                _macPausedUntil = DateTime.UtcNow + MacDeniedPause;
                if (!_macDeniedLogged)
                {
                    _macDeniedLogged = true;
                    App.Logger.WriteLine(LOG_IDENT, "macOS blocked access to Spotify, allow Voidstrap under System Settings, Privacy and Security, Automation");
                }
            }
            return null;
        }
        string[] lines = result.Output.TrimEnd().Split('\n');
        if (lines.Length < 3 || string.IsNullOrWhiteSpace(lines[1]))
            return null;
        TimeSpan position = lines.Length > 3 && TryParseNumber(lines[3], out double seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        TimeSpan duration = lines.Length > 4 && TryParseNumber(lines[4], out double millis) ? TimeSpan.FromMilliseconds(millis) : TimeSpan.Zero;
        byte[]? cover = lines.Length > 5 ? await SpotifyCovers.ReadAsync(lines[5].Trim()).ConfigureAwait(false) : null;
        return new SpotifyDesktopTrack(lines[1].Trim(), lines[2].Trim(), lines[0].Trim() == "playing", cover, position, duration);
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static async Task<Connection?> GetBusAsync()
    {
        Connection? bus = _bus;
        if (bus != null)
            return bus;
        string? address = Voidstrap.Utility.LinuxSessionBus.Address;
        if (string.IsNullOrEmpty(address))
            return null;
        await BusGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_bus != null)
                return _bus;
            Connection connection = Voidstrap.Utility.LinuxSessionBus.Open(address);
            try
            {
                await WithTimeout(connection.ConnectAsync()).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            _bus = connection;
            _busFailureLogged = false;
            return connection;
        }
        finally
        {
            BusGate.Release();
        }
    }

    private static void ResetBus(Exception ex)
    {
        Connection? bus = Interlocked.Exchange(ref _bus, null);
        bus?.Dispose();
        if (_busFailureLogged)
            return;
        _busFailureLogged = true;
        App.Logger.WriteLine(LOG_IDENT, "The session bus connection was lost: " + ex.GetType().Name);
    }

    private static async Task WithTimeout(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(BusTimeout)).ConfigureAwait(false) != task)
            throw new TimeoutException();
        await task.ConfigureAwait(false);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(BusTimeout)).ConfigureAwait(false) != task)
            throw new TimeoutException();
        return await task.ConfigureAwait(false);
    }

    private static async Task<SpotifyDesktopTrack?> ReadLinuxAsync()
    {
        Connection? bus = await GetBusAsync().ConfigureAwait(false);
        if (bus == null)
            return null;
        string[] services = await WithTimeout(bus.ListServicesAsync()).ConfigureAwait(false);
        (string Name, MprisTrack Track)? playing = null;
        (string Name, MprisTrack Track)? followed = null;
        (string Name, MprisTrack Track)? first = null;
        foreach (string name in services)
        {
            if (!name.StartsWith(MprisPrefix, StringComparison.Ordinal))
                continue;
            MprisTrack? track = await ReadPlayerAsync(bus, name).ConfigureAwait(false);
            if (track == null || (!IsSpotifyClient(name) && !track.FromSpotifyWeb))
                continue;
            first ??= (name, track);
            if (name == _player)
                followed = (name, track);
            if (track.Playing)
            {
                playing = (name, track);
                break;
            }
        }
        (string Name, MprisTrack Track)? chosen = playing ?? followed ?? first;
        if (chosen == null)
            return null;
        if (chosen.Value.Name != _player)
        {
            _player = chosen.Value.Name;
            App.Logger.WriteLine(LOG_IDENT, "Following the desktop player " + chosen.Value.Name[MprisPrefix.Length..]);
        }
        MprisTrack picked = chosen.Value.Track;
        byte[]? cover = await SpotifyCovers.ReadAsync(picked.ArtUrl).ConfigureAwait(false);
        return new SpotifyDesktopTrack(picked.Title, picked.Artist, picked.Playing, cover, picked.Position, picked.Duration);
    }

    private static bool IsSpotifyClient(string name)
    {
        string client = name[MprisPrefix.Length..];
        int dot = client.IndexOf('.');
        if (dot >= 0)
            client = client[..dot];
        return client.Contains("spotify", StringComparison.OrdinalIgnoreCase)
            || SpotifyClients.Contains(client, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record MprisTrack(string Title, string Artist, bool Playing, string? ArtUrl, bool FromSpotifyWeb, TimeSpan Position, TimeSpan Duration);

    private static async Task<MprisTrack?> ReadPlayerAsync(Connection bus, string name)
    {
        IDictionary<string, object> properties;
        try
        {
            properties = await WithTimeout(bus.CreateProxy<IMprisPlayer>(name, MprisPath).GetAllAsync()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException)
        {
            return null;
        }
        if (!properties.TryGetValue("Metadata", out object? value) || value is not IDictionary<string, object> metadata)
            return null;
        string title = metadata.TryGetValue("xesam:title", out object? rawTitle) ? rawTitle as string ?? string.Empty : string.Empty;
        if (title.Length == 0)
            return null;
        string artist = string.Empty;
        if (metadata.TryGetValue("xesam:artist", out object? rawArtist))
        {
            artist = rawArtist switch
            {
                string[] names => string.Join(", ", names.Where(static item => !string.IsNullOrEmpty(item))),
                string single => single,
                _ => string.Empty,
            };
        }
        string? artUrl = metadata.TryGetValue("mpris:artUrl", out object? rawArt) ? rawArt as string : null;
        string? url = metadata.TryGetValue("xesam:url", out object? rawUrl) ? rawUrl as string : null;
        bool fromWeb =
            (url != null && url.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase))
            || (artUrl != null && artUrl.Contains("scdn.co", StringComparison.OrdinalIgnoreCase));
        bool playing = properties.TryGetValue("PlaybackStatus", out object? status) && status as string == "Playing";
        TimeSpan duration = metadata.TryGetValue("mpris:length", out object? length) ? Microseconds(length) : TimeSpan.Zero;
        TimeSpan position = properties.TryGetValue("Position", out object? at) ? Microseconds(at) : TimeSpan.Zero;
        return new MprisTrack(title, artist, playing, artUrl, fromWeb, position, duration);
    }

    private static TimeSpan Microseconds(object? value)
    {
        long micros = value switch
        {
            long signed => signed,
            ulong unsigned => unsigned > long.MaxValue ? 0 : (long)unsigned,
            int small => small,
            uint smallUnsigned => smallUnsigned,
            double real => (long)real,
            _ => 0,
        };
        return micros > 0 && micros < TimeSpan.MaxValue.Ticks / 10 ? TimeSpan.FromTicks(micros * 10) : TimeSpan.Zero;
    }

    private static async Task<ProcessResult?> RunAsync(string file, params string[] args)
    {
        ProcessStartInfo info = new(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        using Process? process = Process.Start(info);
        if (process == null)
            return null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            return null;
        }
    }
#else
    private const uint MaximumCoverBytes = 2 * 1024 * 1024;

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    private static string? _coverKey;

    private static byte[]? _coverBytes;

    private static async Task<GlobalSystemMediaTransportControlsSession?> FindAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
        GlobalSystemMediaTransportControlsSession[] sessions = [.. _manager.GetSessions().Where(static session => session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))];
        return sessions.FirstOrDefault(static session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? sessions.FirstOrDefault();
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
        string key = media.Title + "\n" + media.Artist;
        if (key == _coverKey)
            return _coverBytes;
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
        _coverBytes = bytes;
        _coverKey = key;
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
#if CROSSPLAT

[DBusInterface("org.mpris.MediaPlayer2.Player")]
public interface IMprisPlayer : IDBusObject
{
    Task PlayPauseAsync();

    Task NextAsync();

    Task PreviousAsync();

    Task<IDictionary<string, object>> GetAllAsync();
}
#endif
