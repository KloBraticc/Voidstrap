using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Overlays;

internal sealed partial class MusicIslandOverlayWindow : Window
{
    private const string ResourceKey = "MusicIslandOverlayWindow";

    private const double EdgeMargin = 14;

    private const double DragRoom = 28;

    private const double DragReach = 22;

    private const double DragThreshold = 3;

    private const double ControlsWidth = 84;

    private const double WindowWidth = 620;

    private const double WindowHeight = 37 + DragRoom * 2;

    private static readonly double[] MeterEnvelope = [0.5, 1, 0.7, 0.4, 0.6, 0.35];

    private static MusicIslandOverlayWindow? _active;

    private readonly bool _interactive;

    private readonly RobloxOverlayAnchor _anchor;

    private readonly DispatcherTimer _poll;

    private readonly Border _island;

    private readonly ScaleTransform _scale = new(0.85, 0.85);

    private readonly TranslateTransform _pull = new();

    private readonly Image _cover;

    private readonly TextBlock _title;

    private readonly TextBlock _artist;

    private readonly StackPanel _meterPanel;

    private readonly Border _controls;

    private readonly TextBlock _toggleGlyph;

    private readonly Storyboard _meter = new();

    private string? _trackTitle;

    private bool _playing;

    private bool _polling;

    private bool _disposed;

    private bool _shown;

    private bool _expanded;

    private Point? _pressAt;

    private bool _dragging;

