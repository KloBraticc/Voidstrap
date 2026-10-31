using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Voidstrap.UI.Elements.Controls;

public sealed class SpotifyPlayerView : ContentControl
{
    private const string LOG_IDENT = "SpotifyPlayerView";

    private const string PlayerUrl = "https://open.spotify.com/";

    private const string BrowserArguments =
        "--autoplay-policy=no-user-gesture-required --process-per-site --disk-cache-size=52428800 --media-cache-size=10485760 --renderer-process-limit=1 "
        + "--disable-background-networking --disable-extensions --disable-sync --disable-translate --disable-features=msWebOOUI,msPdfOOUI,TranslateUI,AutofillServerCommunication,OptimizationHints,MediaRouter";

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);

    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private DateTime _silentSince;

    private const int AbortedResult = unchecked((int)0x80004004);

    public event EventHandler? Failed;

    private bool _shown;

    private bool _started;

    private WebView2CompositionControl? _view;

    public void SetShown(bool shown)
    {
        _shown = shown;
        if (_view?.CoreWebView2 is { } core)
            core.MemoryUsageTargetLevel = shown ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
        _silentSince = DateTime.UtcNow;
        if (shown)
        {
            _idleTimer.Stop();
            _idleTimer.Tick -= IdleTimer_Tick;
        }
        else if (_started)
        {
            _idleTimer.Tick -= IdleTimer_Tick;
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();
        }
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        if (_shown || _view?.CoreWebView2 is not { } core)
            return;
        if (core.IsDocumentPlayingAudio)
        {
            _silentSince = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - _silentSince >= IdleLimit)
            Shutdown();
    }

    public async Task EnsureStartedAsync()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(Paths.UserData, "SpotifyWebView"),
                new CoreWebView2EnvironmentOptions(BrowserArguments)
            );
            WebView2CompositionControl view = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
            _view = view;
            Content = view;
            await view.EnsureCoreWebView2Async(environment);
            if (!ReferenceEquals(_view, view))
                return;
            CoreWebView2 core = view.CoreWebView2!;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.Navigate(PlayerUrl);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is System.Runtime.InteropServices.COMException com && com.HResult == AbortedResult))
        {
            Shutdown();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            Shutdown();
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (sender is CoreWebView2 core && Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            core.Navigate(e.Uri);
    }

    public void Shutdown()
    {
        _started = false;
        _idleTimer.Stop();
        _idleTimer.Tick -= IdleTimer_Tick;
        WebView2CompositionControl? view = _view;
        _view = null;
        Content = null;
        if (view?.CoreWebView2 is { } core)
            core.NewWindowRequested -= Core_NewWindowRequested;
        view?.Dispose();
    }
}
