using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.Controls;

public static partial class InlineCodeText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(InlineCodeText),
        new PropertyMetadata(null, OnSourceChanged)
    );

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    [GeneratedRegex(@"\b(content|ExtraContent|PlatformContent)\b")]
    private static partial Regex CodeTerms();

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        block.Inlines.Clear();
        string text = e.NewValue as string ?? string.Empty;
        int index = 0;
        foreach (Match match in CodeTerms().Matches(text))
        {
            if (match.Index > index)
                block.Inlines.Add(new Run(text[index..match.Index]));
            block.Inlines.Add(CreateChip(match.Value));
            index = match.Index + match.Length;
        }
        if (index < text.Length)
            block.Inlines.Add(new Run(text[index..]));
    }

    private static InlineUIContainer CreateChip(string term)
    {
        TextBlock label = new() { Text = term, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Border chip = new()
        {
            Child = label,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(1, 0, 1, -3),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center };
    }
}
