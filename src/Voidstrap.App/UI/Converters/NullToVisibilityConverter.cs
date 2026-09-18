using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Voidstrap.UI.Converters;

public class NullToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool empty = value is string text ? string.IsNullOrWhiteSpace(text) : value is null;
		return empty ? Visibility.Collapsed : Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
