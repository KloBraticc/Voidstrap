using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;
using Wpf.Ui.Common;

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

    public event EventHandler? DismissRequested;

    public bool IsPinned { get; private set; }
    public bool IsClosed => _closed;

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
        Grid header = new();
        _drag = new Thumb { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
        FrameworkElementFactory dragVisual = new(typeof(Border));
        dragVisual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        _drag.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = dragVisual };
        _drag.DragDelta += OnDrag;
        _drag.DragCompleted += OnGeometryFinished;
        header.Children.Add(_drag);
        // Laid out like the Voidstrap title bar: small title on the left, caption buttons flush in the corner
        TextBlock caption = new()
        {
            Text = title, Margin = new Thickness(16, 0, 100, 0), VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12, FontWeight = FontWeights.Normal, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        header.Children.Add(caption);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _pin = new CaptionButton(SymbolRegular.Pin24, 14, false, "Pin panel");
        _close = new CaptionButton(SymbolRegular.Dismiss20, 16, true, "Close panel");
        _pin.Click += OnPin;
        _close.Click += OnClosePanel;
        actions.Children.Add(_pin);
        actions.Children.Add(_close);
        header.Children.Add(actions);
        root.Children.Add(header);
        ContentControl host = new() { Content = body, Margin = new Thickness(1, 0, 1, 1) };
        Grid.SetRow(host, 1);
        root.Children.Add(host);
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
        Closed += OnClosed;
        PreviewKeyDown += OnKey;
        _view.Closed += OnViewClosed;
        RobloxWindowTracker.Changed += OnBounds;
        _tracker = RobloxWindowTracker.Acquire();
    }

    private const double CaptionHeight = 30;

    private void UpdatePinVisual()
    {
        // The title bar has no toggled look, so a pinned panel shows its pin in the accent colour
        _pin.SetAccent(IsPinned);
        _pin.ToolTip = IsPinned ? "Unpin panel" : "Pin panel";
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

    public void Present(bool interactive)
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
            if (interactive && IsVisible)
                Activate();
        }
        catch (InvalidOperationException ex)
        {
            App.Logger.WriteLine("SessionPanelWindow", "The panel could not be shown: " + ex.Message);
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
            SetWindowLongPtrW(_handle, GwlExStyle, GetWindowLongPtrW(_handle, GwlExStyle) | WsExLayered | WsExToolWindow);
            _ = SetLayeredWindowAttributes(_handle, 0, 255, 0x2);
            int round = 2;
            _ = DwmSetWindowAttribute(_handle, 33, ref round, sizeof(int));
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
        if (!_requested || (!_interactive && !IsPinned) || !bounds.Valid || !bounds.Foreground)
        {
            HidePanel();
            return;
        }
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double availableWidth = bounds.Width / dpi.DpiScaleX;
            double availableHeight = bounds.Height / dpi.DpiScaleY;
            double width = Math.Clamp(Width, Math.Min(MinWidth, availableWidth), Math.Max(1, availableWidth));
            double height = Math.Clamp(Height, Math.Min(MinHeight, availableHeight), Math.Max(1, availableHeight));
            if (width != Width)
                Width = width;
            if (height != Height)
                Height = height;
            int pixelWidth = (int)Math.Ceiling(width * dpi.DpiScaleX);
            int pixelHeight = (int)Math.Ceiling(height * dpi.DpiScaleY);
            int left = bounds.Left + (int)Math.Round(_x * Math.Max(0, bounds.Width - pixelWidth));
            int top = bounds.Top + (int)Math.Round(_y * Math.Max(0, bounds.Height - pixelHeight));
            bool shown = IsVisible;
            if (shown && _placed && left == _lastLeft && top == _lastTop && pixelWidth == _lastWidth && pixelHeight == _lastHeight)
                return;
            if (!shown)
                Show();
            SetWindowPos(_handle, HwndTopmost, left, top, pixelWidth, pixelHeight, 0x0010);
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
        style = _interactive ? style & ~(WsExNoActivate | WsExTransparent) : style | WsExNoActivate | WsExTransparent;
        SetWindowLongPtrW(_handle, GwlExStyle, style);
    }

    private void OnDrag(object sender, DragDeltaEventArgs e)
    {
        RobloxWindowRect bounds = RobloxWindowTracker.Current;
        if (!bounds.Valid || !double.IsFinite(e.HorizontalChange) || !double.IsFinite(e.VerticalChange))
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _x = Math.Clamp(_x + e.HorizontalChange * dpi.DpiScaleX / Math.Max(1, bounds.Width - Width * dpi.DpiScaleX), 0, 1);
        _y = Math.Clamp(_y + e.VerticalChange * dpi.DpiScaleY / Math.Max(1, bounds.Height - Height * dpi.DpiScaleY), 0, 1);
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

    private void OnClosePanel(object sender, RoutedEventArgs e) => Close();
    private void OnViewClosed(object? sender, EventArgs e) => Close();

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
        SaveLayout();
        SourceInitialized -= OnSourceReady;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        PreviewKeyDown -= OnKey;
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
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Drawn the same way as the Voidstrap title bar's caption buttons: 44 by 30, flush in the corner,
    // a subtle fill that fades in on hover, and the red close hover. Self contained so it never depends
    // on a style being found at runtime.
    private sealed class CaptionButton : Border
    {
        private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(100));
        private readonly Border _hover;
        private readonly Wpf.Ui.Controls.SymbolIcon _icon;
        private readonly bool _close;
        private bool _pressed;

        public event RoutedEventHandler? Click;

        public CaptionButton(SymbolRegular symbol, double size, bool close, string tip)
        {
            _close = close;
            Width = 44;
            Height = CaptionHeight;
            Background = Brushes.Transparent;
            ToolTip = tip;
            Focusable = false;
            SnapsToDevicePixels = true;
            _hover = new Border { Opacity = 0 };
            _hover.SetResourceReference(BackgroundProperty, close ? "RinCaptionCloseBrush" : "SubtleFillColorSecondaryBrush");
            _icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = symbol, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _icon.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            Grid layout = new();
            layout.Children.Add(_hover);
            layout.Children.Add(_icon);
            Child = layout;
            MouseEnter += (_, _) => Refresh();
            MouseLeave += (_, _) => Refresh();
            MouseLeftButtonDown += OnDown;
            MouseLeftButtonUp += OnUp;
            LostMouseCapture += (_, _) => { _pressed = false; Refresh(); };
        }

        public void SetAccent(bool accent)
            => _icon.SetResourceReference(ForegroundProperty, accent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

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
                _icon.SetResourceReference(ForegroundProperty, hovered ? "RinCaptionCloseTextBrush" : "TextFillColorPrimaryBrush");
            _icon.Opacity = _pressed && !_close ? 0.6063 : 1.0;
        }
    }
}
