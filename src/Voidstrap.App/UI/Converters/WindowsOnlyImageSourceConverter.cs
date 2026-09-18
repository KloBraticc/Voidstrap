using System;
using System.Globalization;
using System.Windows.Data;

namespace Voidstrap.UI.Converters;

public class WindowsOnlyImageSourceConverter : IValueConverter
{
	public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Voidstrap.Utility.Platform.IsLinux ? null : value;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
