using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Voidstrap.UI.Elements.Settings.Pages;

public class BoolToVisibilityConverter : IValueConverter
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
		if (value is Visibility visibility)
		{
			return visibility == Visibility.Visible;
		}
		return false;
	}
}
