using System;
using System.Globalization;
using System.Windows.Data;

namespace Voidstrap.UI.Converters;

public class StringFormatConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string text = value as string;
		string text2 = parameter as string;
		if (text == null)
		{
			return "";
		}
		if (text2 == null)
		{
			return text;
		}
		string[] array = text2.Split(new char[1] { '|' });
		object[] array2 = array;
		try
		{
			return string.Format(culture, text, array2);
		}
		catch (FormatException)
		{
			return text;
		}
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
