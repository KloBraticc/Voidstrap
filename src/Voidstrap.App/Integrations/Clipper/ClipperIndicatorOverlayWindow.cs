using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Resources;

namespace Voidstrap.Integrations.Clipper;

public sealed partial class ClipperIndicatorOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private static readonly object Gate = new();
    private static ClipperIndicatorOverlayWindow? _instance;
    private static int _reconcilePending;
    private static DateTime _savedPulseUntilUtc = DateTime.MinValue;

    private readonly DispatcherTimer _tickTimer;
    private readonly Border _redFrameBorder;
    private readonly Border _pillBorder;
    private readonly Ellipse _dot;
    private readonly TextBlock _labelText;
    private readonly TextBlock _subText;
    private bool _disposed;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    internal static void Reconcile()
    {
        Application? app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            if (Interlocked.Exchange(ref _reconcilePending, 1) != 0)
                return;
            try
            {
                app.Dispatcher.BeginInvoke(new Action(ReconcileCore));
            }
            catch
            {
                Interlocked.Exchange(ref _reconcilePending, 0);
            }
            return;
        }
        ReconcileCore();
    }

    public static void PulseSaved()
    {
        _savedPulseUntilUtc = DateTime.UtcNow.AddSeconds(3);
        Reconcile();
    }

    private static bool ShouldShowOverlay(out NativeClipperStatus status)
    {
        status = ClipperService.GetStatus();
        bool anyVisualEnabled = App.Settings.Prop.ClipperRedBorder || App.Settings.Prop.ClipperShowIndicator;
        if (!anyVisualEnabled)
            return false;

        if (DateTime.UtcNow < _savedPulseUntilUtc && App.Settings.Prop.ClipperShowIndicator)
            return true;

        if (!App.Settings.Prop.ClipperEnabled)
            return false;

        if (status.Recording)
            return true;

        if (ClipperService.CurrentMode == ClipperRecordingMode.Clip && status.Running && ClipperService.InGame)
            return true;

        return false;
    }

    private static void ReconcileCore()
    {
        Interlocked.Exchange(ref _reconcilePending, 0);
        if (!ShouldShowOverlay(out NativeClipperStatus status))
        {
            CloseActive();
            return;
        }

        ClipperIndicatorOverlayWindow inst;
        lock (Gate)
        {
            if (_instance is { _disposed: false } active)
                inst = active;
            else
                inst = new ClipperIndicatorOverlayWindow();
        }
        inst.UpdateVisualState(status);
    }

    internal static void CloseActive()
    {
        Application? app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(CloseActive));
            return;
        }
        ClipperIndicatorOverlayWindow? old;
        lock (Gate)
        {
            old = _instance;
            _instance = null;
        }
        if (old != null)
        {
            try
            {
                old.Close();
            }
            catch
            {
            }
        }
    }

    private ClipperIndicatorOverlayWindow()
    {
        lock (Gate)
        {
            _instance = this;
        }

        LinuxOverlaySurface.ReleaseMainWindowClaim(this);

        Title = "Voidstrap Clipper Indicator";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SyncScreenBounds();

        _redFrameBorder = new Border
        {
            BorderThickness = new Thickness(3),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0x24, 0x24)),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0x2B))
        };

        _labelText = new TextBlock
        {
            Text = Strings.Ui_Clipper_BadgeRec,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xF4, 0xF4, 0xF6)),
            VerticalAlignment = VerticalAlignment.Center
        };

        _subText = new TextBlock
        {
            Text = "00:00",
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE0, 0xE0, 0xE6)),
            VerticalAlignment = VerticalAlignment.Center
        };

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(_dot);
        row.Children.Add(_labelText);
        row.Children.Add(_subText);

        _pillBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(0, 42, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xDC, 0x18, 0x18, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = row
        };

        Grid root = new()
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        root.Children.Add(_redFrameBorder);
        root.Children.Add(_pillBorder);

        Content = root;

        Loaded += OnLoaded;
        Closed += OnClosed;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += OnTick;
        _tickTimer.Start();

        Show();
    }

    private void SyncScreenBounds()
    {
        double w = Math.Max(320, SystemParameters.PrimaryScreenWidth);
        double h = Math.Max(240, SystemParameters.PrimaryScreenHeight);
        if (Math.Abs(Left) > 0.5)
            Left = 0;
        if (Math.Abs(Top) > 0.5)
            Top = 0;
        if (Math.Abs(Width - w) > 0.5)
            Width = w;
        if (Math.Abs(Height - h) > 0.5)
            Height = h;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (!ShouldShowOverlay(out NativeClipperStatus status))
        {
            CloseActive();
            return;
        }
        SyncScreenBounds();
        UpdateVisualState(status);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int ex = GetWindowLong(hwnd, GwlExStyle);
                SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);
                try
                {
                    SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
                }
                catch
                {
                }
            }
        }
        else if (PortableOverlay.Active)
        {
            PortableOverlay.Prepare(this);
            PortableOverlay.MarkPassive(this);
        }
        UpdateVisualState(ClipperService.GetStatus());
    }

    private void UpdateVisualState(NativeClipperStatus st)
    {
        if (_disposed)
            return;

        bool activeCapture = st.Recording
            || (ClipperService.CurrentMode == ClipperRecordingMode.Clip && st.Running && ClipperService.InGame);

        _redFrameBorder.Visibility = App.Settings.Prop.ClipperRedBorder && activeCapture
            ? Visibility.Visible
            : Visibility.Collapsed;

        _pillBorder.Visibility = App.Settings.Prop.ClipperShowIndicator
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_pillBorder.Visibility != Visibility.Visible)
            return;

        if (DateTime.UtcNow < _savedPulseUntilUtc)
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x6A));
            _labelText.Text = Strings.Ui_Clipper_BadgeSaved;
            _subText.Text = "MP4";
            return;
        }

        if (st.Recording)
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0x2B));
            _labelText.Text = Strings.Ui_Clipper_BadgeRec;
            TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, st.RecordingMs));
            _subText.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            return;
        }

        if (st.Running)
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52));
            _labelText.Text = Strings.Ui_Clipper_BadgeClip;
            int secs = (int)Math.Round(st.BufferedMs / 1000.0);
            int target = Math.Max(secs, st.BufferSeconds > 0 ? st.BufferSeconds : App.Settings.Prop.ClipperBufferSeconds);
            _subText.Text = $"{secs}s/{target}s";
        }
        else
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));
            _labelText.Text = Strings.Ui_Clipper_BadgeClip;
            _subText.Text = Strings.Ui_Clipper_BadgeIdle;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _tickTimer.Stop();
        _tickTimer.Tick -= OnTick;
        lock (Gate)
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }
    }
}