    private static bool WantsInteractive => App.Settings.Prop.MusicIslandInteractive && Voidstrap.Utility.Platform.IsWindows;

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
            if (active._interactive == WantsInteractive)
            {
                active.ApplyPlacement();
                active._anchor.Refresh();
                return;
            }
            CloseAll();
        }
        _active = new MusicIslandOverlayWindow(WantsInteractive);
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

    private MusicIslandOverlayWindow(bool interactive)
    {
        _interactive = interactive;
        Title = "Voidstrap Music";
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Width = WindowWidth;
        Height = WindowHeight;
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
            Clip = new RectangleGeometry(new Rect(0, 0, 22, 22), 6, 6),
        };
        coverFrame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        _title = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _artist = new TextBlock { FontSize = 12, MaxWidth = 140, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _artist.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _meterPanel = new StackPanel { Orientation = Orientation.Horizontal, Height = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SolidColorBrush green = new(Color.FromRgb(0x32, 0xD7, 0x4B));
        green.Freeze();
        for (int i = 0; i < MeterEnvelope.Length; i++)
        {
            Rectangle bar = new()
            {
                Width = 2,
                Height = 14,
                RadiusX = 1,
                RadiusY = 1,
                Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0),
                Fill = green,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, MeterEnvelope[i] * 0.5),
            };
            _meterPanel.Children.Add(bar);
            DoubleAnimation bounce = new(MeterEnvelope[i] * 0.4, MeterEnvelope[i], TimeSpan.FromMilliseconds(290 + i * 37 % 180))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(bounce, bar);
            Storyboard.SetTargetProperty(bounce, new PropertyPath("RenderTransform.ScaleY"));
            _meter.Children.Add(bounce);
        }

        _toggleGlyph = CreateGlyph("\uE769");
        StackPanel controlRow = new() { Orientation = Orientation.Horizontal, Width = ControlsWidth, HorizontalAlignment = HorizontalAlignment.Left };
        controlRow.Children.Add(CreateControl("previous", CreateGlyph("\uE892")));
        controlRow.Children.Add(CreateControl("toggle", _toggleGlyph));
        controlRow.Children.Add(CreateControl("next", CreateGlyph("\uE893")));
        _controls = new Border { Width = 0, Opacity = 0, ClipToBounds = true, Margin = new Thickness(6, 0, 0, 0), Child = controlRow, VerticalAlignment = VerticalAlignment.Center };

        StackPanel row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(coverFrame);
        row.Children.Add(_title);
        row.Children.Add(_artist);
        row.Children.Add(_meterPanel);
        row.Children.Add(_controls);

        TransformGroup transforms = new();
        transforms.Children.Add(_scale);
        transforms.Children.Add(_pull);
        _island = new Border
        {
            CornerRadius = new CornerRadius(18.5),
            Height = 37,
            Padding = new Thickness(8, 0, 14, 0),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(DragRoom),
            Child = row,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transforms,
            IsHitTestVisible = interactive,
        };
        _island.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _island.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        if (interactive)
        {
            _island.Cursor = Cursors.Hand;
            _island.MouseEnter += OnIslandMouseEnter;
            _island.MouseLeave += OnIslandMouseLeave;
            _island.PreviewMouseLeftButtonDown += OnIslandMouseDown;
            _island.PreviewMouseMove += OnIslandMouseMove;
            _island.PreviewMouseLeftButtonUp += OnIslandMouseUp;
            _island.LostMouseCapture += OnIslandLostCapture;
        }
        Content = new Grid { Children = { _island } };
        ApplyPlacement();

        _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center, customPosition: ComputePosition);
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += OnPollTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static TextBlock CreateGlyph(string glyph)
    {
        TextBlock text = new()
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return text;
    }

    private Border CreateControl(string action, TextBlock glyph)
    {
        Border button = new()
        {
            Tag = action,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            Child = glyph,
        };
        button.MouseEnter += OnControlMouseEnter;
        button.MouseLeave += OnControlMouseLeave;
        button.MouseLeftButtonUp += OnControlMouseUp;
        return button;
    }

    private void ApplyPlacement()
    {
        MusicIslandPosition position = App.Settings.Prop.MusicIslandPosition;
        _island.HorizontalAlignment = position switch
        {
            MusicIslandPosition.BottomLeft or MusicIslandPosition.TopLeft => HorizontalAlignment.Left,
            MusicIslandPosition.BottomCenter or MusicIslandPosition.TopCenter => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Right,
        };
        _island.VerticalAlignment = IsTop(position) ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    }

    private static bool IsTop(MusicIslandPosition position) =>
        position is MusicIslandPosition.TopLeft or MusicIslandPosition.TopCenter or MusicIslandPosition.TopRight;

    public static (int Left, int Top) ComputePosition(RobloxWindowRect rect, double scaleX, double scaleY, int width, int height)
    {
        int insetX = (int)Math.Round((EdgeMargin - DragRoom) * scaleX);
        int insetY = (int)Math.Round((EdgeMargin - DragRoom) * scaleY);
        MusicIslandPosition position = App.Settings.Prop.MusicIslandPosition;
        int left = position switch
        {
            MusicIslandPosition.BottomLeft or MusicIslandPosition.TopLeft => rect.Left + insetX,
            MusicIslandPosition.BottomCenter or MusicIslandPosition.TopCenter => rect.Left + (rect.Width - width) / 2,
            _ => rect.Left + rect.Width - width - insetX,
        };
        int top = IsTop(position) ? rect.Top + insetY : rect.Top + rect.Height - height - insetY;
        return (left, top);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowStyle();
        _poll.Start();
        OnPollTick(null, EventArgs.Empty);
    }

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
        if (track == null || dockShowing || (!track.Playing && !_interactive))
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
        SetPlaying(track.Playing);
        SetShown(true);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        _toggleGlyph.Text = playing ? "\uE769" : "\uE768";
        if (!_shown)
            return;
        if (playing)
            _meter.Resume(this);
        else
            _meter.Pause(this);
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
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        if (shown)
        {
            _meter.Begin(this, true);
            if (!_playing)
                _meter.Pause(this);
        }
        else
        {
            _meter.Stop(this);
            SetExpanded(false);
        }
    }

    private static void Spring(Animatable target, DependencyProperty property, double to, int milliseconds = 420) =>
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new SpringEase() });

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;
        _expanded = expanded;
        _controls.BeginAnimation(WidthProperty, new DoubleAnimation(expanded ? ControlsWidth : 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SpringEase() });
        _controls.BeginAnimation(OpacityProperty, new DoubleAnimation(expanded ? 1 : 0, TimeSpan.FromMilliseconds(expanded ? 240 : 140)));
        _meterPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(expanded ? 0.35 : 1, TimeSpan.FromMilliseconds(200)));
    }

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        if (_shown)
            SetExpanded(true);
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            SetExpanded(false);
    }

    private static double RubberBand(double offset, double reach) =>
        Math.Sign(offset) * reach * (1 - 1 / (Math.Abs(offset) * 0.55 / reach + 1));

    private void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(this);
        _dragging = false;
        Spring(_scale, ScaleTransform.ScaleXProperty, 0.97, 220);
        Spring(_scale, ScaleTransform.ScaleYProperty, 0.97, 220);
    }

    private void OnIslandMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not Point start || e.LeftButton != MouseButtonState.Pressed)
            return;
        Vector delta = e.GetPosition(this) - start;
        if (!_dragging)
        {
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                return;
            _dragging = true;
            _island.CaptureMouse();
        }
        _pull.BeginAnimation(TranslateTransform.XProperty, null);
        _pull.BeginAnimation(TranslateTransform.YProperty, null);
        _pull.X = RubberBand(delta.X, DragReach);
        _pull.Y = RubberBand(delta.Y, DragReach);
        double stretch = 1 + Math.Min(0.06, (Math.Abs(_pull.X) + Math.Abs(_pull.Y)) / 400);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = stretch;
        _scale.ScaleY = stretch;
        e.Handled = true;
    }

    private void OnIslandMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool dragged = _dragging;
        Release();
        if (dragged)
            e.Handled = true;
    }

    private void OnIslandLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging)
            Release();
    }

    private void Release()
    {
        bool dragging = _dragging;
        _pressAt = null;
        _dragging = false;
        if (dragging && _island.IsMouseCaptured)
            _island.ReleaseMouseCapture();
        Spring(_pull, TranslateTransform.XProperty, 0);
        Spring(_pull, TranslateTransform.YProperty, 0);
        Spring(_scale, ScaleTransform.ScaleXProperty, 1);
        Spring(_scale, ScaleTransform.ScaleYProperty, 1);
        if (dragging && !_island.IsMouseOver)
            SetExpanded(false);
    }

    private void OnControlMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button)
            button.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
    }

    private void OnControlMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button)
            button.Background = Brushes.Transparent;
    }

    private async void OnControlMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_disposed || _dragging || sender is not FrameworkElement { Tag: string action })
            return;
        e.Handled = true;
        Task work = action switch
        {
            "previous" => SpotifyDesktopSession.SkipAsync(false),
            "next" => SpotifyDesktopSession.SkipAsync(true),
            _ => SpotifyDesktopSession.ToggleAsync(),
        };
        if (action == "toggle")
            SetPlaying(!_playing);
        await work;
        await Task.Delay(350);
        if (!_disposed)
            OnPollTick(null, EventArgs.Empty);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _poll.Stop();
        _poll.Tick -= OnPollTick;
        _meter.Stop(this);
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        if (_interactive)
        {
            _island.MouseEnter -= OnIslandMouseEnter;
            _island.MouseLeave -= OnIslandMouseLeave;
            _island.PreviewMouseLeftButtonDown -= OnIslandMouseDown;
            _island.PreviewMouseMove -= OnIslandMouseMove;
            _island.PreviewMouseLeftButtonUp -= OnIslandMouseUp;
            _island.LostMouseCapture -= OnIslandLostCapture;
        }
        if (_controls.Child is Panel controlRow)
        {
            foreach (Border button in controlRow.Children.OfType<Border>())
            {
                button.MouseEnter -= OnControlMouseEnter;
                button.MouseLeave -= OnControlMouseLeave;
                button.MouseLeftButtonUp -= OnControlMouseUp;
            }
        }
        _anchor.Dispose();
        if (ReferenceEquals(_active, this))
            _active = null;
    }

    private void ApplyWindowStyle()
    {

        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            LinuxOverlaySurface.MakeClickThrough(this);
            return;
        }
        nint hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLongW(hwnd, -20) | 0x80 | 0x08000000;
        if (!_interactive)
            style |= 0x20;
        _ = SetWindowLongW(hwnd, -20, style);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);
}
