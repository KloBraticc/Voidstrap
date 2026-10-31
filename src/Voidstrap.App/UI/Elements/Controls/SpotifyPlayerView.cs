using System.IO;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Voidstrap.UI.Elements.Controls;

public sealed class SpotifyPlayerView : ContentControl
{
    private const string LOG_IDENT = "SpotifyPlayerView";

    private const string PlayerUrl = "https://open.spotify.com/";

    private bool _started;

    private WebView2CompositionControl? _view;

    public async Task EnsureStartedAsync()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(Paths.Cache, "SpotifyWebView"),
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required")
            );
            _view = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Black };
            Content = _view;
            await _view.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = _view.CoreWebView2!;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.Navigate(PlayerUrl);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            Shutdown();
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
        WebView2CompositionControl? view = _view;
        _view = null;
        Content = null;
        if (view?.CoreWebView2 is { } core)
            core.NewWindowRequested -= Core_NewWindowRequested;
        view?.Dispose();
    }
}
