using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Voidstrap.UI.Converters;

// Black text on light colours and white text on dark ones, so icons on the accent colour stay readable
// whether the accent is near white or deep
public sealed class ContrastForegroundConverter : IValueConverter
{
	private static readonly SolidColorBrush Dark = Frozen(Color.FromArgb(0xE6, 0, 0, 0));
	private static readonly SolidColorBrush Light = Frozen(Colors.White);

	public static Brush For(object? background)
	{
		Color? color = background switch
		{
			SolidColorBrush brush => brush.Color,
			Color plain => plain,
			_ => null
		};
		if (color is not Color c)
			return Light;
		// Perceived brightness, green counts most and blue least
		double brightness = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
		return brightness > 0.6 ? Dark : Light;
	}

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => For(value);

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

	private static SolidColorBrush Frozen(Color color)
	{
		SolidColorBrush brush = new(color);
		brush.Freeze();
		return brush;
	}
}
