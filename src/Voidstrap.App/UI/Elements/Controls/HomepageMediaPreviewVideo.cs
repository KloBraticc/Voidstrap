using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.UI.Elements.Controls;

public class HomepageMediaPreviewVideo : ContentControl
{
    private MediaElement? _media;
    private readonly Image? _portableImage;
    private DispatcherTimer? _portableTimer;
    private Voidstrap.Integrations.Overlays.HomepageBackgroundMedia? _portableMedia;
    private long _portableFrameVersion;
    private bool _portableFailureLogged;
    private WriteableBitmap? _portableBitmap;
    private readonly TextBlock? _portableError;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(HomepageMediaPreviewVideo),
        new PropertyMetadata(string.Empty, OnSourcePathChanged));

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public HomepageMediaPreviewVideo()
    {
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            _portableImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false
            };
            _portableError = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10),
                Visibility = Visibility.Collapsed
            };
            Grid preview = new();
            preview.Children.Add(_portableImage);
            preview.Children.Add(_portableError);
            Content = preview;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            if (IsVisible && IsLoaded)
                StartPortableMedia();
            else
                StopPortableMedia();
            return;
        }
        if (IsVisible && IsLoaded)
            EnsureMedia();
        else
            ReleaseMedia();
    }

    private void EnsureMedia()
    {
        if (_media != null)
            return;
        _media = new MediaElement
        {
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false,
            LoadedBehavior = MediaState.Play,
            UnloadedBehavior = MediaState.Close,
            Volume = 0.0
        };
        _media.SetBinding(MediaElement.SourceProperty, new Binding("HomepageMediaPreviewVideoUri"));
        _media.MediaEnded += OnMediaEnded;
        Content = _media;
    }

    private void ReleaseMedia()
    {
        MediaElement? media = _media;
        if (media == null)
            return;
        _media = null;
        media.MediaEnded -= OnMediaEnded;
        BindingOperations.ClearBinding(media, MediaElement.SourceProperty);
        try
        {
            media.Stop();
            media.Close();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("HomepageMediaPreviewVideo::ReleaseMedia", ex.Message);
        }
        Content = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        IsVisibleChanged -= OnIsVisibleChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            if (IsVisible)
                StartPortableMedia();
            return;
        }

        if (IsVisible)
            EnsureMedia();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        IsVisibleChanged -= OnIsVisibleChanged;
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            StopPortableMedia();
            return;
        }

        ReleaseMedia();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_media is null)
            return;

        try
        {
            _media.Position = TimeSpan.Zero;
            _media.Play();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("HomepageMediaPreviewVideo::OnMediaEnded", "The preview could not loop: " + ex.Message);
        }
    }

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.UsesPortableUi && d is HomepageMediaPreviewVideo preview && preview.IsLoaded && preview.IsVisible)
            preview.StartPortableMedia();
    }

    private void StartPortableMedia()
    {
        StopPortableMedia();
        if (_portableError != null)
        {
            _portableError.Text = string.Empty;
            _portableError.Visibility = Visibility.Collapsed;
        }
        if (_portableImage is null || string.IsNullOrWhiteSpace(SourcePath))
            return;
        if (!File.Exists(SourcePath))
        {
            ShowPortableError("The selected media file could not be found");
            return;
        }

        _portableFrameVersion = 0;
        _portableFailureLogged = false;
        _portableMedia = new Voidstrap.Integrations.Overlays.HomepageBackgroundMedia(SourcePath, 30d, 960, 540);
        _portableTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000d / 30d)
        };
        _portableTimer.Tick += OnPortableFrameTick;
        _portableTimer.Start();
    }

    private void OnPortableFrameTick(object? sender, EventArgs e)
    {
        if (_portableImage is null || _portableMedia is null)
            return;

        try
        {
            _portableMedia.TryReadFrame(_portableFrameVersion, (pixels, width, height, version) =>
            {
                int stride = checked(width * 4);
                if (_portableBitmap == null || _portableBitmap.PixelWidth != width || _portableBitmap.PixelHeight != height)
                {
                    _portableBitmap = new WriteableBitmap(width, height, 96d, 96d, PixelFormats.Bgra32, null);
                    _portableImage.Source = _portableBitmap;
                }
                _portableBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                _portableFrameVersion = version;
            });
            if (_portableMedia.Failure is string failure)
            {
                ShowPortableError(failure);
                StopPortableMedia();
            }
        }
        catch (Exception ex)
        {
            if (_portableFailureLogged)
                return;
            _portableFailureLogged = true;
            ShowPortableError("The media preview could not be rendered");
            App.Logger.WriteLine("HomepageMediaPreviewVideo::PortableFrame", "The media preview could not be rendered: " + ex.Message);
        }
    }

    private void ShowPortableError(string message)
    {
        if (_portableError == null)
            return;
        _portableError.Text = message;
        _portableError.Visibility = Visibility.Visible;
    }

    private void StopPortableMedia()
    {
        if (_portableTimer != null)
        {
            _portableTimer.Stop();
            _portableTimer.Tick -= OnPortableFrameTick;
            _portableTimer = null;
        }

        Voidstrap.Integrations.Overlays.HomepageBackgroundMedia? media = _portableMedia;
        _portableMedia = null;
        media?.Dispose();
        if (_portableImage != null)
            _portableImage.Source = null;
        _portableBitmap = null;
    }
}
