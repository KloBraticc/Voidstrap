using System;
using System.Globalization;
using System.Windows.Data;

namespace Voidstrap.UI.Converters;

internal class RangeConverter : IValueConverter
{
	public int? From { get; set; }

	public int? To { get; set; }

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not int num)
			return false;
		if (!From.HasValue)
		{
			return num < To;
		}
		if (!To.HasValue)
		{
			return num > From;
		}
		return num > From && num < To;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
