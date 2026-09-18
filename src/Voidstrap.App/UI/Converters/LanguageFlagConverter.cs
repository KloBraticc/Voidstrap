using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Voidstrap.UI.Converters;

public class LanguageFlagConverter : IValueConverter
{
	public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string? source = ResolveSource(value);
		if (source == null)
		{
			return null;
		}
		try
		{
			BitmapImage image = new BitmapImage();
			image.BeginInit();
			image.UriSource = new Uri(source, UriKind.Absolute);
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LanguageFlagConverter", "Flag could not be loaded from " + source + ": " + ex.Message);
			return null;
		}
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}

	private static string? ResolveSource(object value)
	{
		if (value is not string name || string.IsNullOrEmpty(name))
		{
			return null;
		}
		return LanguageMetadata.GetFlagSource(Locale.GetIdentifierFromName(name));
	}
}

public class LanguageFlagVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not string name || string.IsNullOrEmpty(name))
		{
			return Visibility.Collapsed;
		}
		return LanguageMetadata.GetFlagSource(Locale.GetIdentifierFromName(name)) == null ? Visibility.Collapsed : Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

public class LanguageStatusConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not string name || string.IsNullOrEmpty(name))
		{
			return string.Empty;
		}
		return LanguageMetadata.GetStatusText(Locale.GetIdentifierFromName(name));
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

public class LanguageStatusVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not string name || string.IsNullOrEmpty(name))
		{
			return Visibility.Collapsed;
		}
		return string.IsNullOrEmpty(LanguageMetadata.GetStatusText(Locale.GetIdentifierFromName(name)))
			? Visibility.Collapsed
			: Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
