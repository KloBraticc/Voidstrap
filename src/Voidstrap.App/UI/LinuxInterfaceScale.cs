using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Voidstrap.UI;

internal static class LinuxInterfaceScale
{
    private const string LogIdent = "LinuxInterfaceScale";

    private const double MinimumFactor = 0.5;

    private const double MaximumFactor = 4.0;

    private const double StandardDpi = 96.0;

    private static readonly ConditionalWeakTable<Window, object> Scaled = new();

    private static ScaleTransform? _transform;

    public static double Factor => _transform?.ScaleX ?? 1.0;

    public static bool EffectsDisabled { get; private set; }

    public static void Install()
    {
        if (!OperatingSystem.IsLinux() || _transform is not null)
            return;

        double factor = Resolve(out string source);
        if (Math.Abs(factor - 1.0) < 0.01)
        {
            App.Logger.WriteLine(LogIdent, "Windows use 100% scale, " + source);
            return;
        }

        ScaleTransform transform = new(factor, factor);
        transform.Freeze();
        _transform = transform;
        ScaleByDefault(typeof(ContextMenu), transform);
        ScaleByDefault(typeof(ToolTip), transform);
        DisableEffects();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        App.Logger.WriteLine(LogIdent, "Windows scale to " + Percent(factor) + " to match " + source);
    }

    public static double For(Window? window)
    {
        return _transform is not null && window is not null && Scaled.TryGetValue(window, out _) ? _transform.ScaleX : 1.0;
    }

    public static void PrepareWindow(Window window)
    {
        if (_transform is null || !ShouldScale(window) || Scaled.TryGetValue(window, out _))
            return;

        Scaled.Add(window, new object());
        ScaleSize(window, FrameworkElement.WidthProperty);
        ScaleSize(window, FrameworkElement.HeightProperty);
        ScaleSize(window, FrameworkElement.MinWidthProperty);
        ScaleSize(window, FrameworkElement.MinHeightProperty);
        ScaleSize(window, FrameworkElement.MaxWidthProperty);
        ScaleSize(window, FrameworkElement.MaxHeightProperty);
        ApplyRoot(window);
    }

    private static bool ShouldScale(Window window)
    {
        return !RoundedWindowChrome.IsOverlaySurface(window)
            && window is Wpf.Ui.Controls.UiWindow or Wpf.Ui.Controls.MessageBox or IBootstrapperDialog;
    }

    private static void ScaleSize(Window window, DependencyProperty property)
    {
        double value = (double)window.GetValue(property);
        if (double.IsFinite(value) && value > 0.0)
            window.SetValue(property, Math.Round(value * _transform!.ScaleX));
    }

    private static void ApplyRoot(Window window)
    {
        if (VisualTreeHelper.GetChildrenCount(window) == 0
            || VisualTreeHelper.GetChild(window, 0) is not FrameworkElement root
            || ReferenceEquals(root.LayoutTransform, _transform))
            return;
        root.LayoutTransform = _transform;
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && Scaled.TryGetValue(window, out _))
            ApplyRoot(window);
    }

    private static void ScaleByDefault(Type type, ScaleTransform transform)
    {
        try
        {
            FrameworkElement.LayoutTransformProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(transform));
        }
        catch (ArgumentException ex)
        {
            App.Logger.WriteLine(LogIdent, type.Name + " keeps its own size: " + ex.Message);
        }
    }

    private static void DisableEffects()
    {
        try
        {
            UIElement.EffectProperty.OverrideMetadata(typeof(FrameworkElement), new UIPropertyMetadata(null, null, CoerceEffect));
            EffectsDisabled = true;
            App.Logger.WriteLine(LogIdent, "Shadow and blur effects are off while windows are scaled, the renderer draws scaled effects over the whole window");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            App.Logger.WriteLine(LogIdent, "Effects stay on: " + ex.Message);
        }
    }

    private static object? CoerceEffect(DependencyObject element, object? value)
    {
        return null;
    }

    private static double Resolve(out string source)
    {
        string? configured = Environment.GetEnvironmentVariable("VOIDSTRAP_SCALE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (double.TryParse(configured.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double requested)
                && requested >= MinimumFactor
                && requested <= MaximumFactor)
            {
                source = "VOIDSTRAP_SCALE";
                return Math.Round(requested, 2);
            }
            App.Logger.WriteLine(LogIdent, "VOIDSTRAP_SCALE was ignored because it is not a number from 0.5 to 4");
        }

        if (Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetXftDpi(out double dpi))
        {
            source = "the desktop text DPI of " + dpi.ToString("0.##", CultureInfo.InvariantCulture);
            return Math.Clamp(Math.Round(dpi / StandardDpi, 2), 1.0, MaximumFactor);
        }

        source = "the desktop publishes no text DPI";
        return 1.0;
    }

    private static string Percent(double factor)
    {
        return (factor * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
