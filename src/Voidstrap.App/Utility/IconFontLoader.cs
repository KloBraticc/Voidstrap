using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Resources;

namespace Voidstrap.Utility;

internal static class IconFontLoader
{
	private static readonly (string ResourceKey, string FileName, string FamilyName)[] Fonts = new[]
	{
		("FluentSystemIcons", "FluentSystemIcons-Regular.ttf", "FluentSystemIcons-Regular"),
		("FluentSystemIconsFilled", "FluentSystemIcons-Filled.ttf", "FluentSystemIcons-Filled")
	};

	public static void Install()
	{
		if (Platform.IsWindows)
		{
			return;
		}
		string directory;
		try
		{
			directory = Path.Combine(Path.GetTempPath(), "Voidstrap", "Fonts");
			Directory.CreateDirectory(directory);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("IconFontLoader::Install", "Could not create font directory: " + ex.Message);
			return;
		}

		foreach ((string resourceKey, string fileName, string familyName) in Fonts)
		{
			try
			{
				string path = Path.Combine(directory, fileName);
				if (!File.Exists(path) || new FileInfo(path).Length == 0)
				{
					if (!Extract(fileName, path))
					{
						App.Logger?.WriteLine("IconFontLoader::Install", "Could not extract " + fileName);
						continue;
					}
				}
				System.Windows.Media.FontFamily family = new System.Windows.Media.FontFamily(new Uri(directory + Path.DirectorySeparatorChar), "./#" + familyName);
				if (!HasGlyphs(family))
				{
					App.Logger?.WriteLine("IconFontLoader::Install", familyName + " loaded from disk but exposes no glyphs");
					continue;
				}
				Application.Current.Resources[resourceKey] = family;
				App.Logger?.WriteLine("IconFontLoader::Install", "Registered " + familyName + " from " + path);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("IconFontLoader::Install", "Failed for " + fileName + ": " + ex.Message);
			}
		}
	}

	private static bool Extract(string fileName, string destination)
	{
		try
		{
			StreamResourceInfo? info = Application.GetResourceStream(new Uri("pack://application:,,,/Wpf.Ui;component/Fonts/" + fileName, UriKind.Absolute));
			if (info?.Stream == null)
			{
				return false;
			}
			using Stream source = info.Stream;
			using FileStream target = File.Create(destination);
			source.CopyTo(target);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static bool HasGlyphs(System.Windows.Media.FontFamily family)
	{
		try
		{
			foreach (Typeface typeface in family.GetTypefaces())
			{
				if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphTypeface) && glyphTypeface.GlyphCount > 0)
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}
}
