using System;
using System.Diagnostics;
using System.Windows.Media;

namespace Voidstrap.Utility;

internal static class SystemAccent
{
	private static readonly Color Fallback = Color.FromRgb(0x6B, 0x4E, 0xE6);

	private static Color? _cached;

	public static Color GetGlassColor()
	{
		if (Platform.IsWindows)
		{
			try
			{
				return System.Windows.SystemParameters.WindowGlassColor;
			}
			catch
			{
			}
		}
		return Get();
	}

	public static Brush GetGlassBrush()
	{
		if (Platform.IsWindows)
		{
			try
			{
				Brush? glass = System.Windows.SystemParameters.WindowGlassBrush;
				if (glass != null)
				{
					return glass;
				}
			}
			catch
			{
			}
		}
		SolidColorBrush brush = new SolidColorBrush(Get());
		brush.Freeze();
		return brush;
	}

	public static Color Get()
	{
		if (_cached.HasValue)
		{
			return _cached.Value;
		}
		Color result = Fallback;
		try
		{
			if (OperatingSystem.IsMacOS())
			{
				result = GetMacOSAccent() ?? Fallback;
			}
			else if (OperatingSystem.IsLinux())
			{
				result = GetLinuxAccent() ?? Fallback;
			}
		}
		catch
		{
		}
		_cached = result;
		return result;
	}

	private static Color? GetMacOSAccent()
	{
		string value = ShellQuery.Run("defaults", "read -g AppleAccentColor").Trim();
		if (!int.TryParse(value, out int index))
		{
			return null;
		}
		return index switch
		{
			0 => Color.FromRgb(0xFF, 0x5A, 0x54),
			1 => Color.FromRgb(0xFF, 0x9F, 0x0A),
			2 => Color.FromRgb(0xFF, 0xD6, 0x0A),
			3 => Color.FromRgb(0x30, 0xD1, 0x58),
			4 => Color.FromRgb(0x00, 0x7A, 0xFF),
			5 => Color.FromRgb(0xBF, 0x5A, 0xF2),
			6 => Color.FromRgb(0xFF, 0x2D, 0x55),
			_ => null
		};
	}

	private static Color? GetLinuxAccent()
	{
		string accent = ShellQuery.Run("gsettings", "get org.gnome.desktop.interface accent-color").Trim().Trim('\'', '"');
		Color? named = FromGnomeName(accent);
		if (named.HasValue)
		{
			return named;
		}
		string theme = ShellQuery.Run("gsettings", "get org.gnome.desktop.interface gtk-theme").Trim().Trim('\'', '"');
		return FromGnomeName(theme);
	}

	private static Color? FromGnomeName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		string value = name.ToLowerInvariant();
		if (value.Contains("blue")) return Color.FromRgb(0x35, 0x84, 0xE4);
		if (value.Contains("teal")) return Color.FromRgb(0x21, 0x90, 0xA4);
		if (value.Contains("green")) return Color.FromRgb(0x3A, 0x94, 0x4A);
		if (value.Contains("yellow")) return Color.FromRgb(0xC8, 0x8A, 0x00);
		if (value.Contains("orange")) return Color.FromRgb(0xED, 0x5B, 0x00);
		if (value.Contains("red")) return Color.FromRgb(0xE6, 0x22, 0x2E);
		if (value.Contains("pink")) return Color.FromRgb(0xD5, 0x63, 0x99);
		if (value.Contains("purple")) return Color.FromRgb(0x91, 0x41, 0xAC);
		if (value.Contains("slate")) return Color.FromRgb(0x6F, 0x83, 0x96);
		return null;
	}
}
