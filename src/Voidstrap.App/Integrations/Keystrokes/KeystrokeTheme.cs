using System;
using System.Collections.Generic;
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

    public bool BackdropBlur { get; set; } = false;

    public double KeyScaleOnPress { get; set; } = 0.96;

    public string FontFamily { get; set; } = "Segoe UI";

    public string FontWeight { get; set; } = "SemiBold";

    public string? KeyBackgroundImage { get; set; }

    public string? KeyPressedBackgroundImage { get; set; }

    public string? OverlayBackgroundImage { get; set; }

    public Dictionary<string, string> KeyImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> KeyPressedImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string KeyImageStretch { get; set; } = "Uniform";

    public double KeyImageOpacity { get; set; } = 1.0;

    public Dictionary<string, KeycapConfig> Keycaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<List<string>> LayoutRows { get; set; } = new();

    public bool ShowMouseCircle { get; set; } = false;

    public double MouseCircleSize { get; set; } = 96.0;

    [JsonIgnore]
    public string? XmlSource { get; set; }

    public string GetXml() => XmlSource ?? KeystrokeXmlTemplates.FromTheme(this);

    public KeycapConfig GetOrCreateKeycap(string key)
    {
        if (!Keycaps.TryGetValue(key, out var cap))
        {
            cap = new KeycapConfig();
            Keycaps[key] = cap;
        }
        return cap;
    }

    public List<List<string>> GetEffectiveLayoutRows()
    {
        if (LayoutRows != null && LayoutRows.Count > 0)
            return LayoutRows;

        return new List<List<string>>
        {
            new() { "W" },
            new() { "A", "S", "D" },
            new() { "LMB", "RMB" },
            new() { "Shift", "Space" }
        };
    }

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

    public KeystrokeTheme Clone(string? newName = null)
    {
        var cloned = new KeystrokeTheme
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
            BackdropBlur = BackdropBlur,
            KeyScaleOnPress = KeyScaleOnPress,
            FontFamily = FontFamily,
            FontWeight = FontWeight,
            KeyBackgroundImage = KeyBackgroundImage,
            KeyPressedBackgroundImage = KeyPressedBackgroundImage,
            OverlayBackgroundImage = OverlayBackgroundImage,
            KeyImages = new Dictionary<string, string>(KeyImages, StringComparer.OrdinalIgnoreCase),
            KeyPressedImages = new Dictionary<string, string>(KeyPressedImages, StringComparer.OrdinalIgnoreCase),
            KeyImageStretch = KeyImageStretch,
            KeyImageOpacity = KeyImageOpacity,
            Keycaps = new Dictionary<string, KeycapConfig>(StringComparer.OrdinalIgnoreCase),
            ShowMouseCircle = ShowMouseCircle,
            MouseCircleSize = MouseCircleSize,
            XmlSource = XmlSource,
            LayoutRows = new List<List<string>>()
        };

        if (LayoutRows != null)
        {
            foreach (var r in LayoutRows)
                cloned.LayoutRows.Add(new List<string>(r));
        }

        foreach (var pair in Keycaps)
            cloned.Keycaps[pair.Key] = pair.Value.Clone();

        return cloned;
    }
}

public sealed class KeycapConfig
{
    public string? Image { get; set; }
    public string? PressedImage { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? CornerRadius { get; set; }
    public string? Text { get; set; }
    public bool HideText { get; set; }

    public KeycapConfig Clone() => new()
    {
        Image = Image,
        PressedImage = PressedImage,
        Width = Width,
        Height = Height,
        CornerRadius = CornerRadius,
        Text = Text,
        HideText = HideText
    };
}
