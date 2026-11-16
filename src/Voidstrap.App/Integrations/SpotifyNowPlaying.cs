using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.Integrations;

public static class SpotifyNowPlaying
{
    private const string LOG_IDENT = "SpotifyNowPlaying";

    private static readonly TimeSpan ReuseFor = TimeSpan.FromMilliseconds(700);

    private static Task<SpotifyDesktopTrack?>? _reading;

    private static SpotifyDesktopTrack? _lastTrack;

    private static DateTime _lastReadAt;

    private static bool _fromApp;

    private static bool? _loggedSource;

    public static async Task<SpotifyDesktopTrack?> ReadAsync(string? knownTitle)
    {
        SpotifyDesktopTrack? track = await ReadSharedAsync();
        return track != null && track.Title == knownTitle ? track with { Cover = null } : track;
    }

    private static Task<SpotifyDesktopTrack?> ReadSharedAsync()
    {
        Task<SpotifyDesktopTrack?>? reading = _reading;
        if (reading is { IsCompleted: false })
            return reading;
        if (DateTime.UtcNow - _lastReadAt < ReuseFor)
            return Task.FromResult(_lastTrack);
        reading = ReadFreshAsync();
        _reading = reading;
        return reading;
    }

    private static async Task<SpotifyDesktopTrack?> ReadFreshAsync()
    {
        SpotifyDesktopTrack? app = await ReadAppAsync();
        SpotifyDesktopTrack? desktop = await SpotifyDesktopSession.ReadAsync(null);
        SpotifyDesktopTrack? chosen;
        if (desktop is { Playing: true })
            chosen = desktop;
        else if (app is { Playing: true })
            chosen = app;
        else if (app != null && desktop != null)
            chosen = _fromApp && app.Title != desktop.Title ? app : desktop;
        else
            chosen = app ?? desktop;
        _lastTrack = chosen;
        _lastReadAt = DateTime.UtcNow;
        if (chosen == null)
            return null;
        _fromApp = ReferenceEquals(chosen, app);
        if (_loggedSource != _fromApp)
        {
            _loggedSource = _fromApp;
            App.Logger.WriteLine(LOG_IDENT, _fromApp ? "Following the Voidstrap Spotify player" : "Following the Spotify desktop app");
        }
        return chosen;
    }

    public static Task ToggleAsync()
    {
        _lastReadAt = DateTime.MinValue;
        return _fromApp ? PressAsync("control-button-playpause") : SpotifyDesktopSession.ToggleAsync();
    }

    public static Task SkipAsync(bool next)
    {
        _lastReadAt = DateTime.MinValue;
        return _fromApp ? PressAsync(next ? "control-button-skip-forward" : "control-button-skip-back") : SpotifyDesktopSession.SkipAsync(next);
    }

    private static async Task PressAsync(string control)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || !SpotifyEngine.Shared.IsRunning)
            return;
        if (dispatcher.CheckAccess())
            await SpotifyEngine.Shared.PressAsync(control);
        else
            await dispatcher.InvokeAsync(() => SpotifyEngine.Shared.PressAsync(control)).Task.Unwrap();
    }

    private static async Task<SpotifyDesktopTrack?> ReadAppAsync()
    {
        SpotifyEngine engine = SpotifyEngine.Shared;
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (!engine.IsRunning || dispatcher == null || dispatcher.HasShutdownStarted)
            return null;
        SpotifyState? state = dispatcher.CheckAccess()
            ? await engine.GetStateAsync()
            : await dispatcher.InvokeAsync(engine.GetStateAsync).Task.Unwrap();
        if (state == null || !state.SignedIn || string.IsNullOrWhiteSpace(state.Title))
            return null;
        TimeSpan position = ParseClock(state.Position);
        TimeSpan duration = ParseClock(state.Duration);
        if (duration < TimeSpan.Zero)
            duration = position - duration;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        byte[]? cover = await SpotifyCovers.ReadAsync(state.Image);
        return new SpotifyDesktopTrack(state.Title, state.Artist, state.Playing, cover, position, duration);
    }

    private static TimeSpan ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TimeSpan.Zero;
        string value = text.Trim();
        bool negative = value.StartsWith('-');
        if (negative)
            value = value[1..];
        double seconds = 0;
        foreach (string part in value.Split(':'))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                return TimeSpan.Zero;
            seconds = seconds * 60 + number;
        }
        return TimeSpan.FromSeconds(negative ? -seconds : seconds);
    }
}
