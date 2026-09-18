using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Crosshair
{
    public partial class CrosshairWindow : Window
    {
        private const double CanvasSize = 512;
        private const double CanvasCenter = CanvasSize / 2;
		private const string ResourceKey = "CrosshairWindow";
		private static readonly object InstanceGate = new();
		private static CrosshairWindow? _activeInstance;
		private static int _reconcilePending;

        private readonly RobloxOverlayAnchor _anchor;

        private Image? _imageCrosshair;
        private MediaElement? _gifCrosshair;
        private string? _imagePath;
        private string? _gifPath;
		private IReadOnlyList<BitmapSource> _imageShapeSources = Array.Empty<BitmapSource>();
		private int _imageLoadGeneration;
		private CancellationTokenSource? _imageLoadCancellation;
        private bool _disposed;

		internal static CrosshairWindow GetOrCreate()
		{
			Application application = Application.Current ?? throw new InvalidOperationException("The application is unavailable");
			if (!application.Dispatcher.CheckAccess())
				return application.Dispatcher.Invoke(GetOrCreate);

			CrosshairWindow instance;
			lock (InstanceGate)
			{
				if (_activeInstance is { _disposed: false } active)
					instance = active;
				else
					instance = new CrosshairWindow();
				application.Resources[ResourceKey] = instance;
			}
			CloseDuplicateWindows(application, instance);
			return instance;
		}

		internal static void Reconcile()
		{
			Application? application = Application.Current;
			if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
				return;
			if (!application.Dispatcher.CheckAccess())
			{
				if (Interlocked.Exchange(ref _reconcilePending, 1) != 0)
					return;
				try
				{
					application.Dispatcher.BeginInvoke(new Action(ReconcileCore));
				}
				catch (InvalidOperationException)
				{
					Interlocked.Exchange(ref _reconcilePending, 0);
				}
				return;
			}
			ReconcileCore();
		}

		private static void ReconcileCore()
		{
			Interlocked.Exchange(ref _reconcilePending, 0);
			bool wanted = OverlayCrosshair.IsEnabled() && !OverlayHub.CompositorCrosshairActive;
			if (!wanted)
			{
				CloseAll();
				return;
			}
			GetOrCreate().RefreshSettings();
		}

		internal static void CloseAll()
		{
			Application? application = Application.Current;
			if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
				return;
			if (!application.Dispatcher.CheckAccess())
			{
				application.Dispatcher.BeginInvoke(new Action(CloseAll));
				return;
			}

			List<CrosshairWindow> windows = GetLiveWindows(application);
			lock (InstanceGate)
				_activeInstance = null;
			if (application.Resources[ResourceKey] is CrosshairWindow)
				application.Resources.Remove(ResourceKey);
			foreach (CrosshairWindow window in windows)
			{
				try
				{
					window.Close();
				}
				catch (InvalidOperationException)
				{
				}
			}
		}

		internal static int CountLiveInstances()
		{
			Application? application = Application.Current;
			if (application == null || !application.Dispatcher.CheckAccess())
				return 0;
			return GetLiveWindows(application).Count;
		}

		private static List<CrosshairWindow> GetLiveWindows(Application application)
		{
			List<CrosshairWindow> windows = new();
			foreach (Window window in application.Windows)
			{
				if (window is CrosshairWindow crosshair && !crosshair._disposed)
					windows.Add(crosshair);
			}
			return windows;
		}

		private static void CloseDuplicateWindows(Application application, CrosshairWindow active)
		{
			foreach (CrosshairWindow window in GetLiveWindows(application))
			{
				if (ReferenceEquals(window, active))
					continue;
				try
				{
					window.Close();
				}
				catch (InvalidOperationException)
				{
				}
			}
		}

		private CrosshairWindow()
        {
			lock (InstanceGate)
			{
				if (_activeInstance is { _disposed: false })
					throw new InvalidOperationException("A crosshair surface is already active");
				_activeInstance = this;
			}

			try
			{
            InitializeComponent();
            Voidstrap.Integrations.Overlays.LinuxOverlaySurface.ReleaseMainWindowClaim(this);

            Title = "Voidstrap Crosshair";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
			ShowActivated = false;
            Width = CanvasSize;
            Height = CanvasSize;
			if (Voidstrap.Utility.Platform.IsLinux)
				Opacity = 0;

            Loaded += OnCrosshairLoaded;
            IsVisibleChanged += OnCrosshairVisibilityChanged;
            Closed += OnCrosshairClosed;

            _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center);

            RefreshCrosshair();
            Show();
			}
			catch
			{
				lock (InstanceGate)
				{
					if (ReferenceEquals(_activeInstance, this))
						_activeInstance = null;
				}
				throw;
			}
        }

        private void OnCrosshairLoaded(object? sender, RoutedEventArgs e)
        {
            MakeWindowClickThrough();
			_anchor.Refresh();
        }

		internal void SetLinuxPresentation(bool visible)
		{
			if (!Voidstrap.Utility.Platform.IsLinux || _disposed)
				return;

			nint handle = ResolveLinuxHandle();
			if (visible && ApplyLinuxShape())
			{
				Opacity = 1;
				Voidstrap.Integrations.Overlays.LinuxOverlaySurface.WakePresentation(this);
				return;
			}

			Opacity = 0;
			if (handle != 0)
				Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetInvisible(handle);
		}

        internal bool ApplyLinuxShape()
        {
            if (!Voidstrap.Utility.Platform.IsLinux || _disposed)
                return false;

            try
            {
                nint handle = ResolveLinuxHandle();
                if (handle == 0)
                    return false;

                if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int deviceWidth, out int deviceHeight)
                    || deviceWidth <= 0
                    || deviceHeight <= 0)
                    return false;

                double scaleX = deviceWidth / CanvasSize;
                double scaleY = deviceHeight / CanvasSize;
                System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles = BuildShapeRectangles(scaleX, scaleY);
                if (rectangles.Count == 0)
                {
					Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetInvisible(handle);
					return false;
				}

				return Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetBoundingRectangles(handle, rectangles);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("CrosshairWindow::ApplyLinuxShape", "The crosshair shape could not be applied: " + ex.Message);
				return false;
            }
        }

        private nint ResolveLinuxHandle()
        {
            try
            {
                nint handle = new WindowInteropHelper(this).Handle;
                if (handle != 0 && Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle))
                    return handle;
            }
            catch (Exception)
            {
            }

            return string.IsNullOrWhiteSpace(Title)
                ? 0
                : Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(Title);
        }

        private System.Collections.Generic.List<(int X, int Y, int Width, int Height)> BuildShapeRectangles(double scaleX, double scaleY)
        {
            System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles = new();
            foreach (UIElement child in CrosshairCanvas.Children)
            {
                if (child is not FrameworkElement element)
                    continue;
				if (element is System.Windows.Shapes.Line)
					continue;

                double left = Canvas.GetLeft(element);
                double top = Canvas.GetTop(element);
                if (double.IsNaN(left) || double.IsNaN(top))
                {
                    AddShapeFromRenderBounds(element, scaleX, scaleY, rectangles);
                    continue;
                }

                double width = element.Width;
                double height = element.Height;
                if (double.IsNaN(width) || width <= 0)
                    width = element.ActualWidth;
                if (double.IsNaN(height) || height <= 0)
                    height = element.ActualHeight;
                if (width <= 0 || height <= 0)
                    continue;

				if (element is Image image && TryAddImageAlphaShape(image, left, top, width, height, scaleX, scaleY, rectangles))
					continue;
				if (element is System.Windows.Shapes.Ellipse ellipse)
					AddEllipseBands(ellipse, left, top, width, height, scaleX, scaleY, rectangles);
                else
                    AddScaledRect(left, top, width, height, scaleX, scaleY, rectangles);
            }

            AddLineShapes(scaleX, scaleY, rectangles);
            return rectangles;
        }

        private void AddLineShapes(
            double scaleX,
            double scaleY,
            System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
        {
            foreach (UIElement child in CrosshairCanvas.Children)
            {
                if (child is not System.Windows.Shapes.Line line)
                    continue;

                double thickness = Math.Max(1d, line.StrokeThickness);
				AddLineCapsule(line.X1, line.Y1, line.X2, line.Y2, thickness, scaleX, scaleY, rectangles);
            }
        }

		private static void AddLineCapsule(
			double x1,
			double y1,
			double x2,
			double y2,
			double thickness,
			double scaleX,
			double scaleY,
			System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
		{
			double radius = Math.Max(0.5d, thickness / 2d);
			if (Math.Abs(y2 - y1) <= 0.001d)
			{
				double left = Math.Min(x1, x2);
				double right = Math.Max(x1, x2);
				if (right > left)
					AddScaledRect(left, y1 - radius, right - left, radius * 2d, scaleX, scaleY, rectangles);
				AddFilledEllipseBands(left - radius, y1 - radius, radius * 2d, radius * 2d, scaleX, scaleY, rectangles);
				if (right > left)
					AddFilledEllipseBands(right - radius, y1 - radius, radius * 2d, radius * 2d, scaleX, scaleY, rectangles);
				return;
			}

			if (Math.Abs(x2 - x1) <= 0.001d)
			{
				double top = Math.Min(y1, y2);
				double bottom = Math.Max(y1, y2);
				if (bottom > top)
					AddScaledRect(x1 - radius, top, radius * 2d, bottom - top, scaleX, scaleY, rectangles);
				AddFilledEllipseBands(x1 - radius, top - radius, radius * 2d, radius * 2d, scaleX, scaleY, rectangles);
				if (bottom > top)
					AddFilledEllipseBands(x1 - radius, bottom - radius, radius * 2d, radius * 2d, scaleX, scaleY, rectangles);
				return;
			}

			double leftBound = Math.Min(x1, x2) - radius;
			double topBound = Math.Min(y1, y2) - radius;
			double rightBound = Math.Max(x1, x2) + radius;
			double bottomBound = Math.Max(y1, y2) + radius;
			AddScaledRect(leftBound, topBound, rightBound - leftBound, bottomBound - topBound, scaleX, scaleY, rectangles);
		}

        private static void AddShapeFromRenderBounds(
            FrameworkElement element,
            double scaleX,
            double scaleY,
            System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return;

            AddScaledRect(0d, 0d, element.ActualWidth, element.ActualHeight, scaleX, scaleY, rectangles);
        }

		private bool TryAddImageAlphaShape(
			Image image,
			double left,
			double top,
			double width,
			double height,
			double scaleX,
			double scaleY,
			System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
		{
			IReadOnlyList<BitmapSource> sources = _imageShapeSources.Count > 0
				? _imageShapeSources
				: image.Source is BitmapSource source ? new[] { source } : Array.Empty<BitmapSource>();
			if (sources.Count == 0)
				return true;

			BitmapSource first = sources[0];
			if (first.PixelWidth <= 0 || first.PixelHeight <= 0)
				return true;

			double fit = Math.Min(width / first.PixelWidth, height / first.PixelHeight);
			double contentWidth = first.PixelWidth * fit;
			double contentHeight = first.PixelHeight * fit;
			double contentLeft = left + (width - contentWidth) / 2d;
			double contentTop = top + (height - contentHeight) / 2d;
			int targetLeft = (int)Math.Floor(contentLeft * scaleX);
			int targetTop = (int)Math.Floor(contentTop * scaleY);
			int targetWidth = Math.Max(1, (int)Math.Ceiling(contentWidth * scaleX));
			int targetHeight = Math.Max(1, (int)Math.Ceiling(contentHeight * scaleY));
			bool[] visible = new bool[targetWidth * targetHeight];

			foreach (BitmapSource candidate in sources)
			{
				BitmapSource bitmap = candidate.Format == PixelFormats.Bgra32
					? candidate
					: new FormatConvertedBitmap(candidate, PixelFormats.Bgra32, null, 0);
				int stride = checked(bitmap.PixelWidth * 4);
				byte[] pixels = new byte[checked(stride * bitmap.PixelHeight)];
				bitmap.CopyPixels(pixels, stride, 0);
				for (int y = 0; y < targetHeight; y++)
				{
					int sourceY = Math.Clamp((int)((y + 0.5d) * bitmap.PixelHeight / targetHeight), 0, bitmap.PixelHeight - 1);
					for (int x = 0; x < targetWidth; x++)
					{
						int sourceX = Math.Clamp((int)((x + 0.5d) * bitmap.PixelWidth / targetWidth), 0, bitmap.PixelWidth - 1);
						if (pixels[sourceY * stride + sourceX * 4 + 3] >= 32)
							visible[y * targetWidth + x] = true;
					}
				}
			}

			for (int y = 0; y < targetHeight; y++)
			{
				int x = 0;
				while (x < targetWidth)
				{
					while (x < targetWidth && !visible[y * targetWidth + x])
						x++;
					int start = x;
					while (x < targetWidth && visible[y * targetWidth + x])
						x++;
					if (x > start)
						rectangles.Add((targetLeft + start, targetTop + y, x - start, 1));
				}
			}
			return true;
		}

        private static void AddEllipseBands(
			System.Windows.Shapes.Ellipse ellipse,
            double left,
            double top,
            double width,
            double height,
            double scaleX,
            double scaleY,
            System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
        {
			if (ellipse.Fill != null || ellipse.Stroke == null || ellipse.StrokeThickness <= 0)
			{
				AddFilledEllipseBands(left, top, width, height, scaleX, scaleY, rectangles);
				return;
			}

            double radiusX = width / 2d;
            double radiusY = height / 2d;
            double centreX = left + radiusX;
            double centreY = top + radiusY;
            int bands = Math.Clamp((int)Math.Round(height), 4, 48);
            double step = height / bands;
            for (int index = 0; index < bands; index++)
            {
                double bandTop = top + index * step;
                double sample = bandTop + step / 2d;
                double normalised = (sample - centreY) / radiusY;
                if (Math.Abs(normalised) >= 1d)
                    continue;

                double half = radiusX * Math.Sqrt(1d - normalised * normalised);
				double innerRadiusX = Math.Max(0d, radiusX - ellipse.StrokeThickness);
				double innerRadiusY = Math.Max(0d, radiusY - ellipse.StrokeThickness);
				double innerHalf = 0d;
				if (innerRadiusX > 0d && innerRadiusY > 0d)
				{
					double innerNormalised = (sample - centreY) / innerRadiusY;
					if (Math.Abs(innerNormalised) < 1d)
						innerHalf = innerRadiusX * Math.Sqrt(1d - innerNormalised * innerNormalised);
				}
				if (innerHalf <= 0d)
				{
					AddScaledRect(centreX - half, bandTop, half * 2d, step, scaleX, scaleY, rectangles);
					continue;
				}
				AddScaledRect(centreX - half, bandTop, half - innerHalf, step, scaleX, scaleY, rectangles);
				AddScaledRect(centreX + innerHalf, bandTop, half - innerHalf, step, scaleX, scaleY, rectangles);
            }
        }

		private static void AddFilledEllipseBands(
			double left,
			double top,
			double width,
			double height,
			double scaleX,
			double scaleY,
			System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
		{
			if (width <= 0d || height <= 0d)
				return;
			double radiusX = width / 2d;
			double radiusY = height / 2d;
			double centreX = left + radiusX;
			double centreY = top + radiusY;
			int bands = Math.Clamp((int)Math.Ceiling(height * Math.Max(1d, scaleY)), 1, 64);
			double step = height / bands;
			for (int index = 0; index < bands; index++)
			{
				double bandTop = top + index * step;
				double sample = bandTop + step / 2d;
				double normalised = (sample - centreY) / radiusY;
				if (Math.Abs(normalised) >= 1d)
					continue;
				double half = radiusX * Math.Sqrt(1d - normalised * normalised);
				AddScaledRect(centreX - half, bandTop, half * 2d, step, scaleX, scaleY, rectangles);
			}
		}

        private static void AddScaledRect(
            double left,
            double top,
            double width,
            double height,
            double scaleX,
            double scaleY,
            System.Collections.Generic.List<(int X, int Y, int Width, int Height)> rectangles)
        {
			int x = (int)Math.Floor(left * scaleX);
			int y = (int)Math.Floor(top * scaleY);
			int right = (int)Math.Ceiling((left + width) * scaleX);
			int bottom = (int)Math.Ceiling((top + height) * scaleY);
			int w = right - x;
			int h = bottom - y;
            if (w <= 0 || h <= 0)
                return;

            rectangles.Add((x, y, w, h));
        }

        private void OnCrosshairClosed(object? sender, EventArgs e)
        {
            _disposed = true;
			lock (InstanceGate)
			{
				if (ReferenceEquals(_activeInstance, this))
					_activeInstance = null;
			}
            _anchor.Dispose();
            Loaded -= OnCrosshairLoaded;
            IsVisibleChanged -= OnCrosshairVisibilityChanged;
            Closed -= OnCrosshairClosed;
            ReleaseGifCrosshair();
            ReleaseImageCrosshair();
            CrosshairCanvas.Children.Clear();
			if (Application.Current?.Resources[ResourceKey] is CrosshairWindow crosshair && ReferenceEquals(crosshair, this))
				Application.Current.Resources.Remove(ResourceKey);
        }

        private void MakeWindowClickThrough()
        {
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                Voidstrap.Integrations.Overlays.LinuxOverlaySurface.MakeClickThrough(this);
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private void OnCrosshairVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_gifCrosshair == null)
                return;
            if (e.NewValue is true)
                _gifCrosshair.Play();
            else
                _gifCrosshair.Pause();
        }

		internal void RefreshSettings() => RefreshCrosshair();

        private void RefreshCrosshair()
        {
            if (_disposed)
                return;
            try
            {
                UpdateCrosshair();
				_anchor.Refresh();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("CrosshairWindow::Update", ex);
                ReleaseGifCrosshair();
                ReleaseImageCrosshair();
                CrosshairCanvas.Children.Clear();
            }
        }

        private void UpdateCrosshair()
        {
			var settings = App.Settings.Prop;
			int selectedShape = Math.Clamp(settings.CrosshairShapeIndex, 0, 3);
            string? selectedPath = selectedShape == 3 ? settings.CrosshairImagePath : null;
			bool wantsGif = IsGifSource(selectedPath);
			bool wantsPortableGif = wantsGif && (Voidstrap.Utility.Platform.IsLinux || IsInlineDataSource(selectedPath));
            bool wantsImage = !string.IsNullOrWhiteSpace(selectedPath) && !wantsGif;
            if (!wantsGif || wantsPortableGif)
                ReleaseGifCrosshair();
            if (!wantsImage && !wantsPortableGif)
                ReleaseImageCrosshair();
            CrosshairCanvas.Children.Clear();

            double centerX = CanvasCenter;
            double centerY = CanvasCenter;

            double scale = OverlayCrosshair.RuntimeScale;
            double size = Math.Clamp(settings.CrosshairSize, 2, 60) * scale;
            double gap = Math.Clamp(settings.CrosshairGap, 0, 40) * scale;
            double thickness = Math.Max(1, Math.Clamp(settings.CrosshairLineThickness, 1, 16) * scale);
            double opacity = Math.Clamp(settings.CrosshairOpacity, 0.05, 1.0);

            var mainBrush = CreateBrush(settings.CrosshairColorHex, Colors.Lime, opacity);
			mainBrush.Freeze();

            var outlineBrush = CreateBrush(settings.CrosshairOutlineColorHex, Colors.Black, opacity);
			outlineBrush.Freeze();

            switch (selectedShape)
            {
                case 3:
                    {
                        if (string.IsNullOrWhiteSpace(selectedPath))
                            return;

                        string path = selectedPath;
                        bool isGif = IsGifSource(path);

                        if (isGif && !wantsPortableGif)
                        {
                            if (_gifCrosshair == null)
                            {
                                _gifCrosshair = new MediaElement
                                {
                                    LoadedBehavior = MediaState.Manual,
                                    UnloadedBehavior = MediaState.Manual,
                                    IsMuted = true,
                                    Stretch = Stretch.Uniform,
                                    Opacity = opacity,
                                    IsHitTestVisible = false,
                                    ScrubbingEnabled = true
                                };

                                _gifCrosshair.MediaOpened += OnGifRestart;
                                _gifCrosshair.MediaEnded += OnGifRestart;
                            }

                            if (!string.Equals(_gifPath, path, StringComparison.OrdinalIgnoreCase))
                            {
                                _gifCrosshair.Stop();
                                _gifCrosshair.Close();
                                _gifCrosshair.Source = new Uri(path, UriKind.Absolute);
                                _gifCrosshair.Position = TimeSpan.Zero;
                                _gifCrosshair.Play();
                                _gifPath = path;
                            }
                            _gifCrosshair.Width = size;
                            _gifCrosshair.Height = size;
                            _gifCrosshair.Opacity = opacity;

                            Canvas.SetLeft(_gifCrosshair, centerX - size / 2);
                            Canvas.SetTop(_gifCrosshair, centerY - size / 2);

                            CrosshairCanvas.Children.Add(_gifCrosshair);
                        }
                        else
                        {
                            if (_imageCrosshair == null)
                            {
                                _imageCrosshair = new Image
                                {
                                    Stretch = Stretch.Uniform
                                };

                                RenderOptions.SetBitmapScalingMode(
                                    _imageCrosshair,
                                    BitmapScalingMode.HighQuality);
                            }

                            if (!string.Equals(_imagePath, path, StringComparison.OrdinalIgnoreCase))
                            {
								CancelImageLoad();
								_imagePath = path;
								_imageShapeSources = Array.Empty<BitmapSource>();
								if (wantsPortableGif)
								{
									Voidstrap.UI.GifImageBehavior.SetSourcePath(_imageCrosshair, string.Empty);
									BeginPortableGifLoad(_imageCrosshair, path);
								}
								else
								{
									Voidstrap.UI.GifImageBehavior.SetSourcePath(_imageCrosshair, string.Empty);
									if (Voidstrap.Utility.Platform.IsLinux && !File.Exists(path))
										BeginPortableImageLoad(_imageCrosshair, path);
									else
									{
										_imageCrosshair.Source = LoadBitmap(path);
										_imageShapeSources = _imageCrosshair.Source is BitmapSource source
											? new[] { source }
											: Array.Empty<BitmapSource>();
									}
								}
                            }
                            _imageCrosshair.Width = size;
                            _imageCrosshair.Height = size;
                            _imageCrosshair.Opacity = opacity;

                            Canvas.SetLeft(_imageCrosshair, centerX - size / 2);
                            Canvas.SetTop(_imageCrosshair, centerY - size / 2);

                            CrosshairCanvas.Children.Add(_imageCrosshair);
                        }

                        break;
                    }

                case 0:
                    DrawCross(centerX, centerY, size, gap, thickness, outlineBrush, true);
                    DrawCross(centerX, centerY, size, gap, thickness, mainBrush, false);
                    break;

                case 1:
                    DrawEllipse(centerX, centerY, size / 3 + 2, outlineBrush);
                    DrawEllipse(centerX, centerY, size / 3, mainBrush);
                    break;

                case 2:
                    DrawCircle(centerX, centerY, size / 2, thickness + 2, outlineBrush);
                    DrawCircle(centerX, centerY, size / 2 - 2, thickness, mainBrush);
                    break;
            }
        }

        private void OnGifRestart(object? sender, RoutedEventArgs e)
        {
            if (_gifCrosshair == null || !IsVisible)
                return;
            _gifCrosshair.Position = TimeSpan.Zero;
            _gifCrosshair.Play();
        }

        private void ReleaseGifCrosshair()
        {
            if (_gifCrosshair == null)
                return;
            _gifCrosshair.MediaOpened -= OnGifRestart;
            _gifCrosshair.MediaEnded -= OnGifRestart;
            try
            {
                _gifCrosshair.Stop();
                _gifCrosshair.Close();
            }
            catch
            {
            }
            _gifCrosshair.Source = null;
            _gifCrosshair = null;
            _gifPath = null;
        }

        private void ReleaseImageCrosshair()
        {
			CancelImageLoad();
            if (_imageCrosshair == null)
                return;
			Voidstrap.UI.GifImageBehavior.SetSourcePath(_imageCrosshair, string.Empty);
            _imageCrosshair.Source = null;
			_imageShapeSources = Array.Empty<BitmapSource>();
            _imageCrosshair = null;
            _imagePath = null;
        }

		private async void BeginPortableGifLoad(Image image, string path)
		{
			(int generation, CancellationToken token) = BeginImageLoad();
			try
			{
				byte[]? bytes = await Voidstrap.Utility.AppImage.LoadBytesAsync(path, token).ConfigureAwait(false);
				if (bytes == null || _disposed || generation != _imageLoadGeneration)
					return;
				IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames = await Task.Run(
					() => Voidstrap.Utility.SafeImaging.DecodeAnimationPortable(bytes, token: token),
					token).ConfigureAwait(false);
				if (frames.Count == 0 || _disposed || generation != _imageLoadGeneration)
					return;
				await Dispatcher.InvokeAsync(() =>
				{
					if (_disposed
						|| generation != _imageLoadGeneration
						|| !ReferenceEquals(image, _imageCrosshair)
						|| !string.Equals(path, _imagePath, StringComparison.OrdinalIgnoreCase))
						return;
					List<BitmapSource> shapeSources = new(frames.Count);
					foreach ((BitmapSource frame, _) in frames)
						shapeSources.Add(frame);
					_imageShapeSources = shapeSources;
					Voidstrap.UI.GifImageBehavior.SetFrames(image, frames);
					_anchor.Refresh();
				});
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				if (!_disposed && generation == _imageLoadGeneration)
					App.Logger.WriteLine("CrosshairWindow::Gif", "The custom GIF could not be loaded: " + ex.Message);
			}
		}

		private async void BeginPortableImageLoad(Image image, string path)
		{
			(int generation, CancellationToken token) = BeginImageLoad();
			try
			{
					BitmapSource? source = await Voidstrap.Utility.AppImage.LoadAsync(path, 128, token).ConfigureAwait(false);
				if (source == null || _disposed || generation != _imageLoadGeneration)
					return;
				await Dispatcher.InvokeAsync(() =>
				{
					if (_disposed
						|| generation != _imageLoadGeneration
						|| !ReferenceEquals(image, _imageCrosshair)
						|| !string.Equals(path, _imagePath, StringComparison.OrdinalIgnoreCase))
						return;
					image.Source = source;
					_imageShapeSources = new[] { source };
					_anchor.Refresh();
				});
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				if (!_disposed && generation == _imageLoadGeneration)
					App.Logger.WriteLine("CrosshairWindow::Image", "The custom image could not be loaded: " + ex.Message);
			}
		}

		private (int Generation, CancellationToken Token) BeginImageLoad()
		{
			CancellationTokenSource current = new();
			_imageLoadCancellation = current;
			return (_imageLoadGeneration, current.Token);
		}

		private void CancelImageLoad()
		{
			_imageLoadGeneration++;
			CancellationTokenSource? cancellation = _imageLoadCancellation;
			_imageLoadCancellation = null;
			if (cancellation == null)
				return;
			cancellation.Cancel();
			cancellation.Dispose();
		}

        private static BitmapSource? LoadBitmap(string path)
        {
			return Voidstrap.Utility.AppImage.LoadSync(path, 128);
        }

		private static bool IsGifSource(string? path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return false;
			if (path.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase))
				return true;
			if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
				return uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
			return path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsInlineDataSource(string? path)
		{
			return path?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true;
		}

        private void DrawCross(double cx, double cy, double size, double gap, double thickness, Brush brush, bool outline)
        {
            double t = outline ? thickness + 2 : thickness;

            DrawLine(cx - size, cy, cx - gap, cy, brush, t);
            DrawLine(cx + gap, cy, cx + size, cy, brush, t);
            DrawLine(cx, cy - size, cx, cy - gap, brush, t);
            DrawLine(cx, cy + gap, cx, cy + size, brush, t);
        }

        private void DrawLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
        {
            CrosshairCanvas.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        private void DrawEllipse(double cx, double cy, double radius, Brush fill)
        {
            var e = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = fill
            };

            Canvas.SetLeft(e, cx - radius);
            Canvas.SetTop(e, cy - radius);
            CrosshairCanvas.Children.Add(e);
        }

        private void DrawCircle(double cx, double cy, double radius, double thickness, Brush stroke)
        {
            if (radius <= 0)
                return;
            var e = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = stroke,
                StrokeThickness = thickness
            };

            Canvas.SetLeft(e, cx - radius);
            Canvas.SetTop(e, cy - radius);
            CrosshairCanvas.Children.Add(e);
        }

        private static SolidColorBrush CreateBrush(string? value, Color fallback, double opacity)
        {
            Color color = fallback;
            try
            {
                if (ColorConverter.ConvertFromString(value ?? string.Empty) is Color parsed)
                    color = parsed;
            }
            catch (Exception)
            {
            }
            return new SolidColorBrush(color) { Opacity = opacity };
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongA")] private static partial int GetWindowLong(IntPtr hwnd, int index);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongA")] private static partial int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
