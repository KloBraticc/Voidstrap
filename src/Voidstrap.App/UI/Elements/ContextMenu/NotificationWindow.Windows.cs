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

    private async Task PresentWindowsNotificationAsync(NotificationItem item)
    {
        if (_notificationTracker == null)
        {
            _notificationTracker = RobloxWindowTracker.Acquire();
            RobloxWindowTracker.Changed += OnNotificationBoundsChanged;
            DismissButton.Click += OnDismissNotification;
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
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotificationBorder.Opacity = 0;
            RootTranslate.X = 28;
            ProgressScale.ScaleX = 1;
            DismissButton.Visibility = Visibility.Visible;
            Show();
            _notificationHandle = new WindowInteropHelper(this).Handle;
            OverlayDiagnostics.RegisterOverlayHandle(_notificationHandle);
            UpdateLayout();
            PlaceWindowsNotification(bounds);

            AnimateNotification(0, 1, 28, 0, 220);
            await Task.Delay(220, token);
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
                ProgressScale.ScaleX = Math.Clamp(remaining / duration, 0, 1);
            }
            AnimateNotification(1, 0, 0, 20, 160);
            await Task.Delay(160, token);
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
                NotificationBorder.Opacity = 0;
                SetImage(null);
                NotificationTitle.Text = string.Empty;
                NotificationText.Inlines.Clear();
            }
        }
    }

    private void AnimateNotification(double fromOpacity, double toOpacity, double fromX, double toX, int milliseconds)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            NotificationBorder.Opacity = toOpacity;
            RootTranslate.X = toX;
            return;
        }
        Duration duration = new(TimeSpan.FromMilliseconds(milliseconds));
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
        NotificationBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease });
        RootTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, toX, duration) { EasingFunction = ease });
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
        if (!bounds.Valid)
        {
            UpdatePosition();
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(Width * dpi.DpiScaleX);
        int marginX = (int)Math.Round(12 * dpi.DpiScaleX);
        int marginY = (int)Math.Round(12 * dpi.DpiScaleY);
        int left = Math.Max(bounds.Left, bounds.Left + bounds.Width - width - marginX);
        Interop.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, left, bounds.Top + marginY, 0, 0,
            Interop.SWP_NOSIZE | Interop.SWP_NOZORDER | Interop.SWP_NOACTIVATE);
    }

    private void ReleaseWindowsPresentation()
    {
        _presenting = false;
        _dismissRequested = true;
        RobloxWindowTracker.Changed -= OnNotificationBoundsChanged;
        DismissButton.Click -= OnDismissNotification;
        _notificationTracker?.Dispose();
        _notificationTracker = null;
        OverlayDiagnostics.UnregisterOverlayHandle(_notificationHandle);
        _notificationHandle = IntPtr.Zero;
    }
}
