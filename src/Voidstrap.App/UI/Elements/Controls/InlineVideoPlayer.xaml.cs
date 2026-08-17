using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Voidstrap.Core;

namespace Voidstrap.UI.Elements.Controls;

public partial class InlineVideoPlayer : UserControl, IDisposable
{
    private const string LogIdent = "InlineVideoPlayer";

    private static readonly string BrowserArguments = string.Join(" ",
        "--autoplay-policy=no-user-gesture-required",
        "--process-per-site",
        "--disable-features=TranslateUI,msWebOOUI,msPdfOOUI,ElasticOverscroll,OverscrollHistoryNavigation,BackForwardCache",
        "--disable-pinch",
        "--overscroll-history-navigation=0");

    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    private CoreWebView2Environment? _environment;
    private WebView2? _webView;
    private VideoEmbed? _embed;
    private bool _starting;
    private bool _disposed;

    public InlineVideoPlayer()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty VideoUrlProperty = DependencyProperty.Register(
        nameof(VideoUrl),
        typeof(string),
        typeof(InlineVideoPlayer),
        new PropertyMetadata(string.Empty));

    public string VideoUrl
    {
        get => (string)GetValue(VideoUrlProperty);
        set => SetValue(VideoUrlProperty, value);
    }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        _embed = VideoEmbed.TryParse(VideoUrl, out VideoEmbed? parsed) ? parsed : null;
        if (_embed is null)
        {
            ShowFallback();
            return;
        }
        _ = StartAsync();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        Teardown();
    }

    private void FallbackLink_Click(object sender, MouseButtonEventArgs e)
    {
        OpenInBrowser();
    }

    private async Task StartAsync()
    {
        if (_disposed || _starting || _webView is not null)
            return;
        VideoEmbed? embed = _embed;
        if (embed is null || !IsRuntimePresent())
        {
            ShowFallback();
            return;
        }

        _starting = true;
        WebView2? view = null;
        try
        {
            CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions(Voidstrap.Utility.RenderAcceleration.ApplyToBrowserArguments(BrowserArguments));
            _environment = await CoreWebView2Environment.CreateAsync(null, GetUserDataFolder(), options).ConfigureAwait(true);
            if (_disposed || _lifetime.IsCancellationRequested)
                return;

            view = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
            };
            PlayerHost.Children.Add(view);

            await view.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
            if (_disposed || _lifetime.IsCancellationRequested)
            {
                Discard(view);
                return;
            }

            CoreWebView2 core = view.CoreWebView2;
            Harden(core);

            core.AddWebResourceRequestedFilter(VideoEmbed.VirtualOrigin + "/*", CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnWebResourceRequested;
            core.Navigate(VideoEmbed.VirtualOrigin + "/");

            _webView = view;
            view = null;
            FallbackLink.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            Discard(view);
        }
        catch (Exception ex)
        {
            Discard(view);
            App.Logger?.WriteLine(LogIdent, "Inline playback failed: " + ex.Message);
            ShowFallback();
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        VideoEmbed? embed = _embed;
        CoreWebView2Environment? environment = _environment;
        if (embed is null || environment is null)
            return;
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(embed.BuildPlayerHtml(VideoEmbed.VirtualOrigin));
            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(payload),
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not serve the player page: " + ex.Message);
        }
    }

    private void Harden(CoreWebView2 core)
    {
        try
        {
            CoreWebView2Settings settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not apply player settings: " + ex.Message);
        }
        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not lower the player memory target: " + ex.Message);
        }
        try
        {
            core.NewWindowRequested += OnNewWindowRequested;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not hook window requests: " + ex.Message);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Launch(e.Uri);
    }

    private void OpenInBrowser()
    {
        if (_embed is not null)
            Launch(_embed.WatchUrl);
    }

    private static void Launch(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not open " + url + ": " + ex.Message);
        }
    }

    private void ShowFallback()
    {
        if (FallbackLink is not null)
            FallbackLink.Visibility = Visibility.Visible;
    }

    private void Teardown()
    {
        WebView2? view = _webView;
        _webView = null;
        Discard(view);
        _environment = null;
    }

    private void Discard(WebView2? view)
    {
        if (view is null)
            return;
        try
        {
            CoreWebView2? core = view.CoreWebView2;
            if (core is not null)
            {
                core.WebResourceRequested -= OnWebResourceRequested;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.Navigate("about:blank");
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not blank the player: " + ex.Message);
        }
        try
        {
            PlayerHost?.Children.Remove(view);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not detach the player: " + ex.Message);
        }
        try
        {
            view.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not dispose the player: " + ex.Message);
        }
    }

    private static string GetUserDataFolder()
    {
        string folder = string.Empty;
        try
        {
            folder = Paths.WebViewData;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not read the player data path: " + ex.Message);
        }
        if (string.IsNullOrWhiteSpace(folder))
            folder = Path.Combine(Path.GetTempPath(), "Voidstrap", "WebView2");
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not create the player data folder: " + ex.Message);
        }
        return folder;
    }

    private static bool IsRuntimePresent()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "WebView2 runtime probe failed: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Could not cancel the player lifetime: " + ex.Message);
        }
        Teardown();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
