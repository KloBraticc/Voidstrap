using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using Voidstrap.Integrations.Keystrokes;

namespace Voidstrap.UI.Elements.Bootstrapper
{
    public partial class CustomDialog
    {
        internal const string KeystrokeRootName = "VoidstrapKeystrokes";

        private const int MaxKeystrokeElements = 400;

        private const string KeyStyleName = "KeyStyle";

        private static readonly HashSet<string> KeystrokeElementNames = new(StringComparer.Ordinal) { KeyStyleName, "Key", "CpsText", "MouseCircle" };

        private static readonly HashSet<string> DialogOnlyElementNames = new(StringComparer.Ordinal)
        {
            "BloxstrapCustomBootstrapper",
            "VoidstrapCustomBootstrapper",
            "FishstrapCustomBootstrapper",
            "TitleBar",
            "Button",
            "ProgressBar",
            "ProgressRing",
            "MarkdownTextBlock",
            "WebPanel",
        };

        private static readonly string[] KeyOwnAttributes = { "Name", "Input", "Text" };

        private static readonly string[] KeyTextAttributes = { "FontSize", "FontWeight", "FontStyle", "FontFamily", "TextDecorations" };

        internal static bool IsDialogOnlyElement(string name) => DialogOnlyElementNames.Contains(name);

        internal static UIElement BuildKeystrokeOverlay(IKeystrokeThemeHost host, string contents)
        {
            XElement xml;
            try
            {
                using MemoryStream stream = new(Encoding.UTF8.GetBytes(contents));
                xml = XElement.Load(stream, LoadOptions.SetLineInfo);
                StripNamespaces(xml);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new CustomThemeException(ex, "CustomTheme.Errors.XMLParseFailed", $"line {ex.LineNumber}: {ex.Message}");
            }

            if (xml.Name.LocalName != KeystrokeRootName)
                throw new CustomThemeException("CustomTheme.Errors.InvalidRoot", KeystrokeRootName);
            AssertThemeVersion(xml.Attribute("Version")?.Value, KeystrokeRootName);
            int count = xml.Descendants().Count();
            if (count > MaxKeystrokeElements)
                throw new CustomThemeException("CustomTheme.Errors.TooManyElements", MaxKeystrokeElements, count);

            host.KeyStyle = xml.Elements(KeyStyleName).FirstOrDefault();
            if (xml.Attribute("HorizontalAlignment") == null)
                xml.SetAttributeValue("HorizontalAlignment", "Center");
            if (xml.Attribute("VerticalAlignment") == null)
                xml.SetAttributeValue("VerticalAlignment", "Center");

            Grid root = new();
            HandleXmlElement_FrameworkElement(host, root, xml);
            ApplyBrush_UIElement(host, root, "Background", Panel.BackgroundProperty, xml);
            foreach (XElement child in xml.Elements())
            {
                if (child.Name.LocalName.StartsWith(KeystrokeRootName + ".", StringComparison.Ordinal))
                    continue;
                UIElement element = HandleXml<UIElement>(host, child);
                if (element is not DummyFrameworkElement)
                    root.Children.Add(element);
            }
            CollapseEmptyPanels(root);
            return root;
        }

        private static bool CollapseEmptyPanels(Panel panel)
        {
            bool anyVisible = false;
            foreach (UIElement child in panel.Children)
            {
                if (child is Panel nested && nested.Children.Count > 0 && !CollapseEmptyPanels(nested))
                    continue;
                if (child.Visibility != Visibility.Collapsed)
                    anyVisible = true;
            }
            if (!anyVisible && panel.Children.Count > 0)
                panel.Visibility = Visibility.Collapsed;
            return anyVisible;
        }

        private static IKeystrokeThemeHost RequireKeystrokes(ICustomThemeHost host, XElement xmlElement) =>
            host as IKeystrokeThemeHost ?? throw new CustomThemeException("CustomTheme.Errors.UnknownElement", xmlElement.Name);

