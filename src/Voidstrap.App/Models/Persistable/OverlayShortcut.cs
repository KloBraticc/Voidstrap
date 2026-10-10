using System.Collections.Generic;

namespace Voidstrap.Models.Persistable;

// A global shortcut as Windows registers it: MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8 and a virtual key
public sealed class OverlayShortcut
{
	public const uint Alt = 0x1;
	public const uint Control = 0x2;
	public const uint Shift = 0x4;
	public const uint Windows = 0x8;

	public uint Modifiers { get; set; } = Control | Alt;

	public uint Key { get; set; } = 0x4C;

	public bool IsSet => Key != 0 && (Modifiers & (Alt | Control | Windows)) != 0;

	public OverlayShortcut Copy() => (OverlayShortcut)MemberwiseClone();

	public bool SameAs(OverlayShortcut? other) => other != null && other.Modifiers == Modifiers && other.Key == Key;

	// Written the way it is pressed, "Ctrl + Alt + L"
	public string Describe()
	{
		if (!IsSet)
			return "None";
		List<string> parts = new();
		if ((Modifiers & Control) != 0)
			parts.Add("Ctrl");
		if ((Modifiers & Alt) != 0)
			parts.Add("Alt");
		if ((Modifiers & Shift) != 0)
			parts.Add("Shift");
		if ((Modifiers & Windows) != 0)
			parts.Add("Win");
		parts.Add(KeyName(Key));
		return string.Join(" + ", parts);
	}

	private static string KeyName(uint key)
	{
		if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
			return ((char)key).ToString();
		if (key is >= 0x70 and <= 0x87)
			return "F" + (key - 0x6F);
		if (key is >= 0x60 and <= 0x69)
			return "Num " + (key - 0x60);
		return key switch
		{
			0x20 => "Space",
			0x09 => "Tab",
			0x0D => "Enter",
			0x08 => "Backspace",
			0x2D => "Insert",
			0x2E => "Delete",
			0x24 => "Home",
			0x23 => "End",
			0x21 => "Page Up",
			0x22 => "Page Down",
			0x25 => "Left",
			0x26 => "Up",
			0x27 => "Right",
			0x28 => "Down",
			0xC0 => "`",
			0xBD => "-",
			0xBB => "=",
			0xDB => "[",
			0xDD => "]",
			0xDC => "\\",
			0xBA => ";",
			0xDE => "'",
			0xBC => ",",
			0xBE => ".",
			0xBF => "/",
			_ => "Key " + key
		};
	}
}
