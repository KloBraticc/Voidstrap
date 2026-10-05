// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Dpi;
using Size = System.Windows.Size;

namespace Wpf.Ui.Controls;

/// <summary>
/// If you use <see cref="WindowChrome"/> to extend the UI elements to the non-client area, you can include this container
/// in the template of <see cref="Window"/> so that the content inside automatically fills the client area.
/// Using this container can let you get rid of various margin adaptations done in
/// Setter/Trigger of the style of <see cref="Window"/> when the window state changes.
/// </summary>
public class ClientAreaBorder : System.Windows.Controls.Border, IThemeControl
{
    private const int SM_CXFRAME = 32;

    private const int SM_CYFRAME = 33;

    private const int SM_CXPADDEDBORDER = 92;

    private Window? _oldWindow;

    private static readonly SolidColorBrush InactiveBorderLight = CreateFrozen(Color.FromArgb(255, 200, 200, 200));
    private static readonly SolidColorBrush InactiveBorderDark = CreateFrozen(Color.FromArgb(255, 58, 58, 58));

    private static SolidColorBrush CreateFrozen(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Thickness? _paddedBorderThickness;

    private static Thickness? _resizeFrameBorderThickness;

    private static Thickness? _windowChromeNonClientFrameThickness;

    private static readonly ConditionalWeakTable<Window, ClientAreaBorder> AttachedBorders = new();

    private bool _themeSubscribed;

    public ThemeType Theme { get; set; } = ThemeType.Unknown;

    public static Func<Window, double>? PortableCornerRadiusProvider { get; set; }

    public static void Refresh(Window? window)
    {
        if (window is not null && AttachedBorders.TryGetValue(window, out ClientAreaBorder? border))
            border.ApplyWindowState();
    }

    /// <summary>
    /// Get the system <see cref="SM_CXPADDEDBORDER"/> value in WPF units.
    /// </summary>
    public Thickness PaddedBorderThickness
    {
        get
        {
            if (_paddedBorderThickness is not null)
                return _paddedBorderThickness.Value;

            if (!System.OperatingSystem.IsWindows())
            {
                _paddedBorderThickness = new Thickness(0);
                return _paddedBorderThickness.Value;
            }

            var paddedBorder = Interop.User32.GetSystemMetrics(Interop.User32.SM.CXPADDEDBORDER);

            var (factorX, factorY) = GetDpi();
            var frameSize = new Size(paddedBorder, paddedBorder);
            var frameSizeInDips = new Size(frameSize.Width / factorX, frameSize.Height / factorY);

            _paddedBorderThickness = new Thickness(frameSizeInDips.Width, frameSizeInDips.Height,
                frameSizeInDips.Width, frameSizeInDips.Height);

            return _paddedBorderThickness.Value;
        }
    }

    /// <summary>
    /// Get the system <see cref="SM_CXFRAME"/> and <see cref="SM_CYFRAME"/> values in WPF units.
    /// </summary>
    public Thickness ResizeFrameBorderThickness => _resizeFrameBorderThickness ??= new Thickness(
        SystemParameters.ResizeFrameVerticalBorderWidth,
        SystemParameters.ResizeFrameHorizontalBorderHeight,
        SystemParameters.ResizeFrameVerticalBorderWidth,
        SystemParameters.ResizeFrameHorizontalBorderHeight);

    /// <summary>
    /// If you use a <see cref="WindowChrome"/> to extend the client area of a window to the non-client area, you need to handle the edge margin issue when the window is maximized.
    /// Use this property to get the correct margin value when the window is maximized, so that when the window is maximized, the client area can completely cover the screen client area by no less than a single pixel at any DPI.
    /// The<see cref="Interop.User32.GetSystemMetrics"/> method cannot obtain this value directly.
    /// </summary>
    public Thickness WindowChromeNonClientFrameThickness => _windowChromeNonClientFrameThickness ??= System.OperatingSystem.IsWindows()
        ? new Thickness(
            ResizeFrameBorderThickness.Left + PaddedBorderThickness.Left,
            ResizeFrameBorderThickness.Top + PaddedBorderThickness.Top,
            ResizeFrameBorderThickness.Right + PaddedBorderThickness.Right,
            ResizeFrameBorderThickness.Bottom + PaddedBorderThickness.Bottom)
        : new Thickness(0);

    public ClientAreaBorder()
    {
        Theme = Appearance.Theme.GetAppTheme();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Theme = Appearance.Theme.GetAppTheme();
        SubscribeTheme();
        AttachWindow();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            AttachWindow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeTheme();

        if (_oldWindow is { } window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
            window.StateChanged -= OnWindowStateChanged;
            window.SizeChanged -= OnWindowSizeChanged;
            window.Closed -= OnWindowClosed;
            AttachedBorders.Remove(window);
            _oldWindow = null;
        }
    }

    private void SubscribeTheme()
    {
        if (_themeSubscribed)
            return;

        Appearance.Theme.Changed += OnThemeChanged;
        _themeSubscribed = true;
    }

    private void UnsubscribeTheme()
    {
        if (!_themeSubscribed)
            return;

        Appearance.Theme.Changed -= OnThemeChanged;
        _themeSubscribed = false;
    }

    private void OnThemeChanged(ThemeType currentTheme, Color systemAccent)
    {
        Theme = currentTheme;

        ApplyDefaultWindowBorder();
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);

        AttachWindow();
    }

