using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Voidstrap.UI.Converters;

public class TagColorConverter : IValueConverter
{
    private sealed record TagBrushes(SolidColorBrush Background, SolidColorBrush Border, SolidColorBrush Foreground);

    private static SolidColorBrush Frozen(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static byte Mix(byte channel, double amount) => (byte)Math.Round(channel + (255 - channel) * amount);

    private static TagBrushes Build(Color color) =>
        new TagBrushes(
            Frozen(Color.FromArgb(0x38, color.R, color.G, color.B)),
            Frozen(Color.FromArgb(0x70, color.R, color.G, color.B)),
            Frozen(Color.FromRgb(Mix(color.R, 0.45), Mix(color.G, 0.45), Mix(color.B, 0.45)))
        );

    private static readonly Dictionary<string, TagBrushes> Palette = new Dictionary<string, TagBrushes>(StringComparer.Ordinal)
    {
        ["Performance"] = Build(Color.FromRgb(52, 152, 219)),
        ["LOD"] = Build(Color.FromRgb(88, 120, 230)),
        ["Fix"] = Build(Color.FromRgb(231, 76, 60)),
        ["Graphics"] = Build(Color.FromRgb(26, 188, 156)),
        ["Experimental"] = Build(Color.FromRgb(241, 196, 15)),
        ["UI"] = Build(Color.FromRgb(155, 89, 182)),
        ["Unknown"] = Build(Color.FromRgb(149, 165, 166))
    };

    private static readonly TagBrushes Overflow = Build(Color.FromRgb(160, 160, 170));

    private static readonly TagBrushes Fallback = Build(Colors.Gray);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        TagBrushes brushes = Fallback;
        if (value is string tag && tag.Length > 0)
        {
            if (Palette.TryGetValue(tag, out TagBrushes? found))
                brushes = found;
            else if (tag[0] == '+')
                brushes = Overflow;
        }
        return (parameter as string) switch
        {
            "Border" => brushes.Border,
            "Foreground" => brushes.Foreground,
            _ => brushes.Background,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
