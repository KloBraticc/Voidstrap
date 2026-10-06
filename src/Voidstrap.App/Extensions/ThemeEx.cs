using System.Collections.Generic;
using Microsoft.Win32;
using Voidstrap.Enums;

namespace Voidstrap.Extensions;

public static class ThemeEx
{
	public static IReadOnlyCollection<Theme> Selections { get; } = new Theme[15]
	{
		Theme.Default,
		Theme.Custom,
		Theme.Dark,
		Theme.UltraGray,
		Theme.Light,
		Theme.Voidstrap,
		Theme.Red,
		Theme.Orange,
		Theme.Yellow,
		Theme.Green,
		Theme.Cyan,
		Theme.Blue,
		Theme.Purple,
		Theme.Pink,
		Theme.Berry
	};

	public static Theme GetFinal(this Theme dialogTheme)
	{
		if (dialogTheme != Theme.Default)
		{
			return dialogTheme;
		}
		if (!Voidstrap.Utility.Platform.SupportsRegistry)
		{
			return Theme.Dark;
		}
		using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
		object? obj = registryKey?.GetValue("AppsUseLightTheme");
		if (obj is int && (int)obj == 0)
		{
			return Theme.Dark;
		}
		return Theme.Light;
	}
}
