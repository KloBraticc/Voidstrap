// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Extensions;
using Wpf.Ui.Interop;

namespace Wpf.Ui.Appearance;

/// <summary>
/// Lets you update the color accents of the application.
/// </summary>
public static class Accent
{
    /// <summary>
    /// The maximum value of the background HSV brightness after which the text on the accent will be turned dark.
    /// </summary>
    private const double BackgroundBrightnessThresholdValue = 90d;

    /// <summary>
    /// SystemAccentColor.
    /// </summary>
    public static Color SystemAccent
    {
        get
        {
            var resource = Application.Current.Resources["SystemAccentColor"];

            if (resource is Color color)
                return color;

            return Colors.Transparent;
        }
    }

    /// <summary>
    /// Brush of the SystemAccentColor.
    /// </summary>
    public static Brush SystemAccentBrush => GetBrush("SystemAccentColorBrush", SystemAccent);

    /// <summary>
    /// SystemAccentColorPrimary.
    /// </summary>
    public static Color PrimaryAccent
    {
        get
        {
            var resource = Application.Current.Resources["SystemAccentColorPrimary"];

            if (resource is Color color)
                return color;

            return Colors.Transparent;
        }
    }

    /// <summary>
    /// Brush of the SystemAccentColorPrimary.
    /// </summary>
    public static Brush PrimaryAccentBrush => GetBrush("SystemAccentColorPrimaryBrush", PrimaryAccent);

    /// <summary>
    /// SystemAccentColorSecondary.
    /// </summary>
    public static Color SecondaryAccent
    {
        get
        {
            var resource = Application.Current.Resources["SystemAccentColorSecondary"];

            if (resource is Color color)
                return color;

            return Colors.Transparent;
        }
    }

    /// <summary>
    /// Brush of the SystemAccentColorSecondary.
    /// </summary>
    public static Brush SecondaryAccentBrush => GetBrush("SystemAccentColorSecondaryBrush", SecondaryAccent);

    /// <summary>
    /// SystemAccentColorTertiary.
    /// </summary>
    public static Color TertiaryAccent
    {
        get
        {
            var resource = Application.Current.Resources["SystemAccentColorTertiary"];

            if (resource is Color color)
                return color;

            return Colors.Transparent;
        }
    }

    /// <summary>
    /// Brush of the SystemAccentColorTertiary.
    /// </summary>
    public static Brush TertiaryAccentBrush => GetBrush("SystemAccentColorTertiaryBrush", TertiaryAccent);

    /// <summary>
    /// Changes the color accents of the application based on the color entered.
    /// </summary>
    /// <param name="systemAccent">Primary accent color.</param>
    /// <param name="themeType">If <see cref="ThemeType.Dark"/>, the colors will be different.</param>
    /// <param name="systemGlassColor">If the color is taken from the Glass Color System, its brightness will be increased with the help of the operations on HSV space.</param>
    public static void Apply(Color systemAccent, ThemeType themeType = ThemeType.Light,
        bool systemGlassColor = false)
    {
        if (systemGlassColor)
        {
            // WindowGlassColor is little darker than accent color
            systemAccent = systemAccent.UpdateBrightness(6f);
        }

        Color primaryAccent, secondaryAccent, tertiaryAccent;

        if (themeType == ThemeType.Dark)
        {
            primaryAccent = systemAccent.Update(15f, -12f);
            secondaryAccent = systemAccent.Update(30f, -24f);
            tertiaryAccent = systemAccent.Update(45f, -36f);
        }
        else
        {
            primaryAccent = systemAccent.UpdateBrightness(-5f);
            secondaryAccent = systemAccent.UpdateBrightness(-10f);
            tertiaryAccent = systemAccent.UpdateBrightness(-15f);
        }

        UpdateColorResources(
            systemAccent,
            primaryAccent,
            secondaryAccent,
            tertiaryAccent
        );
        UpdateRinPrimary(systemAccent, themeType);
    }

    private static void UpdateRinPrimary(Color systemAccent, ThemeType themeType)
    {
        Color primary = themeType == ThemeType.Dark
            ? RinColor.LighterThenDarker(systemAccent, 160, 120)
            : systemAccent;
        Application.Current.Resources["RinPrimaryColor"] = primary;
        Application.Current.Resources["RinPrimaryColorBrush"] = CreateBrush(primary);
    }

    /// <summary>
    /// Changes the color accents of the application based on the entered colors.
    /// </summary>
    /// <param name="systemAccent">Primary color.</param>
    /// <param name="primaryAccent">Alternative light or dark color.</param>
    /// <param name="secondaryAccent">Second alternative light or dark color (most used).</param>
    /// <param name="tertiaryAccent">Third alternative light or dark color.</param>
    public static void Apply(Color systemAccent, Color primaryAccent,
        Color secondaryAccent, Color tertiaryAccent)
    {
        UpdateColorResources(systemAccent, primaryAccent, secondaryAccent, tertiaryAccent);
        UpdateRinPrimary(systemAccent, Theme.GetAppTheme());
    }

