using System;
using System.Globalization;
using System.Windows.Data;

namespace Voidstrap.UI.Converters;

public class StatusLineConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = value as string ?? "";
		bool wantRest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
		int split = text.IndexOf('\n');
		if (split < 0)
		{
			return wantRest ? "" : text;
		}
		if (wantRest)
		{
			return text.Substring(split + 1).Replace("\r", "").Trim();
		}
		return text.Substring(0, split).Replace("\r", "").Trim();
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
