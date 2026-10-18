using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.UI;

namespace Voidstrap.Integrations.Clipper;

public sealed partial class ClipperIndicatorOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly object Gate = new();
    private static ClipperIndicatorOverlayWindow? _instance;
    private static int _reconcilePending;

    private readonly RobloxOverlayAnchor _anchor;
    private readonly DispatcherTimer _tickTimer;
    private readonly Ellipse _dot;
    private readonly TextBlock _labelText;
    private readonly TextBlock _subText;
    private DateTime _savedPulseUntilUtc = DateTime.MinValue;
    private bool _disposed;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
        Application? app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted)
            return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            lock (Gate)
            {
                if (_instance is { _disposed: false } inst)
                {
                    inst._savedPulseUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    inst.UpdateVisualState();
                }
            }
        }));
    }

    private static void ReconcileCore()
    {
        Interlocked.Exchange(ref _reconcilePending, 0);
        bool wanted = App.Settings.Prop.ClipperEnabled
            && App.Settings.Prop.ClipperShowIndicator
            && ClipperService.InGame;

        if (!wanted)
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
        inst.UpdateVisualState();
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
        Width = 148;
        Height = 32;

        _dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52))
        };

        _labelText = new TextBlock
        {
            Text = "REPLAY",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xEC, 0xF0, 0xF0, 0xF2)),
            VerticalAlignment = VerticalAlignment.Center
        };

        _subText = new TextBlock
        {
            Text = "0s",
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xD4, 0xD4, 0xD8)),
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

        Border pill = new()
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = row
        };

        Content = pill;

        Loaded += OnLoaded;
        Closed += OnClosed;

        _anchor = new RobloxOverlayAnchor(
            this,
            hideWhenUnfocused: true,
            placement: RobloxOverlayPlacement.TopRight,
            customPosition: ComputePosition);

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => UpdateVisualState();
        _tickTimer.Start();

        Show();
    }

    private static (int Left, int Top) ComputePosition(RobloxWindowRect rect, double scaleX, double scaleY, int width, int height)
    {
        int marginX = (int)Math.Round(14 * scaleX);
        int marginY = (int)Math.Round(46 * scaleY);
        int left = Math.Max(rect.Left, rect.Left + rect.Width - width - marginX);
        int top = Math.Max(rect.Top, rect.Top + marginY);
        return (left, top);
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
            }
        }
        else if (PortableOverlay.Active)
        {
            PortableOverlay.Prepare(this);
            PortableOverlay.MarkPassive(this);
        }
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (_disposed)
            return;

        if (!string.IsNullOrEmpty(ClipperService.ProvisionStatus))
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0xD4, 0xA7, 0x4A));
            _labelText.Text = "CLIP";
            _subText.Text = "SETUP";
            return;
        }

        if (DateTime.UtcNow < _savedPulseUntilUtc)
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x6A));
            _labelText.Text = "SAVED";
            _subText.Text = "MP4";
            return;
        }

        NativeClipperStatus st = ClipperService.GetStatus();
        if (st.Running)
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52));
            _labelText.Text = "REC";
            int secs = (int)Math.Round(st.BufferedMs / 1000.0);
            int target = Math.Max(secs, st.BufferSeconds > 0 ? st.BufferSeconds : App.Settings.Prop.ClipperBufferSeconds);
            _subText.Text = $"{secs}s/{target}s";
        }
        else
        {
            _dot.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));
            _labelText.Text = "CLIP";
            _subText.Text = "IDLE";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _tickTimer.Stop();
        _anchor.Dispose();
        lock (Gate)
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }
    }
}
