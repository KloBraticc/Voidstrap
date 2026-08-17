using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Voidstrap.UI.Converters;

public class TagColorConverter : IValueConverter
{
	private static Color Darken(Color color, double factor = 0.7)
	{
		return Color.FromRgb((byte)((double)(int)color.R * factor), (byte)((double)(int)color.G * factor), (byte)((double)(int)color.B * factor));
	}

	private static SolidColorBrush Frozen(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static readonly Dictionary<string, SolidColorBrush> Palette = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal)
	{
		["Performance"] = Frozen(Color.FromRgb(52, 152, 219)),
		["LOD"] = Frozen(Darken(Color.FromRgb(41, 122, 175))),
		["Fix"] = Frozen(Darken(Color.FromRgb(231, 76, 60))),
		["Graphics"] = Frozen(Darken(Color.FromRgb(26, 188, 156))),
		["Experimental"] = Frozen(Darken(Color.FromRgb(241, 196, 15))),
		["UI"] = Frozen(Darken(Color.FromRgb(155, 89, 182))),
		["Unknown"] = Frozen(Darken(Color.FromRgb(149, 165, 166)))
	};

	private static readonly SolidColorBrush OverflowBrush = Frozen(Darken(Color.FromRgb(127, 140, 141)));

	private static readonly SolidColorBrush FallbackBrush = Frozen(Darken(Colors.Gray));

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not string tag || tag.Length == 0)
			return FallbackBrush;
		if (Palette.TryGetValue(tag, out SolidColorBrush? brush))
			return brush;
		return tag[0] == '+' ? OverflowBrush : FallbackBrush;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
