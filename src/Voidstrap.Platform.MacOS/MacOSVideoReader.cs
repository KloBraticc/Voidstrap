using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed partial class MacOSVideoReader : IDisposable
{
	private const string ObjC = "/usr/lib/libobjc.A.dylib";
	private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
	private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private static readonly Lazy<nint> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation"));
	private static readonly Lazy<nint> Video = new(() => NativeLibrary.Load(CoreVideo));
	private nint _asset;
	private nint _reader;
	private nint _output;
	private bool _disposed;

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct Size(double Width, double Height);

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct Transform(double A, double B, double C, double D, double Tx, double Ty);

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct Time(long Value, int Timescale, uint Flags, long Epoch);

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct TimeRange(Time Start, Time Duration);

	public MacOSVideoReader(string path, int maxWidth, int maxHeight, int frameRate)
	{
		if (!OperatingSystem.IsMacOS())
			throw new PlatformNotSupportedException();
		_ = Foundation.Value;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint text = SendString(objc_getClass("NSString"), Selector("stringWithUTF8String:"), path);
			nint url = Send(Send(objc_getClass("NSURL"), Selector("fileURLWithPath:"), text), Selector("retain"));
			try
			{
				_asset = Send(Send(objc_getClass("AVURLAsset"), Selector("URLAssetWithURL:options:"), url, 0), Selector("retain"));
			}
			finally
			{
				Send(url, Selector("release"));
			}
			nint tracks = Send(_asset, Selector("tracksWithMediaType:"), Constant(Foundation.Value, "AVMediaTypeVideo"));
			if (Send(tracks, Selector("count")) == 0)
				throw new InvalidOperationException("The file has no video track that macOS can decode");
			nint track = Send(tracks, Selector("objectAtIndex:"), 0);
			Size natural = GetSize(track, Selector("naturalSize"));
			Transform transform;
			Time duration;
			if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
			{
				GetTransformIntel(out transform, track, Selector("preferredTransform"));
				GetTimeIntel(out duration, _asset, Selector("duration"));
			}
			else
			{
				transform = GetTransform(track, Selector("preferredTransform"));
				duration = GetTime(_asset, Selector("duration"));
			}
			double transformedWidth = Math.Abs(transform.A * natural.Width) + Math.Abs(transform.C * natural.Height);
			double transformedHeight = Math.Abs(transform.B * natural.Width) + Math.Abs(transform.D * natural.Height);
			if (!double.IsFinite(transformedWidth) || !double.IsFinite(transformedHeight) || transformedWidth < 1 || transformedHeight < 1 || duration.Timescale <= 0 || duration.Value <= 0)
				throw new InvalidOperationException("The video dimensions or duration are invalid");
			double scale = Math.Min(1d, Math.Min(Math.Clamp(maxWidth, 1, 1920) / transformedWidth, Math.Clamp(maxHeight, 1, 1080) / transformedHeight));
			int width = Math.Max(1, (int)Math.Floor(transformedWidth * scale));
			int height = Math.Max(1, (int)Math.Floor(transformedHeight * scale));
			double minX = Math.Min(0d, transform.A * natural.Width) + Math.Min(0d, transform.C * natural.Height);
			double minY = Math.Min(0d, transform.B * natural.Width) + Math.Min(0d, transform.D * natural.Height);
			Transform fitted = new(transform.A * scale, transform.B * scale, transform.C * scale, transform.D * scale, -minX * scale, -minY * scale);
			Time zero = new(0, 1, 1, 0);
			nint layer = Send(objc_getClass("AVMutableVideoCompositionLayerInstruction"), Selector("videoCompositionLayerInstructionWithAssetTrack:"), track);
			SetTransform(layer, Selector("setTransform:atTime:"), fitted, zero);
			nint instruction = Send(objc_getClass("AVMutableVideoCompositionInstruction"), Selector("videoCompositionInstruction"));
			SetRange(instruction, Selector("setTimeRange:"), new TimeRange(zero, duration));
			Send(instruction, Selector("setLayerInstructions:"), Array(layer));
			nint composition = Send(objc_getClass("AVMutableVideoComposition"), Selector("videoComposition"));
			SetSize(composition, Selector("setRenderSize:"), new Size(width, height));
			SetTime(composition, Selector("setFrameDuration:"), new Time(1, Math.Clamp(frameRate, 1, 60), 1, 0));
			Send(composition, Selector("setInstructions:"), Array(instruction));
			nint settings = Send(objc_getClass("NSMutableDictionary"), Selector("dictionary"));
			nint format = Send(objc_getClass("NSNumber"), Selector("numberWithInt:"), 0x42475241);
			Send(settings, Selector("setObject:forKey:"), format, Constant(Video.Value, "kCVPixelBufferPixelFormatTypeKey"));
			_output = Send(Send(objc_getClass("AVAssetReaderVideoCompositionOutput"), Selector("alloc")), Selector("initWithVideoTracks:videoSettings:"), Array(track), settings);
			Send(_output, Selector("setVideoComposition:"), composition);
			SetBool(_output, Selector("setAlwaysCopiesSampleData:"), false);
			_reader = CreateReader(Send(objc_getClass("AVAssetReader"), Selector("alloc")), Selector("initWithAsset:error:"), _asset, out nint error);
			if (_reader == 0 || !SendBool(_reader, Selector("canAddOutput:"), _output))
				throw new InvalidOperationException(Describe(error));
			Send(_reader, Selector("addOutput:"), _output);
			if (!SendBool(_reader, Selector("startReading")))
				throw new InvalidOperationException(Describe(Send(_reader, Selector("error"))));
		}
		catch
		{
			Dispose();
			throw;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public bool ReadFrame(ref byte[] pixels, out int width, out int height, out double seconds)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		width = height = 0;
		seconds = 0;
		nint pool = objc_autoreleasePoolPush();
		nint sample = 0;
		try
		{
			sample = Send(_output, Selector("copyNextSampleBuffer"));
			if (sample == 0)
			{
				if (Send(_reader, Selector("status")) == 3)
					throw new InvalidOperationException(Describe(Send(_reader, Selector("error"))));
				return false;
			}
			nint image = CMSampleBufferGetImageBuffer(sample);
			if (image == 0 || CVPixelBufferLockBaseAddress(image, 1) != 0)
				throw new InvalidOperationException("The decoded video frame is unavailable");
			try
			{
				width = checked((int)CVPixelBufferGetWidth(image));
				height = checked((int)CVPixelBufferGetHeight(image));
				int stride = checked((int)CVPixelBufferGetBytesPerRow(image));
				nint data = CVPixelBufferGetBaseAddress(image);
				if (width < 1 || height < 1 || width > 1920 || height > 1080 || stride < width * 4 || data == 0 || CVPixelBufferGetPixelFormatType(image) != 0x42475241)
					throw new InvalidOperationException("The decoded video frame has an unsupported pixel format or size");
				int size = checked(width * height * 4);
				if (pixels.Length != size)
					pixels = GC.AllocateUninitializedArray<byte>(size);
				for (int row = 0; row < height; row++)
					Marshal.Copy(data + row * stride, pixels, row * width * 4, width * 4);
				seconds = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sample));
				return true;
			}
			finally
			{
				CVPixelBufferUnlockBaseAddress(image, 1);
			}
		}
		finally
		{
			if (sample != 0)
				CFRelease(sample);
			objc_autoreleasePoolPop(pool);
		}
	}

	private static nint Selector(string name) => sel_registerName(name);
	private static nint Constant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
	private static nint Array(nint value) => Send(objc_getClass("NSArray"), Selector("arrayWithObject:"), value);
	private static string Describe(nint error) => error == 0 ? "macOS could not open this video format" : Marshal.PtrToStringUTF8(Send(Send(error, Selector("localizedDescription")), Selector("UTF8String"))) ?? "macOS could not decode this video";

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			if (_reader != 0)
			{
				Send(_reader, Selector("cancelReading"));
				Send(_reader, Selector("release"));
				_reader = 0;
			}
			if (_output != 0)
			{
				Send(_output, Selector("release"));
				_output = 0;
			}
			if (_asset != 0)
			{
				Send(_asset, Selector("release"));
				_asset = 0;
			}
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
		GC.SuppressFinalize(this);
	}

	[LibraryImport(ObjC)] private static partial nint objc_autoreleasePoolPush();
	[LibraryImport(ObjC)] private static partial void objc_autoreleasePoolPop(nint pool);
	[LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint objc_getClass(string name);
	[LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint sel_registerName(string name);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector, nint value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector, nint first, nint second);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint SendString(nint target, nint selector, string value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint CreateReader(nint target, nint selector, nint asset, out nint error);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static partial bool SendBool(nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static partial bool SendBool(nint target, nint selector, nint value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetBool(nint target, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial Size GetSize(nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial Transform GetTransform(nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static partial void GetTransformIntel(out Transform value, nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial Time GetTime(nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static partial void GetTimeIntel(out Time value, nint target, nint selector);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetSize(nint target, nint selector, Size value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetTime(nint target, nint selector, Time value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetRange(nint target, nint selector, TimeRange value);
	[LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetTransform(nint target, nint selector, Transform value, Time time);
	[LibraryImport(CoreMedia)] private static partial nint CMSampleBufferGetImageBuffer(nint sample);
	[LibraryImport(CoreMedia)] private static partial Time CMSampleBufferGetPresentationTimeStamp(nint sample);
	[LibraryImport(CoreMedia)] private static partial double CMTimeGetSeconds(Time time);
	[LibraryImport(CoreVideo)] private static partial int CVPixelBufferLockBaseAddress(nint image, nuint flags);
	[LibraryImport(CoreVideo)] private static partial int CVPixelBufferUnlockBaseAddress(nint image, nuint flags);
	[LibraryImport(CoreVideo)] private static partial nuint CVPixelBufferGetWidth(nint image);
	[LibraryImport(CoreVideo)] private static partial nuint CVPixelBufferGetHeight(nint image);
	[LibraryImport(CoreVideo)] private static partial nuint CVPixelBufferGetBytesPerRow(nint image);
	[LibraryImport(CoreVideo)] private static partial uint CVPixelBufferGetPixelFormatType(nint image);
	[LibraryImport(CoreVideo)] private static partial nint CVPixelBufferGetBaseAddress(nint image);
	[LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
}
