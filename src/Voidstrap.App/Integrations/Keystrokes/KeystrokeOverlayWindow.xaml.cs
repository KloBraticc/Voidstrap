using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Keystrokes;

public partial class KeystrokeOverlayWindow : Window
{
    private const string ResourceKey = "KeystrokeOverlayWindow";
    private static readonly object InstanceGate = new();
    private static KeystrokeOverlayWindow? _activeInstance;

    private static bool _screenDragHidden;

    internal static void SetScreenDragHidden(bool hidden)
    {
        _screenDragHidden = hidden;
        if (_activeInstance is { _disposed: false } active)
            active.Opacity = hidden ? 0 : 1;
    }
    private static int _reconcilePending;

    private readonly RobloxOverlayAnchor _anchor;
    private readonly KeystrokeXmlView _themeView = new(liveInput: true);
    private string? _loadedTheme;
    private string? _themeFailure;
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
        Opacity = _screenDragHidden ? 0 : 1;

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
        ThemeHost.Content = _themeView;
        _themeView.ThemeStateChanged += OnThemeStateChanged;

        _anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Center, customPosition: ComputeKeystrokesPosition);

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

        KeystrokeTheme theme = KeystrokeThemeManager.ActiveTheme;
        AppSettings settings = App.Settings.Prop;
        string source = theme.GetXml();
        string signature = string.Join(
            '|',
            source,
            settings.KeystrokesShowKeys,
            settings.KeystrokesShowMouseButtons,
            settings.KeystrokesShowSpaceAndShift,
            settings.KeystrokesShowCps
        );
        if (signature == _loadedTheme && _themeView.IsRunning)
            return;
        _loadedTheme = signature;
        _themeFailure = null;
        string directory = System.IO.Path.Combine(Paths.CustomKeystrokeThemes, theme.Name);
        if (_themeView.Load(source, directory) != null)
            _themeView.Load(KeystrokeXmlTemplates.Classic, directory);
    }

    private void OnThemeStateChanged(string? error)
    {
        const string LOG_IDENT = "KeystrokeOverlayWindow::OnThemeStateChanged";

        if (error == null || error == _themeFailure)
            return;
        _themeFailure = error;
        App.Logger.WriteLine(LOG_IDENT, "Keystroke theme error: " + error);
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e) => _anchor.Refresh();

    private void OnOverlayLoaded(object? sender, RoutedEventArgs e)
    {
        MakeClickThrough();
        KeystrokeInputService.KeyNamedChanged += OnKeyNamedChanged;
        KeystrokeThemeManager.ActiveThemeChanged += OnActiveThemeChanged;
        SizeChanged += OnOverlaySizeChanged;
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
        _themeView.ThemeStateChanged -= OnThemeStateChanged;
        _themeView.Dispose();
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

    private void OnKeyNamedChanged(string keyName, bool isDown)
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnKeyNamedChanged(keyName, isDown)));
            return;
        }

        _themeView.SetKey(keyName, isDown);
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
