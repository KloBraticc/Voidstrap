using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSDisplayColor
{
	private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const int DefaultIntent = 0;

	public static bool TryMatchSrgbToMainDisplay(byte red, byte green, byte blue, out byte displayRed, out byte displayGreen, out byte displayBlue)
	{
		displayRed = red;
		displayGreen = green;
		displayBlue = blue;
		if (!OperatingSystem.IsMacOS())
			return false;
		nint srgb = 0;
		nint display = 0;
		nint source = 0;
		nint matched = 0;
		try
		{
			nint library = NativeLibrary.Load(CoreGraphics);
			nint name = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCGColorSpaceSRGB"));
			srgb = CGColorSpaceCreateWithName(name);
			display = CGDisplayCopyColorSpace(CGMainDisplayID());
			if (srgb == 0 || display == 0)
				return false;
			double[] components = [red / 255d, green / 255d, blue / 255d, 1d];
			source = CGColorCreate(srgb, components);
			if (source == 0)
				return false;
			matched = CGColorCreateCopyByMatchingToColorSpace(display, DefaultIntent, source, 0);
			if (matched == 0 || CGColorGetNumberOfComponents(matched) < 3)
				return false;
			nint values = CGColorGetComponents(matched);
			if (values == 0)
				return false;
			double[] result = new double[3];
			Marshal.Copy(values, result, 0, 3);
			displayRed = ToByte(result[0]);
			displayGreen = ToByte(result[1]);
			displayBlue = ToByte(result[2]);
			return true;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
		{
			return false;
		}
		finally
		{
			if (matched != 0) CFRelease(matched);
			if (source != 0) CFRelease(source);
			if (display != 0) CFRelease(display);
			if (srgb != 0) CFRelease(srgb);
		}
	}

	private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255d), 0, 255);

	[LibraryImport(CoreGraphics)] private static partial uint CGMainDisplayID();
	[LibraryImport(CoreGraphics)] private static partial nint CGDisplayCopyColorSpace(uint display);
	[LibraryImport(CoreGraphics)] private static partial nint CGColorSpaceCreateWithName(nint name);
	[LibraryImport(CoreGraphics)] private static partial nint CGColorCreate(nint space, double[] components);
	[LibraryImport(CoreGraphics)] private static partial nint CGColorCreateCopyByMatchingToColorSpace(nint space, int intent, nint color, nint options);
	[LibraryImport(CoreGraphics)] private static partial nuint CGColorGetNumberOfComponents(nint color);
	[LibraryImport(CoreGraphics)] private static partial nint CGColorGetComponents(nint color);
	[LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
}