        private static XElement MergeKeyStyle(IKeystrokeThemeHost host, XElement xmlElement)
        {
            XElement merged = new(xmlElement);
            if (host.KeyStyle is not XElement style)
                return merged;
            string prefix = xmlElement.Name.LocalName + ".";
            foreach (XAttribute attribute in style.Attributes())
            {
                string name = attribute.Name.LocalName;
                if (KeyOwnAttributes.Contains(name) || merged.Attribute(name) != null || merged.Element(prefix + name) != null)
                    continue;
                merged.SetAttributeValue(name, attribute.Value);
            }
            foreach (XElement property in style.Elements())
            {
                string local = property.Name.LocalName;
                if (!local.StartsWith(KeyStyleName + ".", StringComparison.Ordinal))
                    continue;
                string name = local[(KeyStyleName.Length + 1)..];
                if (merged.Attribute(name) != null || merged.Element(prefix + name) != null)
                    continue;
                XElement copy = new(property) { Name = prefix + name };
                merged.Add(copy);
            }
            return merged;
        }

        private static Brush ResolveKeystrokeBrush(ICustomThemeHost host, XElement xmlElement, string name, string fallback)
        {
            string? value = xmlElement.Attribute(name)?.Value;
            if (value == null)
            {
                XElement? property = xmlElement.Element($"{xmlElement.Name}.{name}");
                if (property?.FirstNode is XElement first)
                    return HandleXml<Brush>(host, first);
                value = fallback;
            }
            if (string.Equals(value.Trim(), "Accent", StringComparison.OrdinalIgnoreCase))
                return KeystrokeTheme.CreateBrush("Accent", Colors.DodgerBlue);
            if (value.StartsWith('{') && value.EndsWith('}'))
                return Application.Current?.TryFindResource(value[1..^1]) as Brush
                    ?? throw new CustomThemeException(
                        "CustomTheme.Errors.ElementAttributeConversionError",
                        xmlElement.Name,
                        name,
                        value
                    );
            XElement carrier = new(xmlElement.Name, new XAttribute(name, value));
            return (Brush)GetBrushFromXElement(carrier, name)!;
        }

        private static ImageBrush? ResolveKeystrokeImage(ICustomThemeHost host, XElement xmlElement, string name)
        {
            string? value = xmlElement.Attribute(name)?.Value;
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string? path = GetFullPath(host, value);
            if (path == null || !File.Exists(path))
                throw new CustomThemeException("CustomTheme.Errors.ElementAttributeConversionError", xmlElement.Name, name, value);
            ImageBrush brush = new(Voidstrap.Utility.SafeImaging.FromUri(new Uri(path, UriKind.Absolute)))
            {
                Stretch = ParseXmlAttribute<Stretch>(xmlElement, "ImageStretch", Stretch.Uniform),
                Opacity = Math.Clamp(ParseXmlAttribute<double>(xmlElement, "ImageOpacity", 1.0), 0.0, 1.0),
            };
            brush.Freeze();
            return brush;
        }

        private static (double Width, double Height) DefaultKeySize(string input) => input.ToUpperInvariant() switch
        {
            "LMB" or "RMB" => (71, 46),
            "SHIFT" => (51, 34),
            "SPACE" => (91, 34),
            _ => (46, 46),
        };

        private static string DefaultKeyLabel(string input) => input.ToUpperInvariant() switch
        {
            "SHIFT" => "SHIFT",
            "SPACE" => "—",
            _ => input,
        };

        private static DummyFrameworkElement HandleXmlElement_KeyStyle(ICustomThemeHost dialog, XElement xmlElement)
        {
            RequireKeystrokes(dialog, xmlElement);
            return new DummyFrameworkElement { Visibility = Visibility.Collapsed };
        }

