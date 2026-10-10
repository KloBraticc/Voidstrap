using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed unsafe partial class MacOSKeyboardLayout : IDisposable
{
	private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private static readonly nint CarbonLibrary = OperatingSystem.IsMacOS() ? NativeLibrary.Load(Carbon) : 0;
	private static readonly nint LayoutDataKey = CarbonLibrary == 0 ? 0 : Marshal.ReadIntPtr(NativeLibrary.GetExport(CarbonLibrary, "kTISPropertyUnicodeKeyLayoutData"));
	private nint _source;
	private uint _keyboardType;
	private bool _disposed;

	public bool Refresh(ushort[] fallback, uint keyboardType, out ushort[] keys)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		keys = fallback;
		nint source = TISCopyCurrentKeyboardLayoutInputSource();
		if (source == 0)
			return false;
		keyboardType = keyboardType != 0 ? keyboardType : _keyboardType != 0 ? _keyboardType : LMGetKbdType();
		if (_source != 0 && CFEqual(_source, source) && keyboardType == _keyboardType)
		{
			CFRelease(source);
			return false;
		}
		try
		{
			keys = (ushort[])fallback.Clone();
			bool[] assigned = new bool[keys.Length];
			MapSource(source, keyboardType, keys, assigned);
			nint ascii = TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
			if (ascii != 0)
			{
				try { MapSource(ascii, keyboardType, keys, assigned); }
				finally { CFRelease(ascii); }
			}
			if (_source != 0)
				CFRelease(_source);
			_source = source;
			_keyboardType = keyboardType;
			return true;
		}
		finally
		{
			if (_source != source)
				CFRelease(source);
		}
	}

	private static void MapSource(nint source, uint keyboardType, ushort[] keys, bool[] assigned)
	{
		nint data = TISGetInputSourceProperty(source, LayoutDataKey);
		nint layout = data == 0 ? 0 : CFDataGetBytePtr(data);
		if (layout == 0)
			return;
		char* text = stackalloc char[4];
		for (uint modifiers = 0; modifiers <= 2; modifiers += 2)
		{
			for (ushort code = 0; code <= 94; code++)
			{
				if (code > 50 && code is not (93 or 94))
					continue;
				uint dead = 0;
				if (UCKeyTranslate(layout, code, 3, modifiers, keyboardType, 1, ref dead, 4, out nint count, text) != 0 || count != 1)
					continue;
				int key = VirtualKey(char.ToUpperInvariant(text[0]));
				if (key != 0 && !assigned[key])
				{
					keys[key] = code;
					assigned[key] = true;
				}
			}
		}
	}

	private static int VirtualKey(char value) => value switch
	{
		>= 'A' and <= 'Z' or >= '0' and <= '9' => value,
		';' or ':' => 0xBA,
		'=' or '+' => 0xBB,
		',' or '<' => 0xBC,
		'-' or '_' => 0xBD,
		'.' or '>' => 0xBE,
		'/' or '?' => 0xBF,
		'`' or '~' => 0xC0,
		'[' or '{' => 0xDB,
		'\\' or '|' or '¥' => 0xDC,
		']' or '}' => 0xDD,
		'\'' or '"' => 0xDE,
		_ => 0
	};

	public void Dispose()
	{
		if (_disposed)
			return;
		if (_source != 0)
			CFRelease(_source);
		_source = 0;
		_disposed = true;
		GC.SuppressFinalize(this);
	}

	[LibraryImport(Carbon)] private static partial nint TISCopyCurrentKeyboardLayoutInputSource();
	[LibraryImport(Carbon)] private static partial nint TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
	[LibraryImport(Carbon)] private static partial nint TISGetInputSourceProperty(nint source, nint property);
	[LibraryImport(Carbon)] private static partial byte LMGetKbdType();
	[LibraryImport(Carbon)] private static partial int UCKeyTranslate(nint layout, ushort code, ushort action, uint modifiers, uint keyboardType, uint options, ref uint dead, nint capacity, out nint count, char* text);
	[LibraryImport(CoreFoundation)] private static partial nint CFDataGetBytePtr(nint data);
	[LibraryImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static partial bool CFEqual(nint first, nint second);
	[LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
}
