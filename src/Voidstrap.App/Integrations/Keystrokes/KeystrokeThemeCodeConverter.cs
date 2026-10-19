using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voidstrap.Integrations.Keystrokes;

public enum ThemeCodeLanguage
{
    Json,
    CSharp,
    HtmlCss,
    Rust
}

public static class KeystrokeThemeCodeConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetExtension(ThemeCodeLanguage language) => language switch
    {
        ThemeCodeLanguage.CSharp => ".cs",
        ThemeCodeLanguage.HtmlCss => ".html",
        ThemeCodeLanguage.Rust => ".rs",
        _ => ".json"
    };

    public static string GetDisplayName(ThemeCodeLanguage language) => language switch
    {
        ThemeCodeLanguage.CSharp => "C# (.cs)",
        ThemeCodeLanguage.HtmlCss => "HTML / CSS (.html)",
        ThemeCodeLanguage.Rust => "Rust (.rs)",
        _ => "JSON (.json)"
    };

    public static ThemeCodeLanguage DetectLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ThemeCodeLanguage.Json;

        string trimmed = code.Trim();
        if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<style", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeCodeLanguage.HtmlCss;
        }

        if (trimmed.Contains("pub const THEME", StringComparison.Ordinal) ||
            trimmed.Contains("pub struct", StringComparison.Ordinal) ||
            trimmed.Contains("key_background:", StringComparison.Ordinal))
        {
            return ThemeCodeLanguage.Rust;
        }

        if (trimmed.Contains("public static class", StringComparison.Ordinal) ||
            trimmed.Contains("new KeystrokeTheme", StringComparison.Ordinal) ||
            trimmed.Contains("new KeycapConfig", StringComparison.Ordinal) ||
            trimmed.Contains("using System", StringComparison.Ordinal))
        {
            return ThemeCodeLanguage.CSharp;
        }

        return ThemeCodeLanguage.Json;
    }

    public static string GenerateCode(KeystrokeTheme theme, ThemeCodeLanguage language) => language switch
    {
        ThemeCodeLanguage.CSharp => GenerateCSharp(theme),
        ThemeCodeLanguage.HtmlCss => GenerateHtml(theme),
        ThemeCodeLanguage.Rust => GenerateRust(theme),
        _ => GenerateJson(theme)
    };

    public static bool TryParseCode(string code, ThemeCodeLanguage language, KeystrokeTheme target, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            errorMessage = "Code cannot be empty.";
            return false;
        }

        try
        {
            return language switch
            {
                ThemeCodeLanguage.CSharp => TryParseCSharp(code, target, out errorMessage),
                ThemeCodeLanguage.HtmlCss => TryParseHtml(code, target, out errorMessage),
                ThemeCodeLanguage.Rust => TryParseRust(code, target, out errorMessage),
                _ => TryParseJson(code, target, out errorMessage)
            };
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string GenerateJson(KeystrokeTheme theme)
    {
        return JsonSerializer.Serialize(theme, JsonOptions);
    }

    private static bool TryParseJson(string code, KeystrokeTheme target, out string? errorMessage)
    {
        errorMessage = null;
        var parsed = JsonSerializer.Deserialize<KeystrokeTheme>(code);
        if (parsed == null)
        {
            errorMessage = "Failed to deserialize JSON.";
            return false;
        }

        CopyProperties(parsed, target);
        return true;
    }

    private static string GenerateCSharp(KeystrokeTheme theme)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Voidstrap.Integrations.Keystrokes;");
        sb.AppendLine();
        sb.AppendLine("public static class ThemeDefinition");
        sb.AppendLine("{");
        sb.AppendLine("    public static KeystrokeTheme CreateTheme()");
        sb.AppendLine("    {");
        sb.AppendLine("        var theme = new KeystrokeTheme");
        sb.AppendLine("        {");
        sb.AppendLine($"            Name = \"{EscapeString(theme.Name)}\",");
        sb.AppendLine($"            KeyBackground = \"{EscapeString(theme.KeyBackground)}\",");
        sb.AppendLine($"            KeyPressedBackground = \"{EscapeString(theme.KeyPressedBackground)}\",");
        sb.AppendLine($"            KeyBorderColor = \"{EscapeString(theme.KeyBorderColor)}\",");
        sb.AppendLine($"            KeyBorderThickness = {theme.KeyBorderThickness.ToString("0.0", CultureInfo.InvariantCulture)},");
        sb.AppendLine($"            KeyCornerRadius = {theme.KeyCornerRadius.ToString("0.0", CultureInfo.InvariantCulture)},");
        sb.AppendLine($"            KeyTextColor = \"{EscapeString(theme.KeyTextColor)}\",");
        sb.AppendLine($"            KeyPressedTextColor = \"{EscapeString(theme.KeyPressedTextColor)}\",");
        sb.AppendLine($"            CpsTextColor = \"{EscapeString(theme.CpsTextColor)}\",");
        sb.AppendLine($"            KeyPressedCpsTextColor = \"{EscapeString(theme.KeyPressedCpsTextColor)}\",");
        sb.AppendLine($"            BackdropBlur = {theme.BackdropBlur.ToString().ToLowerInvariant()},");
        sb.AppendLine($"            KeyScaleOnPress = {theme.KeyScaleOnPress.ToString("0.00", CultureInfo.InvariantCulture)},");
        sb.AppendLine($"            FontFamily = \"{EscapeString(theme.FontFamily)}\",");
        sb.AppendLine($"            FontWeight = \"{EscapeString(theme.FontWeight)}\",");
        sb.AppendLine($"            KeyBackgroundImage = {FormatCsStringOrNull(theme.KeyBackgroundImage)},");
        sb.AppendLine($"            KeyPressedBackgroundImage = {FormatCsStringOrNull(theme.KeyPressedBackgroundImage)},");
        sb.AppendLine($"            KeyImageStretch = \"{EscapeString(theme.KeyImageStretch)}\",");
        sb.AppendLine($"            KeyImageOpacity = {theme.KeyImageOpacity.ToString("0.00", CultureInfo.InvariantCulture)}");
        sb.AppendLine("        };");

        foreach (var pair in theme.Keycaps)
        {
            var cap = pair.Value;
            sb.AppendLine();
            sb.AppendLine($"        theme.Keycaps[\"{EscapeString(pair.Key)}\"] = new KeycapConfig");
            sb.AppendLine("        {");
            sb.AppendLine($"            Image = {FormatCsStringOrNull(cap.Image)},");
            sb.AppendLine($"            PressedImage = {FormatCsStringOrNull(cap.PressedImage)},");
            sb.AppendLine($"            Width = {FormatCsDoubleOrNull(cap.Width)},");
            sb.AppendLine($"            Height = {FormatCsDoubleOrNull(cap.Height)},");
            sb.AppendLine($"            CornerRadius = {FormatCsDoubleOrNull(cap.CornerRadius)},");
            sb.AppendLine($"            Text = {FormatCsStringOrNull(cap.Text)},");
            sb.AppendLine($"            HideText = {cap.HideText.ToString().ToLowerInvariant()}");
            sb.AppendLine("        };");
        }

        sb.AppendLine();
        sb.AppendLine("        return theme;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static bool TryParseCSharp(string code, KeystrokeTheme target, out string? errorMessage)
    {
        errorMessage = null;

        string? name = MatchStringProperty(code, "Name");
        if (name != null)
            target.Name = name;

        string? keyBg = MatchStringProperty(code, "KeyBackground");
        if (keyBg != null)
            target.KeyBackground = keyBg;

        string? keyPressBg = MatchStringProperty(code, "KeyPressedBackground");
        if (keyPressBg != null)
            target.KeyPressedBackground = keyPressBg;

        string? borderCol = MatchStringProperty(code, "KeyBorderColor");
        if (borderCol != null)
            target.KeyBorderColor = borderCol;

        double? borderThick = MatchDoubleProperty(code, "KeyBorderThickness");
        if (borderThick.HasValue)
            target.KeyBorderThickness = borderThick.Value;

        double? cornerRad = MatchDoubleProperty(code, "KeyCornerRadius");
        if (cornerRad.HasValue)
            target.KeyCornerRadius = cornerRad.Value;

        string? textCol = MatchStringProperty(code, "KeyTextColor");
        if (textCol != null)
            target.KeyTextColor = textCol;

        string? pressTextCol = MatchStringProperty(code, "KeyPressedTextColor");
        if (pressTextCol != null)
            target.KeyPressedTextColor = pressTextCol;

        string? cpsCol = MatchStringProperty(code, "CpsTextColor");
        if (cpsCol != null)
            target.CpsTextColor = cpsCol;

        string? pressCpsCol = MatchStringProperty(code, "KeyPressedCpsTextColor");
        if (pressCpsCol != null)
            target.KeyPressedCpsTextColor = pressCpsCol;

        bool? blur = MatchBoolProperty(code, "BackdropBlur");
        if (blur.HasValue)
            target.BackdropBlur = blur.Value;

        double? scale = MatchDoubleProperty(code, "KeyScaleOnPress");
        if (scale.HasValue)
            target.KeyScaleOnPress = scale.Value;

        string? fontFamily = MatchStringProperty(code, "FontFamily");
        if (fontFamily != null)
            target.FontFamily = fontFamily;

        string? fontWeight = MatchStringProperty(code, "FontWeight");
        if (fontWeight != null)
            target.FontWeight = fontWeight;

        string? keyBgImg = MatchStringProperty(code, "KeyBackgroundImage");
        target.KeyBackgroundImage = keyBgImg;

        string? keyPressBgImg = MatchStringProperty(code, "KeyPressedBackgroundImage");
        target.KeyPressedBackgroundImage = keyPressBgImg;

        string? stretch = MatchStringProperty(code, "KeyImageStretch");
        if (stretch != null)
            target.KeyImageStretch = stretch;

        double? opacity = MatchDoubleProperty(code, "KeyImageOpacity");
        if (opacity.HasValue)
            target.KeyImageOpacity = opacity.Value;

        var keycapMatches = Regex.Matches(code, @"theme\.Keycaps\[""([^""]+)""\]\s*=\s*new\s*(?:KeycapConfig)?\s*\{([^}]+)\}", RegexOptions.Singleline);
        foreach (Match match in keycapMatches)
        {
            string keyName = match.Groups[1].Value;
            string body = match.Groups[2].Value;

            var cap = target.GetOrCreateKeycap(keyName);
            cap.Image = MatchStringProperty(body, "Image");
            cap.PressedImage = MatchStringProperty(body, "PressedImage");
            cap.Width = MatchDoubleProperty(body, "Width");
            cap.Height = MatchDoubleProperty(body, "Height");
            cap.CornerRadius = MatchDoubleProperty(body, "CornerRadius");
            cap.Text = MatchStringProperty(body, "Text");
            bool? hide = MatchBoolProperty(body, "HideText");
            if (hide.HasValue)
                cap.HideText = hide.Value;
        }

        return true;
    }

    private static string GenerateHtml(KeystrokeTheme theme)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\" />");
        sb.AppendLine($"  <title>{EscapeHtml(theme.Name)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root {");
        sb.AppendLine($"      --key-bg: {theme.KeyBackground};");
        sb.AppendLine($"      --key-pressed-bg: {theme.KeyPressedBackground};");
        sb.AppendLine($"      --key-border-color: {theme.KeyBorderColor};");
        sb.AppendLine($"      --key-border-thickness: {theme.KeyBorderThickness.ToString("0.0", CultureInfo.InvariantCulture)}px;");
        sb.AppendLine($"      --key-corner-radius: {theme.KeyCornerRadius.ToString("0.0", CultureInfo.InvariantCulture)}px;");
        sb.AppendLine($"      --key-text-color: {theme.KeyTextColor};");
        sb.AppendLine($"      --key-pressed-text-color: {theme.KeyPressedTextColor};");
        sb.AppendLine($"      --cps-text-color: {theme.CpsTextColor};");
        sb.AppendLine($"      --key-pressed-cps-text-color: {theme.KeyPressedCpsTextColor};");
        sb.AppendLine($"      --key-font-family: '{EscapeString(theme.FontFamily)}';");
        sb.AppendLine($"      --key-font-weight: {EscapeString(theme.FontWeight)};");
        sb.AppendLine($"      --key-scale-on-press: {theme.KeyScaleOnPress.ToString("0.00", CultureInfo.InvariantCulture)};");
        sb.AppendLine($"      --key-image-stretch: {EscapeString(theme.KeyImageStretch)};");
        sb.AppendLine($"      --key-image-opacity: {theme.KeyImageOpacity.ToString("0.00", CultureInfo.InvariantCulture)};");
        sb.AppendLine($"      --backdrop-blur: {theme.BackdropBlur.ToString().ToLowerInvariant()};");
        if (!string.IsNullOrWhiteSpace(theme.KeyBackgroundImage))
            sb.AppendLine($"      --key-bg-image: url('{EscapeString(theme.KeyBackgroundImage)}');");
        if (!string.IsNullOrWhiteSpace(theme.KeyPressedBackgroundImage))
            sb.AppendLine($"      --key-pressed-bg-image: url('{EscapeString(theme.KeyPressedBackgroundImage)}');");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    .keystrokes-overlay {");
        sb.AppendLine("      display: flex;");
        sb.AppendLine("      flex-direction: column;");
        sb.AppendLine("      gap: 6px;");
        sb.AppendLine("      font-family: var(--key-font-family);");
        sb.AppendLine("      font-weight: var(--key-font-weight);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    .keycap {");
        sb.AppendLine("      background-color: var(--key-bg);");
        sb.AppendLine("      border: var(--key-border-thickness) solid var(--key-border-color);");
        sb.AppendLine("      border-radius: var(--key-corner-radius);");
        sb.AppendLine("      color: var(--key-text-color);");
        sb.AppendLine("      display: inline-flex;");
        sb.AppendLine("      align-items: center;");
        sb.AppendLine("      justify-content: center;");
        sb.AppendLine("      background-size: cover;");
        sb.AppendLine("      background-position: center;");
        sb.AppendLine("      transition: transform 0.08s ease;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    .keycap.pressed {");
        sb.AppendLine("      background-color: var(--key-pressed-bg);");
        sb.AppendLine("      color: var(--key-pressed-text-color);");
        sb.AppendLine("      transform: scale(var(--key-scale-on-press));");
        sb.AppendLine("    }");

        foreach (var pair in theme.Keycaps)
        {
            var cap = pair.Value;
            string key = pair.Key;
            sb.AppendLine();
            sb.AppendLine($"    .keycap[data-key=\"{EscapeString(key)}\"] {{");
            if (cap.Width.HasValue)
                sb.AppendLine($"      width: {cap.Width.Value.ToString("0.0", CultureInfo.InvariantCulture)}px;");
            if (cap.Height.HasValue)
                sb.AppendLine($"      height: {cap.Height.Value.ToString("0.0", CultureInfo.InvariantCulture)}px;");
            if (cap.CornerRadius.HasValue)
                sb.AppendLine($"      border-radius: {cap.CornerRadius.Value.ToString("0.0", CultureInfo.InvariantCulture)}px;");
            if (!string.IsNullOrWhiteSpace(cap.Image))
                sb.AppendLine($"      background-image: url('{EscapeString(cap.Image)}');");
            sb.AppendLine("    }");

            if (!string.IsNullOrWhiteSpace(cap.PressedImage))
            {
                sb.AppendLine($"    .keycap[data-key=\"{EscapeString(key)}\"].pressed {{");
                sb.AppendLine($"      background-image: url('{EscapeString(cap.PressedImage)}');");
                sb.AppendLine("    }");
            }
        }

        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"keystrokes-overlay\">");

        string[][] rows = {
            new[] { "W" },
            new[] { "A", "S", "D" },
            new[] { "LMB", "RMB" },
            new[] { "Shift", "Space" }
        };

        foreach (var row in rows)
        {
            sb.AppendLine("    <div class=\"row\">");
            foreach (var key in row)
            {
                string label = key;
                bool hide = false;
                if (theme.Keycaps.TryGetValue(key, out var cap))
                {
                    if (!string.IsNullOrEmpty(cap.Text))
                        label = cap.Text;
                    hide = cap.HideText;
                }
                sb.AppendLine($"      <div class=\"keycap\" data-key=\"{key}\" data-label=\"{EscapeHtml(label)}\" data-hide-label=\"{hide.ToString().ToLowerInvariant()}\">{EscapeHtml(hide ? string.Empty : label)}</div>");
            }
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static bool TryParseHtml(string code, KeystrokeTheme target, out string? errorMessage)
    {
        errorMessage = null;

        string? title = MatchGroup(code, @"<title>([^<]+)</title>");
        if (!string.IsNullOrWhiteSpace(title))
            target.Name = title.Trim();

        string? keyBg = MatchCssVariable(code, "--key-bg");
        if (keyBg != null)
            target.KeyBackground = keyBg;

        string? keyPressBg = MatchCssVariable(code, "--key-pressed-bg");
        if (keyPressBg != null)
            target.KeyPressedBackground = keyPressBg;

        string? borderCol = MatchCssVariable(code, "--key-border-color");
        if (borderCol != null)
            target.KeyBorderColor = borderCol;

        double? borderThick = MatchCssPixel(code, "--key-border-thickness");
        if (borderThick.HasValue)
            target.KeyBorderThickness = borderThick.Value;

        double? cornerRad = MatchCssPixel(code, "--key-corner-radius");
        if (cornerRad.HasValue)
            target.KeyCornerRadius = cornerRad.Value;

        string? textCol = MatchCssVariable(code, "--key-text-color");
        if (textCol != null)
            target.KeyTextColor = textCol;

        string? pressTextCol = MatchCssVariable(code, "--key-pressed-text-color");
        if (pressTextCol != null)
            target.KeyPressedTextColor = pressTextCol;

        string? cpsCol = MatchCssVariable(code, "--cps-text-color");
        if (cpsCol != null)
            target.CpsTextColor = cpsCol;

        string? pressCpsCol = MatchCssVariable(code, "--key-pressed-cps-text-color");
        if (pressCpsCol != null)
            target.KeyPressedCpsTextColor = pressCpsCol;

        string? fontFamily = MatchCssVariable(code, "--key-font-family")?.Trim('\'', '"', ' ');
        if (!string.IsNullOrWhiteSpace(fontFamily))
            target.FontFamily = fontFamily;

        string? fontWeight = MatchCssVariable(code, "--key-font-weight");
        if (!string.IsNullOrWhiteSpace(fontWeight))
            target.FontWeight = fontWeight;

        double? scale = MatchDouble(MatchCssVariable(code, "--key-scale-on-press"));
        if (scale.HasValue)
            target.KeyScaleOnPress = scale.Value;

        string? stretch = MatchCssVariable(code, "--key-image-stretch");
        if (!string.IsNullOrWhiteSpace(stretch))
            target.KeyImageStretch = stretch;

        double? opacity = MatchDouble(MatchCssVariable(code, "--key-image-opacity"));
        if (opacity.HasValue)
            target.KeyImageOpacity = opacity.Value;

        string? blur = MatchCssVariable(code, "--backdrop-blur");
        if (blur != null)
            target.BackdropBlur = string.Equals(blur.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        string? bgImg = MatchCssUrl(code, "--key-bg-image");
        if (bgImg != null)
            target.KeyBackgroundImage = bgImg;

        string? pressBgImg = MatchCssUrl(code, "--key-pressed-bg-image");
        if (pressBgImg != null)
            target.KeyPressedBackgroundImage = pressBgImg;

        var keycapBlocks = Regex.Matches(code, @"\.keycap\[data-key=[""']?([a-zA-Z0-9]+)[""']?\]\s*\{([^}]+)\}", RegexOptions.Singleline);
        foreach (Match match in keycapBlocks)
        {
            string keyName = match.Groups[1].Value;
            string body = match.Groups[2].Value;
            var cap = target.GetOrCreateKeycap(keyName);

            double? w = MatchCssPixel(body, "width");
            if (w.HasValue)
                cap.Width = w.Value;

            double? h = MatchCssPixel(body, "height");
            if (h.HasValue)
                cap.Height = h.Value;

            double? cr = MatchCssPixel(body, "border-radius");
            if (cr.HasValue)
                cap.CornerRadius = cr.Value;

            string? img = MatchCssUrl(body, "background-image");
            if (img != null)
                cap.Image = img;
        }

        var pressedBlocks = Regex.Matches(code, @"\.keycap\[data-key=[""']?([a-zA-Z0-9]+)[""']?\]\.pressed\s*\{([^}]+)\}", RegexOptions.Singleline);
        foreach (Match match in pressedBlocks)
        {
            string keyName = match.Groups[1].Value;
            string body = match.Groups[2].Value;
            var cap = target.GetOrCreateKeycap(keyName);
            string? img = MatchCssUrl(body, "background-image");
            if (img != null)
                cap.PressedImage = img;
        }

        var htmlTags = Regex.Matches(code, @"<div[^>]*data-key=[""']?([a-zA-Z0-9]+)[""']?[^>]*>", RegexOptions.Singleline);
        foreach (Match match in htmlTags)
        {
            string tagText = match.Value;
            string keyName = match.Groups[1].Value;
            var cap = target.GetOrCreateKeycap(keyName);

            string? label = MatchGroup(tagText, @"data-label=[""']([^""']*)[""']");
            if (!string.IsNullOrEmpty(label))
                cap.Text = label;

            string? hide = MatchGroup(tagText, @"data-hide-label=[""']([^""']*)[""']");
            if (!string.IsNullOrEmpty(hide))
                cap.HideText = string.Equals(hide.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string GenerateRust(KeystrokeTheme theme)
    {
        var sb = new StringBuilder();
        sb.AppendLine("pub struct KeycapConfig {");
        sb.AppendLine("    pub image: Option<&'static str>,");
        sb.AppendLine("    pub pressed_image: Option<&'static str>,");
        sb.AppendLine("    pub width: Option<f64>,");
        sb.AppendLine("    pub height: Option<f64>,");
        sb.AppendLine("    pub corner_radius: Option<f64>,");
        sb.AppendLine("    pub text: Option<&'static str>,");
        sb.AppendLine("    pub hide_text: bool,");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("pub struct KeystrokeTheme {");
        sb.AppendLine("    pub name: &'static str,");
        sb.AppendLine("    pub key_background: &'static str,");
        sb.AppendLine("    pub key_pressed_background: &'static str,");
        sb.AppendLine("    pub key_border_color: &'static str,");
        sb.AppendLine("    pub key_border_thickness: f64,");
        sb.AppendLine("    pub key_corner_radius: f64,");
        sb.AppendLine("    pub key_text_color: &'static str,");
        sb.AppendLine("    pub key_pressed_text_color: &'static str,");
        sb.AppendLine("    pub cps_text_color: &'static str,");
        sb.AppendLine("    pub key_pressed_cps_text_color: &'static str,");
        sb.AppendLine("    pub backdrop_blur: bool,");
        sb.AppendLine("    pub key_scale_on_press: f64,");
        sb.AppendLine("    pub font_family: &'static str,");
        sb.AppendLine("    pub font_weight: &'static str,");
        sb.AppendLine("    pub key_background_image: Option<&'static str>,");
        sb.AppendLine("    pub key_pressed_background_image: Option<&'static str>,");
        sb.AppendLine("    pub key_image_stretch: &'static str,");
        sb.AppendLine("    pub key_image_opacity: f64,");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("pub const THEME: KeystrokeTheme = KeystrokeTheme {");
        sb.AppendLine($"    name: \"{EscapeString(theme.Name)}\",");
        sb.AppendLine($"    key_background: \"{EscapeString(theme.KeyBackground)}\",");
        sb.AppendLine($"    key_pressed_background: \"{EscapeString(theme.KeyPressedBackground)}\",");
        sb.AppendLine($"    key_border_color: \"{EscapeString(theme.KeyBorderColor)}\",");
        sb.AppendLine($"    key_border_thickness: {theme.KeyBorderThickness.ToString("0.0", CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    key_corner_radius: {theme.KeyCornerRadius.ToString("0.0", CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    key_text_color: \"{EscapeString(theme.KeyTextColor)}\",");
        sb.AppendLine($"    key_pressed_text_color: \"{EscapeString(theme.KeyPressedTextColor)}\",");
        sb.AppendLine($"    cps_text_color: \"{EscapeString(theme.CpsTextColor)}\",");
        sb.AppendLine($"    key_pressed_cps_text_color: \"{EscapeString(theme.KeyPressedCpsTextColor)}\",");
        sb.AppendLine($"    backdrop_blur: {theme.BackdropBlur.ToString().ToLowerInvariant()},");
        sb.AppendLine($"    key_scale_on_press: {theme.KeyScaleOnPress.ToString("0.00", CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    font_family: \"{EscapeString(theme.FontFamily)}\",");
        sb.AppendLine($"    font_weight: \"{EscapeString(theme.FontWeight)}\",");
        sb.AppendLine($"    key_background_image: {FormatRustOptionString(theme.KeyBackgroundImage)},");
        sb.AppendLine($"    key_pressed_background_image: {FormatRustOptionString(theme.KeyPressedBackgroundImage)},");
        sb.AppendLine($"    key_image_stretch: \"{EscapeString(theme.KeyImageStretch)}\",");
        sb.AppendLine($"    key_image_opacity: {theme.KeyImageOpacity.ToString("0.00", CultureInfo.InvariantCulture)},");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("pub fn get_keycap(key: &str) -> Option<KeycapConfig> {");
        sb.AppendLine("    match key {");

        foreach (var pair in theme.Keycaps)
        {
            var cap = pair.Value;
            sb.AppendLine($"        \"{EscapeString(pair.Key)}\" => Some(KeycapConfig {{");
            sb.AppendLine($"            image: {FormatRustOptionString(cap.Image)},");
            sb.AppendLine($"            pressed_image: {FormatRustOptionString(cap.PressedImage)},");
            sb.AppendLine($"            width: {FormatRustOptionDouble(cap.Width)},");
            sb.AppendLine($"            height: {FormatRustOptionDouble(cap.Height)},");
            sb.AppendLine($"            corner_radius: {FormatRustOptionDouble(cap.CornerRadius)},");
            sb.AppendLine($"            text: {FormatRustOptionString(cap.Text)},");
            sb.AppendLine($"            hide_text: {cap.HideText.ToString().ToLowerInvariant()},");
            sb.AppendLine("        }),");
        }

        sb.AppendLine("        _ => None,");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static bool TryParseRust(string code, KeystrokeTheme target, out string? errorMessage)
    {
        errorMessage = null;

        string? name = MatchRustString(code, "name");
        if (name != null)
            target.Name = name;

        string? keyBg = MatchRustString(code, "key_background");
        if (keyBg != null)
            target.KeyBackground = keyBg;

        string? keyPressBg = MatchRustString(code, "key_pressed_background");
        if (keyPressBg != null)
            target.KeyPressedBackground = keyPressBg;

        string? borderCol = MatchRustString(code, "key_border_color");
        if (borderCol != null)
            target.KeyBorderColor = borderCol;

        double? borderThick = MatchDouble(MatchGroup(code, @"key_border_thickness:\s*([0-9.]+)"));
        if (borderThick.HasValue)
            target.KeyBorderThickness = borderThick.Value;

        double? cornerRad = MatchDouble(MatchGroup(code, @"key_corner_radius:\s*([0-9.]+)"));
        if (cornerRad.HasValue)
            target.KeyCornerRadius = cornerRad.Value;

        string? textCol = MatchRustString(code, "key_text_color");
        if (textCol != null)
            target.KeyTextColor = textCol;

        string? pressTextCol = MatchRustString(code, "key_pressed_text_color");
        if (pressTextCol != null)
            target.KeyPressedTextColor = pressTextCol;

        string? cpsCol = MatchRustString(code, "cps_text_color");
        if (cpsCol != null)
            target.CpsTextColor = cpsCol;

        string? pressCpsCol = MatchRustString(code, "key_pressed_cps_text_color");
        if (pressCpsCol != null)
            target.KeyPressedCpsTextColor = pressCpsCol;

        string? blur = MatchGroup(code, @"backdrop_blur:\s*(true|false)");
        if (blur != null)
            target.BackdropBlur = string.Equals(blur, "true", StringComparison.OrdinalIgnoreCase);

        double? scale = MatchDouble(MatchGroup(code, @"key_scale_on_press:\s*([0-9.]+)"));
        if (scale.HasValue)
            target.KeyScaleOnPress = scale.Value;

        string? fontFamily = MatchRustString(code, "font_family");
        if (fontFamily != null)
            target.FontFamily = fontFamily;

        string? fontWeight = MatchRustString(code, "font_weight");
        if (fontWeight != null)
            target.FontWeight = fontWeight;

        string? bgImg = MatchRustOptionString(code, "key_background_image");
        target.KeyBackgroundImage = bgImg;

        string? pressBgImg = MatchRustOptionString(code, "key_pressed_background_image");
        target.KeyPressedBackgroundImage = pressBgImg;

        string? stretch = MatchRustString(code, "key_image_stretch");
        if (stretch != null)
            target.KeyImageStretch = stretch;

        double? opacity = MatchDouble(MatchGroup(code, @"key_image_opacity:\s*([0-9.]+)"));
        if (opacity.HasValue)
            target.KeyImageOpacity = opacity.Value;

        var keycapMatches = Regex.Matches(code, @"""([a-zA-Z0-9]+)""\s*=>\s*Some\(KeycapConfig\s*\{([^}]+)\}\)", RegexOptions.Singleline);
        foreach (Match match in keycapMatches)
        {
            string keyName = match.Groups[1].Value;
            string body = match.Groups[2].Value;
            var cap = target.GetOrCreateKeycap(keyName);

            cap.Image = MatchRustOptionString(body, "image");
            cap.PressedImage = MatchRustOptionString(body, "pressed_image");
            cap.Width = MatchRustOptionDouble(body, "width");
            cap.Height = MatchRustOptionDouble(body, "height");
            cap.CornerRadius = MatchRustOptionDouble(body, "corner_radius");
            cap.Text = MatchRustOptionString(body, "text");

            string? hide = MatchGroup(body, @"hide_text:\s*(true|false)");
            if (hide != null)
                cap.HideText = string.Equals(hide, "true", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static void CopyProperties(KeystrokeTheme source, KeystrokeTheme target)
    {
        target.Name = source.Name;
        target.KeyBackground = source.KeyBackground;
        target.KeyPressedBackground = source.KeyPressedBackground;
        target.KeyBorderColor = source.KeyBorderColor;
        target.KeyBorderThickness = source.KeyBorderThickness;
        target.KeyCornerRadius = source.KeyCornerRadius;
        target.KeyTextColor = source.KeyTextColor;
        target.KeyPressedTextColor = source.KeyPressedTextColor;
        target.CpsTextColor = source.CpsTextColor;
        target.KeyPressedCpsTextColor = source.KeyPressedCpsTextColor;
        target.BackdropBlur = source.BackdropBlur;
        target.KeyScaleOnPress = source.KeyScaleOnPress;
        target.FontFamily = source.FontFamily;
        target.FontWeight = source.FontWeight;
        target.KeyBackgroundImage = source.KeyBackgroundImage;
        target.KeyPressedBackgroundImage = source.KeyPressedBackgroundImage;
        target.OverlayBackgroundImage = source.OverlayBackgroundImage;
        target.KeyImages = source.KeyImages != null ? new Dictionary<string, string>(source.KeyImages, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        target.KeyPressedImages = source.KeyPressedImages != null ? new Dictionary<string, string>(source.KeyPressedImages, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        target.KeyImageStretch = source.KeyImageStretch;
        target.KeyImageOpacity = source.KeyImageOpacity;

        target.Keycaps.Clear();
        if (source.Keycaps != null)
        {
            foreach (var pair in source.Keycaps)
                target.Keycaps[pair.Key] = pair.Value.Clone();
        }
    }

    private static string EscapeString(string? val) => (val ?? string.Empty).Replace("\"", "\\\"");

    private static string EscapeHtml(string? val) => (val ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string FormatCsStringOrNull(string? val) => string.IsNullOrWhiteSpace(val) ? "null" : $"\"{EscapeString(val)}\"";

    private static string FormatCsDoubleOrNull(double? val) => val.HasValue ? val.Value.ToString("0.0", CultureInfo.InvariantCulture) : "null";

    private static string FormatRustOptionString(string? val) => string.IsNullOrWhiteSpace(val) ? "None" : $"Some(\"{EscapeString(val)}\")";

    private static string FormatRustOptionDouble(double? val) => val.HasValue ? $"Some({val.Value.ToString("0.0", CultureInfo.InvariantCulture)})" : "None";

    private static string? MatchGroup(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? MatchStringProperty(string input, string propertyName)
    {
        return MatchGroup(input, $@"{propertyName}\s*=\s*""([^""]*)""");
    }

    private static double? MatchDoubleProperty(string input, string propertyName)
    {
        string? raw = MatchGroup(input, $@"{propertyName}\s*=\s*([0-9.]+)");
        return MatchDouble(raw);
    }

    private static bool? MatchBoolProperty(string input, string propertyName)
    {
        string? raw = MatchGroup(input, $@"{propertyName}\s*=\s*(true|false)");
        if (raw == null)
            return null;
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MatchCssVariable(string input, string varName)
    {
        return MatchGroup(input, $@"{varName}\s*:\s*([^;]+);");
    }

    private static double? MatchCssPixel(string input, string propName)
    {
        string? raw = MatchGroup(input, $@"{propName}\s*:\s*([0-9.]+)\s*px");
        return MatchDouble(raw);
    }

    private static string? MatchCssUrl(string input, string propName)
    {
        return MatchGroup(input, $@"{propName}\s*:\s*url\(['""]?([^'"")]*)['""]?\)");
    }

    private static string? MatchRustString(string input, string propName)
    {
        return MatchGroup(input, $@"{propName}\s*:\s*""([^""]*)""");
    }

    private static string? MatchRustOptionString(string input, string propName)
    {
        return MatchGroup(input, $@"{propName}\s*:\s*Some\(""([^""]*)""\)");
    }

    private static double? MatchRustOptionDouble(string input, string propName)
    {
        string? raw = MatchGroup(input, $@"{propName}\s*:\s*Some\(([0-9.]+)\)");
        return MatchDouble(raw);
    }

    private static double? MatchDouble(string? raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            return val;
        return null;
    }
}
