using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Voidstrap.Utility;

internal static partial class SystemAccent
{
	private const string LogIdent = "SystemAccent";

	private static readonly Color Fallback = Color.FromRgb(0x6B, 0x4E, 0xE6);

	private static readonly Color TextLight = Color.FromRgb(0xFF, 0xFF, 0xFF);

	private static readonly Color TextDark = Color.FromRgb(0x00, 0x00, 0x00);

	private static Color? _cached;

	private static bool _subscribed;

	[LibraryImport("dwmapi.dll", EntryPoint = "DwmGetColorizationColor")]
	private static partial int DwmGetColorizationColor(out uint colorizationColor, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

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
			if (OperatingSystem.IsWindows())
			{
				result = GetWindowsAccent() ?? Fallback;
			}
			else if (OperatingSystem.IsMacOS())
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

	public static void ApplyResources()
	{
		Application? application = Application.Current;
		if (application == null)
		{
			return;
		}
		try
		{
			Color fill = ResolveFill(application);
			SolidColorBrush fillBrush = new SolidColorBrush(fill);
			fillBrush.Freeze();
			SolidColorBrush textBrush = new SolidColorBrush(ReadableOn(fill));
			textBrush.Freeze();
			application.Resources["AccentFillColorPrimary"] = fill;
			application.Resources["AccentFillColorPrimaryBrush"] = fillBrush;
			application.Resources["TextOnAccentFillColorPrimary"] = textBrush.Color;
			application.Resources["TextOnAccentFillColorPrimaryBrush"] = textBrush;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The accent brushes could not be applied: " + ex.Message);
		}
		if (_subscribed || !Platform.IsWindows)
		{
			return;
		}
		SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
		_subscribed = true;
	}

	public static void Shutdown()
	{
		if (!_subscribed)
		{
			return;
		}
		SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
		_subscribed = false;
	}

	private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
	{
		if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle)
		{
			return;
		}
		_cached = null;
		Application? application = Application.Current;
		if (application == null)
		{
			return;
		}
		application.Dispatcher.BeginInvoke(new Action(ApplyResources));
	}

	private static Color ResolveFill(Application application)
	{
		foreach (string key in new string[] { "SystemAccentColorSecondary", "SystemAccentColorPrimary", "SystemAccentColor" })
		{
			try
			{
				if (application.Resources[key] is Color themed && themed.A != 0)
				{
					return themed;
				}
			}
			catch
			{
			}
		}
		return Get();
	}

	private static Color ReadableOn(Color color)
	{
		double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
		return luminance > 0.58 ? TextDark : TextLight;
	}

	private static Color? GetWindowsAccent()
	{
		try
		{
			if (DwmGetColorizationColor(out uint colorization, out bool _) == 0)
			{
				Color color = Color.FromRgb((byte)(colorization >> 16), (byte)(colorization >> 8), (byte)colorization);
				if (color.R != 0 || color.G != 0 || color.B != 0)
				{
					return color;
				}
			}
		}
		catch
		{
		}
		try
		{
			Color glass = System.Windows.SystemParameters.WindowGlassColor;
			if (glass.A != 0 && (glass.R != 0 || glass.G != 0 || glass.B != 0))
			{
				return Color.FromRgb(glass.R, glass.G, glass.B);
			}
		}
		catch
		{
		}
		return null;
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
