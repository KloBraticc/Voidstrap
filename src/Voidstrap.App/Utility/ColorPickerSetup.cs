using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using RinColor;
using Voidstrap.Resources;

namespace Voidstrap.Utility;

internal static class ColorPickerSetup
{
	private static bool _configured;

	public static void EnsureConfigured()
	{
		if (_configured)
			return;
		_configured = true;
		RinColorPickerText.Provider = CreateText;
		SettingsSwatchStore store = new();
		store.Load(Parse(App.Settings.Prop.ColorPickerRecent), Parse(App.Settings.Prop.ColorPickerSaved));
		RinColorSwatches.Store = store;
		RinColorClipboard.GetText = ClipboardService.GetText;
		RinColorClipboard.SetText = ClipboardService.SetText;
		if (Platform.IsMacOS)
			RinScreenSampler.Picker = new MacOSColorPicker();
#if CROSSPLAT
		else if (Platform.IsLinux)
			RinScreenSampler.Picker = new Voidstrap.UI.LinuxColorPickPortal();
#endif
	}

	private static RinColorPickerText CreateText()
	{
		return new RinColorPickerText
		{
			Red = Strings.Enums_Theme_Red,
			Green = Strings.Enums_Theme_Green,
			Blue = Strings.Enums_Theme_Blue,
			Hue = Strings.ColorPicker_Hue,
			Saturation = Strings.ColorPicker_Saturation,
			Value = Strings.ColorPicker_Brightness,
			Lightness = Strings.ColorPicker_Lightness,
			Alpha = Strings.ColorPicker_Opacity,
			Hex = Strings.ColorPicker_Hex,
			Recent = Strings.ColorPicker_Recent,
			Saved = Strings.ColorPicker_Saved,
			SaveColor = Strings.ColorPicker_SaveColor,
			RemoveColor = Strings.ColorPicker_RemoveColor,
			PickFromScreen = Strings.ColorPicker_PickFromScreen,
			Copy = Strings.ColorPicker_CopyHex,
			Paste = Strings.ColorPicker_PasteHex,
			Original = Strings.ColorPicker_Original,
			Current = Strings.ColorPicker_New
		};
	}

	private static IEnumerable<Color> Parse(IEnumerable<string>? values)
	{
		if (values == null)
			yield break;
		foreach (string value in values)
			if (RinColorMath.TryParseHex(value, out Color color))
				yield return color;
	}

	private sealed class SettingsSwatchStore : RinColorSwatchMemoryStore
	{
		protected override void OnChanged()
		{
			App.Settings.Prop.ColorPickerRecent = Recent.Select(color => RinColorMath.ToHex(color, true)).ToList();
			App.Settings.Prop.ColorPickerSaved = Saved.Select(color => RinColorMath.ToHex(color, true)).ToList();
			App.Settings.SaveDeferred();
		}
	}
}
