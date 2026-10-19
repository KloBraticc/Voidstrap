using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Keystrokes;

public partial class KeystrokeOverlayWindow : Window
{
    private const string ResourceKey = "KeystrokeOverlayWindow";
    private static readonly object InstanceGate = new();
    private static KeystrokeOverlayWindow? _activeInstance;
    private static int _reconcilePending;

    private readonly RobloxOverlayAnchor _anchor;
    private readonly DispatcherTimer _cpsTimer;
    private bool _disposed;

    public static KeystrokeOverlayWindow? ActiveInstance => _activeInstance;

    public static KeystrokeOverlayWindow GetOrCreate()
    {
        Application application = Application.Current ?? throw new InvalidOperationException("The application is unavailable");
        if (!application.Dispatcher.CheckAccess())
            return application.Dispatcher.Invoke(GetOrCreate);

        KeystrokeOverlayWindow instance;
        lock (InstanceGate)
        {
            if (_activeInstance is { _disposed: false } active)
                instance = active;
            else
                instance = new KeystrokeOverlayWindow();
            application.Resources[ResourceKey] = instance;
        }
        CloseDuplicates(application, instance);
        return instance;
    }

    public static void Reconcile()
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
            return;

        if (!application.Dispatcher.CheckAccess())
        {
            if (System.Threading.Interlocked.Exchange(ref _reconcilePending, 1) != 0)
                return;
            try
            {
                application.Dispatcher.BeginInvoke(new Action(ReconcileCore));
            }
            catch (InvalidOperationException)
            {
                System.Threading.Interlocked.Exchange(ref _reconcilePending, 0);
            }
            return;
        }
        ReconcileCore();
    }

    private static void ReconcileCore()
    {
        System.Threading.Interlocked.Exchange(ref _reconcilePending, 0);
        bool overlayWanted = App.Settings.Prop.KeystrokesOverlayEnabled;
        bool audioWanted = App.Settings.Prop.KeystrokesKeyboardSound || App.Settings.Prop.KeystrokesMouseSound;

        if (overlayWanted || audioWanted)
            KeystrokeInputService.Start();
        else
            KeystrokeInputService.Stop();

        if (!overlayWanted)
        {
            CloseAll();
            return;
        }

        GetOrCreate().RefreshConfiguration();
    }

    public static void CloseAll()
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
            return;

        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(new Action(CloseAll));
            return;
        }

        List<KeystrokeOverlayWindow> list = [];
        foreach (Window w in application.Windows)
        {
            if (w is KeystrokeOverlayWindow k && !k._disposed)
                list.Add(k);
        }

        foreach (var window in list)
        {
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        lock (InstanceGate)
        {
            _activeInstance = null;
        }
        application.Resources.Remove(ResourceKey);
    }

    private static void CloseDuplicates(Application app, KeystrokeOverlayWindow active)
    {
        foreach (Window w in app.Windows)
        {
            if (w is KeystrokeOverlayWindow k && !ReferenceEquals(k, active))
            {
                try
                {
                    k.Close();
                }
                catch
                {
                }
            }
        }
    }

    public KeystrokeOverlayWindow()
    {
        lock (InstanceGate)
        {
            if (_activeInstance is { _disposed: false })
                throw new InvalidOperationException("A keystroke overlay surface is already active");
            _activeInstance = this;
        }

        InitializeComponent();
        LinuxOverlaySurface.ReleaseMainWindowClaim(this);

        _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center, customPosition: ComputeKeystrokesPosition);

        _cpsTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _cpsTimer.Tick += OnCpsTimerTick;

        RefreshConfiguration();
        Show();
    }

    public static (int Left, int Top) ComputeKeystrokesPosition(RobloxWindowRect rect, double scaleX, double scaleY, int width, int height)
    {
        var layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        int marginX = (int)Math.Round(layout.SafeKeystrokesEdgeSpacing * scaleX);
        int marginY = (int)Math.Round(layout.SafeKeystrokesEdgeSpacing * scaleY);
        int minLeft = rect.Left + marginX;
        int maxLeft = Math.Max(minLeft, rect.Left + rect.Width - width - marginX);
        int minTop = rect.Top + marginY;
        int maxTop = Math.Max(minTop, rect.Top + rect.Height - height - marginY);
        int left = minLeft + (int)Math.Round((maxLeft - minLeft) * (layout.SafeKeystrokesHorizontal / 100.0));
        int top = minTop + (int)Math.Round((maxTop - minTop) * (layout.SafeKeystrokesVertical / 100.0));
        return (left, top);
    }

    public void RefreshConfiguration()
    {
        if (_disposed)
            return;

        var settings = App.Settings.Prop;
        var layout = settings.OverlayLayout ?? new OverlayLayoutSettings();

        double scale = layout.SafeKeystrokesScale;
        OverlayScaleTransform.ScaleX = scale;
        OverlayScaleTransform.ScaleY = scale;
        RootLayout.Opacity = layout.SafeKeystrokesOpacity;

        RowW.Visibility = settings.KeystrokesShowKeys ? Visibility.Visible : Visibility.Collapsed;
        RowASD.Visibility = settings.KeystrokesShowKeys ? Visibility.Visible : Visibility.Collapsed;
        RowMouse.Visibility = settings.KeystrokesShowMouseButtons ? Visibility.Visible : Visibility.Collapsed;
        RowBottom.Visibility = settings.KeystrokesShowSpaceAndShift ? Visibility.Visible : Visibility.Collapsed;
        TextLmbCps.Visibility = settings.KeystrokesShowCps ? Visibility.Visible : Visibility.Collapsed;
        TextRmbCps.Visibility = settings.KeystrokesShowCps ? Visibility.Visible : Visibility.Collapsed;

        _anchor?.Refresh();
    }

    private void OnOverlayLoaded(object? sender, RoutedEventArgs e)
    {
        MakeClickThrough();
        KeystrokeInputService.StateChanged += OnKeystrokeStateChanged;
        _cpsTimer.Start();
        RefreshConfiguration();
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        KeystrokeInputService.StateChanged -= OnKeystrokeStateChanged;
        _cpsTimer.Stop();
        _anchor.Dispose();

        lock (InstanceGate)
        {
            if (ReferenceEquals(_activeInstance, this))
                _activeInstance = null;
        }
    }

    private void OnCpsTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        if (App.Settings.Prop.KeystrokesShowCps)
        {
            TextLmbCps.Text = KeystrokeInputService.GetLmbCps() + " CPS";
            TextRmbCps.Text = KeystrokeInputService.GetRmbCps() + " CPS";
        }
    }

    private void OnKeystrokeStateChanged(KeystrokeInputState state)
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnKeystrokeStateChanged(state)));
            return;
        }

        UpdateVisual(state.Target, state.IsDown);
    }

    private void UpdateVisual(KeystrokeTarget target, bool isDown)
    {
        (Border? highlight, TextBlock? text) = GetElements(target);
        if (highlight == null || text == null)
            return;

        highlight.BeginAnimation(UIElement.OpacityProperty, null);
        if (isDown)
        {
            highlight.Opacity = 1.0;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        }
        else
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130));
            highlight.BeginAnimation(UIElement.OpacityProperty, anim);
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
    }

    private (Border? Highlight, TextBlock? Text) GetElements(KeystrokeTarget target)
    {
        return target switch
        {
            KeystrokeTarget.W => (KeyWHighlight, KeyWText),
            KeystrokeTarget.A => (KeyAHighlight, KeyAText),
            KeystrokeTarget.S => (KeySHighlight, KeySText),
            KeystrokeTarget.D => (KeyDHighlight, KeyDText),
            KeystrokeTarget.Space => (KeySpaceHighlight, KeySpaceText),
            KeystrokeTarget.Shift => (KeyShiftHighlight, KeyShiftText),
            KeystrokeTarget.Lmb => (KeyLmbHighlight, KeyLmbText),
            KeystrokeTarget.Rmb => (KeyRmbHighlight, KeyRmbText),
            _ => (null, null)
        };
    }

    private void MakeClickThrough()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            LinuxOverlaySurface.MakeClickThrough(this);
            return;
        }

        nint hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLongW(hwnd, -20);
        _ = SetWindowLongW(hwnd, -20, style | 0x20 | 0x80 | 0x08000000);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);
}
