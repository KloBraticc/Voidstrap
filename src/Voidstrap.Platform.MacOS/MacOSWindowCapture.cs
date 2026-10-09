using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSWindowCapture
{
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	private const uint IncludingWindow = 1 << 3;
	private const uint BoundsIgnoreFraming = 1 << 0;
	private const uint NominalResolution = 1 << 4;

	public static bool HasPermission => OperatingSystem.IsMacOS() && CGPreflightScreenCaptureAccess();

	public static bool RequestPermission() => OperatingSystem.IsMacOS() && CGRequestScreenCaptureAccess();

	public static bool TryCapture(int windowNumber, bool nominalResolution, ref byte[] buffer, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (!OperatingSystem.IsMacOS() || windowNumber <= 0)
			return false;
		MacOSWindow.Rect nullRect = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);
		nint image = CGWindowListCreateImage(nullRect, IncludingWindow, (uint)windowNumber, BoundsIgnoreFraming | (nominalResolution ? NominalResolution : 0));
		if (image == 0)
			return false;
		nint data = 0;
		try
		{
			int imageWidth = (int)CGImageGetWidth(image);
			int imageHeight = (int)CGImageGetHeight(image);
			int bitsPerPixel = (int)CGImageGetBitsPerPixel(image);
			int stride = (int)CGImageGetBytesPerRow(image);
			if (imageWidth <= 0 || imageHeight <= 0 || bitsPerPixel != 32 || stride < imageWidth * 4)
				return false;
			data = CGDataProviderCopyData(CGImageGetDataProvider(image));
			if (data == 0)
				return false;
			nint source = CFDataGetBytePtr(data);
			long length = CFDataGetLength(data);
			if (source == 0 || length < (long)stride * imageHeight)
				return false;
			int needed = checked(imageWidth * imageHeight * 4);
			if (buffer.Length != needed)
				buffer = new byte[needed];
			for (int row = 0; row < imageHeight; row++)
				Marshal.Copy(source + row * stride, buffer, row * imageWidth * 4, imageWidth * 4);
			width = imageWidth;
			height = imageHeight;
			return true;
		}
		finally
		{
			if (data != 0)
				CFRelease(data);
			CFRelease(image);
		}
	}

	[LibraryImport(CoreGraphics)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool CGPreflightScreenCaptureAccess();

	[LibraryImport(CoreGraphics)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool CGRequestScreenCaptureAccess();

	[LibraryImport(CoreGraphics)]
	private static partial nint CGWindowListCreateImage(MacOSWindow.Rect bounds, uint listOption, uint windowId, uint imageOption);

	[LibraryImport(CoreGraphics)]
	private static partial nuint CGImageGetWidth(nint image);

	[LibraryImport(CoreGraphics)]
	private static partial nuint CGImageGetHeight(nint image);

	[LibraryImport(CoreGraphics)]
	private static partial nuint CGImageGetBitsPerPixel(nint image);

	[LibraryImport(CoreGraphics)]
	private static partial nuint CGImageGetBytesPerRow(nint image);

	[LibraryImport(CoreGraphics)]
	private static partial nint CGImageGetDataProvider(nint image);

	[LibraryImport(CoreGraphics)]
	private static partial nint CGDataProviderCopyData(nint provider);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFDataGetBytePtr(nint data);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFDataGetLength(nint data);

	[LibraryImport(CoreFoundation)]
	private static partial void CFRelease(nint value);
}
