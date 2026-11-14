using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Overlays;

internal sealed partial class MusicIslandOverlayWindow : Window
{
    private const string ResourceKey = "MusicIslandOverlayWindow";

    private const double EdgeMargin = 14;

    private static readonly double[] MeterEnvelope = [0.5, 1, 0.7, 0.4, 0.6, 0.35];

    private static MusicIslandOverlayWindow? _active;

    private readonly RobloxOverlayAnchor _anchor;

    private readonly DispatcherTimer _poll;

    private readonly Border _island;

    private readonly Image _cover;

    private readonly TextBlock _title;

    private readonly TextBlock _artist;

    private readonly ScaleTransform[] _bars = new ScaleTransform[6];

    private readonly Storyboard _meter = new();

    private string? _trackTitle;

    private bool _polling;

    private bool _disposed;

    private bool _shown;

    public static void Reconcile()
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted)
            return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(new Action(Reconcile));
            return;
        }
        if (!App.Settings.Prop.MusicIslandOverlay)
        {
            CloseAll();
            return;
        }
        if (_active is { _disposed: false } active)
        {
            active._anchor.Refresh();
            return;
        }
        _active = new MusicIslandOverlayWindow();
        application.Resources[ResourceKey] = _active;
        _active.Show();
    }

    public static void CloseAll()
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted)
            return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(new Action(CloseAll));
            return;
        }
        MusicIslandOverlayWindow? active = _active;
        _active = null;
        application.Resources.Remove(ResourceKey);
        if (active == null)
            return;
        try
        {
            active.Close();
        }
        catch (InvalidOperationException) { }
    }

    private MusicIslandOverlayWindow()
    {
        Title = "Voidstrap Music";
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        LinuxOverlaySurface.ReleaseMainWindowClaim(this);

        _cover = new Image { Width = 22, Height = 22, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);
        Border coverFrame = new()
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 9, 0),
            Child = _cover,
        };
        coverFrame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        coverFrame.Clip = new RectangleGeometry(new Rect(0, 0, 22, 22), 6, 6);

        _title = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _artist = new TextBlock { FontSize = 12, MaxWidth = 140, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _artist.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        StackPanel meter = new() { Orientation = Orientation.Horizontal, Height = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SolidColorBrush green = new(Color.FromRgb(0x32, 0xD7, 0x4B));
        green.Freeze();
        for (int i = 0; i < _bars.Length; i++)
        {
            ScaleTransform scale = new(1, MeterEnvelope[i] * 0.5);
            _bars[i] = scale;
            meter.Children.Add(new Rectangle
            {
                Width = 2,
                Height = 14,
                RadiusX = 1,
                RadiusY = 1,
                Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0),
                Fill = green,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            });
            DoubleAnimation bounce = new(MeterEnvelope[i] * 0.4, MeterEnvelope[i], TimeSpan.FromMilliseconds(290 + i * 37 % 180))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(bounce, meter.Children[i]);
            Storyboard.SetTargetProperty(bounce, new PropertyPath("RenderTransform.ScaleY"));
            _meter.Children.Add(bounce);
        }

        StackPanel row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(coverFrame);
        row.Children.Add(_title);
        row.Children.Add(_artist);
        row.Children.Add(meter);

        _island = new Border
        {
            CornerRadius = new CornerRadius(19),
            Padding = new Thickness(8, 7, 14, 7),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6),
            Child = row,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.85, 0.85),
            IsHitTestVisible = false,
        };
        _island.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _island.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        Content = _island;

        _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center, customPosition: ComputePosition);
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += OnPollTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
    }

    public static (int Left, int Top) ComputePosition(RobloxWindowRect rect, double scaleX, double scaleY, int width, int height)
    {
        int marginX = (int)Math.Round(EdgeMargin * scaleX);
        int marginY = (int)Math.Round(EdgeMargin * scaleY);
        MusicIslandPosition position = App.Settings.Prop.MusicIslandPosition;
        int left = position switch
        {
            MusicIslandPosition.BottomLeft or MusicIslandPosition.TopLeft => rect.Left + marginX,
            MusicIslandPosition.BottomCenter or MusicIslandPosition.TopCenter => rect.Left + Math.Max(0, (rect.Width - width) / 2),
            _ => rect.Left + Math.Max(0, rect.Width - width - marginX),
        };
        int top = position is MusicIslandPosition.TopLeft or MusicIslandPosition.TopCenter or MusicIslandPosition.TopRight
            ? rect.Top + marginY
            : rect.Top + Math.Max(0, rect.Height - height - marginY);
        return (left, top);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MakeClickThrough();
        _poll.Start();
        OnPollTick(null, EventArgs.Empty);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => _anchor.Refresh();

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_polling || _disposed)
            return;
        _polling = true;
        SpotifyDesktopTrack? track;
        try
        {
            track = await SpotifyDesktopSession.ReadAsync(_trackTitle);
        }
        finally
        {
            _polling = false;
        }
        if (_disposed)
            return;
        bool dockShowing = Voidstrap.UI.Elements.Overlay.SessionDock.ActiveInstance?.IsOpen == true;
        if (track == null || !track.Playing || dockShowing)
        {
            SetShown(false);
            return;
        }
        if (track.Title != _trackTitle)
            _cover.Source = null;
        _trackTitle = track.Title;
        _title.Text = track.Title;
        _artist.Text = track.Artist;
        _artist.Visibility = track.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (track.Cover != null)
            _cover.Source = Voidstrap.UI.Elements.Overlay.SessionDock.LoadNowPlayingCover(track.Cover);
        SetShown(true);
    }

    private void SetShown(bool shown)
    {
        if (_shown == shown)
            return;
        _shown = shown;
        TimeSpan duration = TimeSpan.FromMilliseconds(shown ? 420 : 200);
        IEasingFunction ease = shown ? new SpringEase() : new CubicEase { EasingMode = EasingMode.EaseIn };
        double scale = shown ? 1 : 0.85;
        _island.BeginAnimation(OpacityProperty, new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 220 : 180)));
        if (_island.RenderTransform is ScaleTransform transform)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        }
        if (shown)
            _meter.Begin(this, true);
        else
            _meter.Stop(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _poll.Stop();
        _poll.Tick -= OnPollTick;
        _meter.Stop(this);
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        SizeChanged -= OnSizeChanged;
        _anchor.Dispose();
        if (ReferenceEquals(_active, this))
            _active = null;
    }

    private void MakeClickThrough()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            LinuxOverlaySurface.MakeClickThrough(this);
            return;
        }
        nint hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLongW(hwnd, -20);
        _ = SetWindowLongW(hwnd, -20, style | 0x20 | 0x80 | 0x08000000);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);
}
