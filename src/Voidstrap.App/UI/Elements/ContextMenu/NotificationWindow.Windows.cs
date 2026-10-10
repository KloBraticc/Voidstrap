using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voidstrap.Extensions;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Overlay;

public partial class NotificationWindow
{
    private IDisposable? _notificationTracker;
    private bool _presenting;
    private bool _dismissRequested;
    private IntPtr _notificationHandle;
    private bool _cornerLayout;
    private bool _hovered;
    private bool _dismissHot;
    private bool _buttonWasDown;
    private bool _hoverLogged;
    private bool _hoverSeen;
    private System.Windows.Threading.DispatcherTimer? _hoverTimer;

    private async Task PresentWindowsNotificationAsync(NotificationItem item)
    {
        if (_notificationTracker == null)
        {
            _notificationTracker = RobloxWindowTracker.Acquire();
            RobloxWindowTracker.Changed += OnNotificationBoundsChanged;
            DismissButton.MouseLeftButtonDown += OnDismissNotification;
            MouseMove += OnNotificationMouse;
            MouseLeave += OnNotificationMouse;
            _hoverTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            _hoverTimer.Tick += OnHoverTick;
        }

        RobloxWindowRect bounds = RobloxWindowTracker.Current;
        if (OverlayHub.InGame && (!bounds.Valid || !bounds.Foreground))
            return;

        using CancellationTokenSource presentation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.SafeToken(), item.Token);
        CancellationToken token = presentation.Token;
        try
        {
            _dismissRequested = false;
            _presenting = true;
            SetImage(item.Image);
            SetText(item.Text, item.Flag);
            NotificationBorder.BeginAnimation(OpacityProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            UseCornerLayout();
            NotificationBorder.Opacity = 0;
            RootTranslate.X = 0;
            ProgressScale.ScaleX = 1;
            DismissButton.Visibility = Visibility.Visible;
            _hovered = false;
            _dismissHot = false;
            _hoverLogged = false;
            _hoverSeen = false;
            _buttonWasDown = IsLeftButtonDown();
            DismissButton.Background = Brushes.Transparent;
            SetDismissVisible(false, false);
            Show();
            _notificationHandle = new WindowInteropHelper(this).Handle;
            OverlayDiagnostics.RegisterOverlayHandle(_notificationHandle);
            UpdateLayout();
            double slide = Math.Max(1, ActualHeight);
            RootTranslate.Y = slide;
            PlaceWindowsNotification(bounds);

            // Starts below the bottom edge and slides up into the corner
            BeginCachedAnimation();
            AnimateNotification(0, 1, slide, 0, IntroMs, new CubicEase { EasingMode = EasingMode.EaseOut });
            await Task.Delay(IntroMs, token);
            EndCachedAnimation();
            _hoverTimer?.Start();
            double duration = double.IsFinite(item.Duration) ? Math.Clamp(item.Duration, 0.5, 60) : 8;
            double remaining = duration;
            Stopwatch clock = Stopwatch.StartNew();
            double previous = clock.Elapsed.TotalSeconds;
            while (!_dismissRequested && remaining > 0)
            {
                await Task.Delay(100, token);
                double now = clock.Elapsed.TotalSeconds;
                if (!_hovered)
                    remaining -= now - previous;
                previous = now;
            }
            // Fades out while easing back down below the edge
            _hoverTimer?.Stop();
            DismissButton.BeginAnimation(OpacityProperty, null);
            DismissButton.Opacity = 0;
            DismissButton.IsHitTestVisible = false;
            BeginCachedAnimation();
            AnimateNotification(NotificationBorder.Opacity, 0, RootTranslate.Y, Math.Max(1, ActualHeight), OutroMs, new SineEase { EasingMode = EasingMode.EaseInOut });
            await Task.Delay(OutroMs, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _presenting = false;
            _hoverTimer?.Stop();
            if (!_closed)
            {
                EndCachedAnimation();
                Hide();
                NotificationBorder.BeginAnimation(OpacityProperty, null);
                RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                NotificationBorder.Opacity = 0;
                SetImage(null);
                NotificationTitle.Text = string.Empty;
                NotificationText.Inlines.Clear();
            }
        }
    }

    private void UseCornerLayout()
    {
        if (_cornerLayout)
            return;
        _cornerLayout = true;
        // Flush with the bottom and right edges: no outer margin, square corners where it touches the edges,
        // and the height follows the content so there is no empty strip under the card
        NotificationRoot.Margin = new Thickness(0);
        NotificationRoot.ClipToBounds = true;
        NotificationBorder.CornerRadius = new CornerRadius(8, 0, 0, 0);
        NotificationBorder.BorderThickness = new Thickness(1, 1, 0, 0);
        SizeToContent = SizeToContent.Height;
    }

    private const int IntroMs = 260;

    private void AnimateNotification(double fromOpacity, double toOpacity, double fromY, double toY, int milliseconds, IEasingFunction ease)
    {
        NotificationBorder.BeginAnimation(OpacityProperty, null);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            NotificationBorder.Opacity = toOpacity;
            RootTranslate.Y = toY;
            return;
        }
        Duration duration = new(TimeSpan.FromMilliseconds(milliseconds));
        DoubleAnimation fade = new(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        DoubleAnimation slide = new(fromY, toY, duration) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fade, AnimationFrameRate);
        Timeline.SetDesiredFrameRate(slide, AnimationFrameRate);
        fade.Freeze();
        slide.Freeze();
        NotificationBorder.BeginAnimation(OpacityProperty, fade);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // While the card only moves and fades, it is rendered once into a bitmap at the screen's scale
    // and that bitmap is composited each frame instead of laying out and drawing the text again
    private void BeginCachedAnimation()
    {
        if (NotificationBorder.CacheMode is BitmapCache)
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        NotificationBorder.CacheMode = new BitmapCache(Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)) { SnapsToDevicePixels = true };
    }

