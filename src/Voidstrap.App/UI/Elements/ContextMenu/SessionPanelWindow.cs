using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;

namespace Voidstrap.UI.Elements.Overlay;

public sealed partial class SessionPanelWindow : Window
{
    private readonly Window _view;
    private readonly string _key;
    private readonly IDisposable _tracker;
    private readonly Thumb _drag;
    private readonly Thumb _resize;
    private readonly CaptionButton _pin;
    private readonly CaptionButton _close;
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExLayered = 0x80000;
    private const nint WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private IntPtr _handle;
    private bool _placed;
    private int _lastLeft;
    private int _lastTop;
    private int _lastWidth;
    private int _lastHeight;
    private double _x = 0.15;
    private double _y = 0.12;
    private bool _interactive;
    private bool _requested;
    private bool _closed;
    private bool _viewLoaded;
    private bool _userClosed;
    private bool _cloaked;
    private const string PinGlyph = "\uE718";
    // The solid pin, clearly different from the outline one even when the accent colour is close to white
    private const string PinnedGlyph = "\uE842";

    public event EventHandler? DismissRequested;

    public bool IsPinned { get; private set; }
    public bool IsClosed => _closed;

    // Asked to be on screen whenever the dock is open, as opposed to only built ahead of time
    public bool IsRequested => _requested && !_closed;

