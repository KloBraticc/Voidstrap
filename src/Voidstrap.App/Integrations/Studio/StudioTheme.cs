using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Studio;

public static class StudioTheme
{
	private static readonly object _lock = new object();

	private static string? _cached;

	private static DateTime _builtUtc;

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetColorizationColor(out uint colorizationColor, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

	public static string GetPaletteJson()
	{
		lock (_lock)
		{
			if (_cached != null && DateTime.UtcNow - _builtUtc < TimeSpan.FromSeconds(30L))
			{
				return _cached;
			}
			_cached = Build();
			_builtUtc = DateTime.UtcNow;
			return _cached;
		}
	}

	private static string Build()
	{
		string text = AccentHex();
		Color color = Composite(ResColor(new string[2] { "ApplicationBackgroundBrush", "ApplicationBackgroundColor" }, Color.FromRgb(27, 29, 34)), Color.FromRgb(27, 29, 34));
		Color color2 = Composite(ResColor(new string[3] { "CardBackgroundBrush", "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorDefault" }, Color.FromRgb(37, 39, 46)), color);
		Color color3 = Composite(ResColor(new string[2] { "ControlFillColorDefaultBrush", "ControlFillColorDefault" }, Color.FromRgb(46, 49, 56)), color2);
		Color c = Composite(ResColor(new string[2] { "TextFillColorPrimaryBrush", "TextFillColorPrimary" }, Color.FromRgb(233, 236, 242)), color3);
		Color c2 = Composite(ResColor(new string[2] { "TextFillColorSecondaryBrush", "TextFillColorSecondary" }, Color.FromRgb(150, 155, 165)), color3);
		return "{\"accent\":\"" + text + "\",\"bg\":\"" + Hex(color) + "\",\"section\":\"" + Hex(color2) + "\",\"row\":\"" + Hex(color3) + "\",\"text\":\"" + Hex(c) + "\",\"sub\":\"" + Hex(c2) + "\"}";
	}

	private static Color Composite(Color fg, Color back)
	{
		if (fg.A == byte.MaxValue)
		{
			return fg;
		}
		double num = (double)(int)fg.A / 255.0;
		byte r = (byte)Math.Round((double)(int)fg.R * num + (double)(int)back.R * (1.0 - num));
		byte g = (byte)Math.Round((double)(int)fg.G * num + (double)(int)back.G * (1.0 - num));
		byte b = (byte)Math.Round((double)(int)fg.B * num + (double)(int)back.B * (1.0 - num));
		return Color.FromRgb(r, g, b);
	}

	private static string Hex(Color c)
	{
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
	}

	private static string AccentHex()
	{
		try
		{
			if (DwmGetColorizationColor(out var colorizationColor, out var _) == 0)
			{
				byte value = (byte)((colorizationColor >> 16) & 0xFF);
				byte value2 = (byte)((colorizationColor >> 8) & 0xFF);
				byte value3 = (byte)(colorizationColor & 0xFF);
				return $"#{value:X2}{value2:X2}{value3:X2}";
			}
		}
		catch
		{
		}
		return "#0078D4";
	}

	private static Color ResColor(string[] keys, Color fallback)
	{
		try
		{
			Application app = Application.Current;
			if (app == null)
			{
				return fallback;
			}
			Color? result = null;
			((DispatcherObject)app).Dispatcher.Invoke((Action)delegate
			{
				string[] array = keys;
				foreach (string resourceKey in array)
				{
					object obj2 = app.TryFindResource(resourceKey);
					if (obj2 is Color value)
					{
						result = value;
						break;
					}
					if (obj2 is SolidColorBrush solidColorBrush)
					{
						result = solidColorBrush.Color;
						break;
					}
				}
			});
			return result ?? fallback;
		}
		catch
		{
			return fallback;
		}
	}
}
