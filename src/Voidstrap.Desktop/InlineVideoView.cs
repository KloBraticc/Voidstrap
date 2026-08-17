using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Voidstrap.Core;

namespace Voidstrap.Desktop;

public sealed class InlineVideoView : UserControl, IDisposable
{
    private readonly Grid _root;
    private readonly StackPanel _poster;
    private readonly Panel _playerHost;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly Button _playButton;
    private readonly Button _browserButton;
    private readonly Button _stopButton;

    private NativeWebView? _webView;
    private VideoEmbed? _embed;
    private bool _disposed;

    public InlineVideoView()
    {
        _title = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.White,
            Text = "Watch the setup tutorial",
        };
        _status = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
            Text = "Plays here in the app",
        };
        _playButton = new Button
        {
            Content = "Play",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(18, 6, 18, 6),
        };
        _playButton.Click += OnPlayClicked;
        _browserButton = new Button
        {
            Content = "Open in browser",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };
        _browserButton.Click += OnBrowserClicked;

        _poster = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _poster.Children.Add(_title);
        _poster.Children.Add(_status);
        _poster.Children.Add(_playButton);
        _poster.Children.Add(_browserButton);

        _playerHost = new Panel { IsVisible = false };

        _stopButton = new Button
        {
            Content = "Stop",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Padding = new Thickness(10, 3, 10, 3),
            IsVisible = false,
        };
        _stopButton.Click += OnStopClicked;

        _root = new Grid { Background = new SolidColorBrush(Color.FromRgb(11, 11, 15)) };
        _root.Children.Add(_poster);
        _root.Children.Add(_playerHost);
        _root.Children.Add(_stopButton);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8.0),
            BorderThickness = new Thickness(1.0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            ClipToBounds = true,
            Child = _root,
        };

        DetachedFromVisualTree += OnDetached;
    }

    public string Title
    {
        get => _title.Text ?? string.Empty;
        set => _title.Text = value ?? string.Empty;
    }

    public bool IsPlaying => _webView is not null;

    public void SetVideo(string? url)
    {
        _embed = VideoEmbed.TryParse(url, out VideoEmbed? parsed) ? parsed : null;
        bool usable = _embed is not null;
        _playButton.IsEnabled = usable && IsInlinePlaybackSupported();
        _browserButton.IsEnabled = usable;
        if (!usable)
        {
            _status.Text = "This video link is not valid.";
            return;
        }
        _status.Text = _playButton.IsEnabled
            ? "Plays here in the app"
            : "Inline playback needs a system web runtime, use Open in browser.";
    }

    public static bool IsInlinePlaybackSupported()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return true;
        if (OperatingSystem.IsLinux())
            return LinuxWebViewRuntimeDetector.Detect() != LinuxWebViewRuntime.None;
        return false;
    }

    public void Play()
    {
        if (_disposed || _webView is not null)
            return;
        VideoEmbed? embed = _embed;
        if (embed is null)
            return;
        if (!IsInlinePlaybackSupported())
        {
            OpenInBrowser();
            return;
        }

        NativeWebView? view = null;
        try
        {
            view = new NativeWebView();
            _playerHost.Children.Add(view);
            view.NavigateToString(
                embed.BuildPlayerHtml(VideoEmbed.VirtualOrigin),
                new Uri(VideoEmbed.VirtualOrigin));
            _webView = view;
            view = null;

            _poster.IsVisible = false;
            _playerHost.IsVisible = true;
            _stopButton.IsVisible = true;
        }
        catch (Exception ex)
        {
            Teardown(view);
            Log("Inline playback failed: " + ex.Message);
            _status.Text = "Could not play here, use Open in browser.";
        }
    }

    public void Stop()
    {
        NativeWebView? view = _webView;
        _webView = null;
        Teardown(view);
        _poster.IsVisible = true;
        _playerHost.IsVisible = false;
        _stopButton.IsVisible = false;
    }

    public void OpenInBrowser()
    {
        VideoEmbed? embed = _embed;
        if (embed is null)
            return;
        try
        {
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo(embed.WatchUrl) { UseShellExecute = true };
            }
            else if (OperatingSystem.IsMacOS())
            {
                startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                startInfo.ArgumentList.Add(embed.WatchUrl);
            }
            else
            {
                startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                startInfo.ArgumentList.Add(embed.WatchUrl);
            }
            using Process? process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log("Could not open the browser: " + ex.Message);
            _status.Text = "Could not open your browser.";
        }
    }

    private void Teardown(NativeWebView? view)
    {
        if (view is null)
            return;
        try
        {
            view.Stop();
            view.NavigateToString("<html><body></body></html>", new Uri(VideoEmbed.VirtualOrigin));
        }
        catch (Exception ex)
        {
            Log("Could not blank the player: " + ex.Message);
        }
        try
        {
            _playerHost.Children.Remove(view);
        }
        catch (Exception ex)
        {
            Log("Could not detach the player: " + ex.Message);
        }
        try
        {
            (view as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log("Could not dispose the player: " + ex.Message);
        }
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        Play();
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        Stop();
    }

    private void OnBrowserClicked(object? sender, RoutedEventArgs e)
    {
        OpenInBrowser();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Stop();
    }

    private static void Log(string message)
    {
        Debug.WriteLine("[InlineVideoView] " + message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DetachedFromVisualTree -= OnDetached;
        _playButton.Click -= OnPlayClicked;
        _browserButton.Click -= OnBrowserClicked;
        _stopButton.Click -= OnStopClicked;
        Stop();
        GC.SuppressFinalize(this);
    }
}
