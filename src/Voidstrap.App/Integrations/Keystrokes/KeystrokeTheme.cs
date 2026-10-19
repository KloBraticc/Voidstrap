using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    [JsonIgnore]
    private readonly Dictionary<string, Brush?> _brushCache = new(StringComparer.OrdinalIgnoreCase);

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

    public void InvalidateBrushCache()
    {
        lock (_brushCache)
        {
            _brushCache.Clear();
        }
    }

    public string? ResolveImagePath(string? relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            return null;

        string trimmed = relativeOrAbsolutePath.Trim();
        if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
            return trimmed;

        string themeDir = Path.Combine(Voidstrap.Paths.CustomKeystrokeThemes, Name);
        string combined = Path.Combine(themeDir, trimmed);
        if (File.Exists(combined))
            return combined;

        return null;
    }

    public ImageBrush? GetImageBrush(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string? resolved = ResolveImagePath(path);
        if (resolved == null)
            return null;

        lock (_brushCache)
        {
            string cacheKey = $"img:{resolved}:{KeyImageStretch}:{KeyImageOpacity}";
            if (_brushCache.TryGetValue(cacheKey, out var cached))
                return cached as ImageBrush;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(resolved, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                Stretch stretch = KeyImageStretch switch
                {
                    "Fill" => Stretch.Fill,
                    "UniformToFill" => Stretch.UniformToFill,
                    "None" => Stretch.None,
                    _ => Stretch.Uniform
                };

                var brush = new ImageBrush(bitmap)
                {
                    Stretch = stretch,
                    Opacity = Math.Clamp(KeyImageOpacity, 0.0, 1.0)
                };
                brush.Freeze();
                _brushCache[cacheKey] = brush;
                return brush;
            }
            catch
            {
                _brushCache[cacheKey] = null;
                return null;
            }
        }
    }

    public Brush GetKeyBrush(string key, bool isPressed)
    {
        if (isPressed)
        {
            if (KeyPressedImages.TryGetValue(key, out string? pressedImg) && !string.IsNullOrWhiteSpace(pressedImg))
            {
                var imgBrush = GetImageBrush(pressedImg);
                if (imgBrush != null)
                    return imgBrush;
            }

            if (!string.IsNullOrWhiteSpace(KeyPressedBackgroundImage))
            {
                var imgBrush = GetImageBrush(KeyPressedBackgroundImage);
                if (imgBrush != null)
                    return imgBrush;
            }

            return GetKeyPressedBackgroundBrush();
        }
        else
        {
            if (KeyImages.TryGetValue(key, out string? unpressedImg) && !string.IsNullOrWhiteSpace(unpressedImg))
            {
                var imgBrush = GetImageBrush(unpressedImg);
                if (imgBrush != null)
                    return imgBrush;
            }

            if (!string.IsNullOrWhiteSpace(KeyBackgroundImage))
            {
                var imgBrush = GetImageBrush(KeyBackgroundImage);
                if (imgBrush != null)
                    return imgBrush;
            }

            return GetKeyBackgroundBrush();
        }
    }

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
        KeyImageOpacity = KeyImageOpacity
    };
}
