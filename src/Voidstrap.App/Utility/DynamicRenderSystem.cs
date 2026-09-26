using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.Utility
{
    public static class DynamicRenderSystem
    {
        private const double PreloadMarginPx = 400.0;
        private const double FarReleaseMarginPx = 3000.0;
        private const int MaxDecodeWidth = 1024;
        private const int MaxCacheEntries = 192;
        private const long MaxCacheBytes = 40L * 1024 * 1024;
        private const long MaxDownloadBytes = 8L * 1024 * 1024;
        private const long MaxByteCacheBytes = 10L * 1024 * 1024;
        private static readonly int DecodeConcurrency = Platform.IsLinux
            ? Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
            : Math.Max(4, Environment.ProcessorCount);

        public static readonly DependencyProperty LazyImageSourceProperty = DependencyProperty.RegisterAttached(
            "LazyImageSource",
            typeof(string),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(null, OnLazyImageSourceChanged));

        public static readonly DependencyProperty LinuxImageSourceProperty = DependencyProperty.RegisterAttached(
            "LinuxImageSource",
            typeof(string),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(null, OnLinuxImageSourceChanged));

        public static readonly DependencyProperty LinuxImageBrushSourceProperty = DependencyProperty.RegisterAttached(
            "LinuxImageBrushSource",
            typeof(string),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(null, OnLinuxImageBrushSourceChanged));

        public static readonly DependencyProperty LinuxImageBytesProperty = DependencyProperty.RegisterAttached(
            "LinuxImageBytes",
            typeof(object),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(null, OnLinuxImageBytesChanged));

        public static readonly DependencyProperty BrushImageSourceProperty = DependencyProperty.RegisterAttached(
            "BrushImageSource",
            typeof(string),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(null, OnBrushImageSourceChanged));

        public static readonly DependencyProperty BrushDecodeWidthProperty = DependencyProperty.RegisterAttached(
            "BrushDecodeWidth",
            typeof(int),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(512));

        public static readonly DependencyProperty IsBrushLoadingProperty = DependencyProperty.RegisterAttached(
            "IsBrushLoading",
            typeof(bool),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(false));

        public static void SetIsBrushLoading(DependencyObject element, bool value) => element.SetValue(IsBrushLoadingProperty, value);

        public static bool GetIsBrushLoading(DependencyObject element) => (bool)element.GetValue(IsBrushLoadingProperty);

        public static void SetBrushImageSource(DependencyObject element, string? value) => element.SetValue(BrushImageSourceProperty, value);

        public static string? GetBrushImageSource(DependencyObject element) => element.GetValue(BrushImageSourceProperty) as string;

        public static void SetBrushDecodeWidth(DependencyObject element, int value) => element.SetValue(BrushDecodeWidthProperty, value);

        public static int GetBrushDecodeWidth(DependencyObject element) => (int)element.GetValue(BrushDecodeWidthProperty);

        private static void OnBrushImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is System.Windows.Controls.Border host)
            {
                ForwardBrushImageSource(host);
                return;
            }
            if (d is not ImageBrush brush)
                return;
            brush.ImageSource = null;
            PresentLinuxBrushImage(brush, null);
            string? uri = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(uri))
            {
                SetIsBrushLoading(brush, false);
                return;
            }
            int decodeWidth = GetBrushDecodeWidth(brush);
            BitmapSource? cached = CachePeek(CacheKey(uri, decodeWidth <= 0 ? 512 : decodeWidth));
            if (cached != null)
            {
                SetIsBrushLoading(brush, false);
                brush.ImageSource = cached;
                PresentLinuxBrushImage(brush, cached);
                return;
            }
            SetIsBrushLoading(brush, true);
            _ = LoadBrushImageAsync(brush, uri, decodeWidth);
        }

        private static void ForwardBrushImageSource(System.Windows.Controls.Border host)
        {
            if (host.Background is not ImageBrush brush || brush.IsFrozen)
            {
                host.Loaded -= OnBrushHostLoaded;
                host.Loaded += OnBrushHostLoaded;
                return;
            }
            if (host.ReadLocalValue(BrushDecodeWidthProperty) != DependencyProperty.UnsetValue)
                SetBrushDecodeWidth(brush, GetBrushDecodeWidth(host));
            if (Platform.IsLinux)
                BrushImageHosts.AddOrUpdate(brush, host);
            SetBrushImageSource(brush, GetBrushImageSource(host));
        }

        private static readonly ConditionalWeakTable<ImageBrush, System.Windows.Controls.Border> BrushImageHosts = new();
        private static readonly object BrushImageMarker = new();

        private static void PresentLinuxBrushImage(ImageBrush brush, ImageSource? image)
        {
            if (!Platform.IsLinux || !BrushImageHosts.TryGetValue(brush, out System.Windows.Controls.Border? host))
                return;

            Image? view = host.Child switch
            {
                Image existing when ReferenceEquals(existing.Tag, BrushImageMarker) => existing,
                Grid grid when grid.Children.Count > 0 && grid.Children[0] is Image first && ReferenceEquals(first.Tag, BrushImageMarker) => first,
                _ => null
            };
            if (view == null && image == null)
                return;
            if (view == null)
            {
                view = new Image
                {
                    Tag = BrushImageMarker,
                    Stretch = brush.Stretch,
                    RenderTransform = brush.RelativeTransform,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(view, RenderOptions.GetBitmapScalingMode(host));
                UIElement? placeholder = host.Child;
                if (placeholder == null)
                {
                    host.Child = view;
                }
                else
                {
                    host.Child = null;
                    Grid layers = new();
                    layers.Children.Add(view);
                    layers.Children.Add(placeholder);
                    host.Child = layers;
                }
                host.ClipToBounds = true;
                ApplyBrushHostClip(host);
                host.SizeChanged -= OnBrushHostSizeChanged;
                host.SizeChanged += OnBrushHostSizeChanged;
            }
            view.Source = image;
        }

        private static void OnBrushHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.Border host)
                ApplyBrushHostClip(host);
        }

        private static void ApplyBrushHostClip(System.Windows.Controls.Border host)
        {
            CornerRadius radius = host.CornerRadius;
            double width = host.ActualWidth;
            double height = host.ActualHeight;
            if (width <= 0.0 || height <= 0.0)
                return;
            if (radius.TopLeft <= 0.0 && radius.TopRight <= 0.0 && radius.BottomRight <= 0.0 && radius.BottomLeft <= 0.0)
            {
                host.Clip = null;
                return;
            }

            Rect bounds = new(0.0, 0.0, width, height);
            Geometry clip;
            if (radius.TopLeft == radius.TopRight && radius.TopLeft == radius.BottomRight && radius.TopLeft == radius.BottomLeft)
            {
                clip = new RectangleGeometry(bounds, radius.TopLeft, radius.TopLeft);
            }
            else
            {
                StreamGeometry path = new();
                using (StreamGeometryContext context = path.Open())
                {
                    context.BeginFigure(new Point(radius.TopLeft, 0.0), true, true);
                    context.LineTo(new Point(width - radius.TopRight, 0.0), true, false);
                    context.ArcTo(new Point(width, radius.TopRight), new Size(radius.TopRight, radius.TopRight), 0.0, false, SweepDirection.Clockwise, true, false);
                    context.LineTo(new Point(width, height - radius.BottomRight), true, false);
                    context.ArcTo(new Point(width - radius.BottomRight, height), new Size(radius.BottomRight, radius.BottomRight), 0.0, false, SweepDirection.Clockwise, true, false);
                    context.LineTo(new Point(radius.BottomLeft, height), true, false);
                    context.ArcTo(new Point(0.0, height - radius.BottomLeft), new Size(radius.BottomLeft, radius.BottomLeft), 0.0, false, SweepDirection.Clockwise, true, false);
                    context.LineTo(new Point(0.0, radius.TopLeft), true, false);
                    context.ArcTo(new Point(radius.TopLeft, 0.0), new Size(radius.TopLeft, radius.TopLeft), 0.0, false, SweepDirection.Clockwise, true, false);
                }
                clip = path;
            }
            clip.Freeze();
            host.Clip = clip;
        }

        private static void OnBrushHostLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border host)
                return;
            host.Loaded -= OnBrushHostLoaded;
            if (host.Background is ImageBrush { IsFrozen: false })
                ForwardBrushImageSource(host);
        }

        private static async Task LoadBrushImageAsync(ImageBrush brush, string uri, int decodeWidth)
        {
            try
            {
                BitmapSource? image = await GetOrDecodeAsync(uri, decodeWidth <= 0 ? 512 : decodeWidth).ConfigureAwait(false);
                if (brush.Dispatcher.HasShutdownStarted || brush.Dispatcher.HasShutdownFinished)
                    return;
                await brush.Dispatcher.InvokeAsync(() =>
                {
                    if (!string.Equals(GetBrushImageSource(brush), uri, StringComparison.Ordinal))
                        return;
                    if (image != null)
                    {
                        brush.ImageSource = image;
                        PresentLinuxBrushImage(brush, image);
                    }
                    SetIsBrushLoading(brush, false);
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("DynamicRenderSystem", "A brush image could not be loaded: " + ex.Message);
                TryClearLoadingFlag(brush, uri);
            }
        }

        private static void TryClearLoadingFlag(ImageBrush brush, string uri)
        {
            try
            {
                if (brush.Dispatcher.HasShutdownStarted || brush.Dispatcher.HasShutdownFinished)
                    return;
                _ = brush.Dispatcher.InvokeAsync(() =>
                {
                    if (string.Equals(GetBrushImageSource(brush), uri, StringComparison.Ordinal))
                        SetIsBrushLoading(brush, false);
                }, DispatcherPriority.Background);
            }
            catch
            {
            }
        }

        public static void SetLazyImageSource(DependencyObject element, string? value) => element.SetValue(LazyImageSourceProperty, value);

        public static string GetLazyImageSource(DependencyObject element) => (string)element.GetValue(LazyImageSourceProperty);

        public static void SetLinuxImageSource(DependencyObject element, string value) => element.SetValue(LinuxImageSourceProperty, value);

        public static string GetLinuxImageSource(DependencyObject element) => (string)element.GetValue(LinuxImageSourceProperty);

        public static void SetLinuxImageBrushSource(DependencyObject element, string value) => element.SetValue(LinuxImageBrushSourceProperty, value);

        public static string GetLinuxImageBrushSource(DependencyObject element) => (string)element.GetValue(LinuxImageBrushSourceProperty);

        public static void SetLinuxImageBytes(DependencyObject element, object? value) => element.SetValue(LinuxImageBytesProperty, value);

        public static object? GetLinuxImageBytes(DependencyObject element) => element.GetValue(LinuxImageBytesProperty);

        private static void OnLinuxImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!Platform.IsLinux || d is not Image image)
                return;
            string? uri = e.NewValue as string;
            LinuxImageState state = LinuxImageStates.GetOrCreateValue(image);
            CancelLinuxImageLoad(state);
            int generation = ++state.Generation;
            if (string.IsNullOrWhiteSpace(uri))
                return;
            state.Cancellation = new CancellationTokenSource();
            _ = LoadLinuxImageSourceAsync(image, state, uri, DecodeWidthFor(image), generation, state.Cancellation.Token);
        }

        private static async Task LoadLinuxImageSourceAsync(Image image, LinuxImageState state, string uri, int decodeWidth, int generation, CancellationToken token)
        {
            try
            {
                BitmapSource? decoded = await AppImage.LoadAsync(uri, decodeWidth, token).ConfigureAwait(false);
                if (decoded == null || token.IsCancellationRequested || image.Dispatcher.HasShutdownStarted || image.Dispatcher.HasShutdownFinished)
                    return;
                await image.Dispatcher.InvokeAsync(() =>
                {
                    if (state.Generation == generation && !token.IsCancellationRequested && string.Equals(GetLinuxImageSource(image), uri, StringComparison.Ordinal))
                        image.Source = decoded;
                }, DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("DynamicRenderSystem::LinuxImageSource", "Image load failed for " + uri + ": " + ex.Message.Split('\n')[0]);
            }
        }

        private static void OnLinuxImageBrushSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!Platform.IsLinux)
                return;
            string? uri = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(uri))
                return;
            _ = LoadLinuxImageBrushAsync(d, uri);
        }

        private static void OnLinuxImageBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!Platform.IsLinux || d is not Image image || e.NewValue is not byte[] bytes || bytes.Length == 0)
                return;
            LinuxImageState state = LinuxImageStates.GetOrCreateValue(image);
            CancelLinuxImageLoad(state);
            state.Cancellation = new CancellationTokenSource();
            int generation = ++state.Generation;
            int decodeWidth = DecodeWidthFor(image);
            _ = LoadLinuxImageBytesAsync(image, state, bytes, decodeWidth, generation, state.Cancellation.Token);
        }

        private static async Task LoadLinuxImageBytesAsync(Image image, LinuxImageState state, byte[] bytes, int decodeWidth, int generation, CancellationToken token)
        {
            try
            {
                BitmapSource? decoded = await AppImage.DecodeBytesAsync(bytes, decodeWidth, token).ConfigureAwait(false);
                if (decoded == null || token.IsCancellationRequested || image.Dispatcher.HasShutdownStarted || image.Dispatcher.HasShutdownFinished)
                    return;
                await image.Dispatcher.InvokeAsync(() =>
                {
                    if (state.Generation == generation && !token.IsCancellationRequested && ReferenceEquals(GetLinuxImageBytes(image), bytes))
                        image.Source = decoded;
                }, DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("DynamicRenderSystem::LinuxImageBytes", "Forum image decode failed: " + ex.Message.Split('\n')[0]);
            }
        }

        private static async Task LoadLinuxImageBrushAsync(DependencyObject target, string uri)
        {
            BitmapSource? image = await GetOrDecodeAsync(uri, 512).ConfigureAwait(false);
            if (image == null || target.Dispatcher.HasShutdownStarted || target.Dispatcher.HasShutdownFinished)
                return;
            await target.Dispatcher.InvokeAsync(() =>
            {
                if (!string.Equals(GetLinuxImageBrushSource(target), uri, StringComparison.Ordinal))
                    return;
                ImageBrush? current = target switch
                {
                    Border border => border.Background as ImageBrush,
                    Panel panel => panel.Background as ImageBrush,
                    Control control => control.Background as ImageBrush,
                    System.Windows.Shapes.Shape shape => shape.Fill as ImageBrush,
                    _ => null
                };
                ImageBrush brush = CopyImageBrush(current);
                brush.ImageSource = image;
                switch (target)
                {
                    case Border border:
                        border.Background = brush;
                        break;
                    case Panel panel:
                        panel.Background = brush;
                        break;
                    case Control control:
                        control.Background = brush;
                        break;
                    case System.Windows.Shapes.Shape shape:
                        shape.Fill = brush;
                        break;
                }
            }, DispatcherPriority.Background);
        }

        private static ImageBrush CopyImageBrush(ImageBrush? source)
        {
            if (source == null)
                return new ImageBrush { Stretch = Stretch.UniformToFill };
            return new ImageBrush
            {
                AlignmentX = source.AlignmentX,
                AlignmentY = source.AlignmentY,
                Opacity = source.Opacity,
                Stretch = source.Stretch,
                TileMode = source.TileMode,
                Viewbox = source.Viewbox,
                ViewboxUnits = source.ViewboxUnits,
                Viewport = source.Viewport,
                ViewportUnits = source.ViewportUnits
            };
        }

        private sealed class LinuxImageState
        {
            public CancellationTokenSource? Cancellation;
            public int Generation;
            public bool Listening;
        }

        private static readonly ConditionalWeakTable<Image, LinuxImageState> LinuxImageStates = new();
        private static readonly DependencyPropertyDescriptor? ImageSourceDescriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        private static bool _linuxImageGuardInstalled;

        public static void InstallLinuxImageGuard()
        {
            if (!Platform.IsLinux || _linuxImageGuardInstalled)
                return;
            _linuxImageGuardInstalled = true;
            EventManager.RegisterClassHandler(typeof(Image), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLinuxImageLoaded));
            EventManager.RegisterClassHandler(typeof(Image), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnLinuxImageUnloaded));
        }

        private static void OnLinuxImageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image)
                return;
            LinuxImageState state = LinuxImageStates.GetOrCreateValue(image);
            if (!state.Listening && ImageSourceDescriptor != null)
            {
                ImageSourceDescriptor.AddValueChanged(image, OnLinuxImageSourceChanged);
                state.Listening = true;
            }
            ReplaceUriBackedImage(image, state);
        }

        private static void OnLinuxImageUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image || !LinuxImageStates.TryGetValue(image, out LinuxImageState? state))
                return;
            if (state.Listening && ImageSourceDescriptor != null)
            {
                ImageSourceDescriptor.RemoveValueChanged(image, OnLinuxImageSourceChanged);
                state.Listening = false;
            }
            CancelLinuxImageLoad(state);
        }

        private static void OnLinuxImageSourceChanged(object? sender, EventArgs e)
        {
            if (sender is Image image)
                ReplaceUriBackedImage(image, LinuxImageStates.GetOrCreateValue(image));
        }

        private static void ReplaceUriBackedImage(Image image, LinuxImageState state)
        {
            if (image.Source is not BitmapImage bitmap || bitmap.UriSource == null)
                return;
            Uri uri = bitmap.UriSource.IsAbsoluteUri
                ? bitmap.UriSource
                : new Uri("pack://application:,,,/" + bitmap.UriSource.OriginalString.TrimStart('/'), UriKind.Absolute);
            CancelLinuxImageLoad(state);
            state.Cancellation = new CancellationTokenSource();
            int generation = ++state.Generation;
            _ = ReplaceUriBackedImageAsync(image, state, uri, generation, state.Cancellation.Token);
        }

        private static async Task ReplaceUriBackedImageAsync(Image image, LinuxImageState state, Uri uri, int generation, CancellationToken token)
        {
            try
            {
                BitmapSource? decoded = await AppImage.LoadAsync(uri.OriginalString, DecodeWidthFor(image), token).ConfigureAwait(false);
                if (decoded == null || token.IsCancellationRequested || image.Dispatcher.HasShutdownStarted || image.Dispatcher.HasShutdownFinished)
                    return;
                await image.Dispatcher.InvokeAsync(() =>
                {
                    if (state.Generation != generation || token.IsCancellationRequested)
                        return;
                    image.Source = decoded;
                }, DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("DynamicRenderSystem::LinuxImageGuard", "Image load failed for " + uri + ": " + ex.Message.Split('\n')[0]);
            }
        }

        private static void CancelLinuxImageLoad(LinuxImageState state)
        {
            CancellationTokenSource? cancellation = state.Cancellation;
            state.Cancellation = null;
            if (cancellation == null)
                return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private static void OnLazyImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image img)
                return;
            img.Source = null;
            string? uri = e.NewValue as string;
            if (string.IsNullOrEmpty(uri))
                return;
            img.Visibility = Visibility.Visible;
            if (img.IsLoaded)
            {
                Register(img, uri);
            }
            else
            {
                img.Loaded -= OnLazyImageLoaded;
                img.Loaded += OnLazyImageLoaded;
            }
        }

        private static void OnLazyImageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image img)
                return;
            img.Loaded -= OnLazyImageLoaded;
            Register(img, GetLazyImageSource(img));
        }

        private sealed class Entry
        {
            public WeakReference<Image> Img = null!;
            public string Uri = null!;
            public bool Loaded;
			public bool Loading;
			public long RetryAfter;
			public int Attempts;
			public int DecodeWidth;
        }

        private sealed class Watcher
        {
            public readonly List<Entry> Items = new();
            public bool EvalQueued;
			public long LastEvaluationTicks;
        }

        private static readonly Dictionary<ScrollViewer, Watcher> _watchers = new();

        private static void Register(Image img, string uri)
        {
            try
            {
                if (img == null || string.IsNullOrEmpty(uri))
                    return;
                ScrollViewer? sv = FindScrollViewer(img);
                if (sv == null)
                {
                    _ = LoadIntoAsync(img, uri);
                    return;
                }
                if (!_watchers.TryGetValue(sv, out Watcher? w))
                {
                    w = new Watcher();
                    _watchers[sv] = w;
                    sv.ScrollChanged += OnScrollChanged;
                    sv.Unloaded += OnScrollViewerUnloaded;
                }
                for (int i = w.Items.Count - 1; i >= 0; i--)
                {
                    if (w.Items[i].Img.TryGetTarget(out Image? existing) && ReferenceEquals(existing, img))
                        w.Items.RemoveAt(i);
                }
				w.Items.Add(new Entry { Img = new WeakReference<Image>(img), Uri = uri, Loaded = false, DecodeWidth = DecodeWidthFor(img) });
                QueueEval(sv, w);
            }
            catch
            {
                _ = LoadIntoAsync(img, uri);
            }
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is ScrollViewer sv && _watchers.TryGetValue(sv, out Watcher? w))
			{
				long now = Environment.TickCount64;
				if (Platform.IsLinux && now - w.LastEvaluationTicks < 80)
					return;
				w.LastEvaluationTicks = now;
                QueueEval(sv, w);
			}
        }

        private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer sv)
                return;
            sv.ScrollChanged -= OnScrollChanged;
            sv.Unloaded -= OnScrollViewerUnloaded;
            _watchers.Remove(sv);
        }

        private static void QueueEval(ScrollViewer sv, Watcher w)
        {
            if (w.EvalQueued)
                return;
            w.EvalQueued = true;
            sv.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
            {
                w.EvalQueued = false;
                Evaluate(sv, w);
            });
        }

        private static void Evaluate(ScrollViewer sv, Watcher w)
        {
            try
            {
                if (w.Items.Count == 0)
                    return;
                double vpW = sv.ActualWidth;
                double vpH = sv.ActualHeight;
                if (vpW <= 0.0 && vpH <= 0.0)
                    return;
                Rect near = new Rect(0, 0, vpW, vpH);
                near.Inflate(PreloadMarginPx, PreloadMarginPx);
                Rect far = new Rect(0, 0, vpW, vpH);
                far.Inflate(FarReleaseMarginPx, FarReleaseMarginPx);

                for (int i = w.Items.Count - 1; i >= 0; i--)
                {
                    Entry entry = w.Items[i];
                    if (!entry.Img.TryGetTarget(out Image? img))
                    {
                        w.Items.RemoveAt(i);
                        continue;
                    }
                    if (!img.IsVisible)
                        continue;

                    Rect bounds;
                    try
                    {
                        GeneralTransform t = img.TransformToVisual(sv);
                        bounds = t.TransformBounds(new Rect(0, 0, img.ActualWidth > 0 ? img.ActualWidth : 1, img.ActualHeight > 0 ? img.ActualHeight : 1));
                    }
                    catch
                    {
                        if (!entry.Loaded)
                            StartEntryLoad(sv, w, entry, img);
                        continue;
                    }

                    if (!entry.Loaded && near.IntersectsWith(bounds))
                        StartEntryLoad(sv, w, entry, img);
					else if (entry.Loaded && !far.IntersectsWith(bounds) && CachePeek(CacheKey(entry.Uri, entry.DecodeWidth)) != null)
                    {
                        entry.Loaded = false;
                        img.Source = null;
                    }
                }
            }
            catch
            {
            }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject node)
        {
            try
            {
                DependencyObject current = VisualTreeHelper.GetParent(node);
                while (current != null)
                {
                    if (current is ScrollViewer sv)
                        return sv;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch
            {
            }
            return null;
        }

        private static async Task LoadIntoAsync(Image img, string uri)
        {
            if (img == null || string.IsNullOrEmpty(uri))
                return;
            int decodeWidth = DecodeWidthFor(img);
            BitmapSource? bmp = await GetOrDecodeAsync(uri, decodeWidth).ConfigureAwait(false);
            EnqueueAssign(img, uri, bmp);
        }

        private static void StartEntryLoad(ScrollViewer sv, Watcher watcher, Entry entry, Image img)
        {
            if (entry.Loading || Environment.TickCount64 < entry.RetryAfter)
                return;
            entry.Loading = true;
            _ = LoadEntryAsync(sv, watcher, entry, img);
        }

        private static async Task LoadEntryAsync(ScrollViewer sv, Watcher watcher, Entry entry, Image img)
        {
            BitmapSource? bmp = await GetOrDecodeAsync(entry.Uri, entry.DecodeWidth).ConfigureAwait(false);
            EnqueueAssign(img, entry.Uri, bmp);
            await img.Dispatcher.InvokeAsync(() =>
            {
                entry.Loading = false;
                entry.Loaded = bmp != null;
                entry.Attempts = bmp == null ? entry.Attempts + 1 : 0;
                entry.RetryAfter = bmp == null ? Environment.TickCount64 + RetryDelayMs(entry.Attempts) : 0;
            }, DispatcherPriority.Loaded);
            if (bmp != null)
                return;
            await Task.Delay(RetryDelayMs(entry.Attempts)).ConfigureAwait(false);
            await img.Dispatcher.InvokeAsync(() =>
            {
                if (_watchers.TryGetValue(sv, out Watcher? current) && ReferenceEquals(current, watcher))
                    QueueEval(sv, watcher);
            }, DispatcherPriority.Loaded);
        }

        private static int RetryDelayMs(int attempts)
        {
            return Math.Min(30000, 3000 * Math.Max(1, attempts));
        }

        private static readonly object AssignLock = new object();
        private static readonly Dictionary<Dispatcher, List<(Image Img, string Uri, BitmapSource? Bmp)>> _assignQueues = new();
        private static readonly HashSet<Dispatcher> _assignPending = new();

        private static void EnqueueAssign(Image img, string uri, BitmapSource? bmp)
        {
            Dispatcher dispatcher = img.Dispatcher;
            bool queue = false;
            lock (AssignLock)
            {
                if (!_assignQueues.TryGetValue(dispatcher, out List<(Image, string, BitmapSource?)>? list))
                {
                    list = new List<(Image, string, BitmapSource?)>();
                    _assignQueues[dispatcher] = list;
                }
                list.Add((img, uri, bmp));
                if (_assignPending.Add(dispatcher))
                    queue = true;
            }
            if (queue)
                dispatcher.BeginInvoke(Platform.IsLinux ? DispatcherPriority.Background : DispatcherPriority.Render, (Action)delegate
                {
                    FlushAssigns(dispatcher);
                });
        }

        private static void FlushAssigns(Dispatcher dispatcher)
        {
            List<(Image Img, string Uri, BitmapSource? Bmp)> batch;
            bool reschedule = false;
            lock (AssignLock)
            {
                if (!_assignQueues.TryGetValue(dispatcher, out List<(Image, string, BitmapSource?)>? list) || list.Count == 0)
                {
                    _assignPending.Remove(dispatcher);
                    return;
                }
                if (Platform.IsLinux && list.Count > 12)
                {
                    batch = list.GetRange(0, 12);
                    list.RemoveRange(0, 12);
                    reschedule = true;
                }
                else
                {
                    batch = list;
                    _assignQueues.Remove(dispatcher);
                    _assignPending.Remove(dispatcher);
                }
            }
            foreach ((Image img, string uri, BitmapSource? bmp) in batch)
            {
                try
                {
                    if (GetLazyImageSource(img) != uri)
                        continue;
                    if (bmp != null)
                    {
                        img.Visibility = Visibility.Visible;
                        img.Source = bmp;
                    }
                    else
                    {
                        img.Source = null;
                    }
                }
                catch
                {
                }
            }
            if (reschedule)
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    FlushAssigns(dispatcher);
                });
            }
        }

        public static void Prefetch(string uri, int decodeWidth = 256)
        {
            if (string.IsNullOrEmpty(uri))
                return;
            _ = GetOrDecodeAsync(uri, decodeWidth);
        }

        public static void Prefetch(IEnumerable<string> uris, int decodeWidth = 256)
        {
            if (uris == null)
                return;
            foreach (string u in uris)
            {
                Prefetch(u, decodeWidth);
            }
        }

        private static int DecodeWidthFor(Image img)
        {
            double w = 0;
            try
            {
                w = img.ActualWidth;
                if (w <= 0)
                    w = img.Width;
                PresentationSource src = PresentationSource.FromVisual(img);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                w *= dpi;
            }
            catch
            {
            }
            if (double.IsNaN(w) || w <= 0)
                return 256;
            int px = (int)Math.Ceiling(w);
            if (px <= 64) return 64;
            if (px <= 128) return 128;
            if (px <= 256) return 256;
            if (px <= 512) return 512;
            return MaxDecodeWidth;
        }

        private static string CacheKey(string uri, int decodeWidth) => decodeWidth + "|" + uri;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, LinkedListNode<CacheItem>> _cache = new();
        private static readonly LinkedList<CacheItem> _lru = new();
        private static long _cacheBytes;
        private static int _cacheGeneration;
        private static readonly object InflightLock = new object();
        private static readonly Dictionary<string, Task<BitmapSource?>> _inflight = new();
        private static readonly SemaphoreSlim DecodeGate = new SemaphoreSlim(DecodeConcurrency, DecodeConcurrency);

        private static readonly object ByteCacheLock = new object();
        private static readonly Dictionary<string, LinkedListNode<(string Uri, byte[] Bytes)>> _byteCache = new();
        private static readonly LinkedList<(string Uri, byte[] Bytes)> _byteLru = new();
        private static long _byteCacheBytes;

        private static byte[]? ByteCacheGet(string uri)
        {
            lock (ByteCacheLock)
            {
                if (_byteCache.TryGetValue(uri, out LinkedListNode<(string Uri, byte[] Bytes)>? node))
                {
                    _byteLru.Remove(node);
                    _byteLru.AddFirst(node);
                    return node.Value.Bytes;
                }
            }
            return null;
        }

        private static void ByteCachePut(string uri, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return;
            lock (ByteCacheLock)
            {
                if (_byteCache.ContainsKey(uri))
                    return;
                LinkedListNode<(string Uri, byte[] Bytes)> node = new LinkedListNode<(string Uri, byte[] Bytes)>((uri, bytes));
                _byteLru.AddFirst(node);
                _byteCache[uri] = node;
                _byteCacheBytes += bytes.Length;
                while (_byteCacheBytes > MaxByteCacheBytes && _byteLru.Last != null)
                {
                    LinkedListNode<(string Uri, byte[] Bytes)> last = _byteLru.Last;
                    _byteLru.RemoveLast();
                    _byteCache.Remove(last.Value.Uri);
                    _byteCacheBytes -= last.Value.Bytes.Length;
                }
            }
        }

        private sealed class CacheItem
        {
            public string Key = null!;
            public BitmapSource Image = null!;
            public long SizeBytes;
        }

        private static BitmapSource? CacheGet(string key)
        {
            lock (CacheLock)
            {
                if (_cache.TryGetValue(key, out LinkedListNode<CacheItem>? node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Image;
                }
            }
            return null;
        }

        private static BitmapSource? CachePeek(string key)
        {
            lock (CacheLock)
            {
                return _cache.TryGetValue(key, out LinkedListNode<CacheItem>? node) ? node.Value.Image : null;
            }
        }

        private static void CachePut(string key, BitmapSource img, int generation)
        {
            if (img == null)
                return;
            lock (CacheLock)
            {
                if (generation != _cacheGeneration)
                    return;
                if (_cache.TryGetValue(key, out LinkedListNode<CacheItem>? existing))
                {
                    _lru.Remove(existing);
                    _cache.Remove(key);
                    _cacheBytes -= existing.Value.SizeBytes;
                }
                long sizeBytes = EstimateBytes(img);
                LinkedListNode<CacheItem> node = new LinkedListNode<CacheItem>(new CacheItem { Key = key, Image = img, SizeBytes = sizeBytes });
                _lru.AddFirst(node);
                _cache[key] = node;
                _cacheBytes += sizeBytes;
                while ((_cache.Count > MaxCacheEntries || _cacheBytes > MaxCacheBytes) && _lru.Last != null)
                {
                    LinkedListNode<CacheItem> last = _lru.Last;
                    _lru.RemoveLast();
                    _cache.Remove(last.Value.Key);
                    _cacheBytes -= last.Value.SizeBytes;
                }
            }
        }

        private static long EstimateBytes(BitmapSource image)
        {
            int bitsPerPixel = image.Format.BitsPerPixel;
            if (bitsPerPixel <= 0)
                bitsPerPixel = 32;
            return ((long)Math.Max(1, image.PixelWidth) * Math.Max(1, image.PixelHeight) * bitsPerPixel + 7) / 8;
        }

        public static void TrimCache(long targetBytes)
        {
            lock (CacheLock)
            {
                while (_cacheBytes > targetBytes && _lru.Last != null)
                {
                    LinkedListNode<CacheItem> last = _lru.Last;
                    _lru.RemoveLast();
                    _cache.Remove(last.Value.Key);
                    _cacheBytes -= last.Value.SizeBytes;
                }
            }
            lock (ByteCacheLock)
            {
                _byteCache.Clear();
                _byteLru.Clear();
                _byteCacheBytes = 0;
            }
        }

        public static void ClearCache()
        {
            lock (CacheLock)
            {
                _cacheGeneration++;
                _cache.Clear();
                _lru.Clear();
                _cacheBytes = 0;
            }
            lock (ByteCacheLock)
            {
                _byteCache.Clear();
                _byteLru.Clear();
                _byteCacheBytes = 0;
            }
        }

        private static Task<BitmapSource?> GetOrDecodeAsync(string uri, int decodeWidth)
        {
            string key = CacheKey(uri, decodeWidth);
            BitmapSource? cached = CacheGet(key);
            if (cached != null)
                return Task.FromResult<BitmapSource?>(cached);

            Task<BitmapSource?>? task;
            lock (InflightLock)
            {
                cached = CacheGet(key);
                if (cached != null)
                    return Task.FromResult<BitmapSource?>(cached);
                if (!_inflight.TryGetValue(key, out task))
                {
                    int generation;
                    lock (CacheLock)
                    {
                        generation = _cacheGeneration;
                    }
                    task = DecodeAsync(key, uri, decodeWidth, generation);
                    _inflight[key] = task;
                }
            }
            return task;
        }

        private static async Task<BitmapSource?> DecodeAsync(string key, string uri, int decodeWidth, int generation)
        {
            try
            {
                string? embedded = AppImage.EmbeddedAsset(uri);
                bool remote = embedded == null && (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                if (!remote)
                {
                    BitmapSource? local = await DecodeLocalAsync(embedded ?? uri, decodeWidth).ConfigureAwait(false);
                    if (local != null)
                        CachePut(key, local, generation);
                    return local;
                }

                foreach (string candidate in AppImage.GetCandidates(uri, decodeWidth))
                {
                    byte[]? bytes = ByteCacheGet(candidate);
                    if (bytes == null)
                    {
                        try
                        {
                            bytes = Platform.IsLinux
                                ? await AppImage.DownloadBytesAsync(candidate).ConfigureAwait(false)
                                : await DownloadBytesAsync(candidate).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                        {
                            App.Logger?.WriteLine("DynamicRenderSystem::Decode", "Download failed for " + candidate + ": " + ex.Message);
                            continue;
                        }
                        if (bytes != null && bytes.Length > 0)
                            ByteCachePut(candidate, bytes);
                    }
                    if (bytes == null || bytes.Length == 0)
                        continue;
                    BitmapSource? result = await DecodeBytesAsync(bytes, decodeWidth).ConfigureAwait(false);
                    if (result == null)
                        continue;
                    CachePut(key, result, generation);
                    return result;
                }
                return null;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("DynamicRenderSystem::Decode", "Lazy image failed for " + uri + ": " + ex.Message);
                return null;
            }
            finally
            {
                lock (InflightLock)
                {
                    _inflight.Remove(key);
                }
            }
        }

        private static async Task<BitmapSource?> DecodeBytesAsync(byte[] bytes, int decodeWidth)
        {
            await DecodeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(delegate
                {
                    try
                    {
                        return SafeImaging.FromBytes(bytes, decodeWidth);
                    }
                    catch (Exception decodeEx)
                    {
                        App.Logger?.WriteLine("DynamicRenderSystem::Decode", "Decode failed: " + decodeEx.GetType().Name + " " + decodeEx.Message.Split('\n')[0]);
                        return null;
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                DecodeGate.Release();
            }
        }

        private static async Task<BitmapSource?> DecodeLocalAsync(string uri, int decodeWidth)
        {
            await DecodeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(delegate
                {
                    try
                    {
                        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
                            return (BitmapSource?)null;

                        if (parsed.IsFile)
                            return SafeImaging.FromFile(parsed.LocalPath, decodeWidth);

                        if (Voidstrap.Utility.Platform.IsWindows)
                        {
                            BitmapImage bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            bmp.DecodePixelWidth = decodeWidth;
                            bmp.UriSource = parsed;
                            bmp.EndInit();
                            return SafeImaging.Detach(bmp);
                        }

                        System.Windows.Resources.StreamResourceInfo info = System.Windows.Application.GetResourceStream(parsed);
                        if (info?.Stream == null)
                            return (BitmapSource?)null;
                        using Stream resourceStream = info.Stream;
                        return SafeImaging.FromStream(resourceStream, decodeWidth);
                    }
                    catch (Exception decodeEx)
                    {
                        App.Logger?.WriteLine("DynamicRenderSystem::Decode", "Decode failed for " + uri + ": " + decodeEx.GetType().Name + " " + decodeEx.Message.Split('\n')[0]);
                        return null;
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                DecodeGate.Release();
            }
        }

        private static async Task<byte[]?> DownloadBytesAsync(string uri)
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is long contentLength && contentLength > MaxDownloadBytes)
                return null;
            await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            int capacity = response.Content.Headers.ContentLength is long length && length > 0 ? (int)length : 81920;
            using MemoryStream output = new MemoryStream(capacity);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (output.Length + read > MaxDownloadBytes)
                        return null;
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