        private static Border HandleXmlElement_Key(ICustomThemeHost dialog, XElement xmlElement)
        {
            IKeystrokeThemeHost host = RequireKeystrokes(dialog, xmlElement);
            XElement merged = MergeKeyStyle(host, xmlElement);
            string input = GetXmlAttribute(merged, "Input").Trim();
            (double width, double height) = DefaultKeySize(input);
            if (merged.Attribute("Width") == null)
                merged.SetAttributeValue("Width", width);
            if (merged.Attribute("Height") == null)
                merged.SetAttributeValue("Height", height);
            if (merged.Attribute("HorizontalAlignment") == null)
                merged.SetAttributeValue("HorizontalAlignment", "Center");
            if (merged.Attribute("VerticalAlignment") == null)
                merged.SetAttributeValue("VerticalAlignment", "Center");

            Border key = new() { SnapsToDevicePixels = true, RenderTransformOrigin = new Point(0.5, 0.5) };
            HandleXmlElement_FrameworkElement(host, key, merged);
            key.BorderThickness = GetThicknessFromXElement(merged, "BorderThickness") is Thickness thickness ? thickness : new Thickness(1);
            CornerRadius corners = GetCornerRadiusFromXElement(merged, "CornerRadius") is CornerRadius radius ? radius : new CornerRadius(8);
            key.CornerRadius = corners;
            key.BorderBrush = ResolveKeystrokeBrush(host, merged, "BorderBrush", "#363638");
            key.Background = ResolveKeystrokeImage(host, merged, "Image") ?? ResolveKeystrokeBrush(host, merged, "Background", "#202020");

            double inset = Math.Max(key.BorderThickness.Left, key.BorderThickness.Top);
            Border highlight = new()
            {
                Opacity = 0,
                CornerRadius = new CornerRadius(
                    Math.Max(0, corners.TopLeft - inset),
                    Math.Max(0, corners.TopRight - inset),
                    Math.Max(0, corners.BottomRight - inset),
                    Math.Max(0, corners.BottomLeft - inset)
                ),
                Background = ResolveKeystrokeImage(host, merged, "PressedImage") ?? ResolveKeystrokeBrush(host, merged, "PressedBackground", "Accent"),
            };

            Brush foreground = ResolveKeystrokeBrush(host, merged, "Foreground", "#FFFFFF");
            Brush pressedForeground = ResolveKeystrokeBrush(host, merged, "PressedForeground", "#FFFFFF");
            Brush cpsForeground = ResolveKeystrokeBrush(host, merged, "CpsForeground", "#A0A0A5");
            Brush pressedCpsForeground = ResolveKeystrokeBrush(host, merged, "PressedCpsForeground", "#FFFFFF");

            string text = merged.Attribute("Text")?.Value ?? DefaultKeyLabel(input);
            XElement labelXml = new("TextBlock", new XAttribute("HorizontalAlignment", "Center"));
            foreach (string name in KeyTextAttributes)
            {
                if (merged.Attribute(name) is XAttribute attribute)
                    labelXml.SetAttributeValue(name, attribute.Value);
            }
            if (labelXml.Attribute("FontSize") == null)
                labelXml.SetAttributeValue("FontSize", text.Length <= 1 ? 15 : text.Length <= 3 ? 13 : 11);
            if (labelXml.Attribute("FontWeight") == null)
                labelXml.SetAttributeValue("FontWeight", "SemiBold");
            TextBlock label = new();
            HandleXmlElement_TextBlock_Base(host, label, labelXml);
            label.Text = GetTranslatedText(text);
            label.Foreground = foreground;
            label.Visibility = ParseXmlAttribute<bool>(merged, "HideText", false) ? Visibility.Collapsed : Visibility.Visible;

            StackPanel content = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(label);

            bool mouseButton = input.Equals("LMB", StringComparison.OrdinalIgnoreCase) || input.Equals("RMB", StringComparison.OrdinalIgnoreCase);
            TextBlock? cps = null;
            if (mouseButton && ParseXmlAttribute<bool>(merged, "ShowCps", true) && App.Settings.Prop.KeystrokesShowCps)
            {
                cps = new TextBlock
                {
                    FontSize = ParseXmlAttribute<double>(merged, "CpsFontSize", 10),
                    FontFamily = label.FontFamily,
                    Foreground = cpsForeground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0),
                };
                content.Children.Add(cps);
            }

