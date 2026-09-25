using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Models;

namespace Voidstrap.UI;

public static partial class WindowBackdrop
{
    private const double MicaAccentTint = 1.0;

    private const double MicaSaturationBoost = 2.4;

    private const double MaxSurfaceSaturation = 1.0;

    private const double MinimumAccentSaturation = 0.12;

    private const int AccentCacheMs = 5000;

    private const int FlatGradientTolerance = 3;

    private static readonly bool HardwareRendering = (RenderCapability.Tier >> 16) > 0;

    private static readonly object _surfaceGate = new object();

    private static readonly Dictionary<(uint, uint), Color> _vibrantCache = new Dictionary<(uint, uint), Color>();

    private static Brush? _cachedSurfaceBrush;

    private static (uint, uint, uint, byte, uint) _cachedSurfaceKey;

    private static Color _accentColor;

    private static long _accentReadTicks = long.MinValue;

    private const int DwmwaMicaEffect = 1029;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentFlagUseGradientColor = 2;

    private static readonly ConditionalWeakTable<Window, object?> _backdropWindows = new();

    private static readonly ConditionalWeakTable<Window, string> _appliedBackdrops = new();

    private static IntPtr _contextMenuHandle = IntPtr.Zero;

    private static Wpf.Ui.Appearance.BackgroundType _contextMenuBackdrop = Wpf.Ui.Appearance.BackgroundType.None;

    private static readonly ConditionalWeakTable<Window, object?> _renderHooked = new();

    private sealed class BackdropOverride
    {
        public BackdropType? Type { get; init; }

        public bool ForceOpaque { get; init; }
    }

    private static readonly ConditionalWeakTable<Window, BackdropOverride> _overrides = new();

    public static void SetOverride(Window window, BackdropType? type, bool forceOpaque)
    {
        if (window == null)
        {
            return;
        }
        _overrides.Remove(window);
        _appliedBackdrops.Remove(window);

        if (type != null || forceOpaque)
        {
            _overrides.AddOrUpdate(window, new BackdropOverride { Type = type, ForceOpaque = forceOpaque });
        }
        Apply(window);
    }

    public static void ClearOverride(Window window)
    {
        if (window == null)
        {
            return;
        }
        _overrides.Remove(window);
        _appliedBackdrops.Remove(window);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public int Color;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int Size;
    }

    public static void Apply(Window window)
    {
        if (window == null)
        {
            return;
        }
        if (window is Voidstrap.UI.Elements.Settings.MainWindow)
        {
            ApplyMainWindow(window);
            return;
        }
        ApplyBackdrop(window);
    }

    public static void ApplyMainWindow(Window window)
    {
        ApplyBackdrop(window);
    }

