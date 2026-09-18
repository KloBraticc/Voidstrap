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
    private readonly Image? _linuxImage;
    private DispatcherTimer? _linuxTimer;
    private Voidstrap.Integrations.Overlays.HomepageBackgroundMedia? _linuxMedia;
    private long _linuxFrameVersion;
    private bool _linuxFailureLogged;

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
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            _linuxImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false
            };
            Content = _linuxImage;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            return;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.IsLinux)
            return;
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
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            StartLinuxMedia();
            return;
        }

        if (IsVisible)
            EnsureMedia();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            StopLinuxMedia();
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
        if (Voidstrap.Utility.Platform.IsLinux && d is HomepageMediaPreviewVideo preview && preview.IsLoaded)
            preview.StartLinuxMedia();
    }

    private void StartLinuxMedia()
    {
        StopLinuxMedia();
        if (_linuxImage is null || string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
            return;

        _linuxFrameVersion = 0;
        _linuxFailureLogged = false;
        _linuxMedia = new Voidstrap.Integrations.Overlays.HomepageBackgroundMedia(SourcePath, 30d);
        _linuxTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000d / 30d)
        };
        _linuxTimer.Tick += OnLinuxFrameTick;
        _linuxTimer.Start();
    }

    private void OnLinuxFrameTick(object? sender, EventArgs e)
    {
        if (_linuxImage is null || _linuxMedia is null)
            return;

        try
        {
            _linuxMedia.TryReadFrame(_linuxFrameVersion, (pixels, width, height, version) =>
            {
                int stride = checked(width * 4);
                BitmapSource frame = BitmapSource.Create(width, height, 96d, 96d, PixelFormats.Bgra32, null, pixels, stride);
                if (frame.CanFreeze)
                    frame.Freeze();
                _linuxImage.Source = frame;
                _linuxFrameVersion = version;
            });
        }
        catch (Exception ex)
        {
            if (_linuxFailureLogged)
                return;
            _linuxFailureLogged = true;
            App.Logger.WriteLine("HomepageMediaPreviewVideo::LinuxFrame", "The media preview could not be rendered: " + ex.Message);
        }
    }

    private void StopLinuxMedia()
    {
        if (_linuxTimer != null)
        {
            _linuxTimer.Stop();
            _linuxTimer.Tick -= OnLinuxFrameTick;
            _linuxTimer = null;
        }

        Voidstrap.Integrations.Overlays.HomepageBackgroundMedia? media = _linuxMedia;
        _linuxMedia = null;
        media?.Dispose();
        if (_linuxImage != null)
            _linuxImage.Source = null;
    }
}
