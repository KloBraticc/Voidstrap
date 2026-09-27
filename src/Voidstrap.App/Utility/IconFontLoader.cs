using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Resources;

namespace Voidstrap.Utility;

internal static class IconFontLoader
{
	private static readonly (string ResourceKey, string FileName, string FamilyName, string ResourcePath)[] Fonts = new[]
	{
		("FluentSystemIcons", "FluentSystemIcons-Regular.ttf", "FluentSystemIcons-Regular", "pack://application:,,,/Resources/Fonts/SymbolIcons/FluentSystemIcons-Regular.ttf"),
		("FluentSystemIconsFilled", "FluentSystemIcons-Filled.ttf", "FluentSystemIcons-Filled", "pack://application:,,,/Wpf.Ui;component/Fonts/FluentSystemIcons-Filled.ttf")
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

		foreach ((string resourceKey, string fileName, string familyName, string resourcePath) in Fonts)
		{
			try
			{
				string path = Path.Combine(directory, fileName);
				if (!Extract(resourcePath, path))
				{
					App.Logger?.WriteLine("IconFontLoader::Install", "Could not extract " + fileName);
					continue;
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

	private static bool Extract(string resourcePath, string destination)
	{
		string? temporary = null;
		try
		{
			StreamResourceInfo? info = Application.GetResourceStream(new Uri(resourcePath, UriKind.Absolute));
			if (info?.Stream == null)
			{
				return false;
			}
			using Stream source = info.Stream;
			using MemoryStream content = new MemoryStream();
			source.CopyTo(content);
			byte[] current = content.ToArray();
			if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(current))
			{
				return true;
			}
			temporary = destination + "." + Guid.NewGuid().ToString("N");
			File.WriteAllBytes(temporary, current);
			File.Move(temporary, destination, true);
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			try
			{
				if (temporary != null && File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch
			{
			}
		}
	}

	public static System.Windows.Media.FontFamily? Resolve(string familyName)
	{
		try
		{
			string directory = Path.Combine(Paths.Temp, "Fonts");
			if (!Directory.Exists(directory))
				return null;

			return new System.Windows.Media.FontFamily(
				new Uri(directory + Path.DirectorySeparatorChar),
				"./#" + familyName);
		}
		catch (Exception)
		{
			return null;
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