    public SessionPanelWindow(string key, string title, Window view)
    {
        _key = key;
        _view = view;
        Title = title;
        WindowStyle = WindowStyle.None;
        // A transparent WPF window is rendered in software and re-uploaded on every frame, which makes
        // scrolling the server and game lists stutter over the game. On Windows the panel is an opaque
        // window made click-through with a layered style instead, which DWM composites on the GPU.
        AllowsTransparency = !Voidstrap.Utility.Platform.IsWindows;
        if (AllowsTransparency)
            Background = Brushes.Transparent;
        else
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        Foreground = SystemColors.ControlTextBrush;
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = double.IsFinite(view.Width) ? Math.Clamp(view.Width, 380, 1100) : 540;
        Height = double.IsFinite(view.Height) ? Math.Clamp(view.Height, 240, 760) : 460;
        MinWidth = 320;
        MinHeight = 180;
        DataContext = view.DataContext;
        Resources.MergedDictionaries.Add(view.Resources);
        // The view is never shown, so it must not own or be owned by another window
        view.Owner = null;
        DetachTitleBar(view);
        object body = view.Content;
        view.Content = null;
        if (body is Panel contentPanel)
            contentPanel.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        // A view with a TitleScrollHost scrolls up underneath a frosted title bar instead of stopping at it
        ScrollViewer? underTitle = view.FindName("TitleScrollHost") as ScrollViewer;
        Grid header = new() { ClipToBounds = true };
        _drag = new Thumb { Cursor = Cursors.Arrow, Background = Brushes.Transparent };
        FrameworkElementFactory dragVisual = new(typeof(Border));
        dragVisual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        _drag.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = dragVisual };
        _drag.DragDelta += OnDrag;
        _drag.DragCompleted += OnGeometryFinished;
        header.Children.Add(_drag);
        // Laid out like the Voidstrap title bar: small title on the left, caption buttons flush in the corner
        StackPanel caption = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 100, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        if (TitleGlyph(key) is { } glyph)
        {
            TextBlock icon = new() { Text = glyph, FontFamily = TitleIconFont, FontSize = 13, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            caption.Children.Add(icon);
        }
        TextBlock captionText = new() { Text = title, FontSize = 12, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        captionText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        caption.Children.Add(captionText);
        header.Children.Add(caption);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _pin = new CaptionButton(PinGlyph, 13, false, "Pin panel");
        _close = new CaptionButton("\uE8BB", 10, true, "Close panel");
        _pin.Click += OnPin;
        _close.Click += OnClosePanel;
        actions.Children.Add(_pin);
        actions.Children.Add(_close);
        header.Children.Add(actions);
        ContentControl host = new() { Content = body, Margin = new Thickness(1, 0, 1, 1) };
        if (underTitle != null)
        {
            // The view starts under the title bar, its scrolled content is pushed down by the bar's height
            Grid.SetRowSpan(host, 2);
            if (underTitle.Content is FrameworkElement scrolled)
                scrolled.Margin = new Thickness(scrolled.Margin.Left, scrolled.Margin.Top + CaptionHeight, scrolled.Margin.Right, scrolled.Margin.Bottom);
            header.Children.Insert(0, CreateFrostedBackdrop(host, header));
        }
        else
        {
            Grid.SetRow(host, 1);
        }
        root.Children.Add(host);
        // Added after the content so it draws on top of anything scrolled underneath it
        root.Children.Add(header);
        // An invisible grip in the corner, the default thumb drew a grey square there
        _resize = new Thumb { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE };
        FrameworkElementFactory gripVisual = new(typeof(Border));
        gripVisual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        _resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = gripVisual };
        _resize.DragDelta += OnResize;
        _resize.DragCompleted += OnGeometryFinished;
        Grid.SetRow(_resize, 1);
        root.Children.Add(_resize);
        Border frame = new() { CornerRadius = new CornerRadius(AllowsTransparency ? 8 : 0), BorderThickness = new Thickness(1), Child = root };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        Content = frame;
        RestoreLayout();
        SourceInitialized += OnSourceReady;
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closed += OnClosed;
        PreviewKeyDown += OnKey;
        _view.Closed += OnViewClosed;
        // Close or Cancel buttons inside a hosted view close the panel, IsCancel alone only works for dialogs
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHostedClick), true);
        RobloxWindowTracker.Changed += OnBounds;
        _tracker = RobloxWindowTracker.Acquire();
    }

    private const double CaptionHeight = 30;
    // The dock's strip along the bottom of the game (its gap from the edge, the pill and a little room above it).
    // Panels stop above it, so they can never be moved or sized over the dock.
    private const double DockReserve = 104;

    private static int UsableHeight(RobloxWindowRect bounds, DpiScale dpi)
        => Math.Max(1, bounds.Height - (int)Math.Round(DockReserve * dpi.DpiScaleY));
    private const double FrostBlur = 18;
    private static readonly System.Windows.Media.FontFamily TitleIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static string? TitleGlyph(string key) => key switch
    {
        "chat" => "\uE8BD",
        "browser" => "\uE753",
        "games" => "\uE7FC",
        "history" => "\uE81C",
        "music" => "\uE8D6",
        "notifications" => "\uE713",
        "server" => "\uE946",
        _ => null
    };

    // The frosted title bar: a blurred live copy of whatever is scrolled underneath, under a translucent tint.
    // The copy is drawn a little larger than the bar so the blur does not darken its edges.
    private static UIElement CreateFrostedBackdrop(FrameworkElement source, Grid header)
    {
        VisualBrush copy = new(source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        Rectangle blurred = new()
        {
            Fill = copy,
            Margin = new Thickness(-FrostBlur),
            Effect = new BlurEffect { Radius = FrostBlur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            IsHitTestVisible = false
        };
        Rectangle tint = new() { Opacity = 0.62, IsHitTestVisible = false };
        tint.SetResourceReference(Shape.FillProperty, "SolidBackgroundFillColorBaseBrush");
        Border line = new() { BorderThickness = new Thickness(0, 0, 0, 1), IsHitTestVisible = false, Opacity = 0.6 };
        line.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        void Track()
        {
            double width = Math.Max(1, header.ActualWidth + FrostBlur * 2);
            double height = CaptionHeight + FrostBlur * 2;
            copy.Viewbox = new Rect(-FrostBlur, -FrostBlur, width, height);
            copy.Viewport = new Rect(0, 0, width, height);
        }
        header.SizeChanged += (_, _) => Track();
        Track();
        Grid layers = new() { IsHitTestVisible = false };
        layers.Children.Add(blurred);
        layers.Children.Add(tint);
        layers.Children.Add(line);
        return layers;
    }

    private void UpdatePinVisual()
    {
        // A pinned panel shows a filled pin in the accent colour
        _pin.SetGlyph(IsPinned ? PinnedGlyph : PinGlyph);
        _pin.SetAccent(IsPinned);
        _pin.ToolTip = IsPinned ? "Unpin, closes with the dock" : "Pin, opens here with the dock every time";
    }

    private static void DetachTitleBar(Window view)
    {
        // A collapsed WPF-UI title bar still loads, hooks the window procedure of whatever window hosts it
        // and subscribes to theme changes, so it is taken out of the tree instead of being hidden
        if (view.FindName("RootTitleBar") is not FrameworkElement title)
            return;
        if (title.Parent is Panel parent)
            parent.Children.Remove(title);
        else if (title.Parent is Decorator decorator && ReferenceEquals(decorator.Child, title))
            decorator.Child = null;
        else if (title.Parent is ContentControl control && ReferenceEquals(control.Content, title))
            control.Content = null;
        else
            title.Visibility = Visibility.Collapsed;
    }

    public void Present(bool interactive, bool activate = true)
    {
        if (_closed)
            return;
        _requested = true;
        _interactive = interactive;
        try
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            UpdateInputStyle();
            ApplyBounds(RobloxWindowTracker.Current);
            if (activate && interactive && IsVisible)
                Activate();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionPanelWindow", "The panel could not be shown: " + ex.Message);
        }
    }

    // Builds the native window ahead of time without showing it, so the first open is instant
    public void Prepare()
    {
        if (_closed)
            return;
        try
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            UpdateInputStyle();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionPanelWindow", "The panel could not be prepared: " + ex.Message);
        }
    }

    public void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        if (_closed)
            return;
        UpdateInputStyle();
        ApplyBounds(RobloxWindowTracker.Current);
    }

    public void HideForSession()
    {
        _requested = false;
        HidePanel();
    }

    private void OnSourceReady(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        OverlayDiagnostics.RegisterOverlayHandle(_handle);
        if (!AllowsTransparency && _handle != IntPtr.Zero)
        {
            // A plain window the graphics card draws directly. It used to be a layered window so it could be
            // clicked through while pinned over the game, but that made Windows copy every frame back from the
            // graphics card, which is what made scrolling and everything in the panels lag. Panels now hide
            // whenever the dock does, so they never need to be clicked through.
            SetWindowLongPtrW(_handle, GwlExStyle, (GetWindowLongPtrW(_handle, GwlExStyle) | WsExToolWindow) & ~(WsExLayered | WsExTransparent));
            int round = 2;
            _ = DwmSetWindowAttribute(_handle, 33, ref round, sizeof(int));
        }
        // No Windows fade and zoom when it appears or hides, it shows the instant it is asked for
        if (_handle != IntPtr.Zero && Voidstrap.Utility.Platform.IsWindows)
        {
            int disabled = 1;
            _ = DwmSetWindowAttribute(_handle, 3, ref disabled, sizeof(int));
        }
        UpdateInputStyle();
    }

    private void HidePanel()
    {
        _placed = false;
        if (_closed || !IsVisible)
            return;
        try
        {
            Hide();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SetCloak(bool cloak)
    {
        if (_handle == IntPtr.Zero)
            return;
        int value = cloak ? 1 : 0;
        if (DwmSetWindowAttribute(_handle, 13, ref value, sizeof(int)) == 0)
            _cloaked = cloak;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        if (_cloaked && !_closed)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => SetCloak(false)));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewLoaded)
            return;
        _viewLoaded = true;
        _view.RaiseEvent(new RoutedEventArgs(LoadedEvent));
    }

    private void OnBounds(object? sender, RobloxWindowRect bounds)
    {
        if (_closed || Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnBounds(sender, bounds)));
            return;
        }
        ApplyBounds(bounds);
    }

    private void ApplyBounds(RobloxWindowRect bounds)
    {
        if (_handle == IntPtr.Zero || _closed)
            return;
        // Panels only show while the dock is open, pinned ones included
        if (!_requested || !_interactive || !bounds.Valid || !bounds.Foreground)
        {
            HidePanel();
            return;
        }
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double availableWidth = bounds.Width / dpi.DpiScaleX;
            int usableHeight = UsableHeight(bounds, dpi);
            double availableHeight = usableHeight / dpi.DpiScaleY;
            double width = Math.Clamp(Width, Math.Min(MinWidth, availableWidth), Math.Max(1, availableWidth));
            double height = Math.Clamp(Height, Math.Min(MinHeight, availableHeight), Math.Max(1, availableHeight));
            if (width != Width)
                Width = width;
            if (height != Height)
                Height = height;
            int pixelWidth = (int)Math.Ceiling(width * dpi.DpiScaleX);
            int pixelHeight = (int)Math.Ceiling(height * dpi.DpiScaleY);
            int left = bounds.Left + (int)Math.Round(_x * Math.Max(0, bounds.Width - pixelWidth));
            int top = bounds.Top + (int)Math.Round(_y * Math.Max(0, usableHeight - pixelHeight));
            bool shown = IsVisible;
            if (shown && _placed && left == _lastLeft && top == _lastTop && pixelWidth == _lastWidth && pixelHeight == _lastHeight)
                return;
            // Moved into place while still hidden, so it never flashes up somewhere else first
            SetWindowPos(_handle, HwndTopmost, left, top, pixelWidth, pixelHeight, 0x0010);
            if (!shown)
            {
                // The very first show is cloaked until WPF has drawn it, otherwise it appears blank for a frame
                if (!_viewLoaded && !_cloaked && Voidstrap.Utility.Platform.IsWindows)
                    SetCloak(true);
                Show();
            }
            _placed = true;
            _lastLeft = left;
            _lastTop = top;
            _lastWidth = pixelWidth;
            _lastHeight = pixelHeight;
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionPanelWindow", "The panel could not be placed: " + ex.Message);
        }
    }

    private void UpdateInputStyle()
    {
        if (_handle == IntPtr.Zero || !Voidstrap.Utility.Platform.IsWindows)
            return;
        nint style = GetWindowLongPtrW(_handle, GwlExStyle) | WsExToolWindow;
        style = _interactive ? style & ~WsExNoActivate : style | WsExNoActivate;
        style &= ~(WsExLayered | WsExTransparent);
        SetWindowLongPtrW(_handle, GwlExStyle, style);
    }

    private void OnDrag(object sender, DragDeltaEventArgs e)
    {
        RobloxWindowRect bounds = RobloxWindowTracker.Current;
        if (!bounds.Valid || !double.IsFinite(e.HorizontalChange) || !double.IsFinite(e.VerticalChange))
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _x = Math.Clamp(_x + e.HorizontalChange * dpi.DpiScaleX / Math.Max(1, bounds.Width - Width * dpi.DpiScaleX), 0, 1);
        _y = Math.Clamp(_y + e.VerticalChange * dpi.DpiScaleY / Math.Max(1, UsableHeight(bounds, dpi) - Height * dpi.DpiScaleY), 0, 1);
        ApplyBounds(bounds);
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        if (!double.IsFinite(e.HorizontalChange) || !double.IsFinite(e.VerticalChange))
            return;
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
        ApplyBounds(RobloxWindowTracker.Current);
    }

    private void OnGeometryFinished(object sender, DragCompletedEventArgs e) => SaveLayout();

    private void OnPin(object sender, RoutedEventArgs e)
    {
        IsPinned = !IsPinned;
        UpdatePinVisual();
        SaveLayout();
    }

    // Closing a panel yourself also unpins it, so it does not come back next time
    private void CloseByUser()
    {
        if (_closed)
            return;
        _userClosed = true;
        Close();
    }

    private void OnClosePanel(object sender, RoutedEventArgs e) => CloseByUser();

    public void CloseFromDock() => CloseByUser();

    private void OnHostedClick(object sender, RoutedEventArgs e)
    {
        if (_closed || e.OriginalSource is not Button { IsCancel: true })
            return;
        Dispatcher.BeginInvoke(new Action(CloseByUser));
    }

    private void OnViewClosed(object? sender, EventArgs e) => CloseByUser();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DismissRequested?.Invoke(this, EventArgs.Empty);
            if (_closed)
                return;
            SetInteractive(false);
            IntPtr game = RobloxWindowTracker.Current.Hwnd;
            if (game != IntPtr.Zero)
                SetForegroundWindow(game);
        }
    }

    private void RestoreLayout()
    {
        if (App.State.Prop.SessionPanels?.TryGetValue(_key, out SessionPanelLayout? saved) != true || saved == null)
            return;
        if (double.IsFinite(saved.X)) _x = Math.Clamp(saved.X, 0, 1);
        if (double.IsFinite(saved.Y)) _y = Math.Clamp(saved.Y, 0, 1);
        if (double.IsFinite(saved.Width) && saved.Width >= MinWidth) Width = Math.Min(saved.Width, 1600);
        if (double.IsFinite(saved.Height) && saved.Height >= MinHeight) Height = Math.Min(saved.Height, 1200);
        IsPinned = saved.Pinned;
        UpdatePinVisual();
    }

    private void SaveLayout()
    {
        try
        {
            App.State.Prop.SessionPanels ??= new();
            App.State.Prop.SessionPanels[_key] = new SessionPanelLayout { X = _x, Y = _y, Width = Width, Height = Height, Pinned = IsPinned };
            App.State.SaveDeferred();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionPanelWindow", "The panel layout could not be saved: " + ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        if (_userClosed)
            IsPinned = false;
        SaveLayout();
        SourceInitialized -= OnSourceReady;
        Loaded -= OnLoaded;
        ContentRendered -= OnContentRendered;
        Closed -= OnClosed;
        PreviewKeyDown -= OnKey;
        RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHostedClick));
        _drag.DragDelta -= OnDrag;
        _drag.DragCompleted -= OnGeometryFinished;
        _resize.DragDelta -= OnResize;
        _resize.DragCompleted -= OnGeometryFinished;
        _pin.Click -= OnPin;
        _close.Click -= OnClosePanel;
        _view.Closed -= OnViewClosed;
        RobloxWindowTracker.Changed -= OnBounds;
        _tracker.Dispose();
        OverlayDiagnostics.UnregisterOverlayHandle(_handle);
        DismissRequested = null;
        Content = null;
        DataContext = null;
        try
        {
            _view.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(IntPtr hwnd, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Drawn the same way as the Voidstrap title bar's caption buttons: 44 by 30, flush in the corner,
    // a subtle fill that fades in on hover, and the red close hover. Self contained so it never depends
    // on a style being found at runtime.
    private sealed class CaptionButton : Border
    {
        private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(100));
        private readonly Border _hover;
        private readonly Border _toggled;
        private readonly TextBlock _icon;
        private readonly bool _close;
        private bool _pressed;
        private bool _accent;

        public event RoutedEventHandler? Click;

        public CaptionButton(string glyph, double size, bool close, string tip)
        {
            _close = close;
            Width = 44;
            Height = CaptionHeight;
            Background = Brushes.Transparent;
            ToolTip = tip;
            Focusable = false;
            SnapsToDevicePixels = true;
            _hover = new Border { Opacity = 0 };
            // A tinted chip that stays behind a toggled button, the pin while the panel is pinned
            _toggled = new Border { Opacity = 0, Margin = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(5) };
            _toggled.SetResourceReference(BackgroundProperty, "SystemAccentColorPrimaryBrush");
            _hover.SetResourceReference(BackgroundProperty, close ? "RinCaptionCloseBrush" : "SubtleFillColorSecondaryBrush");
            // Windows' own icon font, the pin is missing from the bundled one and fell back to the colour emoji
            _icon = new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };
            _icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            Grid layout = new();
            layout.Children.Add(_toggled);
            layout.Children.Add(_hover);
            layout.Children.Add(_icon);
            Child = layout;
            MouseEnter += (_, _) => Refresh();
            MouseLeave += (_, _) => Refresh();
            MouseLeftButtonDown += OnDown;
            MouseLeftButtonUp += OnUp;
            LostMouseCapture += (_, _) => { _pressed = false; Refresh(); };
        }

        private static readonly System.Windows.Media.FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public void SetGlyph(string glyph) => _icon.Text = glyph;

        public void SetAccent(bool accent)
        {
            // Voidstrap's own accent colour on the icon, plus the tinted chip and the solid glyph, so pinned is
            // obvious whatever the accent colour is
            _accent = accent;
            _toggled.Opacity = accent ? 0.28 : 0;
            _icon.SetResourceReference(TextBlock.ForegroundProperty, accent ? "SystemAccentColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            _icon.FontWeight = accent ? FontWeights.Bold : FontWeights.Normal;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = true;
            CaptureMouse();
            Refresh();
            e.Handled = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            bool click = _pressed && IsMouseOver;
            _pressed = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            Refresh();
            e.Handled = true;
            if (click)
                Click?.Invoke(this, new RoutedEventArgs());
        }

        private void Refresh()
        {
            bool hovered = IsMouseOver;
            double target = _pressed && hovered ? 0.8 : hovered ? 1.0 : 0.0;
            _hover.BeginAnimation(OpacityProperty, new DoubleAnimation(target, Fade) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            if (_close)
                _icon.SetResourceReference(TextBlock.ForegroundProperty, hovered ? "RinCaptionCloseTextBrush" : "TextFillColorPrimaryBrush");
            else if (_accent)
                _icon.SetResourceReference(TextBlock.ForegroundProperty, "SystemAccentColorPrimaryBrush");
            _icon.Opacity = _pressed && !_close ? 0.6063 : 1.0;
        }
    }
}
