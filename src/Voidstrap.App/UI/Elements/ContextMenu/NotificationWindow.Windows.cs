using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Overlay;

public partial class NotificationWindow
{
    private IDisposable? _notificationTracker;
    private bool _presenting;
    private bool _dismissRequested;
    private IntPtr _notificationHandle;
    private bool _cornerLayout;

    private async Task PresentWindowsNotificationAsync(NotificationItem item)
    {
        if (_notificationTracker == null)
        {
            _notificationTracker = RobloxWindowTracker.Acquire();
            RobloxWindowTracker.Changed += OnNotificationBoundsChanged;
            DismissButton.Click += OnDismissNotification;
            NotificationBorder.MouseEnter += OnNotificationHover;
            NotificationBorder.MouseLeave += OnNotificationHover;
        }

        RobloxWindowRect bounds = RobloxWindowTracker.Current;
        if (OverlayHub.InGame && (!bounds.Valid || !bounds.Foreground))
            return;

        using CancellationTokenSource presentation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, item.Token);
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
            SetDismissVisible(false);
            Show();
            _notificationHandle = new WindowInteropHelper(this).Handle;
            OverlayDiagnostics.RegisterOverlayHandle(_notificationHandle);
            UpdateLayout();
            double slide = Math.Max(1, ActualHeight);
            RootTranslate.Y = slide;
            PlaceWindowsNotification(bounds);

            // Starts below the bottom edge and slides up into the corner
            AnimateNotification(0, 1, slide, 0, 240);
            await Task.Delay(240, token);
            double duration = double.IsFinite(item.Duration) ? Math.Clamp(item.Duration, 0.5, 60) : 8;
            double remaining = duration;
            Stopwatch clock = Stopwatch.StartNew();
            double previous = clock.Elapsed.TotalSeconds;
            while (!_dismissRequested && remaining > 0)
            {
                await Task.Delay(500, token);
                double now = clock.Elapsed.TotalSeconds;
                if (!NotificationBorder.IsMouseOver)
                    remaining -= now - previous;
                previous = now;
            }
            AnimateNotification(1, 0, 0, Math.Max(1, ActualHeight), 180);
            await Task.Delay(180, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _presenting = false;
            if (!_closed)
            {
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

    private void AnimateNotification(double fromOpacity, double toOpacity, double fromY, double toY, int milliseconds)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            NotificationBorder.Opacity = toOpacity;
            RootTranslate.Y = toY;
            return;
        }
        Duration duration = new(TimeSpan.FromMilliseconds(milliseconds));
        CubicEase ease = new() { EasingMode = toOpacity > fromOpacity ? EasingMode.EaseOut : EasingMode.EaseIn };
        NotificationBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, duration) { EasingFunction = ease });
    }

    // The close button only shows while the pointer is over the notification, which also pauses its countdown
    private void OnNotificationHover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_closed)
            SetDismissVisible(_presenting && NotificationBorder.IsMouseOver);
    }

    private void SetDismissVisible(bool visible)
    {
        DismissButton.IsHitTestVisible = visible;
        if (!SystemParameters.ClientAreaAnimation)
        {
            DismissButton.BeginAnimation(OpacityProperty, null);
            DismissButton.Opacity = visible ? 1 : 0;
            return;
        }
        DismissButton.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(120))));
    }

    private void OnDismissNotification(object sender, RoutedEventArgs e)
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
        DismissButton.Click -= OnDismissNotification;
        NotificationBorder.MouseEnter -= OnNotificationHover;
        NotificationBorder.MouseLeave -= OnNotificationHover;
        _notificationTracker?.Dispose();
        _notificationTracker = null;
        OverlayDiagnostics.UnregisterOverlayHandle(_notificationHandle);
        _notificationHandle = IntPtr.Zero;
    }
}
