using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSAppearance
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static nint _appKit;

	public static bool TryGetAccentColor(out byte red, out byte green, out byte blue)
	{
		red = green = blue = 0;
		if (!OperatingSystem.IsMacOS())
			return false;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			if (_appKit == 0)
				_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			nint accent = Send(objc_getClass("NSColor"), sel_registerName("controlAccentColor"));
			nint space = Send(objc_getClass("NSColorSpace"), sel_registerName("sRGBColorSpace"));
			nint color = accent == 0 || space == 0 ? 0 : SendObject(accent, sel_registerName("colorUsingColorSpace:"), space);
			if (color == 0)
				return false;
			GetComponents(color, sel_registerName("getRed:green:blue:alpha:"), out double r, out double g, out double b, out _);
			red = ToByte(r);
			green = ToByte(g);
			blue = ToByte(b);
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_getClass(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC)]
	private static partial nint objc_autoreleasePoolPush();

	[LibraryImport(ObjectiveC)]
	private static partial void objc_autoreleasePoolPop(nint pool);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendObject(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void GetComponents(nint receiver, nint selector, out double red, out double green, out double blue, out double alpha);
}
