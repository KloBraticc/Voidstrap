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

        public bool Optional { get; init; }
    }

    public sealed class ThemeGradientStop
    {
        public Color Color { get; set; }

        public double Offset { get; set; }
    }

    public sealed class ThemeGradient
    {
        public const int MaximumStops = 8;

        public bool Radial { get; set; }

        public double Angle { get; set; } = 45;

        public double Radius { get; set; } = 0.75;

        public double CenterX { get; set; } = 0.5;

        public double CenterY { get; set; } = 0.5;

        public List<ThemeGradientStop> Stops { get; } = new List<ThemeGradientStop>();

        public GradientBrush ToBrush()
        {
            GradientStopCollection stops = new GradientStopCollection();
            foreach (ThemeGradientStop stop in Stops.OrderBy(s => s.Offset))
                stops.Add(new GradientStop(stop.Color, Math.Clamp(stop.Offset, 0, 1)));
            if (Radial)
            {
                Point center = new Point(Math.Clamp(CenterX, 0, 1), Math.Clamp(CenterY, 0, 1));
                return new RadialGradientBrush(stops) { Center = center, GradientOrigin = center, RadiusX = Math.Clamp(Radius, 0.05, 2), RadiusY = Math.Clamp(Radius, 0.05, 2) };
            }
            (Point start, Point end) = PointsForAngle(Angle);
            return new LinearGradientBrush(stops, start, end);
        }

        public static (Point Start, Point End) PointsForAngle(double angle)
        {
            double radians = angle * Math.PI / 180.0;
            double dx = Math.Cos(radians) / 2.0;
            double dy = Math.Sin(radians) / 2.0;
            return (new Point(Math.Round(0.5 - dx, 4), Math.Round(0.5 - dy, 4)), new Point(Math.Round(0.5 + dx, 4), Math.Round(0.5 + dy, 4)));
        }

        public static ThemeGradient? FromBrush(object? value)
        {
            if (value is not GradientBrush brush || brush.GradientStops.Count < 2)
                return null;
            ThemeGradient gradient = new ThemeGradient();
            foreach (GradientStop stop in brush.GradientStops.Take(MaximumStops))
                gradient.Stops.Add(new ThemeGradientStop { Color = stop.Color, Offset = Math.Clamp(stop.Offset, 0, 1) });
            if (brush is RadialGradientBrush radial)
            {
                gradient.Radial = true;
                gradient.Radius = Math.Clamp(Math.Max(radial.RadiusX, radial.RadiusY), 0.05, 2);
                gradient.CenterX = Math.Clamp(radial.Center.X, 0, 1);
                gradient.CenterY = Math.Clamp(radial.Center.Y, 0, 1);
            }
            else if (brush is LinearGradientBrush linear)
            {
                double angle = Math.Atan2(linear.EndPoint.Y - linear.StartPoint.Y, linear.EndPoint.X - linear.StartPoint.X) * 180.0 / Math.PI;
                gradient.Angle = Math.Round((angle + 360) % 360);
            }
            return gradient;
        }

        public ThemeGradient Clone()
        {
            ThemeGradient copy = new ThemeGradient { Radial = Radial, Angle = Angle, Radius = Radius, CenterX = CenterX, CenterY = CenterY };
            foreach (ThemeGradientStop stop in Stops)
                copy.Stops.Add(new ThemeGradientStop { Color = stop.Color, Offset = stop.Offset });
            return copy;
        }
    }

    public sealed class ThemeModel
    {
        public Dictionary<string, Color> Colors { get; } = new Dictionary<string, Color>(StringComparer.Ordinal);

        public ThemeGradient? Gradient { get; set; }
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

        public const string WindowGradientKey = "VoidstrapWindowGradient";

        private const int MaximumProfileNameLength = 40;

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

        private static readonly Dictionary<string, HashSet<string>> AllowedAttributes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
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
            new ThemeKeyInfo { Key = "NewTextEditorLink", Label = "Editor link", Group = "Code editor", IsBrush = true, Fallback = "#FF3897E8" },
            new ThemeKeyInfo { Key = "TextFillColorPrimaryBrush", Label = "Primary text", Group = "Text", IsBrush = true, Fallback = "#FFFFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "TextFillColorSecondaryBrush", Label = "Secondary text", Group = "Text", IsBrush = true, Fallback = "#C5FFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "TextFillColorTertiaryBrush", Label = "Hint text", Group = "Text", IsBrush = true, Fallback = "#87FFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "ControlFillColorDefaultBrush", Label = "Control background", Group = "Controls", IsBrush = true, Fallback = "#0FFFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "ControlFillColorSecondaryBrush", Label = "Control hover", Group = "Controls", IsBrush = true, Fallback = "#15FFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "SubtleFillColorSecondaryBrush", Label = "Subtle hover", Group = "Controls", IsBrush = true, Fallback = "#0FFFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "ControlStrokeColorDefaultBrush", Label = "Control outline", Group = "Controls", IsBrush = true, Fallback = "#12FFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "CardBackgroundFillColorDefaultBrush", Label = "Card background", Group = "Cards and borders", IsBrush = true, Fallback = "#0DFFFFFF", Optional = true },
            new ThemeKeyInfo { Key = "CardStrokeColorDefaultBrush", Label = "Card outline", Group = "Cards and borders", IsBrush = true, Fallback = "#19000000", Optional = true },
            new ThemeKeyInfo { Key = "ControlElevationBorderBrush", Label = "Raised border", Group = "Cards and borders", IsBrush = true, Fallback = "#18FFFFFF", Optional = true }
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

            if (parsed.Contains(WindowGradientKey))
            {
                if (parsed[WindowGradientKey] is not GradientBrush gradientBrush)
                    result.Errors.Add("The window gradient (" + WindowGradientKey + ") must be a LinearGradientBrush or RadialGradientBrush.");
                else if (gradientBrush.GradientStops.Count < 2 || gradientBrush.GradientStops.Count > 16)
                    result.Errors.Add("The window gradient needs between 2 and 16 colour stops.");
            }

            foreach (ThemeKeyInfo info in Schema)
            {
                if (!parsed.Contains(info.Key))
                {
                    if (!info.Optional)
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
                if (info.Optional || merged.Contains(info.Key))
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
            ThemeModel model = new ThemeModel();
            foreach (KeyValuePair<string, Color> pair in values)
                model.Colors[pair.Key] = pair.Value;
            return BuildXaml(model);
        }

        public static string BuildXaml(ThemeModel model)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
            sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
            foreach (ThemeKeyInfo info in Schema)
            {
                bool present = model.Colors.TryGetValue(info.Key, out Color c);
                if (!present && info.Optional)
                    continue;
                string hex = present ? ToHex(c) : info.Fallback;
                if (info.IsBrush)
                    sb.AppendLine("  <SolidColorBrush x:Key=\"" + info.Key + "\" Color=\"" + hex + "\" />");
                else
                    sb.AppendLine("  <Color x:Key=\"" + info.Key + "\">" + hex + "</Color>");
            }
            if (model.Gradient is { Stops.Count: >= 2 } gradient)
            {
                string stops = string.Concat(gradient.Stops
                    .OrderBy(stop => stop.Offset)
                    .Take(ThemeGradient.MaximumStops)
                    .Select(stop => "    <GradientStop Color=\"" + ToHex(stop.Color) + "\" Offset=\"" + Math.Clamp(stop.Offset, 0, 1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\" />" + Environment.NewLine));
                System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
                if (gradient.Radial)
                {
                    string center = Math.Clamp(gradient.CenterX, 0, 1).ToString("0.###", invariant) + "," + Math.Clamp(gradient.CenterY, 0, 1).ToString("0.###", invariant);
                    string radius = Math.Clamp(gradient.Radius, 0.05, 2).ToString("0.###", invariant);
                    sb.AppendLine("  <RadialGradientBrush x:Key=\"" + WindowGradientKey + "\" Center=\"" + center + "\" GradientOrigin=\"" + center + "\" RadiusX=\"" + radius + "\" RadiusY=\"" + radius + "\">");
                    sb.Append(stops);
                    sb.AppendLine("  </RadialGradientBrush>");
                }
                else
                {
                    (Point start, Point end) = ThemeGradient.PointsForAngle(gradient.Angle);
                    sb.AppendLine("  <LinearGradientBrush x:Key=\"" + WindowGradientKey + "\" StartPoint=\"" + start.X.ToString("0.####", invariant) + "," + start.Y.ToString("0.####", invariant) + "\" EndPoint=\"" + end.X.ToString("0.####", invariant) + "," + end.Y.ToString("0.####", invariant) + "\">");
                    sb.Append(stops);
                    sb.AppendLine("  </LinearGradientBrush>");
                }
            }
            sb.Append("</ResourceDictionary>");
            return sb.ToString();
        }

        public static ThemeModel ReadModel(ResourceDictionary? dictionary)
        {
            ThemeModel model = new ThemeModel();
            if (dictionary == null)
                return model;
            foreach (ThemeKeyInfo info in Schema)
            {
                if (!dictionary.Contains(info.Key))
                    continue;
                object value = dictionary[info.Key];
                if (value is Color color)
                    model.Colors[info.Key] = color;
                else if (value is SolidColorBrush brush)
                    model.Colors[info.Key] = brush.Color;
            }
            if (dictionary.Contains(WindowGradientKey))
                model.Gradient = ThemeGradient.FromBrush(dictionary[WindowGradientKey]);
            return model;
        }

        public static string? BuiltInXaml(string themeName)
        {
            try
            {
                ResourceDictionary dictionary = new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Style/" + themeName + ".xaml", UriKind.Absolute) };
                return BuildXaml(ReadModel(dictionary));
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not read the " + themeName + " theme: " + ex.Message);
                return null;
            }
        }

        public static string ProfilesDirectory => Path.Combine(Paths.Themes, "Profiles");

        public static bool IsValidProfileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            if (trimmed.Length > MaximumProfileNameLength || trimmed.StartsWith('.') || trimmed.EndsWith('.'))
                return false;
            foreach (char ch in trimmed)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == ' ' || ch == '_' || ch == '-' || ch == '\'' || ch == '(' || ch == ')'))
                    return false;
            }
            string upper = trimmed.ToUpperInvariant();
            return upper is not ("CON" or "PRN" or "AUX" or "NUL") && !(upper.Length == 4 && (upper.StartsWith("COM") || upper.StartsWith("LPT")) && char.IsDigit(upper[3]));
        }

        public static string ProfilePath(string name)
        {
            if (!IsValidProfileName(name))
                throw new ArgumentException("That profile name is not allowed", nameof(name));
            return Path.Combine(ProfilesDirectory, name.Trim() + ".xaml");
        }

        public static IReadOnlyList<string> ListProfiles()
        {
            try
            {
                if (!Directory.Exists(ProfilesDirectory))
                    return Array.Empty<string>();
                return Directory.EnumerateFiles(ProfilesDirectory, "*.xaml")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => IsValidProfileName(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not list theme profiles: " + ex.Message);
                return Array.Empty<string>();
            }
        }

        public static string ReadProfile(string name)
        {
            return ReadFile(ProfilePath(name));
        }

        public static void SaveProfile(string name, string xaml)
        {
            ThemeValidationResult result = Validate(xaml);
            if (!result.Ok)
                throw new InvalidDataException(result.Errors.FirstOrDefault() ?? "That theme is not valid");
            WriteFile(ProfilePath(name), xaml);
        }

        public static void DeleteProfile(string name)
        {
            string path = ProfilePath(name);
            if (File.Exists(path))
                File.Delete(path);
        }

        public static void RenameProfile(string oldName, string newName)
        {
            string source = ProfilePath(oldName);
            string target = ProfilePath(newName);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(target))
                throw new IOException("A profile with that name already exists");
            File.Move(source, target);
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
