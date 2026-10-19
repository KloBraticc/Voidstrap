using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

        lock (InstanceGate)
        {
            if (_activeInstance != null)
            {
                try
                {
                    _activeInstance.Close();
                }
                catch
                {
                }
                _activeInstance = null;
            }
            application.Resources.Remove(ResourceKey);
        }
    }

    private static void CloseDuplicates(Application application, KeystrokeOverlayWindow keep)
    {
        List<Window> duplicates = new();
        foreach (Window window in application.Windows)
        {
            if (window is KeystrokeOverlayWindow overlay && !ReferenceEquals(overlay, keep))
                duplicates.Add(overlay);
        }
        foreach (Window duplicate in duplicates)
        {
            try
            {
                duplicate.Close();
            }
            catch
            {
            }
        }
    }

    public KeystrokeOverlayWindow()
    {
        lock (InstanceGate)
        {
            if (_activeInstance is { _disposed: false } existing && !ReferenceEquals(existing, this))
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

        double presetFactor = settings.KeystrokesScalePreset switch
        {
            "Compact" => 0.80,
            "Large" => 1.25,
            _ => 1.00
        };
        double scale = layout.SafeKeystrokesScale * presetFactor;
        OverlayScaleTransform.ScaleX = scale;
        OverlayScaleTransform.ScaleY = scale;
        RootLayout.Opacity = layout.SafeKeystrokesOpacity;

        RowW.Visibility = settings.KeystrokesShowKeys ? Visibility.Visible : Visibility.Collapsed;
        RowASD.Visibility = settings.KeystrokesShowKeys ? Visibility.Visible : Visibility.Collapsed;
        RowMouse.Visibility = settings.KeystrokesShowMouseButtons ? Visibility.Visible : Visibility.Collapsed;
        RowBottom.Visibility = settings.KeystrokesShowSpaceAndShift ? Visibility.Visible : Visibility.Collapsed;
        TextLmbCps.Visibility = settings.KeystrokesShowCps ? Visibility.Visible : Visibility.Collapsed;
        TextRmbCps.Visibility = settings.KeystrokesShowCps ? Visibility.Visible : Visibility.Collapsed;

        ApplyActiveTheme();
        _anchor?.Refresh();
    }

    public void ApplyActiveTheme()
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyActiveTheme));
            return;
        }

        var theme = KeystrokeThemeManager.ActiveTheme;

        var borders = new[]
        {
            KeyW, KeyA, KeyS, KeyD,
            KeyLmb, KeyRmb, KeyShift, KeySpace
        };

        var highlights = new[]
        {
            KeyWHighlight, KeyAHighlight, KeySHighlight, KeyDHighlight,
            KeyLmbHighlight, KeyRmbHighlight, KeyShiftHighlight, KeySpaceHighlight
        };

        var texts = new[]
        {
            KeyWText, KeyAText, KeySText, KeyDText,
            KeyLmbText, KeyRmbText, KeyShiftText, KeySpaceText
        };

        Brush bgBrush = theme.GetKeyBackgroundBrush();
        Brush borderBrush = theme.GetKeyBorderBrush();
        Brush pressedBgBrush = theme.GetKeyPressedBackgroundBrush();
        Brush textBrush = theme.GetKeyTextBrush();
        Brush cpsBrush = theme.GetCpsTextBrush();
        CornerRadius cornerRadius = new(theme.KeyCornerRadius);
        CornerRadius highlightCorner = new(Math.Max(0, theme.KeyCornerRadius - 1));
        Thickness borderThickness = new(theme.KeyBorderThickness);

        System.Windows.Media.FontFamily fontFamily = new(theme.FontFamily);
        FontWeight fontWeight = theme.FontWeight switch
        {
            "Normal" => FontWeights.Normal,
            "Medium" => FontWeights.Medium,
            "Bold" => FontWeights.Bold,
            "ExtraBold" => FontWeights.ExtraBold,
            _ => FontWeights.SemiBold
        };

        foreach (var b in borders)
        {
            b.Background = bgBrush;
            b.BorderBrush = borderBrush;
            b.BorderThickness = borderThickness;
            b.CornerRadius = cornerRadius;
            b.Effect = null;
        }

        foreach (var h in highlights)
        {
            h.Background = pressedBgBrush;
            h.CornerRadius = highlightCorner;
        }

        foreach (var t in texts)
        {
            t.Foreground = textBrush;
            t.FontFamily = fontFamily;
            t.FontWeight = fontWeight;
        }

        TextLmbCps.Foreground = cpsBrush;
        TextLmbCps.FontFamily = fontFamily;
        TextRmbCps.Foreground = cpsBrush;
        TextRmbCps.FontFamily = fontFamily;
    }

    public async Task TriggerPreviewSequenceAsync()
    {
        KeystrokeTarget[] sequence = {
            KeystrokeTarget.W, KeystrokeTarget.A, KeystrokeTarget.S, KeystrokeTarget.D,
            KeystrokeTarget.Lmb, KeystrokeTarget.Rmb, KeystrokeTarget.Shift, KeystrokeTarget.Space
        };

        foreach (var key in sequence)
        {
            UpdateVisual(key, true);
            if (key is KeystrokeTarget.Lmb or KeystrokeTarget.Rmb)
                KeystrokeAudioService.PlayMousePress();
            else
                KeystrokeAudioService.PlayKeyboardPress();

            await Task.Delay(130);
            UpdateVisual(key, false);
            await Task.Delay(70);
        }
    }

    private void OnOverlayLoaded(object? sender, RoutedEventArgs e)
    {
        MakeClickThrough();
        KeystrokeInputService.StateChanged += OnKeystrokeStateChanged;
        KeystrokeThemeManager.ActiveThemeChanged += OnActiveThemeChanged;
        _cpsTimer.Start();
        RefreshConfiguration();
    }

    private void OnActiveThemeChanged()
    {
        ApplyActiveTheme();
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        KeystrokeInputService.StateChanged -= OnKeystrokeStateChanged;
        KeystrokeThemeManager.ActiveThemeChanged -= OnActiveThemeChanged;
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
        (Border? key, Border? highlight, TextBlock? text, TextBlock? cps) = GetElements(target);
        if (highlight == null || text == null || key == null)
            return;

        var theme = KeystrokeThemeManager.ActiveTheme;
        highlight.BeginAnimation(UIElement.OpacityProperty, null);
        if (isDown)
        {
            highlight.Opacity = 1.0;
            text.Foreground = theme.GetKeyPressedTextBrush();
            if (cps != null)
                cps.Foreground = theme.GetKeyPressedCpsTextBrush();

            if (theme.KeyScaleOnPress < 0.999)
            {
                key.RenderTransformOrigin = new Point(0.5, 0.5);
                key.RenderTransform = new ScaleTransform(theme.KeyScaleOnPress, theme.KeyScaleOnPress);
            }
        }
        else
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130));
            highlight.BeginAnimation(UIElement.OpacityProperty, anim);
            text.Foreground = theme.GetKeyTextBrush();
            if (cps != null)
                cps.Foreground = theme.GetCpsTextBrush();
            key.RenderTransform = Transform.Identity;
        }
    }

    private (Border? Key, Border? Highlight, TextBlock? Text, TextBlock? Cps) GetElements(KeystrokeTarget target)
    {
        return target switch
        {
            KeystrokeTarget.W => (KeyW, KeyWHighlight, KeyWText, null),
            KeystrokeTarget.A => (KeyA, KeyAHighlight, KeyAText, null),
            KeystrokeTarget.S => (KeyS, KeySHighlight, KeySText, null),
            KeystrokeTarget.D => (KeyD, KeyDHighlight, KeyDText, null),
            KeystrokeTarget.Space => (KeySpace, KeySpaceHighlight, KeySpaceText, null),
            KeystrokeTarget.Shift => (KeyShift, KeyShiftHighlight, KeyShiftText, null),
            KeystrokeTarget.Lmb => (KeyLmb, KeyLmbHighlight, KeyLmbText, TextLmbCps),
            KeystrokeTarget.Rmb => (KeyRmb, KeyRmbHighlight, KeyRmbText, TextRmbCps),
            _ => (null, null, null, null)
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
