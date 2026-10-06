using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Voidstrap.Enums;
using Voidstrap.Extensions;

namespace Voidstrap.UI;

internal static class ThemeSwatch
{
	private const double Size = 18;

	private const double Radius = 4.5;

	private static readonly Dictionary<Theme, ImageSource> Cache = new Dictionary<Theme, ImageSource>();

	public static ImageSource? Get(Theme theme)
	{
		Theme resolved = theme == Theme.Default ? SafeFinal(theme) : theme;
		if (Cache.TryGetValue(resolved, out ImageSource? cached))
			return cached;
		ImageSource? swatch = Build(resolved);
		if (swatch != null)
			Cache[resolved] = swatch;
		return swatch;
	}

	public static void Refresh()
	{
		Cache.Remove(Theme.Custom);
		ThemeSwatchSource.Instance.Bump();
	}

	private static Theme SafeFinal(Theme theme)
	{
		try
		{
			Theme final = theme.GetFinal();
			return final == Theme.Default ? Theme.Dark : final;
		}
		catch (Exception)
		{
			return Theme.Dark;
		}
	}

	private static ImageSource? Build(Theme theme)
	{
		try
		{
			ResourceDictionary? dictionary = Load(theme);
			Color primary = ReadColor(dictionary, "WindowBackgroundColorPrimary") ?? Color.FromRgb(0x20, 0x20, 0x20);
			Color secondary = ReadColor(dictionary, "WindowBackgroundColorSecondary") ?? primary;
			Color third = ReadColor(dictionary, "WindowBackgroundColorThird") ?? secondary;
			primary = Opaque(primary);
			secondary = Opaque(secondary);
			third = Opaque(third);
			bool light = Luminance(primary) > 0.55;
			Brush fill;
			if (dictionary != null && dictionary.Contains(Voidstrap.Utility.CustomTheme.WindowGradientKey)
				&& Voidstrap.Utility.CustomTheme.ReadModel(dictionary).Gradient is { } custom)
			{
				foreach (Voidstrap.Utility.ThemeGradientStop stop in custom.Stops)
					stop.Color = Opaque(stop.Color);
				fill = custom.ToBrush();
			}
			else if (NearlyEqual(primary, secondary) && NearlyEqual(primary, third))
			{
				fill = new SolidColorBrush(primary);
			}
			else
			{
				fill = new LinearGradientBrush
				{
					StartPoint = new Point(0, 0),
					EndPoint = new Point(1, 1),
					GradientStops =
					{
						new GradientStop(primary, 0.0),
						new GradientStop(secondary, 0.42),
						new GradientStop(third, 0.78),
						new GradientStop(primary, 1.0)
					}
				};
			}
			fill.Freeze();
			Pen border = new Pen(new SolidColorBrush(light ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
			border.Freeze();

			DrawingGroup drawing = new DrawingGroup();
			using (DrawingContext context = drawing.Open())
				context.DrawRoundedRectangle(fill, border, new Rect(0.5, 0.5, Size - 1, Size - 1), Radius, Radius);
			drawing.Freeze();
			DrawingImage image = new DrawingImage(drawing);
			image.Freeze();
			return image;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ThemeSwatch", "The " + theme + " theme preview could not be drawn: " + ex.Message);
			return null;
		}
	}

	private static ResourceDictionary? Load(Theme theme)
	{
		if (theme == Theme.Custom)
			return Voidstrap.Utility.CustomTheme.LoadForApp();
		string name = Enum.GetName(theme) ?? "Dark";
		try
		{
			return new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Style/" + name + ".xaml", UriKind.Absolute) };
		}
		catch (Exception)
		{
			return new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Style/Dark.xaml", UriKind.Absolute) };
		}
	}

	private static Color? ReadColor(ResourceDictionary? dictionary, string key)
	{
		if (dictionary == null || !dictionary.Contains(key))
			return null;
		return dictionary[key] switch
		{
			Color color => color,
			SolidColorBrush brush => brush.Color,
			_ => null
		};
	}

	private static Color Opaque(Color color)
	{
		return Color.FromRgb(color.R, color.G, color.B);
	}

	private static bool NearlyEqual(Color first, Color second)
	{
		return Math.Abs(first.R - second.R) <= 3 && Math.Abs(first.G - second.G) <= 3 && Math.Abs(first.B - second.B) <= 3;
	}

	private static double Luminance(Color color)
	{
		return (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
	}
}

public sealed class ThemeSwatchSource : INotifyPropertyChanged
{
	public static ThemeSwatchSource Instance { get; } = new ThemeSwatchSource();

	public event PropertyChangedEventHandler? PropertyChanged;

	public int Revision { get; private set; }

	public void Bump()
	{
		Revision++;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
	}
}

public sealed class ThemeSwatchConverter : IMultiValueConverter
{
	public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
	{
		return values.Length > 0 && values[0] is Theme theme ? ThemeSwatch.Get(theme) : null;
	}

	public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
