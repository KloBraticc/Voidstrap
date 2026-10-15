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
			if (Voidstrap.Utility.Platform.UsesPortableUi)
				return Voidstrap.Utility.SafeImaging.FromUri(new Uri(source, UriKind.Absolute));
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
		string? identifier = LanguageIdentifier.From(value);
		return identifier == null ? null : LanguageMetadata.GetFlagSource(identifier);
	}
}

internal static class LanguageIdentifier
{
	public static string? From(object value)
	{
		return value switch
		{
			Voidstrap.UI.ViewModels.LanguageOption option => option.MetadataId,
			string name when !string.IsNullOrEmpty(name) => Locale.GetIdentifierFromName(name),
			_ => null
		};
	}
}

public class LanguageFlagVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		string? identifier = LanguageIdentifier.From(value);
		return identifier == null || LanguageMetadata.GetFlagSource(identifier) == null ? Visibility.Collapsed : Visibility.Visible;
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
		string? identifier = LanguageIdentifier.From(value);
		return identifier == null ? string.Empty : LanguageMetadata.GetStatusText(identifier);
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
		string? identifier = LanguageIdentifier.From(value);
		return identifier == null || string.IsNullOrEmpty(LanguageMetadata.GetStatusText(identifier))
			? Visibility.Collapsed
			: Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
