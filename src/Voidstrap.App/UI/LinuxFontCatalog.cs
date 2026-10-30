using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Voidstrap.UI;

public static class LinuxFontCatalog
{
#if CROSSPLAT
	private static readonly string[] FallbackAliasNames =
	[
		"Segoe UI",
		"Segoe UI Variable",
		"Segoe UI Semibold",
		"Segoe UI Light",
		"Segoe UI Semilight",
		"Arial"
	];
#endif

    public static void Install()
    {
#if CROSSPLAT
		if (!OperatingSystem.IsLinux())
			return;

		Type catalog = typeof(ProGPU.Text.FontApi);
		FieldInfo? fontsField = catalog.GetField("s_cachedSystemFonts", BindingFlags.NonPublic | BindingFlags.Static);
		FieldInfo? lockField = catalog.GetField("s_cachedSystemFontsLock", BindingFlags.NonPublic | BindingFlags.Static);
		if (fontsField is null || lockField?.GetValue(null) is not object gate)
		{
			App.Logger.WriteLine("LinuxFontCatalog", "The renderer font catalog was not found, keeping its order");
			return;
		}

		ProGPU.Text.FontApi.GetSystemFonts();
		int moved = 0;
		ProGPU.Text.FontInfo? fallbackInfo = null;
		lock (gate)
		{
			if (fontsField.GetValue(null) is not List<ProGPU.Text.FontInfo> fonts)
				return;

			EnsureBundledFontsPresent(fonts);

			List<ProGPU.Text.FontInfo> regular = new(fonts.Count);
			List<ProGPU.Text.FontInfo> others = new(fonts.Count);
			foreach (ProGPU.Text.FontInfo font in fonts)
			{
				if (font.Weight == 400 && !font.IsItalic && font.Width == 5)
					regular.Add(font);
				else
					others.Add(font);
			}

			moved = regular.Count;
			fonts.Clear();
			fonts.AddRange(regular);
			fonts.AddRange(others);

			fallbackInfo = FindBundledRegular(fonts);
			if (fallbackInfo is not null)
			{
				foreach (string alias in FallbackAliasNames)
				{
					if (!fonts.Exists(font => string.Equals(font.FamilyName, alias, StringComparison.OrdinalIgnoreCase)))
					{
						fonts.Add(new ProGPU.Text.FontInfo
						{
							Name = alias,
							FamilyName = alias,
							FilePath = fallbackInfo.FilePath,
							FaceIndex = fallbackInfo.FaceIndex,
							Weight = fallbackInfo.Weight,
							Width = fallbackInfo.Width,
							IsItalic = fallbackInfo.IsItalic
						});
					}
				}
			}
		}

		if (fallbackInfo is not null)
		{
			try
			{
				ProGPU.Text.TtfFont? fallbackFont = ProGPU.Text.FontManager.LoadSystemFont(fallbackInfo);
				if (fallbackFont is not null)
					ProGPU.Text.FontApi.RegisterPlatformFallbackFont(fallbackFont);
			}
			catch
			{
			}
		}

		App.Logger.WriteLine("LinuxFontCatalog", "Name lookups now prefer the regular face of " + moved + " font files");
#endif
    }

#if CROSSPLAT
	private static void EnsureBundledFontsPresent(List<ProGPU.Text.FontInfo> fonts)
	{
		if (fonts.Exists(font => string.Equals(font.FamilyName, "Inter 18pt", StringComparison.OrdinalIgnoreCase))
			&& fonts.Exists(font => string.Equals(font.FamilyName, "Selawik", StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		try
		{
			string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts", "voidstrap");
			if (!Directory.Exists(directory))
				return;

			foreach (string file in Directory.EnumerateFiles(directory, "*.ttf"))
			{
				if (fonts.Exists(font => string.Equals(font.FilePath, file, StringComparison.Ordinal)))
					continue;
				fonts.AddRange(ProGPU.Text.FontApi.ParseFontInfos(file));
			}
		}
		catch
		{
		}
	}

	private static ProGPU.Text.FontInfo? FindBundledRegular(List<ProGPU.Text.FontInfo> fonts)
	{
		foreach (string preferred in new[] { "Inter 18pt", "Selawik" })
		{
			ProGPU.Text.FontInfo? regular = fonts.Find(font =>
				string.Equals(font.FamilyName, preferred, StringComparison.OrdinalIgnoreCase)
				&& font.Weight == 400
				&& !font.IsItalic);
			if (regular is not null)
				return regular;

			ProGPU.Text.FontInfo? any = fonts.Find(font =>
				string.Equals(font.FamilyName, preferred, StringComparison.OrdinalIgnoreCase));
			if (any is not null)
				return any;
		}

		return fonts.Count > 0 ? fonts[0] : null;
	}
#endif
}
