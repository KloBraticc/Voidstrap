using System;
using System.Collections.Generic;
using System.Reflection;

namespace Voidstrap.UI;

public static class LinuxFontCatalog
{
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
		lock (gate)
		{
			if (fontsField.GetValue(null) is not List<ProGPU.Text.FontInfo> fonts)
				return;

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
		}

		App.Logger.WriteLine("LinuxFontCatalog", "Name lookups now prefer the regular face of " + moved + " font files");
#endif
	}
}
