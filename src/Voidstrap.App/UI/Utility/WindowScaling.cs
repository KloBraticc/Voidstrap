using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Voidstrap.UI.Utility;

public static class WindowScaling
{
	private static double _scaleFactor;

	private static bool _displaySubscribed;

	private static bool _preferenceSubscribed;

	public static double ScaleFactor => _scaleFactor;

	static WindowScaling()
	{
		_scaleFactor = 1.0;
		RecalculateScaleFactor();
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		_displaySubscribed = true;
		if (Environment.OSVersion.Version.Major < 10)
		{
			return;
		}
		SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
		_preferenceSubscribed = true;
	}

	private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		RecalculateScaleFactor();
	}

	private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
	{
		if (e.Category == UserPreferenceCategory.Window)
		{
			RecalculateScaleFactor();
		}
	}

	public static void Shutdown()
	{
		if (_displaySubscribed)
		{
			SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
			_displaySubscribed = false;
		}
		if (_preferenceSubscribed)
		{
			SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
			_preferenceSubscribed = false;
		}
	}

	public static void RecalculateScaleFactor()
	{
		try
		{
			using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
			_scaleFactor = (double)graphics.DpiX / 96.0;
		}
		catch
		{
			_scaleFactor = 1.0;
		}
	}

	public static int GetScaledValue(int value)
	{
		return (int)Math.Round((double)value * _scaleFactor);
	}

	public static Size GetScaledSize(Size size)
	{
		return new Size(GetScaledValue(size.Width), GetScaledValue(size.Height));
	}

	public static Point GetScaledPoint(Point point)
	{
		return new Point(GetScaledValue(point.X), GetScaledValue(point.Y));
	}

	public static Padding GetScaledPadding(Padding padding)
	{
		return new Padding(GetScaledValue(padding.Left), GetScaledValue(padding.Top), GetScaledValue(padding.Right), GetScaledValue(padding.Bottom));
	}

	public static Rectangle GetScaledRectangle(Rectangle rect)
	{
		return new Rectangle(GetScaledValue(rect.X), GetScaledValue(rect.Y), GetScaledValue(rect.Width), GetScaledValue(rect.Height));
	}
}
