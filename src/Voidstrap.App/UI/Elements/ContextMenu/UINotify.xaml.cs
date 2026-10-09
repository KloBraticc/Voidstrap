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
using Voidstrap.Extensions;

namespace Voidstrap.UI.Elements.Overlay
{
    public partial class NotificationWindow : Window
    {
        private const int MaxQueuedNotifications = 20;
        private const int EdgeMargin = 10;
        private const int RenderWarmUpFrames = 6;
        private const int RenderWarmUpTimeoutMs = 2500;
        private const int IntroSlideMs = 420;
        private const int IntroFadeMs = 260;
        private const int OutroMs = 320;
        private const int AnimationFrameRate = 60;
        private const int OutroTimeoutMs = 1200;
		private const int LinuxCornerRadius = 10;
		private const int LinuxParkedPosition = -32000;
		private const int LinuxPresentFrames = 2;
		private const int LinuxPresentTimeoutMs = 250;
		private const int LinuxWarmDelayMs = 3000;
		private const int LinuxWarmMs = 1500;
		private static readonly DependencyProperty LinuxFadeProperty = DependencyProperty.Register("LinuxFade", typeof(double), typeof(NotificationWindow), new PropertyMetadata(1.0));
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
		private bool _linuxAlpha = true;
		private bool _linuxAlphaResolved;
		private bool _linuxSyncing;
		private bool _linuxWarmed;
		private nint _linuxHandle;
		private int _linuxPixelWidth;
		private int _linuxPixelHeight;
		private int _linuxShapeOffset = int.MinValue;
		private int _linuxPreviousOffset = int.MinValue;
		private int _linuxOpacityStep = -1;

        public bool IsUsable => !_closed;

        public NotificationWindow()
        {
            Title = "Voidstrap Notification";
            InitializeComponent();
			_linuxSurface = Voidstrap.Utility.Platform.IsLinux;
			if (_linuxSurface)
			{
				Voidstrap.Integrations.Overlays.LinuxOverlaySurface.ReleaseMainWindowClaim(this);
				Thickness margin = NotificationRoot.Margin;
				_linuxHorizontalInset = margin.Right;
				_linuxVerticalInset = margin.Top;
				Width = Math.Max(1, Width - margin.Left - margin.Right);
				Height = Math.Max(1, Height - margin.Top - margin.Bottom);
				NotificationRoot.Margin = new Thickness(0);
				SizeChanged += Window_SizeChanged;
			}
            AccentStripe.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemAccentColorBrush");
            ProgressBar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemAccentColorBrush");

            SourceInitialized += Window_SourceInitialized;
            Closed += Window_Closed;
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            MakeClickThrough();
        }

		private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (!_linuxSurface)
				return;
			_linuxPixelWidth = 0;
			_linuxPixelHeight = 0;
			if (_linuxAlphaResolved && !_linuxAlpha)
				SyncLinuxSurface(true);
		}

