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
        private const int RenderWarmUpFrames = 6;
        private const int RenderWarmUpTimeoutMs = 2500;
        private const int OutroMs = 320;
        private const int AnimationFrameRate = 60;
        private const int OutroTimeoutMs = 1200;
		private readonly Queue<NotificationItem> _queue = new();
		private readonly CancellationTokenSource _lifetimeCts = new();
		private double _slideDistance = 360;
		private readonly bool _linuxSurface;
		private readonly double _linuxHorizontalInset;
		private readonly double _linuxVerticalInset;

        private bool _isProcessing = false;
		private bool _closed;
		private bool _renderReady;
		private Task? _renderReadyTask;
		private int _renderHold;
		private int _renderFrames;
		private bool _reportedFrameRate;

        public bool IsUsable => !_closed;

        public NotificationWindow()
        {
            Title = "Voidstrap Notification";
            InitializeComponent();
			_linuxSurface = Voidstrap.Utility.Platform.IsLinux;
			if (_linuxSurface)
			{
				Thickness margin = NotificationRoot.Margin;
				_linuxHorizontalInset = margin.Right;
				_linuxVerticalInset = margin.Top;
				Width = Math.Max(1, Width - margin.Left - margin.Right);
				Height = Math.Max(1, Height - margin.Top - margin.Bottom);
				NotificationRoot.Margin = new Thickness(0);
				Opacity = 0;
				SizeChanged += Window_SizeChanged;
			}
            AccentStripe.Fill = Voidstrap.Utility.SystemAccent.GetGlassBrush();
            ProgressBar.Fill = Voidstrap.Utility.SystemAccent.GetGlassBrush();

            SourceInitialized += Window_SourceInitialized;
            Closed += Window_Closed;
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            MakeClickThrough();
        }

		private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (_linuxSurface)
				Voidstrap.Integrations.Overlays.LinuxOverlaySurface.ApplyRoundedShape(this, 10);
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

                    SetImage(item.Image);

					ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
					ProgressScale.ScaleX = 0;
					RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
					NotificationBorder.BeginAnimation(OpacityProperty, null);
					BeginAnimation(OpacityProperty, null);
					if (_linuxSurface)
					{
						RootTranslate.X = 0;
						NotificationBorder.Opacity = 1;
						Opacity = 0;
					}
					else
					{
						NotificationBorder.Opacity = 0;
					}

					PositionBeforeShow();
					if (!IsVisible)
						Show();
					UpdateLayout();
					UpdatePosition();
					RootTranslate.X = _linuxSurface ? 0 : _slideDistance;
					EnsureOpaqueBackground();
					if (_linuxSurface)
					{
						Voidstrap.Integrations.Overlays.LinuxOverlaySurface.ApplyRoundedShape(this, 10);
						Voidstrap.Integrations.Overlays.LinuxOverlaySurface.WakePresentation(this);
					}

					await EnsureRenderLoopReadyAsync();
					if (_closed || _lifetimeCts.IsCancellationRequested)
						break;
					HoldRenderLoop();
					RootTranslate.X = _linuxSurface ? 0 : _slideDistance;
					NotificationBorder.Opacity = _linuxSurface ? 1 : 0;

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    Timeline.SetDesiredFrameRate(fadeIn, AnimationFrameRate);
					if (_linuxSurface)
					{
						BeginAnimation(OpacityProperty, fadeIn);
					}
					else
					{
						var slideIn = new DoubleAnimation(_slideDistance, 0, TimeSpan.FromMilliseconds(420))
						{
							EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
						};
						Timeline.SetDesiredFrameRate(slideIn, AnimationFrameRate);
						RootTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
						NotificationBorder.BeginAnimation(OpacityProperty, fadeIn);
					}
					var progressAnim = new DoubleAnimation
					{
						From = 0,
						To = 1,
                        Duration = TimeSpan.FromSeconds(duration)
                    };
					Timeline.SetDesiredFrameRate(progressAnim, AnimationFrameRate);
					ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, progressAnim);

                    await Task.Delay(TimeSpan.FromSeconds(duration), _lifetimeCts.Token);

                    await PlayOutroAsync();
                    ReportFrameRate(duration);

                    SetImage(null);
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
				ReleaseRenderLoop();
				if (!_closed)
				{
					RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
					NotificationBorder.BeginAnimation(OpacityProperty, null);
					BeginAnimation(OpacityProperty, null);
					NotificationBorder.Opacity = 0;
					Opacity = _linuxSurface ? 0 : 1;
					Hide();
					SetImage(null);
					NotificationText.Inlines.Clear();
				}
            }
        }

		private void SetImage(BitmapSource? image)
		{
			if (image == null)
			{
				NotificationImage.Background = null;
				NotificationImage.Visibility = Visibility.Collapsed;
				return;
			}

			ImageBrush brush = new(image)
			{
				AlignmentX = AlignmentX.Center,
				AlignmentY = AlignmentY.Center,
				Stretch = Stretch.Uniform
			};
			if (brush.CanFreeze)
				brush.Freeze();
			NotificationImage.Background = brush;
			NotificationImage.Visibility = Visibility.Visible;
			NotificationImage.InvalidateMeasure();
			NotificationImage.InvalidateArrange();
			NotificationImage.InvalidateVisual();
		}

		private void SetText(string? text, BitmapSource? flag)
		{
			NotificationText.Inlines.Clear();
			EnsureReadableForeground();
			string message = text ?? string.Empty;
			int marker = message.IndexOf(FlagPlaceholder);
			if (marker < 0 || flag == null || !Voidstrap.Utility.Platform.IsWindows)
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

		private async Task PlayOutroAsync()
		{
			TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

			DoubleAnimation slideOut = new(0, _slideDistance, TimeSpan.FromMilliseconds(OutroMs))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
			};

			DoubleAnimation fadeOut = new(1, 0, TimeSpan.FromMilliseconds(OutroMs))
			{
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
			};

			Timeline.SetDesiredFrameRate(slideOut, AnimationFrameRate);
			Timeline.SetDesiredFrameRate(fadeOut, AnimationFrameRate);

			void completed(object? sender, EventArgs e) => finished.TrySetResult(true);

			ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			ProgressScale.ScaleX = 0;
			if (_linuxSurface)
			{
				fadeOut.Completed += completed;
				BeginAnimation(OpacityProperty, fadeOut);
			}
			else
			{
				slideOut.Completed += completed;
				RootTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
				NotificationBorder.BeginAnimation(OpacityProperty, fadeOut);
			}

			try
			{
				Task guard = Task.Delay(OutroTimeoutMs, _lifetimeCts.Token);
				Task done = await Task.WhenAny(finished.Task, guard);
				if (ReferenceEquals(done, guard) && guard.IsCanceled)
					throw new OperationCanceledException(_lifetimeCts.Token);
			}
			finally
			{
				fadeOut.Completed -= completed;
				slideOut.Completed -= completed;
			}
		}

		private void ReportFrameRate(double duration)
		{
			if (_reportedFrameRate || Volatile.Read(ref _renderHold) == 0)
				return;

			_reportedFrameRate = true;
			double seconds = duration + (OutroMs / 1000.0);
			App.Logger.WriteLine(
				"NotificationWindow::ReportFrameRate",
				"Rendered " + _renderFrames + " frames over " + seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
				"s, about " + (_renderFrames / Math.Max(0.1, seconds)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " per second");
		}

		private void HoldRenderLoop()
		{
			if (!Voidstrap.Utility.Platform.IsLinux)
				return;

			if (Interlocked.Exchange(ref _renderHold, 1) == 1)
				return;

			_renderFrames = 0;

			CompositionTarget.Rendering += OnRenderLoopHold;
		}

		private void ReleaseRenderLoop()
		{
			if (Interlocked.Exchange(ref _renderHold, 0) == 0)
				return;

			CompositionTarget.Rendering -= OnRenderLoopHold;
		}

		private void OnRenderLoopHold(object? sender, EventArgs e)
		{
			_renderFrames++;
		}

		public void Prewarm()
		{
			if (_closed || _renderReady || !Voidstrap.Utility.Platform.IsLinux)
				return;

			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.BeginInvoke(new Action(Prewarm));
				return;
			}

			_ = PrewarmAsync();
		}

		private async Task PrewarmAsync()
		{
			try
			{
				NotificationBorder.Opacity = 0;
				if (_linuxSurface)
				{
					Left = -32000;
					Top = -32000;
					Opacity = 0;
				}
				else
				{
					PositionBeforeShow();
				}
				if (!IsVisible)
					Show();
				UpdateLayout();
				RootTranslate.X = _linuxSurface ? 0 : _slideDistance;
				if (_linuxSurface)
					Voidstrap.Integrations.Overlays.LinuxOverlaySurface.ApplyRoundedShape(this, 10);
				await EnsureRenderLoopReadyAsync();
				if (!_closed && !_isProcessing)
					Hide();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("NotificationWindow::Prewarm", "The notification surface could not be prepared: " + ex.Message);
			}
		}

		private async Task EnsureRenderLoopReadyAsync()
		{
			if (_renderReady || !Voidstrap.Utility.Platform.IsLinux)
				return;

			_renderReadyTask ??= new RenderWarmUp(RenderWarmUpFrames, RenderWarmUpTimeoutMs).Completion;
			await _renderReadyTask;
			_renderReady = true;
		}

		private sealed partial class RenderWarmUp
		{
			private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			private readonly DispatcherTimer _deadline;
			private readonly int _frames;
			private int _seen;
			private int _finished;

			public RenderWarmUp(int frames, int timeoutMilliseconds)
			{
				_frames = frames;
				_deadline = new DispatcherTimer(DispatcherPriority.Send)
				{
					Interval = TimeSpan.FromMilliseconds(timeoutMilliseconds)
				};
				_deadline.Tick += OnDeadline;
				CompositionTarget.Rendering += OnRendering;
				_deadline.Start();
			}

			public Task Completion => _completion.Task;

			private void OnRendering(object? sender, EventArgs e)
			{
				_seen++;
				if (_seen >= _frames)
					Finish();
			}

			private void OnDeadline(object? sender, EventArgs e) => Finish();

			private void Finish()
			{
				if (Interlocked.Exchange(ref _finished, 1) != 0)
					return;

				CompositionTarget.Rendering -= OnRendering;
				_deadline.Stop();
				_deadline.Tick -= OnDeadline;
				_completion.TrySetResult(true);
			}
		}

		private void EnsureOpaqueBackground()
		{
			if (NotificationBorder.Background is not LinearGradientBrush gradient)
				return;

			bool translucent = false;
			foreach (GradientStop stop in gradient.GradientStops)
			{
				if (stop.Color.A < 255)
				{
					translucent = true;
					break;
				}
			}

			if (!translucent)
				return;

			LinearGradientBrush opaque = new()
			{
				StartPoint = gradient.StartPoint,
				EndPoint = gradient.EndPoint
			};

			foreach (GradientStop stop in gradient.GradientStops)
			{
				Color colour = stop.Color;
				opaque.GradientStops.Add(new GradientStop(Color.FromRgb(colour.R, colour.G, colour.B), stop.Offset));
			}

			opaque.Freeze();
			NotificationBorder.Background = opaque;
		}

		private static readonly System.Windows.Media.FontFamily NotificationFont =
			new("Inter 18pt, Selawik, Segoe UI Variable, Segoe UI, Ubuntu, Cantarell, Noto Sans, DejaVu Sans, Liberation Sans");

		private void EnsureReadableForeground()
		{
			NotificationText.SetCurrentValue(TextBlock.FontFamilyProperty, NotificationFont);

			if (NotificationText.Foreground is SolidColorBrush brush && brush.Color.A > 0 && brush.Color != Colors.Black)
				return;

			NotificationText.Foreground = Brushes.White;
		}

		private void UpdatePosition()
		{
			if (!Voidstrap.Utility.Platform.IsWindows)
			{
				_slideDistance = ActualWidth > 0 ? ActualWidth : Width;
				FallbackPosition();
				if (!TryMoveTopRightByHandle())
					QueueLinuxReposition();
				return;
			}

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

		private void PositionBeforeShow()
		{
			if (Voidstrap.Utility.Platform.IsWindows)
				return;

			_slideDistance = ActualWidth > 0 ? ActualWidth : Width;
			FallbackPosition();
		}

		private bool TryMoveTopRightByHandle()
		{
			try
			{
				nint handle = new WindowInteropHelper(this).Handle;
				if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle))
					handle = string.IsNullOrWhiteSpace(Title)
						? 0
						: Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(Title);

				if (handle == 0)
					return false;

				if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWorkArea(out int areaLeft, out int areaTop, out int areaWidth, out int areaHeight)
					|| areaWidth <= 0
					|| areaHeight <= 0)
					return false;

				int width = (int)Math.Round(ActualWidth > 0 ? ActualWidth : Width);
				int height = (int)Math.Round(ActualHeight > 0 ? ActualHeight : Height);
				if (width <= 0 || height <= 0)
					return false;

				int x = areaLeft + Math.Max(0, areaWidth - width - EdgeMargin - (int)Math.Round(_linuxHorizontalInset));
				int y = areaTop + EdgeMargin + (int)Math.Round(_linuxVerticalInset);
				return Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(handle, x, y, width, height);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("NotificationWindow::MoveTopRight", "The notification could not be positioned: " + ex.Message);
				return false;
			}
		}

		private void QueueLinuxReposition()
		{
			if (_closed || Dispatcher.HasShutdownStarted)
				return;

			Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
			{
				if (_closed || !IsVisible)
					return;

				if (!TryMoveTopRightByHandle())
					FallbackPosition();
			}));
		}

		private void FallbackPosition()
		{
			Rect workArea = Voidstrap.Utility.ScreenMetrics.WorkArea;
			_slideDistance = ActualWidth > 0 ? ActualWidth : Width;
			Left = workArea.Right - _slideDistance - EdgeMargin - _linuxHorizontalInset;
			Top = workArea.Top + EdgeMargin + _linuxVerticalInset;
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
            if (!Voidstrap.Utility.Platform.IsWindows)
            {
                Title = "Voidstrap Notification";
                Voidstrap.Integrations.Overlays.LinuxOverlaySurface.MakeClickThrough(this, 10);
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
			nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
			SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

		[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
		private static partial nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

		[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
		private static partial nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

        #endregion

        private void Window_Closed(object? sender, EventArgs e)
        {
			_closed = true;
			ReleaseRenderLoop();
            SourceInitialized -= Window_SourceInitialized;
            Closed -= Window_Closed;
			SizeChanged -= Window_SizeChanged;
            _lifetimeCts.Cancel();
            _queue.Clear();
            RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            NotificationBorder.BeginAnimation(OpacityProperty, null);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SetImage(null);
            NotificationText.Inlines.Clear();
            _lifetimeCts.Dispose();

            if (ReferenceEquals(Application.Current?.Resources["NotificationWindow"], this))
                Application.Current.Resources.Remove("NotificationWindow");
        }

        private static partial class Interop
        {
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;

            [StructLayout(LayoutKind.Sequential)]
            public partial struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public partial struct MONITORINFO
            {
                public uint cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [LibraryImport("user32.dll")]
            public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

            [LibraryImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);

            [LibraryImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

            [LibraryImport("user32.dll")]
            public static partial IntPtr GetForegroundWindow();

            [LibraryImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        }

        private partial class NotificationItem
        {
            public string Text { get; set; } = null!;
            public BitmapSource? Image { get; set; }
            public BitmapSource? Flag { get; set; }
            public double Duration { get; set; } = 5;
        }
    }
}
