using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly Wpf.Ui.Controls.Button _pin;
    private readonly Wpf.Ui.Controls.Button _close;
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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid header = new() { Margin = new Thickness(8, 2, 8, 0) };
        _drag = new Thumb { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
        FrameworkElementFactory dragVisual = new(typeof(Border));
        dragVisual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        _drag.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = dragVisual };
        _drag.DragDelta += OnDrag;
        _drag.DragCompleted += OnGeometryFinished;
        header.Children.Add(_drag);
        header.Children.Add(new TextBlock
        {
            Text = title, Margin = new Thickness(8, 0, 88, 0), VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false
        });
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _pin = new Wpf.Ui.Controls.Button { Icon = SymbolRegular.Pin24, Appearance = ControlAppearance.Transparent, Width = 34, Height = 32, Padding = new Thickness(0), ToolTip = "Pin panel", Focusable = false };
        _close = new Wpf.Ui.Controls.Button { Icon = SymbolRegular.Dismiss24, Appearance = ControlAppearance.Transparent, Width = 34, Height = 32, Padding = new Thickness(0), ToolTip = "Close panel", Focusable = false };
        _pin.Click += OnPin;
        _close.Click += OnClosePanel;
        actions.Children.Add(_pin);
        actions.Children.Add(_close);
        header.Children.Add(actions);
        root.Children.Add(header);
        ContentControl host = new() { Content = body, Margin = new Thickness(1, 0, 1, 1) };
        Grid.SetRow(host, 1);
        root.Children.Add(host);
        _resize = new Thumb { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Opacity = 0.35 };
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
        _pin.Appearance = IsPinned ? ControlAppearance.Primary : ControlAppearance.Transparent;
        _pin.ToolTip = IsPinned ? "Unpin panel" : "Pin panel";
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
        _pin.Appearance = IsPinned ? ControlAppearance.Primary : ControlAppearance.Transparent;
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
}