            Grid layers = new();
            layers.Children.Add(highlight);
            string propertyPrefix = merged.Name.LocalName + ".";
            foreach (XElement child in merged.Elements())
            {
                if (child.Name.LocalName.StartsWith(propertyPrefix, StringComparison.Ordinal))
                    continue;
                layers.Children.Add(HandleXml<FrameworkElement>(host, child));
            }
            layers.Children.Add(content);
            key.Child = layers;

            if (!KeystrokeThemeManager.IsKeyAllowedBySettings(input, App.Settings.Prop))
                key.Visibility = Visibility.Collapsed;

            host.AddKey(
                new KeystrokeKeyVisual
                {
                    Input = input,
                    Key = key,
                    Highlight = highlight,
                    Label = label,
                    Cps = cps,
                    CpsFormat = merged.Attribute("CpsFormat")?.Value ?? "{0} CPS",
                    Foreground = foreground,
                    PressedForeground = pressedForeground,
                    CpsForeground = cpsForeground,
                    PressedCpsForeground = pressedCpsForeground,
                    PressedScale = Math.Clamp(ParseXmlAttribute<double>(merged, "PressedScale", 0.96), 0.5, 1.5),
                }
            );
            return key;
        }

        private static TextBlock HandleXmlElement_CpsText(ICustomThemeHost dialog, XElement xmlElement)
        {
            IKeystrokeThemeHost host = RequireKeystrokes(dialog, xmlElement);
            string input = GetXmlAttribute(xmlElement, "Input", "LMB").Trim();
            TextBlock text = new();
            HandleXmlElement_TextBlock_Base(host, text, xmlElement);
            if (!App.Settings.Prop.KeystrokesShowCps || !KeystrokeThemeManager.IsKeyAllowedBySettings(input, App.Settings.Prop))
                text.Visibility = Visibility.Collapsed;
            host.AddCpsText(input, text, xmlElement.Attribute("Format")?.Value ?? "{0} CPS");
            return text;
        }

        private static Border HandleXmlElement_MouseCircle(ICustomThemeHost dialog, XElement xmlElement)
        {
            IKeystrokeThemeHost host = RequireKeystrokes(dialog, xmlElement);
            double size = Math.Clamp(ParseXmlAttribute<double>(xmlElement, "Size", 88), 24, 400);
            double dotSize = Math.Clamp(ParseXmlAttribute<double>(xmlElement, "DotSize", Math.Max(12, Math.Round(size * 0.27))), 4, size);
            if (xmlElement.Attribute("HorizontalAlignment") == null)
                xmlElement.SetAttributeValue("HorizontalAlignment", "Center");
            if (xmlElement.Attribute("VerticalAlignment") == null)
                xmlElement.SetAttributeValue("VerticalAlignment", "Center");
            Border circle = new();
            HandleXmlElement_FrameworkElement(host, circle, xmlElement);
            circle.Width = size;
            circle.Height = size;
            circle.CornerRadius = new CornerRadius(size / 2.0);
            circle.BorderThickness = GetThicknessFromXElement(xmlElement, "BorderThickness") is Thickness thickness ? thickness : new Thickness(1.25);
            circle.BorderBrush = ResolveKeystrokeBrush(host, xmlElement, "BorderBrush", "#363638");
            circle.Background = ResolveKeystrokeBrush(host, xmlElement, "Background", "#202020");
            Ellipse dot = new()
            {
                Width = dotSize,
                Height = dotSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Fill = ResolveKeystrokeBrush(host, xmlElement, "DotFill", "Accent"),
            };
            circle.Child = dot;
            host.AddMouseCircle(dot, Math.Max(0, (size - dotSize) / 2.0 - circle.BorderThickness.Left));
            return circle;
        }
    }
}