        #region Public API
        public const char FlagPlaceholder = '￼';

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
						await PrepareLinuxShowAsync();
						if (_closed || _lifetimeCts.IsCancellationRequested)
							break;
					}
					else
					{
						NotificationBorder.Opacity = 0;
						PositionBeforeShow();
						if (!IsVisible)
							Show();
						UpdateLayout();
						UpdatePosition();
						RootTranslate.X = _slideDistance;
						EnsureOpaqueBackground();
						RootTranslate.X = _slideDistance;
						NotificationBorder.Opacity = 0;
					}

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(IntroFadeMs))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    Timeline.SetDesiredFrameRate(fadeIn, AnimationFrameRate);
					var slideIn = new DoubleAnimation(_slideDistance, 0, TimeSpan.FromMilliseconds(IntroSlideMs))
					{
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					Timeline.SetDesiredFrameRate(slideIn, AnimationFrameRate);
					if (_linuxSurface && !_linuxAlpha)
						StartLinuxSync();
					RootTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
					if (_linuxSurface && !_linuxAlpha)
						BeginAnimation(LinuxFadeProperty, fadeIn);
					else
						NotificationBorder.BeginAnimation(OpacityProperty, fadeIn);
					var progressAnim = new DoubleAnimation
					{
						From = 0,
						To = 1,
                        Duration = TimeSpan.FromSeconds(duration)
                    };
					Timeline.SetDesiredFrameRate(progressAnim, AnimationFrameRate);
					ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, progressAnim);

					if (_linuxSyncing)
					{
						TimeSpan intro = TimeSpan.FromMilliseconds(IntroSlideMs + 40);
						await Task.Delay(intro, _lifetimeCts.SafeToken());
						StopLinuxSync();
						SyncLinuxSurface(true);
						await Task.Delay(TimeSpan.FromSeconds(duration) - intro, _lifetimeCts.SafeToken());
					}
					else
					{
						await Task.Delay(TimeSpan.FromSeconds(duration), _lifetimeCts.SafeToken());
					}

                    await PlayOutroAsync();

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
				StopLinuxSync();
				if (!_closed)
				{
					RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
					NotificationBorder.BeginAnimation(OpacityProperty, null);
					BeginAnimation(OpacityProperty, null);
					if (_linuxSurface)
					{
						ParkLinuxSurface();
					}
					else
					{
						NotificationBorder.Opacity = 0;
						Opacity = 1;
						Hide();
					}
					SetImage(null);
					NotificationText.Inlines.Clear();
				}
            }
        }

		private void SetImage(BitmapSource? image)
		{
			if (image == null)
			{
				NotificationImage.Source = null;
				NotificationImage.Visibility = Visibility.Collapsed;
				return;
			}

			NotificationImage.Source = image;
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
			slideOut.Completed += completed;
			if (_linuxSurface && !_linuxAlpha)
				StartLinuxSync();
			RootTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
			if (_linuxSurface && !_linuxAlpha)
				BeginAnimation(LinuxFadeProperty, fadeOut);
			else
				NotificationBorder.BeginAnimation(OpacityProperty, fadeOut);

			try
			{
				Task guard = Task.Delay(OutroTimeoutMs, _lifetimeCts.SafeToken());
				Task done = await Task.WhenAny(finished.Task, guard);
				if (ReferenceEquals(done, guard) && guard.IsCanceled)
					throw new OperationCanceledException(_lifetimeCts.SafeToken());
			}
			finally
			{
				slideOut.Completed -= completed;
				StopLinuxSync();
			}
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
				if (!_linuxSurface)
					return;
				RootTranslate.X = _slideDistance;
				ApplyLinuxHiddenState();
				if (!IsVisible)
				{
					Left = LinuxParkedPosition;
					Top = LinuxParkedPosition;
					Show();
				}
				UpdateLayout();
				_slideDistance = ActualWidth > 0 ? ActualWidth : Width;
				RootTranslate.X = _slideDistance;
				EnsureOpaqueBackground();
				await EnsureRenderLoopReadyAsync();
				ResolveLinuxAlpha();
				ApplyLinuxHiddenState();
				await WarmLinuxAnimationsAsync();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("NotificationWindow::Prewarm", "The notification surface could not be prepared: " + ex.Message);
			}
		}

		private async Task WarmLinuxAnimationsAsync()
		{
			if (_linuxWarmed || _closed || _isProcessing)
				return;

			_linuxWarmed = true;
			try
			{
				await Task.Delay(LinuxWarmDelayMs, _lifetimeCts.SafeToken());
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (_closed || _isProcessing)
				return;

			NotificationText.Text = "Voidstrap\nServer location • 0 players";
			NotificationBorder.Opacity = 1;
			TimeSpan length = TimeSpan.FromMilliseconds(LinuxWarmMs);
			DoubleAnimation slide = new(_slideDistance, 0, TimeSpan.FromMilliseconds(IntroSlideMs))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			DoubleAnimation fade = new(0, 1, TimeSpan.FromMilliseconds(IntroFadeMs))
			{
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
			};
			DoubleAnimation progress = new(0, 1, length);
			Timeline.SetDesiredFrameRate(slide, AnimationFrameRate);
			Timeline.SetDesiredFrameRate(fade, AnimationFrameRate);
			Timeline.SetDesiredFrameRate(progress, AnimationFrameRate);
			RootTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
			NotificationBorder.BeginAnimation(OpacityProperty, fade);
			ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, progress);
			try
			{
				await Task.Delay(length, _lifetimeCts.SafeToken());
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (_closed || _isProcessing)
				return;
			RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
			NotificationBorder.BeginAnimation(OpacityProperty, null);
			ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			ProgressScale.ScaleX = 0;
			NotificationText.Text = string.Empty;
			ParkLinuxSurface();
		}

		private async Task PrepareLinuxShowAsync()
		{
			BeginAnimation(LinuxFadeProperty, null);
			if (IsVisible)
			{
				ParkLinuxSurface();
			}
			else
			{
				RootTranslate.X = _slideDistance;
				ApplyLinuxHiddenState();
				Left = LinuxParkedPosition;
				Top = LinuxParkedPosition;
				Show();
			}
			UpdateLayout();
			_slideDistance = ActualWidth > 0 ? ActualWidth : Width;
			RootTranslate.X = _slideDistance;
			EnsureOpaqueBackground();
			await EnsureRenderLoopReadyAsync();
			if (_closed || _lifetimeCts.IsCancellationRequested)
				return;
			ResolveLinuxAlpha();
			RootTranslate.X = 0;
			NotificationBorder.Opacity = 1;
			Voidstrap.Integrations.Overlays.LinuxOverlaySurface.WakePresentation(this);
			await new RenderWarmUp(LinuxPresentFrames, LinuxPresentTimeoutMs).Completion;
			RootTranslate.X = _slideDistance;
			ApplyLinuxHiddenState();
			Voidstrap.Integrations.Overlays.LinuxOverlaySurface.WakePresentation(this);
			await new RenderWarmUp(LinuxPresentFrames, LinuxPresentTimeoutMs).Completion;
			if (_closed || _lifetimeCts.IsCancellationRequested)
				return;
			UpdatePosition();
		}

		private void ApplyLinuxHiddenState()
		{
			if (_linuxAlpha)
			{
				NotificationBorder.Opacity = 0;
				return;
			}

			NotificationBorder.Opacity = 1;
			BeginAnimation(LinuxFadeProperty, null);
			SetValue(LinuxFadeProperty, 0.0);
			SyncLinuxSurface(true);
		}

		private void ParkLinuxSurface()
		{
			RootTranslate.X = _slideDistance;
			ApplyLinuxHiddenState();
			Left = LinuxParkedPosition;
			Top = LinuxParkedPosition;
			nint handle = ResolveLinuxHandle();
			if (handle != 0 && LinuxWindowSize(handle, out int width, out int height))
				Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(handle, LinuxParkedPosition, LinuxParkedPosition, width, height);
		}

		private void ResolveLinuxAlpha()
		{
			if (_linuxAlphaResolved)
				return;

			nint handle = ResolveLinuxHandle();
			if (handle == 0)
				return;

			_linuxAlphaResolved = true;
			bool forcedOpaque = Environment.GetEnvironmentVariable("VOIDSTRAP_OVERLAY_ALPHA") == "0";
			_linuxAlpha = !forcedOpaque
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowDepth(handle, out int depth)
				&& depth == 32;
			if (_linuxAlpha)
				Voidstrap.Platform.Linux.LinuxWindowInterop.TryClearShape(handle);
			App.Logger.WriteLine("NotificationWindow::ResolveLinuxAlpha", _linuxAlpha
				? "The notification surface has an alpha channel, it slides and fades exactly like Windows"
				: "The notification surface has no alpha channel, the compositor fades it and its shape follows the slide");
		}

		private void StartLinuxSync()
		{
			if (_linuxSyncing)
				return;

			_linuxSyncing = true;
			_linuxPreviousOffset = int.MinValue;
			CompositionTarget.Rendering += OnLinuxSurfaceFrame;
		}

		private void StopLinuxSync()
		{
			if (!_linuxSyncing)
				return;

			_linuxSyncing = false;
			CompositionTarget.Rendering -= OnLinuxSurfaceFrame;
		}

		private void OnLinuxSurfaceFrame(object? sender, EventArgs e)
		{
			SyncLinuxSurface(false);
		}

		private void SyncLinuxSurface(bool force)
		{
			if (!_linuxSurface || _linuxAlpha || _closed)
				return;

			nint handle = ResolveLinuxHandle();
			if (handle == 0 || !LinuxWindowSize(handle, out int width, out int height))
				return;

			double logicalWidth = ActualWidth > 0 ? ActualWidth : Width;
			double scale = logicalWidth > 0 ? width / logicalWidth : 1;
			int offset = (int)Math.Round(RootTranslate.X * scale);
			int shapeOffset = force || _linuxPreviousOffset == int.MinValue ? offset : Math.Max(offset, _linuxPreviousOffset);
			_linuxPreviousOffset = offset;
			if (force || shapeOffset != _linuxShapeOffset)
			{
				int radius = Math.Max(1, (int)Math.Round(LinuxCornerRadius * Math.Max(0.5, scale)));
				Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetOffsetRoundedShape(handle, shapeOffset, width, height, radius, width, height);
				_linuxShapeOffset = shapeOffset;
			}

			int step = (int)Math.Round(Math.Clamp((double)GetValue(LinuxFadeProperty), 0, 1) * 100);
			if (force || step != _linuxOpacityStep)
			{
				Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(handle, step / 100.0);
				_linuxOpacityStep = step;
			}
		}

		private bool LinuxWindowSize(nint handle, out int width, out int height)
		{
			if (_linuxPixelWidth <= 0 || _linuxPixelHeight <= 0)
			{
				if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out _linuxPixelWidth, out _linuxPixelHeight))
				{
					_linuxPixelWidth = 0;
					_linuxPixelHeight = 0;
				}
			}
			width = _linuxPixelWidth;
			height = _linuxPixelHeight;
			return width > 0 && height > 0;
		}

		private nint ResolveLinuxHandle()
		{
			if (_linuxHandle != 0 && Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(_linuxHandle))
				return _linuxHandle;

			try
			{
				nint handle = new WindowInteropHelper(this).Handle;
				if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle))
					handle = string.IsNullOrWhiteSpace(Title) ? 0 : Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(Title);
				_linuxHandle = handle;
				_linuxPixelWidth = 0;
				_linuxPixelHeight = 0;
				return handle;
			}
			catch (Exception)
			{
				return 0;
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
			if (Voidstrap.Utility.Platform.IsMacOS)
			{
				FallbackPosition();
				nint native = Voidstrap.UI.MacWindowMode.ResolveNativeWindow(this);
				if (native != 0)
				{
					Voidstrap.Platform.MacOS.MacOSOverlayWindow.Configure(native, true);
					Voidstrap.Platform.MacOS.MacOSOverlayWindow.MoveTo(native, Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
				}
				return;
			}
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

				if (!TryGetLinuxTargetWorkArea(out int areaLeft, out int areaTop, out int areaWidth, out int areaHeight)
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

		private static bool TryGetLinuxTargetWorkArea(out int left, out int top, out int width, out int height)
		{
			Voidstrap.Platform.Linux.LinuxWindowGeometry runtime = Voidstrap.Platform.Linux.LinuxWindowInterop.FindRuntimeWindow();
			if (runtime.Valid
				&& runtime.Width > 0
				&& runtime.Height > 0
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetMonitorWorkAreaAt(runtime.Left + runtime.Width / 2, runtime.Top + runtime.Height / 2, out left, out top, out width, out height)
				&& width > 0
				&& height > 0)
				return true;

			if (Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPrimaryScreen(out _, out _, out _, out _, out left, out top, out width, out height)
				&& width > 0
				&& height > 0)
				return true;

			return Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWorkArea(out left, out top, out width, out height);
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
                Voidstrap.Integrations.Overlays.LinuxOverlaySurface.MakeClickThrough(this);
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
			StopLinuxSync();
            SourceInitialized -= Window_SourceInitialized;
            Closed -= Window_Closed;
			SizeChanged -= Window_SizeChanged;
            _lifetimeCts.Cancel();
            _queue.Clear();
            RootTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            NotificationBorder.BeginAnimation(OpacityProperty, null);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			BeginAnimation(LinuxFadeProperty, null);
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