    private static void ApplyBackdrop(Window window)
    {
        if (window == null)
        {
            return;
        }
        if (!window.Dispatcher.CheckAccess())
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
            {
                return;
            }
            window.Dispatcher.BeginInvoke(() => ApplyBackdrop(window));
            return;
        }
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            if (!Voidstrap.Integrations.Overlays.LinuxOverlaySurface.IsOverlayWindow(window))
                ApplyLinuxSurface(window);
            return;
        }
        if (window.AllowsTransparency)
        {
            return;
        }
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            ApplySurface(window, Wpf.Ui.Appearance.BackgroundType.None);
            return;
        }
        if (!_renderHooked.TryGetValue(window, out _))
        {
            _renderHooked.AddOrUpdate(window, null);
            window.ContentRendered += OnWindowContentRendered;
        }
        _overrides.TryGetValue(window, out BackdropOverride? theOverride);

        if (theOverride != null && theOverride.ForceOpaque)
        {
            ApplySurface(window, Wpf.Ui.Appearance.BackgroundType.None);
            return;
        }

        Wpf.Ui.Appearance.BackgroundType requestedBackgroundType = Resolve(theOverride?.Type ?? App.Settings.Prop.WindowBackdrop);
        Wpf.Ui.Appearance.BackgroundType backgroundType = requestedBackgroundType;
        IntPtr handle = IntPtr.Zero;
        string appliedKey = string.Empty;
        bool applied = false;
        try
        {
            handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                ApplySurface(window, Wpf.Ui.Appearance.BackgroundType.None);
                return;
            }
            appliedKey = handle.ToInt64().ToString() + ":" + requestedBackgroundType + ":" + GetSurfaceOpacity(EffectiveBackdrop(window));
            if (_appliedBackdrops.TryGetValue(window, out string? previousKey) && previousKey == appliedKey)
                return;
            ResetNativeBackdrop(handle);
            SetGlassFrame(window, backgroundType != Wpf.Ui.Appearance.BackgroundType.Aero);
            if (backgroundType == Wpf.Ui.Appearance.BackgroundType.None)
            {
                Wpf.Ui.Appearance.Background.Remove(handle);
                applied = true;
            }
            else if (backgroundType == Wpf.Ui.Appearance.BackgroundType.Aero)
            {
                Wpf.Ui.Appearance.Background.RemoveContentBackground(window);
                applied = ApplyAeroBlur(handle, window);
            }
            else
            {
                Wpf.Ui.Appearance.Background.RemoveContentBackground(window);
                applied = Wpf.Ui.Appearance.Background.Apply(handle, backgroundType);
                if (!applied)
                {
                    applied = ApplyNativeBackdrop(handle, backgroundType);
                }
                applied = VerifyAppliedBackdrop(handle, backgroundType, applied, window);
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("WindowBackdrop::ApplyBackdrop", ex);
        }
        if (!applied && backgroundType != Wpf.Ui.Appearance.BackgroundType.None)
        {
            Wpf.Ui.Appearance.Background.Remove(handle);
            backgroundType = Wpf.Ui.Appearance.BackgroundType.None;
        }
        if (applied && appliedKey.Length > 0)
        {
            _appliedBackdrops.AddOrUpdate(window, appliedKey);
        }
        else
        {
            _appliedBackdrops.Remove(window);
        }
        ApplySurface(window, backgroundType);
        if (!applied)
        {
            App.Logger.WriteLine("WindowBackdrop::ApplyBackdrop", $"Backdrop unavailable: {requestedBackgroundType}, using solid surface");
        }
        else if (backgroundType != Wpf.Ui.Appearance.BackgroundType.None)
        {
            App.Logger.WriteLine("WindowBackdrop::ApplyBackdrop", $"{window.GetType().Name}: {backgroundType}");
        }
        try
        {
            window.InvalidateVisual();
            if (applied && backgroundType != Wpf.Ui.Appearance.BackgroundType.None)
            {
                _ = DwmFlush();
            }
        }
        catch
        {
        }
    }

    private static void OnWindowContentRendered(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }
        window.ContentRendered -= OnWindowContentRendered;
        _appliedBackdrops.Remove(window);
        ApplyBackdrop(window);
    }

    public static void ApplyContextMenu(ContextMenu contextMenu)
    {
        if (contextMenu == null || !contextMenu.Dispatcher.CheckAccess())
        {
            return;
        }
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            contextMenu.Background = CreateLinuxSurfaceBrush();
            if (PresentationSource.FromVisual(contextMenu) is HwndSource linuxSource
                && linuxSource.Handle != IntPtr.Zero
                && linuxSource.CompositionTarget is { } target)
            {
                target.BackgroundColor = CreateSurfaceColor(BackdropType.None);
            }
            return;
        }
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            Color color = CreateSurfaceColor();
            color.A = byte.MaxValue;
            SolidColorBrush brush = new(color);
            brush.Freeze();
            contextMenu.Background = brush;
            return;
        }
        if (PresentationSource.FromVisual(contextMenu) is not HwndSource source || source.Handle == IntPtr.Zero)
        {
            return;
        }
        IntPtr handle = source.Handle;
        Wpf.Ui.Appearance.BackgroundType backgroundType = Resolve(App.Settings.Prop.WindowBackdrop);
        if (backgroundType == Wpf.Ui.Appearance.BackgroundType.Aero)
        {
            backgroundType = Wpf.Ui.Appearance.BackgroundType.None;
        }
        if (_contextMenuHandle == handle && _contextMenuBackdrop == backgroundType && contextMenu.Background != null)
        {
            return;
        }
        _contextMenuHandle = IntPtr.Zero;
        bool applied = backgroundType == Wpf.Ui.Appearance.BackgroundType.None;
        try
        {
            Wpf.Ui.Appearance.Background.Remove(handle);
            ResetNativeBackdrop(handle);
            int rounded = 2;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
            if (!applied)
            {
                if (backgroundType == Wpf.Ui.Appearance.BackgroundType.Aero)
                {
                    applied = ApplyAeroBlur(handle, null);
                }
                else
                {
                    applied = Wpf.Ui.Appearance.Background.Apply(handle, backgroundType);
                    applied = VerifyAppliedBackdrop(handle, backgroundType, applied, null);
                }
            }
            Brush surface = applied && backgroundType != Wpf.Ui.Appearance.BackgroundType.None
                ? CreateSurfaceBrush(contextMenu)
                : Brushes.Transparent;
            if (applied && backgroundType != Wpf.Ui.Appearance.BackgroundType.None && !IsFullyTransparent(surface))
            {
                if (source.CompositionTarget is { } target)
                    target.BackgroundColor = Colors.Transparent;
                contextMenu.Background = surface;
            }
            else
            {
                Color color = CreateSurfaceColor();
                color.A = byte.MaxValue;
                SolidColorBrush brush = new(color);
                brush.Freeze();
                contextMenu.Background = brush;
            }
            _contextMenuHandle = handle;
            _contextMenuBackdrop = backgroundType;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("WindowBackdrop::ApplyContextMenu", ex);
        }
    }

    private static bool IsFullyTransparent(Brush brush)
    {
        if (brush is SolidColorBrush solid)
        {
            return solid.Color.A == 0;
        }
        if (brush is GradientBrush gradient)
        {
            foreach (GradientStop stop in gradient.GradientStops)
            {
                if (stop.Color.A != 0)
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    private static BackdropType EffectiveBackdrop(Window? window)
    {
        if (window != null && _overrides.TryGetValue(window, out BackdropOverride? theOverride))
        {
            if (theOverride.ForceOpaque)
            {
                return BackdropType.None;
            }
            if (theOverride.Type != null)
            {
                return theOverride.Type.Value;
            }
        }
        return App.Settings.Prop.WindowBackdrop;
    }

    private static void SetGlassFrame(Window? window, bool extended)
    {
        if (window == null)
        {
            return;
        }
        WindowChrome? chrome = WindowChrome.GetWindowChrome(window);
        if (chrome == null)
        {
            return;
        }
        Thickness wanted = extended ? new Thickness(-1) : new Thickness(0);
        if (chrome.GlassFrameThickness == wanted)
        {
            return;
        }
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = chrome.CaptionHeight,
            CornerRadius = chrome.CornerRadius,
            GlassFrameThickness = wanted,
            NonClientFrameEdges = chrome.NonClientFrameEdges,
            ResizeBorderThickness = chrome.ResizeBorderThickness,
            UseAeroCaptionButtons = chrome.UseAeroCaptionButtons
        });
    }

    private static bool ApplyAeroBlur(IntPtr handle, Window? window)
    {
        if (App.Settings.Prop.Theme2.GetFinal() == Theme.Light)
        {
            Wpf.Ui.Interop.UnsafeNativeMethods.RemoveWindowDarkMode(handle);
        }
        else
        {
            Wpf.Ui.Interop.UnsafeNativeMethods.ApplyWindowDarkMode(handle);
        }
        Wpf.Ui.Interop.UnsafeNativeMethods.RemoveWindowCaption(handle);

        Color tint = CreateSurfaceColor(EffectiveBackdrop(window));
        int color = unchecked((int)((uint)tint.A << 24 | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R));
        int state = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)
            ? AccentEnableAcrylicBlurBehind
            : AccentEnableBlurBehind;

        if (SetAccentPolicy(handle, state, AccentFlagUseGradientColor, color))
        {
            App.Logger.WriteLine("WindowBackdrop::ApplyAeroBlur", $"Accent glass state {state}");
            return true;
        }

        SetAccentPolicy(handle, AccentDisabled);
        SetGlassFrame(window, true);
        int transient = 3;
        bool fallback = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref transient, sizeof(int)) == 0
            && VerifySystemBackdrop(handle, transient);
        App.Logger.WriteLine("WindowBackdrop::ApplyAeroBlur", $"Accent glass unavailable, acrylic fallback: {fallback}");
        return fallback;
    }

    private static void ResetNativeBackdrop(IntPtr handle)
    {
        SetAccentPolicy(handle, AccentDisabled);
        int disabled = 0;
        _ = DwmSetWindowAttribute(handle, DwmwaMicaEffect, ref disabled, sizeof(int));
        disabled = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref disabled, sizeof(int));
    }

    private static bool ApplyNativeBackdrop(IntPtr handle, Wpf.Ui.Appearance.BackgroundType backgroundType)
    {
        int value = backgroundType switch
        {
            Wpf.Ui.Appearance.BackgroundType.Auto => 0,
            Wpf.Ui.Appearance.BackgroundType.Mica => 2,
            Wpf.Ui.Appearance.BackgroundType.Acrylic => 3,
            Wpf.Ui.Appearance.BackgroundType.Tabbed => 4,
            _ => 1
        };
        if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, sizeof(int)) != 0)
        {
            if (backgroundType != Wpf.Ui.Appearance.BackgroundType.Mica
                && backgroundType != Wpf.Ui.Appearance.BackgroundType.Auto)
            {
                return false;
            }
            int legacy = 1;
            return DwmSetWindowAttribute(handle, DwmwaMicaEffect, ref legacy, sizeof(int)) == 0;
        }
        return true;
    }

    private static bool VerifyAppliedBackdrop(IntPtr handle, Wpf.Ui.Appearance.BackgroundType backgroundType, bool applied, Window? window)
    {
        if (backgroundType == Wpf.Ui.Appearance.BackgroundType.Aero)
        {
            return ApplyAeroBlur(handle, window);
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22523))
        {
            int expected = backgroundType switch
            {
                Wpf.Ui.Appearance.BackgroundType.Auto => 0,
                Wpf.Ui.Appearance.BackgroundType.Mica => 2,
                Wpf.Ui.Appearance.BackgroundType.Acrylic => 3,
                Wpf.Ui.Appearance.BackgroundType.Tabbed => 4,
                _ => 1
            };
            return VerifySystemBackdrop(handle, expected);
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && (backgroundType == Wpf.Ui.Appearance.BackgroundType.Mica
                || backgroundType == Wpf.Ui.Appearance.BackgroundType.Auto))
        {
            int actual = 0;
            return DwmGetWindowAttribute(handle, DwmwaMicaEffect, ref actual, sizeof(int)) == 0 && actual == 1;
        }
        if (backgroundType == Wpf.Ui.Appearance.BackgroundType.Acrylic)
        {
            return ApplyAeroBlur(handle, window);
        }
        return applied;
    }

    private static bool VerifySystemBackdrop(IntPtr handle, int expected)
    {
        int actual = int.MinValue;
        return DwmGetWindowAttribute(handle, DwmwaSystemBackdropType, ref actual, sizeof(int)) == 0 && actual == expected;
    }

    private static bool SetAccentPolicy(IntPtr handle, int state) => SetAccentPolicy(handle, state, 0, 0);

    private static bool SetAccentPolicy(IntPtr handle, int state, int flags, int color)
    {
        AccentPolicy policy = new() { State = state, Flags = flags, Color = color };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            WindowCompositionAttributeData data = new()
            {
                Attribute = WcaAccentPolicy,
                Data = pointer,
                Size = size
            };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static void ApplyThemeToAllOpenWindows()
    {
        Application application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke((Action)ApplyThemeToAllOpenWindows);
            return;
        }
        InvalidateSurfaceCache();
        foreach (Window window in application.Windows.Cast<Window>().ToArray())
        {
            _appliedBackdrops.Remove(window);
            ApplyBackdrop(window);
            if (window is Voidstrap.UI.Elements.ContextMenu.MenuContainer menu)
                menu.ApplyBackdrop();
        }
    }

    public static void ApplyGradientOpacityChange()
    {
        Application application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke((Action)ApplyGradientOpacityChange);
            return;
        }
        foreach (Window window in application.Windows.Cast<Window>().ToArray())
        {
            if (window is Voidstrap.UI.Elements.Settings.MainWindow)
            {
                continue;
            }
            if (_backdropWindows.TryGetValue(window, out _))
            {
                if (EffectiveBackdrop(window) == BackdropType.Aero)
                {
                    _appliedBackdrops.Remove(window);
                    ApplyBackdrop(window);
                    continue;
                }
                window.Background = CreateSurfaceBrush(window);
            }
        }
    }

    internal static bool HasBackdrop(Window window)
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            return false;
        }
        return Resolve(EffectiveBackdrop(window)) != Wpf.Ui.Appearance.BackgroundType.None;
    }

    private static Wpf.Ui.Appearance.BackgroundType Resolve(BackdropType backdrop)
    {
        if (SystemParameters.HighContrast)
        {
            return Wpf.Ui.Appearance.BackgroundType.None;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            if (backdrop == BackdropType.Aero && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                return Wpf.Ui.Appearance.BackgroundType.Aero;
            }
            return Wpf.Ui.Appearance.BackgroundType.None;
        }
        return backdrop switch
        {
            BackdropType.Mica => Wpf.Ui.Appearance.BackgroundType.Mica,
            BackdropType.MicaAlt => ResolveMicaAlt(),
            BackdropType.Acrylic => Wpf.Ui.Appearance.BackgroundType.Acrylic,
            BackdropType.Aero => Wpf.Ui.Appearance.BackgroundType.Aero,
            BackdropType.None => Wpf.Ui.Appearance.BackgroundType.None,
            _ => Wpf.Ui.Appearance.BackgroundType.Auto
        };
    }

    private static Wpf.Ui.Appearance.BackgroundType ResolveMicaAlt()
    {
        if (Wpf.Ui.Appearance.Background.IsSupported(Wpf.Ui.Appearance.BackgroundType.Tabbed))
        {
            return Wpf.Ui.Appearance.BackgroundType.Tabbed;
        }
        return Wpf.Ui.Appearance.BackgroundType.Mica;
    }

    public static Color GetSurfaceColor(Color color)
    {
        Color vibrant = Vibrant(color, EffectiveBackdrop(null));
        return Color.FromArgb(GetSurfaceOpacity(), vibrant.R, vibrant.G, vibrant.B);
    }

    private static Color Vibrant(Color color, BackdropType backdrop)
    {
        if (backdrop == BackdropType.None || !Voidstrap.Utility.Platform.IsWindows)
        {
            return color;
        }
        Color accent = ResolveAccentColor();
        (uint, uint) cacheKey = (Pack(color), Pack(accent));
        lock (_surfaceGate)
        {
            if (_vibrantCache.TryGetValue(cacheKey, out Color cached))
            {
                return cached;
            }
        }
        ToHsl(color, out double hue, out double saturation, out double lightness);
        ToHsl(accent, out double accentHue, out double accentSaturation, out _);
        if (accentSaturation >= MinimumAccentSaturation)
        {
            hue = accentHue;
            saturation += (accentSaturation - saturation) * MicaAccentTint;
        }
        saturation = Math.Clamp(saturation * MicaSaturationBoost, 0.0, MaxSurfaceSaturation);
        Color result = FromHsl(hue, saturation, lightness);
        lock (_surfaceGate)
        {
            _vibrantCache[cacheKey] = result;
        }
        return result;
    }

    private static uint Pack(Color color)
    {
        return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }

    private static Color ResolveAccentColor()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _accentReadTicks) < AccentCacheMs)
        {
            return _accentColor;
        }
        try
        {
            _accentColor = SystemParameters.WindowGlassColor;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("WindowBackdrop::ResolveAccentColor", "The system accent colour could not be read: " + ex.Message);
            _accentColor = Colors.Transparent;
        }
        Volatile.Write(ref _accentReadTicks, now);
        return _accentColor;
    }

    internal static void InvalidateSurfaceCache()
    {
        lock (_surfaceGate)
        {
            _cachedSurfaceBrush = null;
            _vibrantCache.Clear();
        }
        Volatile.Write(ref _accentReadTicks, long.MinValue);
    }

    private static bool NearlyEqual(Color first, Color second)
    {
        return Math.Abs(first.R - second.R) <= FlatGradientTolerance
            && Math.Abs(first.G - second.G) <= FlatGradientTolerance
            && Math.Abs(first.B - second.B) <= FlatGradientTolerance;
    }

    private static void ToHsl(Color color, out double hue, out double saturation, out double lightness)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        lightness = (max + min) / 2.0;
        if (delta <= 0.0)
        {
            hue = 0.0;
            saturation = 0.0;
            return;
        }
        saturation = lightness > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
        if (max == r)
        {
            hue = ((g - b) / delta + (g < b ? 6.0 : 0.0)) / 6.0;
        }
        else if (max == g)
        {
            hue = ((b - r) / delta + 2.0) / 6.0;
        }
        else
        {
            hue = ((r - g) / delta + 4.0) / 6.0;
        }
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation <= 0.0)
        {
            byte grey = (byte)Math.Clamp(Math.Round(lightness * 255.0), 0.0, 255.0);
            return Color.FromRgb(grey, grey, grey);
        }
        double q = lightness < 0.5 ? lightness * (1.0 + saturation) : lightness + saturation - (lightness * saturation);
        double p = (2.0 * lightness) - q;
        return Color.FromRgb(Channel(p, q, hue + (1.0 / 3.0)), Channel(p, q, hue), Channel(p, q, hue - (1.0 / 3.0)));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0.0)
        {
            t += 1.0;
        }
        if (t > 1.0)
        {
            t -= 1.0;
        }
        double value;
        if (t < 1.0 / 6.0)
        {
            value = p + ((q - p) * 6.0 * t);
        }
        else if (t < 1.0 / 2.0)
        {
            value = q;
        }
        else if (t < 2.0 / 3.0)
        {
            value = p + ((q - p) * ((2.0 / 3.0) - t) * 6.0);
        }
        else
        {
            value = p;
        }
        return (byte)Math.Clamp(Math.Round(value * 255.0), 0.0, 255.0);
    }

    public static Brush CreateSurfaceBrush(FrameworkElement element)
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            return CreateLinuxSurfaceBrush();
        }
        if (EffectiveBackdrop(element as Window) == BackdropType.Aero)
        {
            return Brushes.Transparent;
        }
        Color fallback = CreateSurfaceColor();
        Color primary = ResolveColor(element, "WindowBackgroundColorPrimary", fallback);
        Color secondary = ResolveColor(element, "WindowBackgroundColorSecondary", primary);
        Color third = ResolveColor(element, "WindowBackgroundColorThird", secondary);
        (uint, uint, uint, byte, uint) surfaceKey = (Pack(primary), Pack(secondary), Pack(third), GetSurfaceOpacity(), Pack(ResolveAccentColor()));
        lock (_surfaceGate)
        {
            if (_cachedSurfaceBrush != null && _cachedSurfaceKey.Equals(surfaceKey))
            {
                return _cachedSurfaceBrush;
            }
        }
        Color surfacePrimary = GetSurfaceColor(primary);
        Color surfaceSecondary = GetSurfaceColor(secondary);
        Color surfaceThird = GetSurfaceColor(third);
        Brush brush;
        if (!HardwareRendering || (NearlyEqual(surfacePrimary, surfaceSecondary) && NearlyEqual(surfacePrimary, surfaceThird)))
        {
            SolidColorBrush solid = new(surfacePrimary);
            if (solid.CanFreeze)
            {
                solid.Freeze();
            }
            brush = solid;
        }
        else
        {
            LinearGradientBrush gradient = new()
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            gradient.GradientStops.Add(new GradientStop(surfacePrimary, 0));
            gradient.GradientStops.Add(new GradientStop(surfaceSecondary, 0.42));
            gradient.GradientStops.Add(new GradientStop(surfaceThird, 0.78));
            gradient.GradientStops.Add(new GradientStop(surfacePrimary, 1));
            if (gradient.CanFreeze)
            {
                gradient.Freeze();
            }
            brush = gradient;
        }
        lock (_surfaceGate)
        {
            _cachedSurfaceBrush = brush;
            _cachedSurfaceKey = surfaceKey;
        }
        return brush;
    }

    internal static Brush CreateOpaqueSurfaceBrush(FrameworkElement element)
    {
        Color fallback = CreateSurfaceColor(BackdropType.None);
        fallback.A = byte.MaxValue;
        Color primary = ResolveColor(element, "WindowBackgroundColorPrimary", fallback);
        Color secondary = ResolveColor(element, "WindowBackgroundColorSecondary", primary);
        Color third = ResolveColor(element, "WindowBackgroundColorThird", secondary);
        double amount = GetSurfaceOpacity() / (double)byte.MaxValue;
        primary = MixColor(fallback, primary, amount);
        secondary = MixColor(fallback, secondary, amount);
        third = MixColor(fallback, third, amount);
        if (primary == secondary && primary == third)
        {
            SolidColorBrush solid = new(primary);
            if (solid.CanFreeze)
            {
                solid.Freeze();
            }
            return solid;
        }
        LinearGradientBrush gradient = new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        gradient.GradientStops.Add(new GradientStop(primary, 0));
        gradient.GradientStops.Add(new GradientStop(secondary, 0.42));
        gradient.GradientStops.Add(new GradientStop(third, 0.78));
        gradient.GradientStops.Add(new GradientStop(primary, 1));
        if (gradient.CanFreeze)
        {
            gradient.Freeze();
        }
        return gradient;
    }

    internal static Brush CreateLinuxSurfaceBrush()
    {
        Color opaque = CreateSurfaceColor(BackdropType.None);
        opaque.A = byte.MaxValue;
        SolidColorBrush brush = new(opaque);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    private static Color MixColor(Color first, Color second, double amount)
    {
        double factor = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(first.R + ((second.R - first.R) * factor)),
            (byte)Math.Round(first.G + ((second.G - first.G) * factor)),
            (byte)Math.Round(first.B + ((second.B - first.B) * factor)));
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color ResolveColor(FrameworkElement element, string key, Color fallback)
    {
        if (element.TryFindResource(key) is Color color)
        {
            return color;
        }
        if (key == "WindowBackgroundColorThird" && element.TryFindResource("WindowBackgroundColorTertiary") is Color tertiary)
        {
            return tertiary;
        }
        return fallback;
    }

    private static void ApplySurface(Window window, Wpf.Ui.Appearance.BackgroundType backgroundType)
    {
        if (window is Voidstrap.UI.Elements.Settings.MainWindow mainWindow)
        {
            if (backgroundType == Wpf.Ui.Appearance.BackgroundType.None)
            {
                Color color = CreateSurfaceColor();
                color.A = byte.MaxValue;
                SolidColorBrush solid = new(color);
                if (solid.CanFreeze)
                {
                    solid.Freeze();
                }
                window.Background = solid;
            }
            else
            {
                window.Background = Brushes.Transparent;
            }
            mainWindow.ApplyBackdropSurface();
            return;
        }
        if (backgroundType != Wpf.Ui.Appearance.BackgroundType.None)
        {
            window.Background = CreateSurfaceBrush(window);
            _backdropWindows.AddOrUpdate(window, null);
        }
        else if (_backdropWindows.Remove(window))
        {
            SolidColorBrush brush = new(CreateSurfaceColor());
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
            window.Background = brush;
        }
    }

    private static void ApplyLinuxSurface(Window window)
    {
        window.Background = CreateLinuxSurfaceBrush();
        if (window is Voidstrap.UI.Elements.Settings.MainWindow mainWindow)
        {
            mainWindow.ApplyBackdropSurface();
        }

        _backdropWindows.Remove(window);
    }

    internal static BackdropType ResolveFor(Window? window) => EffectiveBackdrop(window);

    internal static Color CreateSurfaceColor() => CreateSurfaceColor(App.Settings.Prop.WindowBackdrop);

    internal static Color CreateSurfaceColor(BackdropType backdrop)
    {
        Theme theme = App.Settings.Prop.Theme2.GetFinal();
        if (theme == Theme.Light)
        {
            return Color.FromArgb(GetSurfaceOpacity(backdrop), 243, 243, 243);
        }
        return Color.FromArgb(GetSurfaceOpacity(backdrop), 28, 28, 30);
    }

    private static byte GetSurfaceOpacity() => GetSurfaceOpacity(App.Settings.Prop.WindowBackdrop);

    private static byte GetSurfaceOpacity(BackdropType backdrop)
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            return byte.MaxValue;
        }
        if (backdrop != BackdropType.Aero && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return byte.MaxValue;
        }
        double opacity = backdrop switch
        {
            BackdropType.MicaAlt => 54,
            BackdropType.Mica => 64,
            BackdropType.Acrylic => 48,
            BackdropType.Aero => 92,
            BackdropType.None => byte.MaxValue,
            _ => 68
        };
        if (opacity >= byte.MaxValue)
        {
            return byte.MaxValue;
        }
        double gradientOpacity = Math.Clamp(Voidstrap.UI.ViewModels.Settings.AppearanceViewModel.SharedGradientOpacity, 0.0, 1.0);
        double scaled = opacity * (0.55 + (gradientOpacity * 0.45));
        return (byte)Math.Clamp(Math.Round(scaled), 24.0, byte.MaxValue);
    }

}