    private void EndCachedAnimation() => NotificationBorder.CacheMode = null;

    // Roblox can keep the mouse to itself, so hover and the close click are read from the real cursor
    // position while a notification is up instead of relying only on mouse messages reaching this window
    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_closed || !_presenting || _dismissRequested || !IsVisible)
            return;
        IntPtr handle = _notificationHandle;
        if (handle == IntPtr.Zero || !GetCursorPos(out NativePoint cursor) || !GetWindowRect(handle, out NativeRect window))
            return;
        bool insideRect = cursor.X >= window.Left && cursor.X < window.Right && cursor.Y >= window.Top && cursor.Y < window.Bottom;
        // The window is exactly the size of the card, so being inside its rectangle is hovering the card
        bool hovered = IsMouseOver || insideRect;
        if (!_hoverLogged)
        {
            _hoverLogged = true;
            CursorInfo info = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
            bool showing = GetCursorInfo(ref info) && (info.Flags & 1) != 0;
            IntPtr under = GetAncestor(WindowFromPoint(cursor), 2);
            App.Logger.WriteLine("NotificationWindow", $"Hover tracking started, notification at {window.Left},{window.Top} to {window.Right},{window.Bottom}, cursor at {cursor.X},{cursor.Y}, cursor {(showing ? "visible" : "hidden by the game")}, window under cursor is {(under == handle ? "the notification" : "another window")}");
        }
        if (hovered != _hovered)
        {
            _hovered = hovered;
            SetDismissVisible(hovered, true);
            if (hovered && !_hoverSeen)
            {
                _hoverSeen = true;
                App.Logger.WriteLine("NotificationWindow", "Pointer is over the notification, showing the close button");
            }
        }
        bool hot = hovered && DismissContains(window, cursor);
        if (hot != _dismissHot)
        {
            _dismissHot = hot;
            if (hot)
                DismissButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            else
                DismissButton.Background = Brushes.Transparent;
        }
        bool down = IsLeftButtonDown();
        if (hot && down && !_buttonWasDown)
            _dismissRequested = true;
        _buttonWasDown = down;
    }

    // The close button's bounds are taken relative to this window and offset by the native window rectangle,
    // so they line up with the cursor whatever the DPI or how the window was moved
    private bool DismissContains(NativeRect window, NativePoint cursor)
    {
        if (DismissButton.ActualWidth <= 0 || DismissButton.ActualHeight <= 0)
            return false;
        try
        {
            Rect bounds = DismissButton.TransformToAncestor(this).TransformBounds(new Rect(0, 0, DismissButton.ActualWidth, DismissButton.ActualHeight));
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            // A few pixels of slack make the small icon easy to hit
            double slack = 4;
            double left = window.Left + (bounds.Left - slack) * dpi.DpiScaleX;
            double top = window.Top + (bounds.Top - slack) * dpi.DpiScaleY;
            double right = window.Left + (bounds.Right + slack) * dpi.DpiScaleX;
            double bottom = window.Top + (bounds.Bottom + slack) * dpi.DpiScaleY;
            return cursor.X >= left && cursor.X < right && cursor.Y >= top && cursor.Y < bottom;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnNotificationMouse(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_closed && _presenting)
            OnHoverTick(null, EventArgs.Empty);
    }

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    private void SetDismissVisible(bool visible, bool animate)
    {
        DismissButton.IsHitTestVisible = visible;
        DismissButton.BeginAnimation(OpacityProperty, null);
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            DismissButton.Opacity = visible ? 1 : 0;
            return;
        }
        DoubleAnimation fade = new(visible ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(140)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(fade, AnimationFrameRate);
        DismissButton.BeginAnimation(OpacityProperty, fade);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public NativePoint Position;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(NativePoint point);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    private void OnDismissNotification(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dismissRequested = true;
        e.Handled = true;
    }

    private void OnNotificationBoundsChanged(object? sender, RobloxWindowRect bounds)
    {
        if (_closed || !_presenting || Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnNotificationBoundsChanged(sender, bounds)));
            return;
        }
        if (!bounds.Valid || !bounds.Foreground)
        {
            _dismissRequested = true;
            Hide();
            return;
        }
        PlaceWindowsNotification(bounds);
    }

    private void PlaceWindowsNotification(RobloxWindowRect bounds)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int right;
        int bottom;
        int areaLeft;
        int areaTop;
        if (bounds.Valid)
        {
            // Bottom right corner of the game's client area, touching both edges
            areaLeft = bounds.Left;
            areaTop = bounds.Top;
            right = bounds.Left + bounds.Width;
            bottom = bounds.Top + bounds.Height;
        }
        else
        {
            Rect work = SystemParameters.WorkArea;
            areaLeft = (int)Math.Round(work.Left * dpi.DpiScaleX);
            areaTop = (int)Math.Round(work.Top * dpi.DpiScaleY);
            right = (int)Math.Round(work.Right * dpi.DpiScaleX);
            bottom = (int)Math.Round(work.Bottom * dpi.DpiScaleY);
        }
        int left = Math.Max(areaLeft, right - width);
        int top = Math.Max(areaTop, bottom - height);
        Interop.SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0,
            Interop.SWP_NOSIZE | Interop.SWP_NOZORDER | Interop.SWP_NOACTIVATE);
    }

    private void ReleaseWindowsPresentation()
    {
        _presenting = false;
        _dismissRequested = true;
        RobloxWindowTracker.Changed -= OnNotificationBoundsChanged;
        DismissButton.MouseLeftButtonDown -= OnDismissNotification;
        MouseMove -= OnNotificationMouse;
        MouseLeave -= OnNotificationMouse;
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTick;
            _hoverTimer = null;
        }
        _notificationTracker?.Dispose();
        _notificationTracker = null;
        OverlayDiagnostics.UnregisterOverlayHandle(_notificationHandle);
        _notificationHandle = IntPtr.Zero;
    }
}
