using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Voidstrap.UI.Converters;

public class BooleanToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is bool)
		{
			return (!(bool)value) ? Visibility.Collapsed : Visibility.Visible;
		}
		return Visibility.Collapsed;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return value is Visibility visibility && visibility == Visibility.Visible;
	}
}
