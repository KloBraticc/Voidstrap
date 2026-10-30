using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    private readonly Dictionary<string, (Border Key, Border Highlight, TextBlock Text, TextBlock? Cps)> _keyElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly KeystrokeScriptView _scriptView = new(liveMouse: true);
    private string? _loadedScript;
    private string? _scriptFailure;
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

        try
        {
            InitializeComponent();
        }
        catch
        {
            lock (InstanceGate)
            {
                if (ReferenceEquals(_activeInstance, this))
                    _activeInstance = null;
            }
            throw;
        }
        LinuxOverlaySurface.ReleaseMainWindowClaim(this);
        ScriptHost.Content = _scriptView;
        _scriptView.ScriptStateChanged += OnScriptStateChanged;

        _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center, customPosition: ComputeKeystrokesPosition);

        _cpsTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
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
        if (ApplyScript(theme))
            return;

        Brush borderBrush = theme.GetKeyBorderBrush();
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

        if (theme.ShowMouseCircle)
        {
            OverlayMouseCircle.Visibility = Visibility.Visible;
            double size = theme.MouseCircleSize > 0 ? theme.MouseCircleSize : 88.0;
            OverlayMouseCircle.Width = size;
            OverlayMouseCircle.Height = size;
            OverlayMouseCircle.CornerRadius = new CornerRadius(size / 2.0);
            OverlayMouseCircle.Background = theme.GetKeyBackgroundBrush();
            OverlayMouseCircle.BorderBrush = borderBrush;
            OverlayMouseCircle.BorderThickness = borderThickness;

            double dotSize = Math.Max(12, Math.Round(size * 0.27));
            OverlayMouseDot.Width = dotSize;
            OverlayMouseDot.Height = dotSize;
            OverlayMouseDot.Fill = theme.GetKeyPressedBackgroundBrush();
        }
        else
        {
            OverlayMouseCircle.Visibility = Visibility.Collapsed;
        }

        _keyElements.Clear();

        if (theme.LayoutRows != null && theme.LayoutRows.Count > 0)
        {
            DefaultKeysPanel.Visibility = Visibility.Collapsed;
            DynamicKeysPanel.Visibility = Visibility.Visible;
            DynamicKeysPanel.Children.Clear();

            var settings = App.Settings.Prop;

            foreach (var row in theme.LayoutRows)
            {
                var visibleKeys = row.Where(k => KeystrokeThemeManager.IsKeyAllowedBySettings(k, settings)).ToList();
                if (visibleKeys.Count == 0)
                    continue;

                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 3)
                };

                for (int i = 0; i < visibleKeys.Count; i++)
                {
                    string keyName = visibleKeys[i];
                    var cap = theme.Keycaps.TryGetValue(keyName, out var c) ? c : null;

                    double width = cap?.Width ?? 46;
                    double height = cap?.Height ?? 46;
                    double cr = cap?.CornerRadius ?? theme.KeyCornerRadius;

                    var keyBorder = new Border
                    {
                        Width = width,
                        Height = height,
                        CornerRadius = new CornerRadius(cr),
                        BorderThickness = borderThickness,
                        BorderBrush = borderBrush,
                        Background = theme.GetKeyBrush(keyName, false),
                        Effect = null,
                        Margin = new Thickness(0, 0, i < visibleKeys.Count - 1 ? 3 : 0, 0)
                    };

                    var grid = new Grid();
                    var highlight = new Border
                    {
                        CornerRadius = new CornerRadius(Math.Max(0, cr - 1)),
                        Opacity = 0,
                        Background = theme.GetKeyBrush(keyName, true)
                    };
                    grid.Children.Add(highlight);

                    string label = !string.IsNullOrEmpty(cap?.Text) ? cap.Text : keyName;
                    var textBlock = new TextBlock
                    {
                        Text = label,
                        FontSize = Math.Max(10, Math.Min(15, height * 0.33)),
                        FontWeight = fontWeight,
                        FontFamily = fontFamily,
                        Foreground = textBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = (cap?.HideText == true) ? Visibility.Collapsed : Visibility.Visible
                    };
                    grid.Children.Add(textBlock);

                    keyBorder.Child = grid;
                    rowPanel.Children.Add(keyBorder);
                    _keyElements[keyName] = (keyBorder, highlight, textBlock, null);
                }

                DynamicKeysPanel.Children.Add(rowPanel);
            }
        }
        else
        {
            DefaultKeysPanel.Visibility = Visibility.Visible;
            DynamicKeysPanel.Visibility = Visibility.Collapsed;

            var keyTuples = new (Border Key, Border Highlight, TextBlock Text, string Name)[]
            {
                (KeyW, KeyWHighlight, KeyWText, "W"),
                (KeyA, KeyAHighlight, KeyAText, "A"),
                (KeyS, KeySHighlight, KeySText, "S"),
                (KeyD, KeyDHighlight, KeyDText, "D"),
                (KeyLmb, KeyLmbHighlight, KeyLmbText, "LMB"),
                (KeyRmb, KeyRmbHighlight, KeyRmbText, "RMB"),
                (KeyShift, KeyShiftHighlight, KeyShiftText, "Shift"),
                (KeySpace, KeySpaceHighlight, KeySpaceText, "Space")
            };

            foreach (var item in keyTuples)
            {
                item.Key.Background = theme.GetKeyBrush(item.Name, false);
                item.Key.BorderBrush = borderBrush;
                item.Key.BorderThickness = borderThickness;
                item.Key.CornerRadius = cornerRadius;
                item.Key.Effect = null;

                item.Highlight.Background = theme.GetKeyBrush(item.Name, true);
                item.Highlight.CornerRadius = highlightCorner;

                item.Text.Foreground = textBrush;
                item.Text.FontFamily = fontFamily;
                item.Text.FontWeight = fontWeight;

                var cap = theme.Keycaps.TryGetValue(item.Name, out var c) ? c : null;

                double defaultWidth = item.Name switch
                {
                    "LMB" or "RMB" => 71,
                    "Shift" => 51,
                    "Space" => 91,
                    _ => 46
                };
                double defaultHeight = item.Name switch
                {
                    "Shift" or "Space" => 34,
                    _ => 46
                };
                string defaultText = item.Name switch
                {
                    "Shift" => "SHIFT",
                    "Space" => "—",
                    _ => item.Name
                };

                item.Key.Width = (cap?.Width.HasValue == true && cap.Width.Value > 0) ? cap.Width.Value : defaultWidth;
                item.Key.Height = (cap?.Height.HasValue == true && cap.Height.Value > 0) ? cap.Height.Value : defaultHeight;

                if (cap?.CornerRadius.HasValue == true)
                {
                    item.Key.CornerRadius = new CornerRadius(cap.CornerRadius.Value);
                    item.Highlight.CornerRadius = new CornerRadius(Math.Max(0, cap.CornerRadius.Value - 1));
                }
                else
                {
                    item.Key.CornerRadius = cornerRadius;
                    item.Highlight.CornerRadius = highlightCorner;
                }

                item.Text.Text = !string.IsNullOrEmpty(cap?.Text) ? cap.Text : defaultText;
                item.Text.Visibility = (cap?.HideText == true) ? Visibility.Collapsed : Visibility.Visible;

                TextBlock? cps = item.Name switch
                {
                    "LMB" => TextLmbCps,
                    "RMB" => TextRmbCps,
                    _ => null
                };
                _keyElements[item.Name] = (item.Key, item.Highlight, item.Text, cps);
            }

            TextLmbCps.Foreground = cpsBrush;
            TextLmbCps.FontFamily = fontFamily;
            TextRmbCps.Foreground = cpsBrush;
            TextRmbCps.FontFamily = fontFamily;
        }

        foreach (string keyName in _keyElements.Keys.ToList())
        {
            if (KeystrokeInputService.IsNamedDown(keyName))
                UpdateVisualNamed(keyName, true);
        }
    }

    private bool ApplyScript(KeystrokeTheme theme)
    {
        string? source = theme.ScriptSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            _scriptView.Unload();
            _loadedScript = null;
            ScriptHost.Visibility = Visibility.Collapsed;
            OverlayMainLayout.Visibility = Visibility.Visible;
            return false;
        }

        if (source != _loadedScript || !_scriptView.IsRunning)
        {
            _loadedScript = source;
            _scriptFailure = null;
            _scriptView.Load(source, System.IO.Path.Combine(Paths.CustomKeystrokeThemes, theme.Name));
        }
        if (!_scriptView.IsRunning)
        {
            ScriptHost.Visibility = Visibility.Collapsed;
            OverlayMainLayout.Visibility = Visibility.Visible;
            return false;
        }

        ScriptHost.Visibility = Visibility.Visible;
        OverlayMainLayout.Visibility = Visibility.Collapsed;
        return true;
    }

    private void OnScriptStateChanged(string? error)
    {
        const string LOG_IDENT = "KeystrokeOverlayWindow::OnScriptStateChanged";

        if (error == null || error == _scriptFailure)
            return;
        _scriptFailure = error;
        App.Logger.WriteLine(LOG_IDENT, "Overlay code error: " + error);
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e) => _anchor.Refresh();

    private void OnOverlayLoaded(object? sender, RoutedEventArgs e)
    {
        MakeClickThrough();
        KeystrokeInputService.KeyNamedChanged += OnKeyNamedChanged;
        KeystrokeThemeManager.ActiveThemeChanged += OnActiveThemeChanged;
        SizeChanged += OnOverlaySizeChanged;
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
        KeystrokeInputService.KeyNamedChanged -= OnKeyNamedChanged;
        KeystrokeThemeManager.ActiveThemeChanged -= OnActiveThemeChanged;
        SizeChanged -= OnOverlaySizeChanged;
        _scriptView.ScriptStateChanged -= OnScriptStateChanged;
        _scriptView.Dispose();
        _cpsTimer.Stop();
        _cpsTimer.Tick -= OnCpsTimerTick;
        _anchor.Dispose();

        lock (InstanceGate)
        {
            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
                Application.Current?.Resources.Remove(ResourceKey);
            }
        }
    }

    private void OnCpsTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        if (App.Settings.Prop.KeystrokesShowCps && !_scriptView.IsRunning)
            UpdateCpsText();
    }

    private void UpdateCpsText()
    {
        string lmb = KeystrokeInputService.GetLmbCps() + " CPS";
        string rmb = KeystrokeInputService.GetRmbCps() + " CPS";
        if (TextLmbCps.Text != lmb)
            TextLmbCps.Text = lmb;
        if (TextRmbCps.Text != rmb)
            TextRmbCps.Text = rmb;
    }

    private void OnKeyNamedChanged(string keyName, bool isDown)
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnKeyNamedChanged(keyName, isDown)));
            return;
        }

        if (_scriptView.IsRunning)
        {
            _scriptView.SetKey(keyName, isDown);
            return;
        }

        UpdateVisualNamed(keyName, isDown);
        if (isDown && keyName is "LMB" or "RMB" && App.Settings.Prop.KeystrokesShowCps)
            UpdateCpsText();
    }

    private void UpdateVisualNamed(string keyName, bool isDown)
    {
        if (!_keyElements.TryGetValue(keyName, out var item))
            return;

        var (key, highlight, text, cps) = item;
        var theme = KeystrokeThemeManager.ActiveTheme;
        highlight.BeginAnimation(UIElement.OpacityProperty, null);

        if (theme.Keycaps.TryGetValue(keyName, out var cap))
        {
            if (!string.IsNullOrEmpty(cap.Text))
                text.Text = cap.Text;
            text.Visibility = cap.HideText ? Visibility.Collapsed : Visibility.Visible;
        }

        if (isDown)
        {
            highlight.Opacity = 1.0;
            highlight.Background = theme.GetKeyBrush(keyName, true);
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
            key.Background = theme.GetKeyBrush(keyName, false);
            text.Foreground = theme.GetKeyTextBrush();
            if (cps != null)
                cps.Foreground = theme.GetCpsTextBrush();
            key.RenderTransform = Transform.Identity;
            key.Effect = null;
        }

        if (theme.ShowMouseCircle && keyName is "LMB" or "RMB")
        {
            OverlayMouseDot.RenderTransformOrigin = new Point(0.5, 0.5);
            OverlayMouseDot.RenderTransform = isDown ? new ScaleTransform(1.2, 1.2) : Transform.Identity;
        }
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
