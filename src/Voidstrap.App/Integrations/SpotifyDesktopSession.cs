using System.Threading.Tasks;
#if !CROSSPLAT
using System.Linq;
using Windows.Media.Control;
using Windows.Storage.Streams;
#endif

namespace Voidstrap.Integrations;

public sealed record SpotifyDesktopTrack(string Title, string Artist, bool Playing, byte[]? Cover);

public static class SpotifyDesktopSession
{
#if CROSSPLAT
    public static Task<SpotifyDesktopTrack?> ReadAsync(string? knownTitle) => Task.FromResult<SpotifyDesktopTrack?>(null);

    public static Task ToggleAsync() => Task.CompletedTask;
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
            return new SpotifyDesktopTrack(media.Title, media.Artist, playing, cover);
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
