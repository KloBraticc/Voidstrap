using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
        private const int MaxCacheEntries = 48;
        private const long MaxCacheBytes = 10L * 1024 * 1024;
        private const long MaxDownloadBytes = 8L * 1024 * 1024;
        private const long MaxByteCacheBytes = 3L * 1024 * 1024;

        public static readonly DependencyProperty LazyImageSourceProperty = DependencyProperty.RegisterAttached(
            "LazyImageSource",
            typeof(string),
            typeof(DynamicRenderSystem),
            new PropertyMetadata(null, OnLazyImageSourceChanged));

        public static void SetLazyImageSource(DependencyObject element, string value) => element.SetValue(LazyImageSourceProperty, value);

        public static string GetLazyImageSource(DependencyObject element) => (string)element.GetValue(LazyImageSourceProperty);

        private static void OnLazyImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image img)
                return;
            img.Source = null;
            string uri = e.NewValue as string;
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
            public WeakReference<Image> Img;
            public string Uri;
            public bool Loaded;
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
                ScrollViewer sv = FindScrollViewer(img);
                if (sv == null)
                {
                    _ = LoadIntoAsync(img, uri);
                    return;
                }
                if (!_watchers.TryGetValue(sv, out Watcher w))
                {
                    w = new Watcher();
                    _watchers[sv] = w;
                    sv.ScrollChanged += OnScrollChanged;
                    sv.Unloaded += OnScrollViewerUnloaded;
                }
                for (int i = w.Items.Count - 1; i >= 0; i--)
                {
                    if (w.Items[i].Img.TryGetTarget(out Image existing) && ReferenceEquals(existing, img))
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
            if (sender is ScrollViewer sv && _watchers.TryGetValue(sv, out Watcher w))
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
                    if (!entry.Img.TryGetTarget(out Image img))
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
                        {
                            entry.Loaded = true;
                            _ = LoadIntoAsync(img, entry.Uri);
                        }
                        continue;
                    }

                    if (!entry.Loaded && near.IntersectsWith(bounds))
                    {
                        entry.Loaded = true;
                        _ = LoadIntoAsync(img, entry.Uri);
                    }
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

        private static ScrollViewer FindScrollViewer(DependencyObject node)
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
            BitmapSource bmp = await GetOrDecodeAsync(uri, decodeWidth).ConfigureAwait(false);
            EnqueueAssign(img, uri, bmp);
        }

        private static readonly object AssignLock = new object();
        private static readonly Dictionary<Dispatcher, List<(Image Img, string Uri, BitmapSource Bmp)>> _assignQueues = new();
        private static readonly HashSet<Dispatcher> _assignPending = new();

        private static void EnqueueAssign(Image img, string uri, BitmapSource bmp)
        {
            Dispatcher dispatcher = img.Dispatcher;
            bool queue = false;
            lock (AssignLock)
            {
                if (!_assignQueues.TryGetValue(dispatcher, out List<(Image, string, BitmapSource)> list))
                {
                    list = new List<(Image, string, BitmapSource)>();
                    _assignQueues[dispatcher] = list;
                }
                list.Add((img, uri, bmp));
                if (_assignPending.Add(dispatcher))
                    queue = true;
            }
            if (queue)
                dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
                {
                    FlushAssigns(dispatcher);
                });
        }

        private static void FlushAssigns(Dispatcher dispatcher)
        {
            List<(Image Img, string Uri, BitmapSource Bmp)> batch;
            lock (AssignLock)
            {
                if (!_assignQueues.TryGetValue(dispatcher, out List<(Image, string, BitmapSource)> list) || list.Count == 0)
                {
                    _assignPending.Remove(dispatcher);
                    return;
                }
                batch = list;
                _assignQueues.Remove(dispatcher);
                _assignPending.Remove(dispatcher);
            }
            foreach ((Image img, string uri, BitmapSource bmp) in batch)
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
                        img.Visibility = Visibility.Collapsed;
                    }
                }
                catch
                {
                }
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
        private static readonly Dictionary<string, Task<BitmapSource>> _inflight = new();
        private static readonly SemaphoreSlim DecodeGate = new SemaphoreSlim(Math.Max(4, Environment.ProcessorCount), Math.Max(4, Environment.ProcessorCount));

        private static readonly object ByteCacheLock = new object();
        private static readonly Dictionary<string, LinkedListNode<(string Uri, byte[] Bytes)>> _byteCache = new();
        private static readonly LinkedList<(string Uri, byte[] Bytes)> _byteLru = new();
        private static long _byteCacheBytes;

        private static byte[] ByteCacheGet(string uri)
        {
            lock (ByteCacheLock)
            {
                if (_byteCache.TryGetValue(uri, out LinkedListNode<(string Uri, byte[] Bytes)> node))
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
            public string Key;
            public BitmapSource Image;
            public long SizeBytes;
        }

        private static BitmapSource CacheGet(string key)
        {
            lock (CacheLock)
            {
                if (_cache.TryGetValue(key, out LinkedListNode<CacheItem> node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Image;
                }
            }
            return null;
        }

        private static BitmapSource CachePeek(string key)
        {
            lock (CacheLock)
            {
                return _cache.TryGetValue(key, out LinkedListNode<CacheItem> node) ? node.Value.Image : null;
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
                if (_cache.TryGetValue(key, out LinkedListNode<CacheItem> existing))
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

        private static Task<BitmapSource> GetOrDecodeAsync(string uri, int decodeWidth)
        {
            string key = CacheKey(uri, decodeWidth);
            BitmapSource cached = CacheGet(key);
            if (cached != null)
                return Task.FromResult(cached);

            Task<BitmapSource> task;
            lock (InflightLock)
            {
                cached = CacheGet(key);
                if (cached != null)
                    return Task.FromResult(cached);
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

        private static async Task<BitmapSource> DecodeAsync(string key, string uri, int decodeWidth, int generation)
        {
            try
            {
                bool remote = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!remote)
                {
                    BitmapSource local = await DecodeLocalAsync(uri, decodeWidth).ConfigureAwait(false);
                    if (local != null)
                        CachePut(key, local, generation);
                    return local;
                }

                foreach (string candidate in AppImage.GetCandidates(uri, decodeWidth))
                {
                    byte[] bytes = ByteCacheGet(candidate);
                    if (bytes == null)
                    {
                        bytes = await DownloadBytesAsync(candidate).ConfigureAwait(false);
                        if (bytes != null && bytes.Length > 0)
                            ByteCachePut(candidate, bytes);
                    }
                    if (bytes == null || bytes.Length == 0)
                        continue;
                    BitmapSource result = await DecodeBytesAsync(bytes, decodeWidth).ConfigureAwait(false);
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

        private static async Task<BitmapSource> DecodeBytesAsync(byte[] bytes, int decodeWidth)
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
                        App.Logger?.WriteLine("DynamicRenderSystem::Decode", "VSDIAG decode failed: " + decodeEx.GetType().Name + " " + decodeEx.Message.Split('\n')[0]);
                        return null;
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                DecodeGate.Release();
            }
        }

        private static async Task<BitmapSource> DecodeLocalAsync(string uri, int decodeWidth)
        {
            await DecodeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(delegate
                {
                    try
                    {
                        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed))
                            return (BitmapSource)null;

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
                            return (BitmapSource)null;
                        using Stream resourceStream = info.Stream;
                        return SafeImaging.FromStream(resourceStream, decodeWidth);
                    }
                    catch (Exception decodeEx)
                    {
                        App.Logger?.WriteLine("DynamicRenderSystem::Decode", "VSDIAG decode failed for " + uri + ": " + decodeEx.GetType().Name + " " + decodeEx.Message.Split('\n')[0]);
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
