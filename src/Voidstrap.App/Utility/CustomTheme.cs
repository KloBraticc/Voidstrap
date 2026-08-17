using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace Voidstrap.Utility
{
    public sealed class ThemeKeyInfo
    {
        public string Key { get; init; } = "";

        public string Label { get; init; } = "";

        public string Group { get; init; } = "";

        public bool IsBrush { get; init; }

        public string Fallback { get; init; } = "#FF202020";
    }

    public sealed class ThemeValidationResult
    {
        public bool Ok => Errors.Count == 0;

        public List<string> Errors { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public int ErrorLine { get; set; }

        public ResourceDictionary? Dictionary { get; set; }
    }

    public static class CustomTheme
    {
        private const string LOG_IDENT = "CustomTheme";

        private const int MaximumXamlCharacters = 200000;

        public const long MaximumXamlFileBytes = 1048576;

        private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly HashSet<string> AllowedElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "ResourceDictionary",
            "Color",
            "SolidColorBrush",
            "LinearGradientBrush",
            "RadialGradientBrush",
            "GradientStop",
            "GradientStopCollection",
            "GradientBrush.GradientStops",
            "LinearGradientBrush.GradientStops",
            "RadialGradientBrush.GradientStops"
        };

        private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedAttributes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["ResourceDictionary"] = new HashSet<string>(StringComparer.Ordinal),
            ["Color"] = new HashSet<string>(StringComparer.Ordinal) { "Key" },
            ["SolidColorBrush"] = new HashSet<string>(StringComparer.Ordinal) { "Key", "Color", "Opacity" },
            ["LinearGradientBrush"] = new HashSet<string>(StringComparer.Ordinal) { "Key", "StartPoint", "EndPoint", "MappingMode", "SpreadMethod", "ColorInterpolationMode", "Opacity" },
            ["RadialGradientBrush"] = new HashSet<string>(StringComparer.Ordinal) { "Key", "Center", "GradientOrigin", "RadiusX", "RadiusY", "MappingMode", "SpreadMethod", "ColorInterpolationMode", "Opacity" },
            ["GradientStop"] = new HashSet<string>(StringComparer.Ordinal) { "Color", "Offset" },
            ["GradientStopCollection"] = new HashSet<string>(StringComparer.Ordinal),
            ["GradientBrush.GradientStops"] = new HashSet<string>(StringComparer.Ordinal),
            ["LinearGradientBrush.GradientStops"] = new HashSet<string>(StringComparer.Ordinal),
            ["RadialGradientBrush.GradientStops"] = new HashSet<string>(StringComparer.Ordinal)
        };

        public static IReadOnlyList<ThemeKeyInfo> Schema { get; } = new List<ThemeKeyInfo>
        {
            new ThemeKeyInfo { Key = "WindowBackgroundColorPrimary", Label = "Window base", Group = "Window", IsBrush = false, Fallback = "#CC202020" },
            new ThemeKeyInfo { Key = "WindowBackgroundColorSecondary", Label = "Window glow", Group = "Window", IsBrush = false, Fallback = "#CC202020" },
            new ThemeKeyInfo { Key = "WindowBackgroundColorThird", Label = "Window edge", Group = "Window", IsBrush = false, Fallback = "#CC202020" },
            new ThemeKeyInfo { Key = "PrimaryBackgroundColor", Label = "Panels and footer", Group = "Surfaces", IsBrush = true, Fallback = "#FF202020" },
            new ThemeKeyInfo { Key = "ControlFillColorDefault", Label = "Control fill", Group = "Surfaces", IsBrush = false, Fallback = "#11FFFFFF" },
            new ThemeKeyInfo { Key = "ComboBoxPopupAcrylicBackground", Label = "Dropdown background", Group = "Surfaces", IsBrush = false, Fallback = "#F0202020" },
            new ThemeKeyInfo { Key = "NewTextEditorBackground", Label = "Editor background", Group = "Code editor", IsBrush = true, Fallback = "#CC202020" },
            new ThemeKeyInfo { Key = "NewTextEditorForeground", Label = "Editor text", Group = "Code editor", IsBrush = true, Fallback = "#FFE9EAEC" },
            new ThemeKeyInfo { Key = "NewTextEditorLink", Label = "Editor link", Group = "Code editor", IsBrush = true, Fallback = "#FF3897E8" }
        };

        public static ThemeValidationResult Validate(string xaml)
        {
            ThemeValidationResult result = new ThemeValidationResult();

            if (string.IsNullOrWhiteSpace(xaml))
            {
                result.Errors.Add("The theme is empty.");
                return result;
            }
            if (xaml.Length > MaximumXamlCharacters)
            {
                result.Errors.Add("The theme is too large.");
                return result;
            }

            XDocument doc;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumXamlCharacters,
                    MaxCharactersFromEntities = 0
                };
                using StringReader source = new StringReader(xaml);
                using XmlReader reader = XmlReader.Create(source, settings);
                doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (Exception ex)
            {
                result.Errors.Add("This is not valid XML: " + ex.Message);
                return result;
            }

            if (doc.Root == null || doc.Root.Name.LocalName != "ResourceDictionary" || doc.Root.Name.NamespaceName != PresentationNamespace)
            {
                result.Errors.Add("The outer tag must be a ResourceDictionary.");
                return result;
            }

            foreach (XElement element in doc.Root.DescendantsAndSelf())
            {
                string name = element.Name.LocalName;
                if (!AllowedElements.Contains(name) || element.Name.NamespaceName != PresentationNamespace)
                {
                    result.Errors.Add("The tag <" + name + "> is not allowed in a theme. Themes may only contain colours and brushes.");
                    if (result.Errors.Count > 6)
                        return result;
                    continue;
                }
                if (!AllowedAttributes.TryGetValue(name, out HashSet<string>? allowed))
                {
                    result.Errors.Add("The tag <" + name + "> is not allowed in a theme.");
                    return result;
                }
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        if (attribute.Value != PresentationNamespace && attribute.Value != XamlNamespace)
                        {
                            result.Errors.Add("The theme contains an unsupported XML namespace.");
                            return result;
                        }
                        continue;
                    }
                    bool isKey = attribute.Name.LocalName == "Key" && attribute.Name.NamespaceName == XamlNamespace;
                    bool isPresentationAttribute = string.IsNullOrEmpty(attribute.Name.NamespaceName) || attribute.Name.NamespaceName == PresentationNamespace;
                    bool attributeAllowed = isKey ? allowed.Contains("Key") : isPresentationAttribute && allowed.Contains(attribute.Name.LocalName);
                    if (!attributeAllowed || attribute.Value.Contains('{'))
                    {
                        result.Errors.Add("The attribute " + attribute.Name.LocalName + " is not allowed on <" + name + ">.");
                        return result;
                    }
                }
            }

            if (result.Errors.Count > 0)
                return result;

            ResourceDictionary parsed;
            try
            {
                using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
                parsed = XamlReader.Load(stream) as ResourceDictionary
                    ?? throw new InvalidOperationException("The outer tag must be a ResourceDictionary.");
            }
            catch (XamlParseException ex)
            {
                result.ErrorLine = ex.LineNumber;
                result.Errors.Add(ex.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                return result;
            }

            foreach (ThemeKeyInfo info in Schema)
            {
                if (!parsed.Contains(info.Key))
                {
                    result.Warnings.Add(info.Label + " is not set, the built in colour will be used.");
                    continue;
                }
                object value = parsed[info.Key];
                if (info.IsBrush && value is not Brush)
                    result.Errors.Add(info.Label + " (" + info.Key + ") must be a brush, for example a SolidColorBrush.");
                else if (!info.IsBrush && value is not Color)
                    result.Errors.Add(info.Label + " (" + info.Key + ") must be a colour, for example #FF202020.");
            }

            if (result.Ok)
                result.Dictionary = parsed;
            return result;
        }

        public static ResourceDictionary LoadBaseDictionary()
        {
            try
            {
                return new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/UI/Style/Dark.xaml", UriKind.Absolute)
                };
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not load the base theme: " + ex.Message);
                return new ResourceDictionary();
            }
        }

        public static ResourceDictionary Merge(ResourceDictionary? user)
        {
            ResourceDictionary merged = LoadBaseDictionary();
            if (user == null)
                return merged;

            foreach (object key in user.Keys)
            {
                try
                {
                    merged[key] = user[key];
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine(LOG_IDENT, "Skipped theme key " + key + ": " + ex.Message);
                }
            }

            foreach (ThemeKeyInfo info in Schema)
            {
                if (merged.Contains(info.Key))
                    continue;
                try
                {
                    object parsed = ColorConverter.ConvertFromString(info.Fallback);
                    if (parsed is Color color)
                        merged[info.Key] = info.IsBrush ? new SolidColorBrush(color) : color;
                }
                catch
                {
                }
            }
            return merged;
        }

        public static ResourceDictionary LoadForApp()
        {
            string path = Paths.CustomThemeXaml;
            try
            {
                if (File.Exists(path))
                {
                    ThemeValidationResult result = Validate(ReadFile(path));
                    if (result.Ok)
                        return Merge(result.Dictionary);
                    App.Logger?.WriteLine(LOG_IDENT, "Custom theme rejected, using the built in theme instead: " + string.Join(" ", result.Errors.Take(2)));
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not read the custom theme: " + ex.Message);
            }
            return Merge(null);
        }

        public static string ReadFile(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists)
                throw new FileNotFoundException("The theme file was not found", path);
            if (file.Length <= 0 || file.Length > MaximumXamlFileBytes)
                throw new InvalidDataException("The theme file size is invalid");
            return File.ReadAllText(path);
        }

        public static void WriteFile(string path, string xaml)
        {
            if (xaml.Length > MaximumXamlCharacters)
                throw new InvalidDataException("The theme is too large");
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("The theme file has no parent directory");
            Directory.CreateDirectory(directory);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(xaml);
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(temporary, fullPath, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }

        public static string BuildXaml(IEnumerable<KeyValuePair<string, Color>> values)
        {
            Dictionary<string, Color> map = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Color> pair in values)
                map[pair.Key] = pair.Value;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
            sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
            foreach (ThemeKeyInfo info in Schema)
            {
                string hex = map.TryGetValue(info.Key, out Color c) ? ToHex(c) : info.Fallback;
                if (info.IsBrush)
                    sb.AppendLine("  <SolidColorBrush x:Key=\"" + info.Key + "\" Color=\"" + hex + "\" />");
                else
                    sb.AppendLine("  <Color x:Key=\"" + info.Key + "\">" + hex + "</Color>");
            }
            sb.Append("</ResourceDictionary>");
            return sb.ToString();
        }

        public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        public static bool TryParseColor(string? text, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            try
            {
                object parsed = ColorConverter.ConvertFromString(text.Trim());
                if (parsed is Color c)
                {
                    color = c;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
