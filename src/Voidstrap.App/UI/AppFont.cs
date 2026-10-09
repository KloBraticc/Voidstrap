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

	private static readonly FontFamily LinuxWpfUiFontFamily = Voidstrap.Utility.Platform.IsMacOS
		? new(new Uri(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts") + Path.DirectorySeparatorChar), "./#Inter 18pt, ./#Selawik, Helvetica Neue")
		: new("Inter 18pt, Selawik, Segoe UI Variable, Segoe UI, Ubuntu, Cantarell, Noto Sans, DejaVu Sans, Liberation Sans");

	public static bool HasCustomFont => _current != null;

	public static FontFamily CurrentFontFamily => _current ?? DefaultFontFamily;

	private static FontFamily DefaultFontFamily
	{
		get
		{
			if (Voidstrap.Utility.Platform.UsesPortableUi)
			{
				return LinuxWpfUiFontFamily;
			}

			if (Application.Current?.TryFindResource("ContentControlThemeFontFamily") is FontFamily fontFamily)
			{
				return fontFamily;
			}

			return new FontFamily("Segoe UI");
		}
	}

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
		if (Voidstrap.Utility.Platform.UsesPortableUi && Application.Current != null)
		{
			Application.Current.Resources["ContentControlThemeFontFamily"] = LinuxWpfUiFontFamily;
			Application.Current.Resources[SystemFonts.MessageFontFamilyKey] = LinuxWpfUiFontFamily;
		}
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
		if (Voidstrap.Utility.Platform.UsesPortableUi)
		{
			EventManager.RegisterClassHandler(typeof(Page), FrameworkElement.LoadedEvent, (RoutedEventHandler)delegate(object sender, RoutedEventArgs _)
			{
				if (sender is Page page)
				{
					ApplyToPage(page);
				}
			});
		}
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

	private static void ApplyToPage(Page page)
	{
		try
		{
			FontFamily family = CurrentFontFamily;
			if (!string.Equals(page.FontFamily?.Source, family.Source, StringComparison.OrdinalIgnoreCase))
			{
				page.SetCurrentValue(Page.FontFamilyProperty, family);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AppFont", "The page font could not be applied: " + ex.Message);
		}
	}

	public static void Apply(Window window)
	{
		if (window == null || !Voidstrap.Utility.Platform.UsesPortableUi && window is not WpfUiWindow)
		{
			return;
		}
		try
		{
			if (_current != null || Voidstrap.Utility.Platform.UsesPortableUi)
			{
				window.FontFamily = CurrentFontFamily;
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
			if (Voidstrap.Utility.Platform.UsesPortableUi)
			{
				ApplyToPages(window);
			}
		}
	}

	private static void ApplyToPages(DependencyObject root)
	{
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int index = 0; index < count; index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, index);
			if (child is Page page)
			{
				ApplyToPage(page);
			}
			ApplyToPages(child);
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
