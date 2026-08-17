using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.UI.Elements.Overlay
{
    public partial class NotificationWindow : Window
    {
        private const int MaxQueuedNotifications = 20;
        private const int EdgeMargin = 10;
        private readonly Queue<NotificationItem> _queue = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private double _slideDistance = 360;

        private bool _isProcessing = false;
		private bool _closed;

        public bool IsUsable => !_closed;

        public NotificationWindow()
        {
            InitializeComponent();
            AccentStripe.Fill = Voidstrap.Utility.SystemAccent.GetGlassBrush();
            ProgressBar.Fill = Voidstrap.Utility.SystemAccent.GetGlassBrush();

            SourceInitialized += Window_SourceInitialized;
            Closed += Window_Closed;
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            MakeClickThrough();
        }

        #region Public API
        public const char FlagPlaceholder = '\uFFFC';

        public void ShowNotification(string message, BitmapSource? image = null, double durationSeconds = 5, BitmapSource? flag = null)
        {
			if (_closed)
				return;
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.BeginInvoke(new Action(() => ShowNotification(message, image, durationSeconds, flag)));
				return;
			}
            while (_queue.Count >= MaxQueuedNotifications)
                _queue.Dequeue();
            _queue.Enqueue(new NotificationItem
            {
                Text = message,
                Image = image,
                Flag = flag,
                Duration = durationSeconds
            });

            if (!_isProcessing)
                _ = ProcessQueue();
        }

        #endregion
        #region Notification Logic

        private async Task ProcessQueue()
        {
            _isProcessing = true;

            try
            {
                while (_queue.Count > 0 && !_lifetimeCts.IsCancellationRequested)
                {
                    var item = _queue.Dequeue();
                    double duration = double.IsFinite(item.Duration) ? Math.Clamp(item.Duration, 0.5, 60) : 5;
                    SetText(item.Text, item.Flag);

                    if (item.Image != null)
                    {
                        NotificationImage.Source = item.Image;
                        NotificationImage.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        NotificationImage.Source = null;
                        NotificationImage.Visibility = Visibility.Collapsed;
                    }

					ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
					ProgressScale.ScaleX = 0;
					RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
					RootTranslate.X = _slideDistance;
					NotificationBorder.BeginAnimation(OpacityProperty, null);
					NotificationBorder.Opacity = 0;

					if (!IsVisible)
						Show();
					UpdateLayout();
					UpdatePosition();

                    var slideIn = new DoubleAnimation(_slideDistance, 0, TimeSpan.FromMilliseconds(420))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    RootTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    NotificationBorder.BeginAnimation(OpacityProperty, fadeIn);
					var progressAnim = new DoubleAnimation
					{
						From = 0,
						To = 1,
                        Duration = TimeSpan.FromSeconds(duration)
                    };
					ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, progressAnim);

                    await Task.Delay(TimeSpan.FromSeconds(duration), _lifetimeCts.Token);

                    var slideOut = new DoubleAnimation(0, _slideDistance, TimeSpan.FromMilliseconds(320))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };
                    RootTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);

                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                    };
                    NotificationBorder.BeginAnimation(OpacityProperty, fadeOut);
					ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
					ProgressScale.ScaleX = 0;

                    await Task.Delay(340, _lifetimeCts.Token);

                    NotificationImage.Source = null;
                    NotificationImage.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _queue.Clear();
                App.Logger.WriteLine("NotificationWindow::ProcessQueue", "Notification processing stopped: " + ex.Message);
            }
            finally
            {
                _isProcessing = false;
				if (!_closed)
				{
					RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
					NotificationBorder.BeginAnimation(OpacityProperty, null);
					NotificationBorder.Opacity = 0;
					Hide();
					NotificationImage.Source = null;
					NotificationText.Inlines.Clear();
				}
            }
        }

		private void SetText(string? text, BitmapSource? flag)
		{
			NotificationText.Inlines.Clear();
			string message = text ?? string.Empty;
			int marker = message.IndexOf(FlagPlaceholder);
			if (marker < 0 || flag == null)
			{
				NotificationText.Text = message.Replace(FlagPlaceholder.ToString(), string.Empty);
				return;
			}
			if (marker > 0)
				NotificationText.Inlines.Add(new Run(message.Substring(0, marker)));
			NotificationText.Inlines.Add(new InlineUIContainer(new Image
			{
				Source = flag,
				Height = 11,
				Stretch = Stretch.Uniform,
				Margin = new Thickness(0, 0, 4, -1),
				SnapsToDevicePixels = true
			})
			{
				BaselineAlignment = BaselineAlignment.Center
			});
			if (marker + 1 < message.Length)
				NotificationText.Inlines.Add(new Run(message.Substring(marker + 1)));
		}

		private void UpdatePosition()
		{
			IntPtr self = new WindowInteropHelper(this).Handle;
			if (self == IntPtr.Zero)
				return;

			if (!TryGetTargetWorkArea(self, out Interop.RECT work) || !Interop.GetWindowRect(self, out Interop.RECT bounds))
			{
				FallbackPosition();
				return;
			}

			int width = bounds.Right - bounds.Left;
			int height = bounds.Bottom - bounds.Top;
			if (width <= 0 || height <= 0)
			{
				FallbackPosition();
				return;
			}

			_slideDistance = ActualWidth > 0 ? ActualWidth : Width;

			int x = work.Right - width - EdgeMargin;
			int y = work.Top + EdgeMargin;
			Interop.SetWindowPos(self, IntPtr.Zero, x, y, 0, 0, Interop.SWP_NOSIZE | Interop.SWP_NOZORDER | Interop.SWP_NOACTIVATE);
		}

		private void FallbackPosition()
		{
			Rect workArea = SystemParameters.WorkArea;
			_slideDistance = ActualWidth > 0 ? ActualWidth : Width;
			Left = workArea.Right - _slideDistance - EdgeMargin;
			Top = workArea.Top + EdgeMargin;
		}

		private static bool TryGetTargetWorkArea(IntPtr self, out Interop.RECT work)
		{
			work = default;
			IntPtr anchor = IntPtr.Zero;
			try
			{
				anchor = RobloxLightingOverlay.RobloxWindow.GetHandle();
			}
			catch
			{
			}
			if (anchor == IntPtr.Zero)
				anchor = Interop.GetForegroundWindow();
			if (anchor == IntPtr.Zero)
				anchor = self;

			IntPtr monitor = Interop.MonitorFromWindow(anchor, Interop.MONITOR_DEFAULTTONEAREST);
			if (monitor == IntPtr.Zero)
				return false;

			Interop.MONITORINFO info = new() { cbSize = (uint)Marshal.SizeOf<Interop.MONITORINFO>() };
			if (!Interop.GetMonitorInfoW(monitor, ref info))
				return false;

			work = info.rcWork;
			return work.Right > work.Left && work.Bottom > work.Top;
		}

        #endregion
        #region Click Through

        private void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
			nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
			SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

		[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
		private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
		private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

        #endregion

        private void Window_Closed(object? sender, EventArgs e)
        {
			_closed = true;
            SourceInitialized -= Window_SourceInitialized;
            Closed -= Window_Closed;
            _lifetimeCts.Cancel();
            _queue.Clear();
            RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            NotificationBorder.BeginAnimation(OpacityProperty, null);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotificationImage.Source = null;
            NotificationText.Inlines.Clear();
            _lifetimeCts.Dispose();

            if (ReferenceEquals(Application.Current?.Resources["NotificationWindow"], this))
                Application.Current.Resources.Remove("NotificationWindow");
        }

        private static class Interop
        {
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public uint cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

            [DllImport("user32.dll")]
            public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        }

        private class NotificationItem
        {
            public string Text { get; set; }
            public BitmapSource? Image { get; set; }
            public BitmapSource? Flag { get; set; }
            public double Duration { get; set; } = 5;
        }
    }
}
