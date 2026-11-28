using System.Windows;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Linq;

namespace Voidstrap.UI.Elements.Editor;

public partial class KeystrokeThemeEditorWindow
{
    private static readonly string[] ThemeElementNames =
        ["Key", "KeyStyle", "CpsText", "MouseCircle", "StackPanel", "Grid", "Border", "TextBlock", "Image", "Ellipse", "Rectangle", "Line"];

    private static readonly string[] CommonThemeAttributes =
        ["Name", "Width", "Height", "Margin", "Opacity", "Visibility", "HorizontalAlignment", "VerticalAlignment", "Grid.Row", "Grid.Column"];

    private XDocument? _visualDocument;
    private string? _visualSource;

    private sealed record ThemeElementChoice(XElement Element)
    {
        public override string ToString()
        {
            string indent = new(' ', Math.Min(20, Element.Ancestors().Count()) * 2);
            string? label = (string?)Element.Attribute("Input") ?? (string?)Element.Attribute("Name");
            return label == null ? indent + Element.Name.LocalName : $"{indent}{Element.Name.LocalName} ({label})";
        }
    }

    private XElement? SelectedThemeElement => (ThemeElementPicker.SelectedItem as ThemeElementChoice)?.Element;

    private void OnEditingTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, EditingTabs) && VisualThemeTab?.IsSelected == true && CodeEditor != null)
            RefreshThemeElements();
    }

    private void RefreshThemeElements()
    {
        string source = CodeEditor.Text;
        if (string.Equals(source, _visualSource, StringComparison.Ordinal))
            return;

        _visualSource = source;
        try
        {
            using StringReader input = new(source);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            _visualDocument = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            _visualDocument = null;
        }
        NewThemeElement.ItemsSource = ThemeElementNames;
        if (string.IsNullOrEmpty(NewThemeElement.Text))
            NewThemeElement.SelectedIndex = 0;
        ShowThemeElements();
    }

    private void ShowThemeElements(XElement? selected = null)
    {
        ThemeElementChoice[] elements = _visualDocument?.Root?.DescendantsAndSelf().Select(node => new ThemeElementChoice(node)).ToArray() ?? [];
        ThemeElementPicker.ItemsSource = elements;
        ThemeElementPicker.SelectedItem = elements.FirstOrDefault(item => ReferenceEquals(item.Element, selected)) ?? elements.FirstOrDefault();
    }

    private void OnThemeElementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeAttributePicker == null || ThemeAttributeValue == null)
            return;

        XElement? element = SelectedThemeElement;
        string[] specific = element?.Name.LocalName switch
        {
            "Key" or "KeyStyle" => ["Input", "Text", "HideText", "Background", "PressedBackground", "Foreground", "PressedForeground",
                "BorderBrush", "BorderThickness", "CornerRadius", "FontFamily", "FontWeight", "FontSize", "ShowCps", "CpsFormat",
                "CpsFontSize", "CpsForeground", "PressedCpsForeground", "PressedScale", "Image", "PressedImage", "ImageStretch", "ImageOpacity"],
            "MouseCircle" => ["Size", "DotSize", "DotFill", "Background", "BorderBrush", "BorderThickness"],
            "CpsText" => ["Input", "Format", "Foreground", "FontFamily", "FontWeight", "FontSize"],
            _ => ["Background", "Foreground", "BorderBrush", "BorderThickness", "CornerRadius", "Padding", "Orientation",
                "Text", "FontFamily", "FontWeight", "FontSize", "Source", "Stretch"],
        };
        ThemeAttributePicker.ItemsSource = element == null ? [] : element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => attribute.Name.LocalName).Concat(specific).Concat(CommonThemeAttributes).Distinct().ToArray();
        ThemeAttributePicker.SelectedIndex = -1;
        ThemeAttributePicker.Text = string.Empty;
        ThemeAttributeValue.Text = string.Empty;
    }

    private void OnThemeAttributeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeAttributePicker.SelectedItem is string name && ThemeAttributeValue != null)
            ThemeAttributeValue.Text = (string?)SelectedThemeElement?.Attribute(name) ?? string.Empty;
    }

    private void OnApplyThemeAttribute(object sender, RoutedEventArgs e) => EditThemeAttribute(ThemeAttributeValue.Text);

    private void OnDeleteThemeAttribute(object sender, RoutedEventArgs e) => EditThemeAttribute(null);

    private void EditThemeAttribute(string? value)
    {
        string name = ThemeAttributePicker.Text.Trim();
        if (!CanEditVisualTheme() || SelectedThemeElement is not { } element || !IsThemeName(name) || name == "xmlns")
            return;

        element.SetAttributeValue(name, value);
        ApplyVisualEdit(element);
    }

    private void OnAddThemeElement(object sender, RoutedEventArgs e)
    {
        string name = NewThemeElement.Text.Trim();
        if (!CanEditVisualTheme() || SelectedThemeElement is not { } selected || _visualDocument?.Root is not { } root || !IsThemeName(name))
            return;

        XElement element = new(name);
        if (name == "KeyStyle")
        {
            if (root.Element(name) is { } existing)
            {
                ShowThemeElements(existing);
                return;
            }
            root.AddFirst(element);
        }
        else
        {
            if (name == "Key")
                element.SetAttributeValue("Input", "W");
            else if (name == "CpsText")
                element.SetAttributeValue("Input", "LMB");

            if (selected == root && !name.Contains('.'))
                (root.Element("StackPanel") ?? root).Add(element);
            else if (selected == root || selected.Name.LocalName is "Grid" or "StackPanel" || name.StartsWith(selected.Name.LocalName + ".", StringComparison.Ordinal))
                selected.Add(element);
            else
                selected.AddAfterSelf(element);
        }
        ApplyVisualEdit(element);
    }

    private void OnDeleteThemeElement(object sender, RoutedEventArgs e)
    {
        if (CanEditVisualTheme() && SelectedThemeElement is { Parent: { } parent } element)
        {
            element.Remove();
            ApplyVisualEdit(parent);
        }
    }

    private void OnMoveThemeElementUp(object sender, RoutedEventArgs e) => MoveThemeElement(up: true);

    private void OnMoveThemeElementDown(object sender, RoutedEventArgs e) => MoveThemeElement(up: false);

    private void MoveThemeElement(bool up)
    {
        if (!CanEditVisualTheme() || SelectedThemeElement is not { Parent: not null } element)
            return;
        XElement? sibling = up ? element.ElementsBeforeSelf().LastOrDefault() : element.ElementsAfterSelf().FirstOrDefault();
        if (sibling == null)
            return;

        element.Remove();
        if (up)
            sibling.AddBeforeSelf(element);
        else
            sibling.AddAfterSelf(element);
        ApplyVisualEdit(element);
    }

    private bool CanEditVisualTheme()
    {
        if (string.Equals(_visualSource, CodeEditor.Text, StringComparison.Ordinal))
            return _visualDocument != null;

        RefreshThemeElements();
        return false;
    }

    private void ApplyVisualEdit(XElement selected)
    {
        string source = _visualDocument!.ToString(SaveOptions.DisableFormatting);
        _visualSource = source;
        CodeEditor.Document.Replace(0, CodeEditor.Document.TextLength, source);
        ShowThemeElements(selected);
    }

    private static bool IsThemeName(string name)
    {
        if (name.Length == 0)
            return false;
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
