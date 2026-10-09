using System;
using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace Wpf.Ui.Converters;

public sealed class ShortcutTextConverter : IValueConverter
{
    public static ShortcutTextConverter Instance { get; } = new();

    public static string Format(string? text)
    {
        if (string.IsNullOrEmpty(text) || !OperatingSystem.IsMacOS())
            return text ?? string.Empty;

        StringBuilder result = new();
        foreach (string part in text.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            result.Append(part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => "⌘",
                "SHIFT" => "⇧",
                "ALT" => "⌥",
                "WIN" or "WINDOWS" => "⌃",
                "ENTER" => "Return",
                "DEL" or "DELETE" => "⌦",
                "BACKSPACE" => "⌫",
                _ => part
            });
        }
        return result.ToString();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Format(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
