using FontFamily = System.Windows.Media.FontFamily;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI;

public static class AppFont
{
	private static bool _registered;

	private static FontFamily? _current;

	public static bool HasCustomFont => _current != null;

	public static FontFamily CurrentFontFamily => _current ?? new FontFamily("Segoe UI");

	public static string CurrentFontName
	{
		get
		{
			try
			{
				if (_current == null)
				{
					return "";
				}
				string text = string.Join(" ", _current.FamilyNames.Values);
				return string.IsNullOrWhiteSpace(text) ? "" : text;
			}
			catch
			{
				return "";
			}
		}
	}

	public static void Initialize()
	{
		Load();
		Register();
		ApplyToAllWindows();
	}

	public static void Register()
	{
		if (_registered)
		{
			return;
		}
		_registered = true;
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, (RoutedEventHandler)delegate(object sender, RoutedEventArgs _)
		{
			if (sender is Window window)
			{
				Apply(window);
			}
		});
	}

	private static void Load()
	{
		_current = null;
		try
		{
			string appFontPath = App.Settings.Prop.AppFontPath;
			if (!string.IsNullOrEmpty(appFontPath) && File.Exists(appFontPath))
			{
				_current = Fonts.GetFontFamilies(new Uri(appFontPath, UriKind.Absolute)).FirstOrDefault();
			}
		}
		catch
		{
			_current = null;
		}
	}

	public static void Apply(Window window)
	{
		if (window == null || window is not WpfUiWindow)
		{
			return;
		}
		try
		{
			if (_current != null)
			{
				window.FontFamily = _current;
			}
			else
			{
				((DependencyObject)window).ClearValue(Control.FontFamilyProperty);
			}
		}
		catch
		{
		}
	}

	public static void ApplyToAllWindows()
	{
		Application current = Application.Current;
		if (current == null)
		{
			return;
		}
		foreach (Window window in current.Windows)
		{
			Apply(window);
		}
	}

	public static bool SetFromFile(string sourcePath)
	{
		App.Settings.Prop.AppFontPath = sourcePath;
		App.Settings.Save();
		Load();
		ApplyToAllWindows();
		return _current != null;
	}

	public static void Clear()
	{
		App.Settings.Prop.AppFontPath = "";
		App.Settings.Save();
		_current = null;
		ApplyToAllWindows();
	}
}
