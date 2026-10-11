using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSImageDecoder
{
	private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
	private static readonly Lazy<nint> Library = new(() => NativeLibrary.Load(ImageIO));
	private static readonly Lazy<nint> FoundationLibrary = new(() => NativeLibrary.Load(Foundation));

	public static byte[] Decode(string path, int maxWidth, int maxHeight, out int width, out int height)
	{
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (stream.Length <= 0 || stream.Length > 64L * 1024 * 1024)
			throw new InvalidOperationException("The image file size is invalid");
		byte[] bytes = new byte[checked((int)stream.Length)];
		stream.ReadExactly(bytes);
		return Decode(bytes, maxWidth, maxHeight, out width, out height);
	}

	public static byte[] Decode(byte[] bytes, int maxWidth, int maxHeight, out int width, out int height)
	{
		if (!OperatingSystem.IsMacOS())
			throw new PlatformNotSupportedException();
		width = height = 0;
		if (bytes.Length == 0 || bytes.LongLength > 64L * 1024 * 1024)
			throw new InvalidOperationException("The image file size is invalid");
		nint encodedData = CFDataCreate(0, bytes, bytes.Length);
		if (encodedData == 0)
			throw new InvalidOperationException("macOS could not allocate the image data");
		nint source = 0;
		nint options = 0;
		nint number = 0;
		nint image = 0;
		nint colorSpace = 0;
		nint context = 0;
		try
		{
			source = CGImageSourceCreateWithData(encodedData, 0);
			if (source == 0)
				throw new InvalidOperationException("macOS could not open this image format");
			options = CFDictionaryCreateMutable(0, 0, NativeLibrary.GetExport(FoundationLibrary.Value, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(FoundationLibrary.Value, "kCFTypeDictionaryValueCallBacks"));
			nint yes = Constant(FoundationLibrary.Value, "kCFBooleanTrue");
			int edge = Math.Clamp(Math.Max(maxWidth, maxHeight), 1, 4096);
			number = CFNumberCreate(0, 3, ref edge);
			CFDictionarySetValue(options, Constant(Library.Value, "kCGImageSourceCreateThumbnailFromImageAlways"), yes);
			CFDictionarySetValue(options, Constant(Library.Value, "kCGImageSourceCreateThumbnailWithTransform"), yes);
			CFDictionarySetValue(options, Constant(Library.Value, "kCGImageSourceThumbnailMaxPixelSize"), number);
			image = CGImageSourceCreateThumbnailAtIndex(source, 0, options);
			if (image == 0)
				throw new InvalidOperationException("macOS could not decode this image format");
			int sourceWidth = checked((int)CGImageGetWidth(image));
			int sourceHeight = checked((int)CGImageGetHeight(image));
			if (sourceWidth < 1 || sourceHeight < 1 || sourceWidth > 4096 || sourceHeight > 4096)
				throw new InvalidOperationException("The decoded image dimensions are invalid");
			double scale = Math.Min(1d, Math.Min(Math.Clamp(maxWidth, 1, 4096) / (double)sourceWidth, Math.Clamp(maxHeight, 1, 4096) / (double)sourceHeight));
			width = Math.Max(1, (int)Math.Floor(sourceWidth * scale));
			height = Math.Max(1, (int)Math.Floor(sourceHeight * scale));
			byte[] pixels = new byte[checked(width * height * 4)];
			colorSpace = CGColorSpaceCreateDeviceRGB();
			unsafe
			{
				fixed (byte* data = pixels)
				{
					context = CGBitmapContextCreate((nint)data, (nuint)width, (nuint)height, 8, (nuint)(width * 4), colorSpace, 0x2002);
					if (context == 0)
						throw new InvalidOperationException("macOS could not create an image buffer");
					CGContextDrawImage(context, new MacOSWindow.Rect(0, 0, width, height), image);
					CGContextRelease(context);
					context = 0;
				}
			}
			for (int offset = 0; offset < pixels.Length; offset += 4)
			{
				int alpha = pixels[offset + 3];
				if (alpha is 0 or 255)
					continue;
				pixels[offset] = (byte)Math.Min(255, (pixels[offset] * 255 + alpha / 2) / alpha);
				pixels[offset + 1] = (byte)Math.Min(255, (pixels[offset + 1] * 255 + alpha / 2) / alpha);
				pixels[offset + 2] = (byte)Math.Min(255, (pixels[offset + 2] * 255 + alpha / 2) / alpha);
			}
			return pixels;
		}
		finally
		{
			if (context != 0) CGContextRelease(context);
			if (colorSpace != 0) CFRelease(colorSpace);
			if (image != 0) CFRelease(image);
			if (number != 0) CFRelease(number);
			if (options != 0) CFRelease(options);
			if (source != 0) CFRelease(source);
			if (encodedData != 0) CFRelease(encodedData);
		}
	}

	private static nint Constant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
	[LibraryImport(Foundation)] private static partial nint CFDataCreate(nint allocator, byte[] bytes, nint length);
	[LibraryImport(Foundation)] private static partial nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
	[LibraryImport(Foundation)] private static partial void CFDictionarySetValue(nint dictionary, nint key, nint value);
	[LibraryImport(Foundation)] private static partial nint CFNumberCreate(nint allocator, int type, ref int value);
	[LibraryImport(Foundation)] private static partial void CFRelease(nint value);
	[LibraryImport(ImageIO)] private static partial nint CGImageSourceCreateWithData(nint data, nint options);
	[LibraryImport(ImageIO)] private static partial nint CGImageSourceCreateThumbnailAtIndex(nint source, nuint index, nint options);
	[LibraryImport(Graphics)] private static partial nuint CGImageGetWidth(nint image);
	[LibraryImport(Graphics)] private static partial nuint CGImageGetHeight(nint image);
	[LibraryImport(Graphics)] private static partial nint CGColorSpaceCreateDeviceRGB();
	[LibraryImport(Graphics)] private static partial nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bits, nuint stride, nint colorSpace, uint info);
	[LibraryImport(Graphics)] private static partial void CGContextDrawImage(nint context, MacOSWindow.Rect rect, nint image);
	[LibraryImport(Graphics)] private static partial void CGContextRelease(nint context);
}
