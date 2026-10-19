using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Voidstrap.Integrations.Keystrokes;

public sealed class KeystrokeTheme
{
    public string Name { get; set; } = "Voidstrap (Default)";

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public string KeyBackground { get; set; } = "#202020";

    public string KeyPressedBackground { get; set; } = "Accent";

    public string KeyBorderColor { get; set; } = "#363638";

    public double KeyBorderThickness { get; set; } = 1.0;

    public double KeyCornerRadius { get; set; } = 8.0;

    public string KeyTextColor { get; set; } = "#FFFFFF";

    public string KeyPressedTextColor { get; set; } = "#FFFFFF";

    public string CpsTextColor { get; set; } = "#A0A0A5";

    public string KeyPressedCpsTextColor { get; set; } = "#FFFFFF";

    public string KeyGlowColor { get; set; } = "Accent";

    public double KeyGlowRadius { get; set; } = 8.0;

    public double KeyGlowOpacity { get; set; } = 0.5;

    public bool BackdropBlur { get; set; } = false;

    public double KeyScaleOnPress { get; set; } = 0.96;

    public string FontFamily { get; set; } = "Segoe UI";

    public string FontWeight { get; set; } = "SemiBold";

    public static Color GetEffectiveAccentColor()
    {
        try
        {
            return Voidstrap.Utility.SystemAccent.Get();
        }
        catch
        {
            return Color.FromRgb(0, 120, 212);
        }
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;

        if (string.Equals(hex.Trim(), "Accent", StringComparison.OrdinalIgnoreCase))
            return GetEffectiveAccentColor();

        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6)
        {
            if (byte.TryParse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                byte.TryParse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                byte.TryParse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return Color.FromArgb(255, r, g, b);
            }
        }
        else if (clean.Length == 8)
        {
            if (byte.TryParse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte a) &&
                byte.TryParse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                byte.TryParse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                byte.TryParse(clean.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return Color.FromArgb(a, r, g, b);
            }
        }

        return fallback;
    }

    public static SolidColorBrush CreateBrush(string? hex, Color fallback)
    {
        var brush = new SolidColorBrush(ParseColor(hex, fallback));
        brush.Freeze();
        return brush;
    }

    public SolidColorBrush GetKeyBackgroundBrush() => CreateBrush(KeyBackground, Color.FromArgb(255, 32, 32, 32));

    public SolidColorBrush GetKeyPressedBackgroundBrush() => CreateBrush(KeyPressedBackground, GetEffectiveAccentColor());

    public SolidColorBrush GetKeyBorderBrush() => CreateBrush(KeyBorderColor, Color.FromArgb(255, 54, 54, 56));

    public SolidColorBrush GetKeyTextBrush() => CreateBrush(KeyTextColor, Colors.White);

    public SolidColorBrush GetKeyPressedTextBrush() => CreateBrush(KeyPressedTextColor, Colors.White);

    public SolidColorBrush GetCpsTextBrush() => CreateBrush(CpsTextColor, Color.FromArgb(255, 160, 160, 165));

    public SolidColorBrush GetKeyPressedCpsTextBrush() => CreateBrush(KeyPressedCpsTextColor, Colors.White);

    public Color GetGlowColor() => ParseColor(KeyGlowColor, GetEffectiveAccentColor());

    public KeystrokeTheme Clone(string? newName = null) => new()
    {
        Name = newName ?? Name,
        IsBuiltIn = false,
        KeyBackground = KeyBackground,
        KeyPressedBackground = KeyPressedBackground,
        KeyBorderColor = KeyBorderColor,
        KeyBorderThickness = KeyBorderThickness,
        KeyCornerRadius = KeyCornerRadius,
        KeyTextColor = KeyTextColor,
        KeyPressedTextColor = KeyPressedTextColor,
        CpsTextColor = CpsTextColor,
        KeyPressedCpsTextColor = KeyPressedCpsTextColor,
        KeyGlowColor = KeyGlowColor,
        KeyGlowRadius = KeyGlowRadius,
        KeyGlowOpacity = KeyGlowOpacity,
        BackdropBlur = BackdropBlur,
        KeyScaleOnPress = KeyScaleOnPress,
        FontFamily = FontFamily,
        FontWeight = FontWeight
    };
}
