using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Crosshair
{
    public partial class CrosshairWindow : Window
    {
        private const double CanvasSize = 128;
        private const double CanvasCenter = CanvasSize / 2;
		private const string ResourceKey = "CrosshairWindow";
		private static readonly object InstanceGate = new();
		private static CrosshairWindow? _activeInstance;
		private static int _reconcilePending;

        private readonly RobloxOverlayAnchor _anchor;

        private Image? _imageCrosshair;
		private Image? _shapeCrosshair;
		private (int Shape, string? Fill, string? Outline, int Size, int Gap, int Thickness, double Opacity, double Dpi)? _shapeKey;
        private string? _imagePath;
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
			RefreshCrosshair();
        }

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			base.OnDpiChanged(oldDpi, newDpi);
			RefreshCrosshair();
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
                if (child is not Image image)
                    continue;

                double left = Canvas.GetLeft(image);
                double top = Canvas.GetTop(image);
                double width = image.Width;
                double height = image.Height;
                if (double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
                    continue;

				TryAddImageAlphaShape(image, left, top, width, height, scaleX, scaleY, rectangles);
            }
            return rectangles;
        }

		private void TryAddImageAlphaShape(
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
				return;

			BitmapSource first = sources[0];
			if (first.PixelWidth <= 0 || first.PixelHeight <= 0)
				return;

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
				BitmapSource bitmap = candidate.Format == PixelFormats.Bgra32 || candidate.Format == PixelFormats.Pbgra32
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
            Closed -= OnCrosshairClosed;
            ReleaseImageCrosshair();
			ReleaseShapeCrosshair();
            CrosshairCanvas.Children.Clear();
			if (Application.Current?.Resources[ResourceKey] is CrosshairWindow crosshair && ReferenceEquals(crosshair, this))
				Application.Current.Resources.Remove(ResourceKey);
        }

        private void MakeWindowClickThrough()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
            {
                Voidstrap.Integrations.Overlays.LinuxOverlaySurface.MakeClickThrough(this);
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
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
                ReleaseImageCrosshair();
				ReleaseShapeCrosshair();
                CrosshairCanvas.Children.Clear();
            }
        }

        private void UpdateCrosshair()
        {
			var settings = App.Settings.Prop;
			int selectedShape = Math.Clamp(settings.CrosshairShapeIndex, 0, 3);
			double opacity = Math.Clamp(settings.CrosshairOpacity, 0.0, 1.0);

			if (selectedShape != 3)
			{
				ReleaseImageCrosshair();
				ShowShape(selectedShape, settings.CrosshairColorHex, settings.CrosshairOutlineColorHex, settings.CrosshairSize, settings.CrosshairGap, settings.CrosshairLineThickness, opacity);
				return;
			}

			ReleaseShapeCrosshair();
			string? path = settings.CrosshairImagePath;
			if (string.IsNullOrWhiteSpace(path))
			{
				ReleaseImageCrosshair();
				CrosshairCanvas.Children.Clear();
				return;
			}

			if (_imageCrosshair == null)
			{
				_imageCrosshair = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
				RenderOptions.SetBitmapScalingMode(_imageCrosshair, BitmapScalingMode.HighQuality);
			}

			if (!string.Equals(_imagePath, path, StringComparison.OrdinalIgnoreCase))
			{
				CancelImageLoad();
				_imagePath = path;
				_imageShapeSources = Array.Empty<BitmapSource>();
				Voidstrap.UI.GifImageBehavior.SetFrames(_imageCrosshair, Array.Empty<(BitmapSource, int)>());
				if (IsGifSource(path))
					BeginGifLoad(_imageCrosshair, path);
				else
					BeginImageLoad(_imageCrosshair, path);
			}

			double size = Math.Clamp(settings.CrosshairSize, 2, Voidstrap.UI.CrosshairRenderer.MaxSize) * OverlayCrosshair.RuntimeScale;
			_imageCrosshair.Width = size;
			_imageCrosshair.Height = size;
			_imageCrosshair.Opacity = opacity;
			Canvas.SetLeft(_imageCrosshair, CanvasCenter - size / 2);
			Canvas.SetTop(_imageCrosshair, CanvasCenter - size / 2);
			ShowOnly(_imageCrosshair);
        }

		private void ShowShape(int shape, string? fill, string? outline, int size, int gap, int thickness, double opacity)
		{
			double dpi = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
			var key = (shape, fill, outline, size, gap, thickness, opacity, dpi);
			if (_shapeCrosshair == null)
			{
				_shapeCrosshair = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Width = CanvasSize, Height = CanvasSize, UseLayoutRounding = true };
				Canvas.SetLeft(_shapeCrosshair, 0);
				Canvas.SetTop(_shapeCrosshair, 0);
			}
			if (_shapeKey != key)
			{
				int dimension = (int)Math.Round(CanvasSize * dpi);
				_shapeCrosshair.Source = Voidstrap.UI.CrosshairRenderer.Render(dimension, OverlayCrosshair.RuntimeScale * dpi, shape, fill, outline, size, gap, thickness, opacity);
				_shapeKey = key;
			}
			ShowOnly(_shapeCrosshair);
		}

		private void ShowOnly(Image image)
		{
			if (CrosshairCanvas.Children.Count == 1 && ReferenceEquals(CrosshairCanvas.Children[0], image))
				return;
			CrosshairCanvas.Children.Clear();
			CrosshairCanvas.Children.Add(image);
		}

		private void ReleaseShapeCrosshair()
		{
			if (_shapeCrosshair == null)
				return;
			CrosshairCanvas.Children.Remove(_shapeCrosshair);
			_shapeCrosshair.Source = null;
			_shapeCrosshair = null;
			_shapeKey = null;
		}

        private void ReleaseImageCrosshair()
        {
			CancelImageLoad();
            if (_imageCrosshair == null)
                return;
			CrosshairCanvas.Children.Remove(_imageCrosshair);
			Voidstrap.UI.GifImageBehavior.SetFrames(_imageCrosshair, Array.Empty<(BitmapSource, int)>());
			_imageShapeSources = Array.Empty<BitmapSource>();
            _imageCrosshair = null;
            _imagePath = null;
        }

		private async void BeginGifLoad(Image image, string path)
		{
			(int generation, CancellationToken token) = StartImageLoad();
			try
			{
				byte[]? bytes = await Voidstrap.Utility.AppImage.LoadBytesAsync(path, token).ConfigureAwait(false);
				if (bytes == null || _disposed || generation != _imageLoadGeneration)
					return;
				IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames = await Task.Run(
					() => DecodeGifFrames(bytes, token),
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

		private async void BeginImageLoad(Image image, string path)
		{
			(int generation, CancellationToken token) = StartImageLoad();
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

		private (int Generation, CancellationToken Token) StartImageLoad()
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

		private static IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> DecodeGifFrames(byte[] bytes, CancellationToken token)
		{
			IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames = Voidstrap.Utility.SafeImaging.DecodeAnimationPortable(bytes, 256, token);
			if (frames.Count > 0 || !Voidstrap.Utility.Platform.IsWindows)
				return frames;

			using MemoryStream stream = new(bytes);
			GifBitmapDecoder decoder = new(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			if (decoder.Frames.Count == 0)
				return frames;
			BitmapFrame first = decoder.Frames[0];
			List<(BitmapSource, int)> decoded = new(decoder.Frames.Count);
			foreach (BitmapFrame frame in decoder.Frames)
			{
				token.ThrowIfCancellationRequested();
				if (frame.PixelWidth != first.PixelWidth || frame.PixelHeight != first.PixelHeight || decoded.Count >= 120)
					break;
				int delay = 100;
				if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay") && metadata.GetQuery("/grctlext/Delay") is ushort centiseconds && centiseconds > 1)
					delay = centiseconds * 10;
				BitmapSource converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
				converted.Freeze();
				decoded.Add((converted, delay));
			}
			return decoded;
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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongA")] private static partial int GetWindowLong(IntPtr hwnd, int index);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongA")] private static partial int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
