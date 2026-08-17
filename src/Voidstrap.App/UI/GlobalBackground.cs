using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;

namespace Voidstrap.UI;

public static class GlobalBackground
{
    public readonly record struct State(string? FilePath, double GradientOpacity, double BlackOverlayOpacity, bool DisplayEverywhere);

    private sealed class Config
    {
        public string? BackgroundFilePath { get; set; }

        public double GradientOpacity { get; set; } = 1.0;

        public double BlackOverlayOpacity { get; set; }

        public bool DisplayEverywhere { get; set; }

        public State ToState()
        {
            return Normalize(new State(BackgroundFilePath, GradientOpacity, BlackOverlayOpacity, DisplayEverywhere));
        }
    }

    private sealed class GlobalBackgroundHost : Grid, IDisposable
    {
        private readonly Image _image;
        private readonly MediaElement _media;
        private readonly Border _shade;
        private bool _disposed;

        public UIElement ContentElement { get; }

        public string? AppliedPath { get; private set; }

        public DateTime AppliedWriteTimeUtc { get; private set; }

        public GlobalBackgroundHost(UIElement content)
        {
            ContentElement = content;
            _image = new Image
            {
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false
            };
            _media = new MediaElement
            {
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Volume = 0.0,
                Visibility = Visibility.Collapsed
            };
            _shade = new Border
            {
                Background = Brushes.Black,
                IsHitTestVisible = false
            };
            _media.MediaEnded += OnMediaEnded;
            Children.Add(_image);
            Children.Add(_media);
            Children.Add(_shade);
            Children.Add(content);
        }

        public void Apply(State state, BitmapSource? bitmap, DateTime writeTimeUtc)
        {
            _shade.Opacity = state.BlackOverlayOpacity;
            if (string.Equals(AppliedPath, state.FilePath, StringComparison.OrdinalIgnoreCase) && AppliedWriteTimeUtc == writeTimeUtc)
            {
                return;
            }
            ClearMedia();
            AppliedPath = state.FilePath;
            AppliedWriteTimeUtc = writeTimeUtc;
            string extension = Path.GetExtension(state.FilePath).ToLowerInvariant();
            if (extension is ".mp4" or ".webm" or ".avi" or ".mov")
            {
                _media.Source = new Uri(state.FilePath!, UriKind.Absolute);
                _media.Visibility = Visibility.Visible;
                _media.Play();
                return;
            }
            if (extension == ".gif" && bitmap != null && Voidstrap.Utility.Platform.IsWindows)
            {
                ImageBehavior.SetAnimatedSource(_image, bitmap);
                ImageBehavior.SetRepeatBehavior(_image, RepeatBehavior.Forever);
            }
            else
            {
                _image.Source = bitmap;
            }
            _image.Visibility = bitmap == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnMediaEnded(object? sender, RoutedEventArgs e)
        {
            if (_disposed || _media.Source == null)
            {
                return;
            }
            _media.Position = TimeSpan.Zero;
            _media.Play();
        }

        private void ClearMedia()
        {
            ImageBehavior.SetAnimatedSource(_image, null);
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            try
            {
                _media.Stop();
                _media.Close();
            }
            catch
            {
            }
            _media.Source = null;
            _media.Visibility = Visibility.Collapsed;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _media.MediaEnded -= OnMediaEnded;
            ClearMedia();
            Children.Clear();
            GC.SuppressFinalize(this);
        }
    }

    private static readonly string FilePath = Paths.BackgroundSettings;
    private static readonly Lock StateLock = new();
    private static readonly Lock CacheLock = new();
    private static bool _registered;
    private static int _refreshQueued;
    private static int _refreshRequested;
    private static State? _live;
    private static string? _cachedPath;
    private static DateTime _cachedWriteTimeUtc;
    private static int _cachedDecodeWidth;
    private static BitmapSource? _cachedImage;

    public static event Action<State>? Changed;

    public static State Current
    {
        get
        {
            lock (StateLock)
            {
                return _live ??= Load();
            }
        }
    }