    private Window? FindWindow()
    {
        if (TemplatedParent is Window templated)
            return templated;

        if (Window.GetWindow(this) is { } owner)
            return owner;

        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is Window window)
                return window;

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void AttachWindow()
    {
        Window? newWindow = FindWindow();

        if (!ReferenceEquals(newWindow, _oldWindow))
        {
            if (_oldWindow is { } oldWindow)
            {
                oldWindow.StateChanged -= OnWindowStateChanged;
                oldWindow.Activated -= OnWindowActivated;
                oldWindow.Deactivated -= OnWindowDeactivated;
                oldWindow.SizeChanged -= OnWindowSizeChanged;
                oldWindow.Closed -= OnWindowClosed;
                AttachedBorders.Remove(oldWindow);
            }

            if (newWindow is not null)
            {
                newWindow.StateChanged -= OnWindowStateChanged;
                newWindow.StateChanged += OnWindowStateChanged;
                newWindow.Activated -= OnWindowActivated;
                newWindow.Activated += OnWindowActivated;
                newWindow.Deactivated -= OnWindowDeactivated;
                newWindow.Deactivated += OnWindowDeactivated;

                if (!System.OperatingSystem.IsWindows())
                {
                    newWindow.SizeChanged -= OnWindowSizeChanged;
                    newWindow.SizeChanged += OnWindowSizeChanged;
                    newWindow.Closed -= OnWindowClosed;
                    newWindow.Closed += OnWindowClosed;
                    AttachedBorders.AddOrUpdate(newWindow, this);
                    SubscribeTheme();
                }
            }

            _oldWindow = newWindow;
        }

        ApplyWindowState();
    }

    private void ApplyWindowState()
    {
        if (!System.OperatingSystem.IsWindows())
        {
            Padding = default;
            ApplyPortableCornerRadius();
        }

        ApplyDefaultWindowBorder();
    }

    private void ApplyPortableCornerRadius()
    {
        double radius = 0;

        if (_oldWindow is { } window && PortableCornerRadiusProvider is { } provider)
        {
            try
            {
                radius = provider(window);
            }
            catch (Exception)
            {
                radius = 0;
            }
        }

        if (double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0)
            radius = 0;

        if (CornerRadius.TopLeft != radius || CornerRadius.BottomRight != radius)
            CornerRadius = new CornerRadius(radius);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        if (!System.OperatingSystem.IsWindows())
        {
            ApplyWindowState();
            return;
        }

        Padding = window.WindowState switch
        {
            WindowState.Maximized => WindowChromeNonClientFrameThickness,
            _ => default,
        };
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyWindowState();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.StateChanged -= OnWindowStateChanged;
        window.Activated -= OnWindowActivated;
        window.Deactivated -= OnWindowDeactivated;
        window.SizeChanged -= OnWindowSizeChanged;
        window.Closed -= OnWindowClosed;
        AttachedBorders.Remove(window);
        UnsubscribeTheme();

        if (ReferenceEquals(window, _oldWindow))
            _oldWindow = null;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        ApplyDefaultWindowBorder();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        ApplyDefaultWindowBorder();
    }

    private void ApplyDefaultWindowBorder()
    {
        if (_oldWindow is not UiWindow uiWindow)
        {
            ResetDefaultWindowBorder(_oldWindow);
            return;
        }

        if (!uiWindow.DefaultBorderEnabled)
        {
            ResetDefaultWindowBorder(uiWindow);
            return;
        }

        ThemeType theme = Theme;

        if (uiWindow.DefaultBorderThemeOverwrite != ThemeType.Unknown)
            theme = uiWindow.DefaultBorderThemeOverwrite;

        BorderBrush = theme == ThemeType.Light ? InactiveBorderLight : InactiveBorderDark;

        BorderThickness = new Thickness(1);
    }

    private void ResetDefaultWindowBorder(Window? window)
    {
        if (window == null)
            return;

        BorderBrush = window.BorderBrush;
        BorderThickness = window.BorderThickness;
    }

    private (double factorX, double factorY) GetDpi()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            return (target.TransformToDevice.M11, target.TransformToDevice.M22);

        var systemDPi = DpiHelper.GetSystemDpi();

        return (systemDPi.DpiScaleX, systemDPi.DpiScaleY);
    }
}