    /// <summary>
    /// Applies system accent color to the application.
    /// </summary>
    public static void ApplySystemAccent()
    {
        Apply(GetColorizationColor(), Theme.GetAppTheme());
    }

    /// <summary>
    /// Gets current Desktop Window Manager colorization color.
    /// <para>It should be the color defined in the system Personalization.</para>
    /// </summary>
    public static Color GetColorizationColor()
    {
        return UnsafeNativeMethods.GetDwmColor();
    }

    /// <summary>
    /// Updates application resources.
    /// </summary>
    private static void UpdateColorResources(Color systemAccent, Color primaryAccent,
        Color secondaryAccent, Color tertiaryAccent)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine("INFO | SystemAccentColor: " + systemAccent, "Wpf.Ui.Accent");
        System.Diagnostics.Debug.WriteLine("INFO | SystemAccentColorPrimary: " + primaryAccent, "Wpf.Ui.Accent");
        System.Diagnostics.Debug.WriteLine("INFO | SystemAccentColorSecondary: " + secondaryAccent, "Wpf.Ui.Accent");
        System.Diagnostics.Debug.WriteLine("INFO | SystemAccentColorTertiary: " + tertiaryAccent, "Wpf.Ui.Accent");
#endif

        if (secondaryAccent.GetBrightness() > BackgroundBrightnessThresholdValue)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("INFO | Text on accent is DARK", "Wpf.Ui.Accent");
#endif
            Application.Current.Resources["TextOnAccentFillColorPrimary"] = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            Application.Current.Resources["TextOnAccentFillColorSecondary"] = Color.FromArgb(0x80, 0x00, 0x00, 0x00);
            Application.Current.Resources["TextOnAccentFillColorDisabled"] = Color.FromArgb(0x77, 0x00, 0x00, 0x00);
            Application.Current.Resources["TextOnAccentFillColorSelectedText"] = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
            Application.Current.Resources["AccentTextFillColorDisabled"] = Color.FromArgb(0x5D, 0x00, 0x00, 0x00);
        }
        else
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("INFO | Text on accent is LIGHT", "Wpf.Ui.Accent");
#endif
            Application.Current.Resources["TextOnAccentFillColorPrimary"] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            Application.Current.Resources["TextOnAccentFillColorSecondary"] = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            Application.Current.Resources["TextOnAccentFillColorDisabled"] = Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF);
            Application.Current.Resources["TextOnAccentFillColorSelectedText"] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            Application.Current.Resources["AccentTextFillColorDisabled"] = Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF);
        }

        Application.Current.Resources["SystemAccentColor"] = systemAccent;
        Application.Current.Resources["SystemAccentColorPrimary"] = primaryAccent;
        Application.Current.Resources["SystemAccentColorSecondary"] = secondaryAccent;
        Application.Current.Resources["SystemAccentColorTertiary"] = tertiaryAccent;

        Application.Current.Resources["SystemAccentBrush"] = CreateBrush(secondaryAccent);
        Application.Current.Resources["SystemAccentColorBrush"] = CreateBrush(systemAccent);
        Application.Current.Resources["SystemAccentColorPrimaryBrush"] = CreateBrush(primaryAccent);
        Application.Current.Resources["SystemAccentColorSecondaryBrush"] = CreateBrush(secondaryAccent);
        Application.Current.Resources["SystemAccentColorTertiaryBrush"] = CreateBrush(tertiaryAccent);
        Application.Current.Resources["SystemFillColorAttentionBrush"] = CreateBrush(secondaryAccent);
        Application.Current.Resources["AccentTextFillColorPrimaryBrush"] = CreateBrush(tertiaryAccent);
        Application.Current.Resources["AccentTextFillColorSecondaryBrush"] = CreateBrush(tertiaryAccent);
        Application.Current.Resources["AccentTextFillColorTertiaryBrush"] = CreateBrush(secondaryAccent);
        Application.Current.Resources["AccentFillColorSelectedTextBackgroundBrush"] = CreateBrush(systemAccent);
        Application.Current.Resources["AccentFillColorDefaultBrush"] = CreateBrush(secondaryAccent);

        Application.Current.Resources["AccentFillColorSecondaryBrush"] = CreateBrush(secondaryAccent, 0.9);
        Application.Current.Resources["AccentFillColorTertiaryBrush"] = CreateBrush(secondaryAccent, 0.8);
    }

    private static Brush GetBrush(string resourceKey, Color fallbackColor)
    {
        return Application.Current.Resources[resourceKey] as Brush ?? CreateBrush(fallbackColor);
    }

    private static SolidColorBrush CreateBrush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        if (brush.CanFreeze)
            brush.Freeze();

        return brush;
    }
}
