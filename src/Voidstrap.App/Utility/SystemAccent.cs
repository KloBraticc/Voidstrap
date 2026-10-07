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

	private static Color? _appliedColor;

	private static Wpf.Ui.Appearance.ThemeType? _appliedTheme;

	private static int _refreshPending;

	private static System.Windows.Threading.DispatcherOperation? _refreshOperation;

	[LibraryImport("dwmapi.dll", EntryPoint = "DwmGetColorizationColor")]
	private static partial int DwmGetColorizationColor(out uint colorizationColor, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

	public static Color GetGlassColor()
	{
		if (TryGetCustomColor(out Color custom))
			return custom;
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
		if (Platform.IsWindows && !TryGetCustomColor(out _))
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
		if (TryGetCustomColor(out Color custom))
			return custom;
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
				result = GetMacOSAccent() ?? MacOSBlue;
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

	public static bool ApplyResources()
	{
		Application? application = Application.Current;
		if (application == null)
		{
			return false;
		}
		bool changed = false;
		try
		{
			Wpf.Ui.Appearance.ThemeType theme = Wpf.Ui.Appearance.Theme.GetAppTheme();
			Color color = Get();
			if (_appliedColor != color || _appliedTheme != theme || application.Resources["SystemAccentColor"] is not Color current || current != color)
			{
				Wpf.Ui.Appearance.Accent.Apply(color, theme == Wpf.Ui.Appearance.ThemeType.Light ? Wpf.Ui.Appearance.ThemeType.Light : Wpf.Ui.Appearance.ThemeType.Dark);
				Color fill = ResolveFill(application);
				SolidColorBrush fillBrush = new SolidColorBrush(fill);
				fillBrush.Freeze();
				SolidColorBrush textBrush = new SolidColorBrush(ReadableOn(fill));
				textBrush.Freeze();
				application.Resources["AccentFillColorPrimary"] = fill;
				application.Resources["AccentFillColorPrimaryBrush"] = fillBrush;
				application.Resources["TextOnAccentFillColorPrimary"] = textBrush.Color;
				application.Resources["TextOnAccentFillColorPrimaryBrush"] = textBrush;
				_appliedColor = color;
				_appliedTheme = theme;
				changed = true;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The accent brushes could not be applied: " + ex.Message);
		}
		if (_subscribed)
		{
			return changed;
		}
		if (Platform.IsWindows)
		{
			SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
			_subscribed = true;
		}
		else if (OperatingSystem.IsLinux())
		{
			LinuxAppearancePortal.WatchAccent(OnLinuxAccentChanged);
			_subscribed = true;
		}
		else if (OperatingSystem.IsMacOS())
		{
			application.Activated += OnMacApplicationActivated;
			_subscribed = true;
		}
		return changed;
	}

	internal static bool TryGetCustomColor(out Color color)
	{
		color = default;
		string? value = App.Settings?.Prop.CustomAccentColor;
		if (value == null || value.Length != 7 || value[0] != '#'
			|| !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
			return false;
		color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
		return true;
	}

	public static void Refresh()
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
			return;
		if (System.Threading.Interlocked.Exchange(ref _refreshPending, 1) != 0)
			return;
		try
		{
			_refreshOperation = application.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RefreshCore));
		}
		catch (InvalidOperationException)
		{
			System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
		}
	}

	private static void RefreshCore()
	{
		_refreshOperation = null;
		System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
			return;
		if (ApplyResources())
			Voidstrap.UI.WindowBackdrop.RefreshAccentTint();
	}

	public static void Shutdown()
	{
		_refreshOperation?.Abort();
		_refreshOperation = null;
		System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
		if (!_subscribed)
		{
			return;
		}
		if (Platform.IsWindows)
		{
			SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
		}
		else if (OperatingSystem.IsLinux())
		{
			LinuxAppearancePortal.StopWatching();
		}
		else if (OperatingSystem.IsMacOS() && Application.Current is Application application)
		{
			application.Activated -= OnMacApplicationActivated;
		}
		_subscribed = false;
	}

	private static void OnMacApplicationActivated(object? sender, EventArgs e)
	{
		Color color = GetMacOSAccent() ?? MacOSBlue;
		if (_cached == color)
			return;
		_cached = color;
		App.Logger?.WriteLine(LogIdent, "System accent changed to " + Describe(color));
		if (!TryGetCustomColor(out _))
			Refresh();
	}

	private static void OnLinuxAccentChanged(Color color)
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
		{
			return;
		}
		application.Dispatcher.BeginInvoke(new Action(delegate
		{
			if (_cached == color)
			{
				return;
			}
			_cached = color;
			App.Logger?.WriteLine(LogIdent, "System accent changed to " + Describe(color));
			if (!TryGetCustomColor(out _))
				Refresh();
		}));
	}

	private static string Describe(Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

	private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
	{
		if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle)
		{
			return;
		}
		_cached = null;
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
		{
			return;
		}
		if (!TryGetCustomColor(out _))
			application.Dispatcher.BeginInvoke(new Action(Refresh));
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

	private static readonly Color MacOSBlue = Color.FromRgb(0x00, 0x7A, 0xFF);

	private static Color? GetMacOSAccent()
	{
		return Voidstrap.Platform.MacOS.MacOSAppearance.TryGetAccentColor(out byte red, out byte green, out byte blue)
			? Color.FromRgb(red, green, blue)
			: MacOSBlue;
	}

	private static Color? GetLinuxAccent()
	{
		Color? portal = LinuxAppearancePortal.ReadAccent();
		if (portal.HasValue)
		{
			App.Logger?.WriteLine(LogIdent, "System accent " + Describe(portal.Value) + " from the desktop portal");
			return portal;
		}
		if (Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed)
		{
			App.Logger?.WriteLine(LogIdent, "The desktop portal has no accent color, using the Voidstrap accent");
			return null;
		}
		string accent = ShellQuery.Run("gsettings", "get org.gnome.desktop.interface accent-color").Trim().Trim('\'', '"');
		Color? named = FromGnomeName(accent);
		if (named.HasValue)
		{
			return named;
		}
		string theme = ShellQuery.Run("gsettings", "get org.gnome.desktop.interface gtk-theme").Trim().Trim('\'', '"');
		return FromGnomeName(theme);
	}

	internal static Color? FromGnomeName(string name)
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
