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
using Voidstrap.Models.Persistable;

namespace Voidstrap.UI.Elements.Overlay;

public partial class NotificationWindow
{
    private IDisposable? _notificationTracker;
    private bool _presenting;
    private bool _dismissRequested;
    private IntPtr _notificationHandle;
    private NotificationAppearance? _appearance;
    private bool _hovered;
    private bool _dismissHot;
    private bool _buttonWasDown;
    private bool _hoverLogged;
    private bool _hoverSeen;
    private bool _leaveLogged;
    private System.Windows.Threading.DispatcherTimer? _hoverTimer;
    private Voidstrap.UI.NativeSlide? _nativeSlide;
    // The game's area (or the screen's work area) the notification is placed in, in screen pixels
    private Int32Rect _area;

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
            NotificationAppearance appearance = Voidstrap.UI.NotificationStyle.Current;
            _appearance = appearance;
            NotificationRoot.BeginAnimation(OpacityProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            UseCornerLayout(appearance, item.Kind);
            NotificationRoot.Opacity = 0;
            RootTranslate.X = 0;
            ProgressScale.ScaleX = 1;
            DismissButton.Visibility = Visibility.Visible;
            _hovered = false;
            _dismissHot = false;
            _hoverLogged = false;
            _hoverSeen = false;
            _leaveLogged = false;
            _buttonWasDown = IsLeftButtonDown();
            DismissButton.Background = Brushes.Transparent;
            SetDismissVisible(false, false);
            Show();
            _notificationHandle = new WindowInteropHelper(this).Handle;
            OverlayDiagnostics.RegisterOverlayHandle(_notificationHandle);
            UpdateLayout();
            Size travel = new(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            int intro = Voidstrap.UI.NotificationStyle.Length(appearance, true);
            // Sliding moves the window itself, nothing inside it is redrawn while it moves
            bool nativeIntro = intro > 0 && appearance.Intro == NotificationMotion.Slide && Voidstrap.UI.NativeSlide.Supported;
            if (nativeIntro)
            {
                Voidstrap.UI.NotificationStyle.Stop(NotificationRoot, RootTranslate, RootScale);
                NotificationRoot.Opacity = 1;
                RootTranslate.X = RootTranslate.Y = 0;
                RootScale.ScaleX = RootScale.ScaleY = 1;
                PlaceWindowsNotification(bounds);
                if (!SlideWindow(appearance, true, intro))
                    nativeIntro = false;
            }
            if (!nativeIntro)
            {
                Voidstrap.UI.NotificationStyle.PrepareIntro(NotificationRoot, RootTranslate, RootScale, appearance, travel);
                PlaceWindowsNotification(bounds);
                // Enters the way the notification settings say
                BeginCachedAnimation();
                Voidstrap.UI.NotificationStyle.Play(NotificationRoot, RootTranslate, RootScale, appearance, travel, true);
            }
            if (intro > 0)
                await Task.Delay(intro, token);
            EndCachedAnimation();
            _nativeSlide?.Stop(true);
            _hoverTimer?.Start();
            double duration = appearance.SafeSecondsOnScreen;
            double remaining = duration;
            Stopwatch clock = Stopwatch.StartNew();
            double previous = clock.Elapsed.TotalSeconds;
            while (!_dismissRequested && remaining > 0)
            {
                await Task.Delay(100, token);
                double now = clock.Elapsed.TotalSeconds;
                if (!_hovered || !appearance.PauseWhileHovered)
                    remaining -= now - previous;
                previous = now;
            }
            // Fades out while easing back down below the edge
            _hoverTimer?.Stop();
            DismissButton.BeginAnimation(OpacityProperty, null);
            DismissButton.Opacity = 0;
            DismissButton.IsHitTestVisible = false;
            int outro = Voidstrap.UI.NotificationStyle.Length(appearance, false);
            if (!(outro > 0 && appearance.Outro == NotificationMotion.Slide && Voidstrap.UI.NativeSlide.Supported && SlideWindow(appearance, false, outro)))
            {
                BeginCachedAnimation();
                Voidstrap.UI.NotificationStyle.Play(NotificationRoot, RootTranslate, RootScale, appearance, new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), false);
            }
            if (outro > 0)
                await Task.Delay(outro, token);
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
                _nativeSlide?.Stop(true);
                NotificationRoot.BeginAnimation(OpacityProperty, null);
                RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                RootScale.ScaleX = RootScale.ScaleY = 1;
                NotificationRoot.Opacity = 0;
                SetImage(null);
                NotificationTitle.Text = string.Empty;
                NotificationText.Inlines.Clear();
            }
        }
    }

    // Applied on every notification so changes in the notification settings show on the next one
    private void UseCornerLayout(NotificationAppearance appearance, Voidstrap.UI.NotificationKind kind)
    {
        // The whole card fades as one with its shadow, the window itself clips it at the screen edge
        NotificationBorder.BeginAnimation(OpacityProperty, null);
        NotificationBorder.Opacity = 1;
        NotificationRoot.Margin = Voidstrap.UI.NotificationStyle.ShadowMargins(appearance);
        NotificationRoot.ClipToBounds = false;
        NotificationBorder.Effect = null;
        NotificationBorder.CornerRadius = Voidstrap.UI.NotificationStyle.Corners(appearance);
        Voidstrap.UI.NotificationStyle.FillShadow(CardShadow, NotificationBorder.CornerRadius);
        NotificationBorder.BorderThickness = Voidstrap.UI.NotificationStyle.Borders(appearance);
        NotificationBorder.Background = Voidstrap.UI.NotificationStyle.Background(this, appearance);
        // Laid out at the normal width and scaled as a whole, so everything keeps its proportions
        NotificationBorder.Width = appearance.SafeCardWidth;
        double scale = appearance.SafeScale;
        NotificationBorder.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        double text = appearance.SafeTextScale;
        NotificationHeaderText.FontSize = 11 * text;
        NotificationStatus.FontSize = 12 * text;
        NotificationTitle.FontSize = 14 * text;
        NotificationText.FontSize = 12 * text;
        NotificationText.MaxHeight = 64 * text;
        bool header = kind != Voidstrap.UI.NotificationKind.General && appearance.ShowsHeader(kind == Voidstrap.UI.NotificationKind.Friends);
        NotificationHeader.Visibility = header ? Visibility.Visible : Visibility.Collapsed;
        Width = appearance.SafeWidth + NotificationRoot.Margin.Left + NotificationRoot.Margin.Right;
        SizeToContent = SizeToContent.Height;
    }

    // While the card only moves and fades, it is rendered once into a bitmap at the screen's scale
    // and that bitmap is composited each frame instead of laying out and drawing the text again
    private void BeginCachedAnimation()
    {
        // Cached on the root so the card and its shadow are drawn once, the blur is not redone every frame
        if (NotificationRoot.CacheMode is BitmapCache)
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        NotificationRoot.CacheMode = new BitmapCache(Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)) { SnapsToDevicePixels = true };
    }

    private void EndCachedAnimation() => NotificationRoot.CacheMode = null;

    // Roblox can keep the mouse to itself, so hover and the close click are read from the real cursor
    // position while a notification is up instead of relying only on mouse messages reaching this window
    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_closed || !_presenting || _dismissRequested || !IsVisible)
            return;
        IntPtr handle = _notificationHandle;
        if (handle == IntPtr.Zero || !GetCursorPos(out NativePoint cursor) || !GetWindowRect(handle, out NativeRect window))
            return;
        // The window also holds the shadow, so only the card's own rectangle inside it counts as hovering
        DpiScale hoverDpi = VisualTreeHelper.GetDpi(this);
        Thickness shadow = NotificationRoot.Margin;
        bool insideRect = cursor.X >= window.Left + shadow.Left * hoverDpi.DpiScaleX && cursor.X < window.Right - shadow.Right * hoverDpi.DpiScaleX
            && cursor.Y >= window.Top + shadow.Top * hoverDpi.DpiScaleY && cursor.Y < window.Bottom - shadow.Bottom * hoverDpi.DpiScaleY;
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
            SetDismissVisible(hovered && (_appearance?.CloseButtonOnHover ?? true), true);
            // Logged once each way per notification, moving in and out would otherwise fill the log
            if (hovered ? !_hoverSeen : !_leaveLogged)
            {
                if (hovered)
                    _hoverSeen = true;
                else
                    _leaveLogged = true;
                App.Logger.WriteLine("NotificationWindow", hovered
                    ? $"Pointer entered the notification at {cursor.X},{cursor.Y}, showing the close button"
                    : "Pointer left the notification, hiding the close button");
            }
        }
        bool hot = hovered && (_appearance?.CloseButtonOnHover ?? true) && DismissContains(window, cursor);
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
        // Mid slide the window is where the slide put it, it is placed again once it has arrived
        if (_nativeSlide?.IsRunning == true)
            return;
        PlaceWindowsNotification(bounds);
    }

    // Slides the window in from, or out to, the edge nearest to it, clipped to the game so it comes out of the edge
    private bool SlideWindow(NotificationAppearance appearance, bool intro, int milliseconds)
    {
        _nativeSlide ??= new Voidstrap.UI.NativeSlide(this);
        if (_nativeSlide.CurrentPosition() is not Point home || _area.Width <= 0 || _area.Height <= 0)
            return false;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double width = Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        double height = Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        Vector direction = Voidstrap.UI.NotificationStyle.SlideDirection(appearance);
        Point away = direction.Y > 0 ? new Point(home.X, _area.Y + _area.Height)
            : direction.Y < 0 ? new Point(home.X, _area.Y - height)
            : direction.X > 0 ? new Point(_area.X + _area.Width, home.Y)
            : new Point(_area.X - width, home.Y);
        return intro
            ? _nativeSlide.Start(away, home, _area, TimeSpan.FromMilliseconds(milliseconds), true, null)
            : _nativeSlide.Start(home, away, _area, TimeSpan.FromMilliseconds(milliseconds), false, null);
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
        _area = new Int32Rect(areaLeft, areaTop, Math.Max(0, right - areaLeft), Math.Max(0, bottom - areaTop));
        NotificationAppearance appearance = _appearance ?? Voidstrap.UI.NotificationStyle.Current;
        double gap = appearance.SafeEdgeSpacing * dpi.DpiScaleX;
        // Placed by the card, then the window is pushed out by the shadow room around it
        Thickness shadow = NotificationRoot.Margin;
        int shadowLeft = (int)Math.Round(shadow.Left * dpi.DpiScaleX);
        int shadowTop = (int)Math.Round(shadow.Top * dpi.DpiScaleY);
        int cardWidth = width - shadowLeft - (int)Math.Round(shadow.Right * dpi.DpiScaleX);
        int cardHeight = height - shadowTop - (int)Math.Round(shadow.Bottom * dpi.DpiScaleY);
        Point spot = Voidstrap.UI.NotificationStyle.Place(appearance, right - areaLeft, bottom - areaTop, cardWidth, cardHeight, gap);
        int left = Math.Max(areaLeft, areaLeft + (int)Math.Round(spot.X));
        int top = Math.Max(areaTop, areaTop + (int)Math.Round(spot.Y));
        Interop.SetWindowPos(handle, IntPtr.Zero, left - shadowLeft, top - shadowTop, 0, 0,
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
