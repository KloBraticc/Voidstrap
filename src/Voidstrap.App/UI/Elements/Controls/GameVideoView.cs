using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Controls;

public sealed class GameVideoView : ContentControl
{
    private const string LOG_IDENT = "GameVideoView";

    private const int AbortedResult = unchecked((int)0x80004004);

    private const string VirtualHost = "game-video.voidstrap.invalid";

    public static readonly DependencyProperty VideoPathProperty = DependencyProperty.Register(
        nameof(VideoPath),
        typeof(string),
        typeof(GameVideoView),
        new PropertyMetadata(null, OnVideoPathChanged)
    );

    private WebView2CompositionControl? _view;

    private bool _starting;

    private DispatcherTimer? _frameTimer;

    private HomepageBackgroundMedia? _media;

    private WriteableBitmap? _bitmap;

    private Image? _image;

    private long _frameVersion;

    private DateTime _portableStarted;

    private static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(20);

    private string? _playingPath;

    public GameVideoView()
    {
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
        IsVisibleChanged += Control_IsVisibleChanged;
    }

    public string? VideoPath
    {
        get => (string?)GetValue(VideoPathProperty);
        set => SetValue(VideoPathProperty, value);
    }

    private static void OnVideoPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((GameVideoView)sender).Refresh();
    }

    private void Control_Loaded(object sender, RoutedEventArgs e) => Refresh();

    private void Control_Unloaded(object sender, RoutedEventArgs e) => Teardown();

    private void Control_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (!IsLoaded)
            return;
        if (!IsVisible || string.IsNullOrEmpty(VideoPath))
            Teardown();
        else if (Voidstrap.Utility.Platform.UsesPortableUi)
            StartPortable(VideoPath);
        else
            _ = StartAsync(VideoPath);
    }

    private void StartPortable(string path)
    {
        if (_media != null && _playingPath == path)
            return;
        Teardown();
        _playingPath = path;
        _frameVersion = 0;
        _portableStarted = DateTime.UtcNow;
        _image = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        _media = new HomepageBackgroundMedia(path, 30d, 960, 540);
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 30d) };
        _frameTimer.Tick += FrameTimer_Tick;
        _frameTimer.Start();
    }

    private void FrameTimer_Tick(object? sender, EventArgs e)
    {
        if (_media == null || _image == null)
            return;
        try
        {
            _media.TryReadFrame(_frameVersion, DrawFrame);
            if (_frameVersion == 0 && DateTime.UtcNow - _portableStarted > FirstFrameLimit)
            {
                App.Logger.WriteLine(LOG_IDENT, "The game video produced no frames, showing the screenshot instead");
                Teardown();
                return;
            }
            if (_media.Failure != null)
            {
                App.Logger.WriteLine(LOG_IDENT, "The game video could not be decoded: " + _media.Failure);
                Teardown();
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            Teardown();
        }
    }

    private void DrawFrame(byte[] pixels, int width, int height, long version)
    {
        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96d, 96d, PixelFormats.Bgra32, null);
            _image!.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, checked(width * 4), 0);
        _frameVersion = version;
    }

    private async Task StartAsync(string path)
    {
        if (_starting || (_view != null && _playingPath == path))
            return;
        _starting = true;
        Teardown();
        WebView2CompositionControl? view = null;
        try
        {
            string folder = Path.Combine(Paths.Cache, "GameVideoWebView");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                null,
                folder,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required")
            );
            view = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Black };
            Content = view;
            await view.EnsureCoreWebView2Async(environment);
            if (!IsVisible || VideoPath != path)
            {
                Teardown();
                return;
            }
            CoreWebView2 core = view.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.DenyCors);
            string page = Path.GetFileNameWithoutExtension(path) + ".html";
            string source = Uri.EscapeDataString(Path.GetFileName(path));
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetDirectoryName(path)!, page),
                "<!doctype html><html><body style=\"margin:0;background:#000;overflow:hidden\">"
                    + $"<video src=\"{source}\" autoplay muted loop playsinline style=\"width:100vw;height:100vh;object-fit:cover;pointer-events:none\"></video>"
                    + "</body></html>"
            );
            core.Navigate($"https://{VirtualHost}/{page}");
            _view = view;
            _playingPath = path;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == AbortedResult)
        {
            view?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            view?.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            Content = null;
            view?.Dispose();
        }
        finally
        {
            _starting = false;
        }
        if (VideoPath != path || (_view != null && !IsVisible))
            Refresh();
    }

    private void Teardown()
    {
        if (_frameTimer != null)
        {
            _frameTimer.Stop();
            _frameTimer.Tick -= FrameTimer_Tick;
            _frameTimer = null;
        }
        _media?.Dispose();
        _media = null;
        _playingPath = null;
        _bitmap = null;
        _image = null;
        WebView2CompositionControl? view = _view ?? Content as WebView2CompositionControl;
        _view = null;
        Content = null;
        view?.Dispose();
    }
}
