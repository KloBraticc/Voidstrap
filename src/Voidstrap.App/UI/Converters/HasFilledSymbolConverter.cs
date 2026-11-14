using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Converters;

public sealed class HasFilledSymbolConverter : IValueConverter
{
    private static readonly Lazy<IDictionary<int, ushort>?> FilledGlyphs = new(LoadFilledGlyphs);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SymbolRegular symbol || symbol == SymbolRegular.Empty)
        {
            return false;
        }
        if (!Enum.TryParse(symbol.ToString(), out SymbolFilled filled))
        {
            return false;
        }
        IDictionary<int, ushort>? glyphs = FilledGlyphs.Value;
        return glyphs != null && glyphs.ContainsKey((int)filled);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    private static IDictionary<int, ushort>? LoadFilledGlyphs()
    {
        try
        {
            if (Application.Current?.TryFindResource("FluentSystemIconsFilled") is not System.Windows.Media.FontFamily family)
            {
                return null;
            }
            foreach (Typeface typeface in family.GetTypefaces())
            {
                if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs))
                {
                    return new Dictionary<int, ushort>(glyphs.CharacterToGlyphMap);
                }
            }
        }
        catch { }
        return null;
    }
}
