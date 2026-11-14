using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.Integrations;

public static class SpotifyNowPlaying
{
    private const string LOG_IDENT = "SpotifyNowPlaying";

    private const int MaximumCoverBytes = 2 * 1024 * 1024;

    private static readonly HttpClient CoverClient = VpnHttpClient.Create(TimeSpan.FromSeconds(8));

    private static string? _coverUrl;

    private static byte[]? _coverBytes;

    private static bool _fromApp;

    private static bool? _loggedSource;

    public static async Task<SpotifyDesktopTrack?> ReadAsync(string? knownTitle)
    {
        SpotifyDesktopTrack? app = await ReadAppAsync(knownTitle);
        SpotifyDesktopTrack? desktop = await SpotifyDesktopSession.ReadAsync(knownTitle);
        SpotifyDesktopTrack? chosen;
        if (app is { Playing: true })
            chosen = app;
        else if (desktop is { Playing: true })
            chosen = desktop;
        else if (app != null && desktop != null)
            chosen = _fromApp ? app : desktop;
        else
            chosen = app ?? desktop;
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

    public static Task ToggleAsync() =>
        _fromApp ? PressAsync("control-button-playpause") : SpotifyDesktopSession.ToggleAsync();

    public static Task SkipAsync(bool next) =>
        _fromApp ? PressAsync(next ? "control-button-skip-forward" : "control-button-skip-back") : SpotifyDesktopSession.SkipAsync(next);

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

    private static async Task<SpotifyDesktopTrack?> ReadAppAsync(string? knownTitle)
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
        byte[]? cover = state.Title == knownTitle ? null : await ReadCoverAsync(state.Image);
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

    private static async Task<byte[]?> ReadCoverAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (url == _coverUrl)
            return _coverBytes;
        try
        {
            using HttpResponseMessage response = await CoverClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumCoverBytes)
                return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaximumCoverBytes)
                return null;
            _coverUrl = url;
            _coverBytes = bytes;
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The cover could not be downloaded: " + ex.GetType().Name);
            return null;
        }
    }
}
