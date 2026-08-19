using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Crosshair
{
    public partial class CrosshairWindow : Window
    {
        private const double CanvasSize = 512;
        private const double CanvasCenter = CanvasSize / 2;

        private readonly ModsViewModel _viewModel;
        private readonly RobloxOverlayAnchor _anchor;

        private Image? _imageCrosshair;
        private MediaElement? _gifCrosshair;
        private string? _imagePath;
        private string? _gifPath;
        private bool _disposed;
		private int _refreshPending;

        public CrosshairWindow(ModsViewModel vm)
        {
            InitializeComponent();

            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
			ShowActivated = false;
            Width = CanvasSize;
            Height = CanvasSize;

            Loaded += OnCrosshairLoaded;
            IsVisibleChanged += OnCrosshairVisibilityChanged;
            Closed += OnCrosshairClosed;

            _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center);

            RefreshCrosshair();
            Show();
        }

        private void OnCrosshairLoaded(object? sender, RoutedEventArgs e) => MakeWindowClickThrough();

        private void OnCrosshairClosed(object? sender, EventArgs e)
        {
            _disposed = true;
            _anchor.Dispose();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Loaded -= OnCrosshairLoaded;
            IsVisibleChanged -= OnCrosshairVisibilityChanged;
            Closed -= OnCrosshairClosed;
            ReleaseGifCrosshair();
            ReleaseImageCrosshair();
            CrosshairCanvas.Children.Clear();
            if (Application.Current?.Resources["CrosshairWindow"] is CrosshairWindow crosshair && ReferenceEquals(crosshair, this))
                Application.Current.Resources.Remove("CrosshairWindow");
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
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
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

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
			if (!IsCrosshairProperty(e.PropertyName))
				return;
			if (Interlocked.Exchange(ref _refreshPending, 1) != 0)
				return;
			try
            {
				Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(FlushCrosshairRefresh));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _refreshPending, 0);
            }
        }

		private static bool IsCrosshairProperty(string? propertyName)
		{
			return propertyName == null
				|| propertyName == nameof(ModsViewModel.SelectedShape)
				|| propertyName == nameof(ModsViewModel.ImageUrl)
				|| propertyName == nameof(ModsViewModel.CursorColorHex)
				|| propertyName == nameof(ModsViewModel.CursorOutlineColorHex)
				|| propertyName == nameof(ModsViewModel.CursorSize)
				|| propertyName == nameof(ModsViewModel.CrosshairThickness)
				|| propertyName == nameof(ModsViewModel.Gap)
				|| propertyName == nameof(ModsViewModel.CursorOpacity);
		}

		private void FlushCrosshairRefresh()
		{
			Interlocked.Exchange(ref _refreshPending, 0);
			RefreshCrosshair();
		}

        private void RefreshCrosshair()
        {
            if (_disposed)
                return;
            try
            {
                UpdateCrosshair();
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
            string? selectedPath = _viewModel.SelectedShape == ModsViewModel.CrosshairShape.Image ? _viewModel.ImageUrl : null;
            bool wantsGif = !string.IsNullOrWhiteSpace(selectedPath) && selectedPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
            bool wantsImage = !string.IsNullOrWhiteSpace(selectedPath) && !wantsGif;
            if (!wantsGif)
                ReleaseGifCrosshair();
            if (!wantsImage)
                ReleaseImageCrosshair();
            CrosshairCanvas.Children.Clear();

            double centerX = CanvasCenter;
            double centerY = CanvasCenter;

            double scale = 0.75;
            double size = _viewModel.CursorSize * scale;
            double gap = _viewModel.Gap * scale;
            double thickness = Math.Max(1, _viewModel.CrosshairThickness * scale);
            double opacity = Math.Clamp(_viewModel.CursorOpacity, 0.05, 1.0);

            var mainBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_viewModel.CursorColorHex))
            { Opacity = opacity };
			mainBrush.Freeze();

            var outlineBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_viewModel.CursorOutlineColorHex))
            { Opacity = opacity };
			outlineBrush.Freeze();

            switch (_viewModel.SelectedShape)
            {
                case ModsViewModel.CrosshairShape.Image:
                    {
                        if (string.IsNullOrWhiteSpace(_viewModel.ImageUrl))
                            return;

                        string path = selectedPath;
                        bool isGif = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

                        if (isGif)
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
                                _imageCrosshair.Source = LoadBitmap(path);
                                _imagePath = path;
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

                case ModsViewModel.CrosshairShape.Cross:
                    DrawCross(centerX, centerY, size, gap, thickness, outlineBrush, true);
                    DrawCross(centerX, centerY, size, gap, thickness, mainBrush, false);
                    break;

                case ModsViewModel.CrosshairShape.Dot:
                    DrawEllipse(centerX, centerY, size / 3 + 2, outlineBrush);
                    DrawEllipse(centerX, centerY, size / 3, mainBrush);
                    break;

                case ModsViewModel.CrosshairShape.Circle:
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
            if (_imageCrosshair == null)
                return;
            _imageCrosshair.Source = null;
            _imageCrosshair = null;
            _imagePath = null;
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