    public static void Update(string? backgroundFilePath, double gradientOpacity, double blackOverlayOpacity, bool displayEverywhere)
    {
        State next = Normalize(new State(backgroundFilePath, gradientOpacity, blackOverlayOpacity, displayEverywhere));
        State previous;
        lock (StateLock)
        {
            previous = _live ?? Load();
            _live = next;
        }
        if (!string.Equals(previous.FilePath, next.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ClearCache();
        }
        Changed?.Invoke(next);
        if (!string.Equals(previous.FilePath, next.FilePath, StringComparison.OrdinalIgnoreCase)
            || previous.BlackOverlayOpacity != next.BlackOverlayOpacity
            || previous.DisplayEverywhere != next.DisplayEverywhere)
        {
            Refresh();
        }
    }

    public static void Refresh()
    {
        Application? app = Application.Current;
        if (app == null)
        {
            return;
        }
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }
        app.Dispatcher.BeginInvoke((Action)delegate
        {
            try
            {
                Interlocked.Exchange(ref _refreshRequested, 0);
                State state = Current;
                foreach (Window window in app.Windows)
                {
                    Apply(window, state);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (Volatile.Read(ref _refreshRequested) != 0)
                {
                    Refresh();
                }
            }
        });
    }

    public static void Reload()
    {
        ClearCache();
        State state = Current;
        Changed?.Invoke(state);
        Refresh();
    }

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            WindowBackdrop.Apply(window);
            Apply(window, Current);
        }
    }

    public static void Apply(Window window)
    {
        Apply(window, Current);
    }

    private static void Apply(Window window, State state)
    {
        try
        {
            if (window == null || window is Voidstrap.UI.Elements.Settings.MainWindow || (window.GetType().Namespace ?? string.Empty).Contains("Bootstrapper", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!state.DisplayEverywhere || string.IsNullOrWhiteSpace(state.FilePath) || !File.Exists(state.FilePath))
            {
                Remove(window);
                return;
            }
            if (window.Content is not UIElement content)
            {
                return;
            }
            GlobalBackgroundHost host;
            if (content is GlobalBackgroundHost existing)
            {
                host = existing;
            }
            else
            {
                window.Content = null;
                host = new GlobalBackgroundHost(content);
                window.Content = host;
                window.Closed -= OnWindowClosed;
                window.Closed += OnWindowClosed;
            }
            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(state.FilePath);
            string extension = Path.GetExtension(state.FilePath).ToLowerInvariant();
            BitmapSource? bitmap = extension is ".mp4" or ".webm" or ".avi" or ".mov"
                ? null
                : GetBackgroundImage(state.FilePath, Math.Max(800, (int)Math.Ceiling(window.ActualWidth)));
            host.Apply(state, bitmap, writeTimeUtc);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("GlobalBackground", "Background apply failed: " + ex.Message);
        }
    }

    private static void Remove(Window window)
    {
        window.Closed -= OnWindowClosed;
        if (window.Content is not GlobalBackgroundHost host)
        {
            return;
        }
        UIElement content = host.ContentElement;
        host.Children.Remove(content);
        host.Dispose();
        window.Content = content;
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }
        window.Closed -= OnWindowClosed;
        if (window.Content is GlobalBackgroundHost host)
        {
            host.Dispose();
        }
    }

    private static BitmapSource? GetBackgroundImage(string path, int decodeWidth)
    {
        DateTime writeTimeUtc;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }
        decodeWidth = Math.Clamp(decodeWidth, 320, 2560);
        lock (CacheLock)
        {
            if (_cachedImage != null && _cachedDecodeWidth == decodeWidth && _cachedWriteTimeUtc == writeTimeUtc && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedImage;
            }
            BitmapSource? image;
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) && Voidstrap.Utility.Platform.IsWindows)
            {
                BitmapImage animated = new();
                animated.BeginInit();
                animated.CacheOption = BitmapCacheOption.OnLoad;
                animated.DecodePixelWidth = decodeWidth;
                animated.UriSource = new Uri(path, UriKind.Absolute);
                animated.EndInit();
                image = animated;
            }
            else
            {
                image = Voidstrap.Utility.SafeImaging.FromFile(path, decodeWidth);
            }
            if (image == null)
            {
                return null;
            }
            if (image.CanFreeze && !image.IsFrozen)
            {
                image.Freeze();
            }
            _cachedPath = path;
            _cachedWriteTimeUtc = writeTimeUtc;
            _cachedDecodeWidth = decodeWidth;
            _cachedImage = image;
            return image;
        }
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            _cachedPath = null;
            _cachedWriteTimeUtc = default;
            _cachedDecodeWidth = 0;
            _cachedImage = null;
        }
    }

    private static State Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return Voidstrap.Utility.JsonFile.Deserialize<Config>(FilePath, Voidstrap.Utility.JsonOptions.Tolerant).ToState();
            }
        }
        catch
        {
        }
        return Normalize(new State(null, 1.0, 0.0, false));
    }

    private static State Normalize(State state)
    {
        string? path = string.IsNullOrWhiteSpace(state.FilePath) ? null : Path.GetFullPath(state.FilePath);
        return new State(path, Math.Clamp(state.GradientOpacity, 0.0, 1.0), Math.Clamp(state.BlackOverlayOpacity, 0.0, 1.0), state.DisplayEverywhere);
    }
}
