using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Voidstrap.UI.Converters;

public class ImageBrushConverter : IValueConverter
{
	private static readonly ImageSourceConverter SourceConverter = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		ImageSource? image = value switch
		{
			ImageSource source => source,
			string { Length: > 0 } text => TryConvert(text),
			Uri uri => TryConvert(uri),
			_ => null
		};
		if (image == null)
			return null;
		ImageBrush brush = new(image) { Stretch = Stretch.UniformToFill };
		RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
		return brush;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}

	private static ImageSource? TryConvert(object value)
	{
		try
		{
			return SourceConverter.ConvertFrom(value) as ImageSource;
		}
		catch (Exception ex) when (ex is NotSupportedException or FormatException or UriFormatException or System.IO.IOException)
		{
			return null;
		}
	}
}
