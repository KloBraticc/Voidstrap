using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Converters;

public sealed class HasFilledSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SymbolRegular symbol && symbol != SymbolRegular.Empty && Enum.IsDefined(typeof(SymbolFilled), symbol.ToString());
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
