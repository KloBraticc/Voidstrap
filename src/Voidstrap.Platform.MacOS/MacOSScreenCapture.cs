using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static unsafe partial class MacOSScreenCapture
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	private const int BlockHasCopyDispose = 1 << 25;
	private const uint PixelFormatBgra = 0x42475241;

	[StructLayout(LayoutKind.Sequential)]
	private struct BlockDescriptor
	{
		public nuint Reserved;
		public nuint Size;
		public nint Copy;
		public nint Dispose;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BlockLiteral
	{
		public nint Isa;
		public int Flags;
		public int Reserved;
		public nint Invoke;
		public BlockDescriptor* Descriptor;
		public nint Context;
	}

	private sealed class CaptureRequest(uint windowId, bool image)
	{
		public readonly uint WindowId = windowId;
		private readonly TaskCompletionSource<nint> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public nint Wait(TimeSpan timeout)
		{
			if (!_result.Task.Wait(timeout))
				_result.TrySetResult(0);
			return _result.Task.GetAwaiter().GetResult();
		}

		public void Complete(nint result)
		{
			if (_result.TrySetResult(result) || result == 0)
				return;
			if (image)
				CFRelease(result);
			else
				objc_release(result);
		}
	}

	private static readonly object Gate = new();
	private static BlockDescriptor* _descriptor;
	private static nint _stackBlock;
	private static nint _framework;
	private static uint _cachedWindowId;
	private static nint _cachedWindow;
	private static bool _unavailable;

	public static bool IsAvailable
	{
		get
		{
			if (!OperatingSystem.IsMacOSVersionAtLeast(14))
				return false;
			lock (Gate)
				return EnsureLoaded();
		}
	}

	public static bool TryCapture(int windowNumber, int width, int height, ref byte[] buffer, out int capturedWidth, out int capturedHeight)
	{
		capturedWidth = 0;
		capturedHeight = 0;
		if (windowNumber <= 0 || width <= 0 || height <= 0 || !IsAvailable)
			return false;
		lock (Gate)
		{
			nint pool = objc_autoreleasePoolPush();
			try
			{
				nint window = FindWindow((uint)windowNumber);
				if (window == 0)
					return false;
				nint image = Screenshot(window, width, height);
				if (image == 0)
				{
					ForgetWindow();
					return false;
				}
				try
				{
					return CopyPixels(image, ref buffer, out capturedWidth, out capturedHeight);
				}
				finally
				{
					CFRelease(image);
				}
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
		}
	}

	private static nint FindWindow(uint windowId)
	{
		if (_cachedWindow != 0 && _cachedWindowId == windowId)
			return _cachedWindow;
		ForgetWindow();
		nint content = Send(objc_getClass("SCShareableContent"), sel_registerName("class"));
		if (content == 0)
			return 0;
		CaptureRequest request = new(windowId, false);
		nint block = CreateBlock(request, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnContent);
		try
		{
			SendBoolBoolBlock(content, sel_registerName("getShareableContentExcludingDesktopWindows:onScreenWindowsOnly:completionHandler:"), true, true, block);
			_cachedWindow = request.Wait(TimeSpan.FromSeconds(3));
		}
		finally
		{
			BlockRelease(block);
		}
		_cachedWindowId = windowId;
		return _cachedWindow;
	}

	public static void ReleaseWindow()
	{
		if (!OperatingSystem.IsMacOS())
			return;
		lock (Gate)
			ForgetWindow();
	}

	private static void ForgetWindow()
	{
		if (_cachedWindow != 0)
			objc_release(_cachedWindow);
		_cachedWindow = 0;
		_cachedWindowId = 0;
	}

	private static nint Screenshot(nint window, int width, int height)
	{
		nint filter = SendObject(Send(objc_getClass("SCContentFilter"), sel_registerName("alloc")), sel_registerName("initWithDesktopIndependentWindow:"), window);
		nint config = Send(Send(objc_getClass("SCStreamConfiguration"), sel_registerName("alloc")), sel_registerName("init"));
		if (filter == 0 || config == 0)
		{
			if (filter != 0)
				objc_release(filter);
			if (config != 0)
				objc_release(config);
			return 0;
		}
		try
		{
			SendNUInt(config, sel_registerName("setWidth:"), (nuint)width);
			SendNUInt(config, sel_registerName("setHeight:"), (nuint)height);
			SendUInt(config, sel_registerName("setPixelFormat:"), PixelFormatBgra);
			SendBool(config, sel_registerName("setShowsCursor:"), false);
			CaptureRequest request = new(0, true);
			nint block = CreateBlock(request, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnImage);
			try
			{
				SendObjectObjectBlock(objc_getClass("SCScreenshotManager"), sel_registerName("captureImageWithFilter:configuration:completionHandler:"), filter, config, block);
				return request.Wait(TimeSpan.FromSeconds(2));
			}
			finally
			{
				BlockRelease(block);
			}
		}
		finally
		{
			objc_release(filter);
			objc_release(config);
		}
	}

	[UnmanagedCallersOnly]
	private static void OnContent(nint block, nint content, nint error)
	{
		CaptureRequest request = (CaptureRequest)GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Target!;
		nint result = 0;
		try
		{
			if (content == 0)
				return;
			nint windows = Send(content, sel_registerName("windows"));
			nint count = windows == 0 ? 0 : (nint)SendReturnsNUInt(windows, sel_registerName("count"));
			for (nint index = 0; index < count; index++)
			{
				nint candidate = SendIndex(windows, sel_registerName("objectAtIndex:"), (nuint)index);
				if (candidate != 0 && SendReturnsUInt(candidate, sel_registerName("windowID")) == request.WindowId)
				{
					result = objc_retain(candidate);
					return;
				}
			}
		}
		finally
		{
			request.Complete(result);
		}
	}

	[UnmanagedCallersOnly]
	private static void OnImage(nint block, nint image, nint error)
	{
		CaptureRequest request = (CaptureRequest)GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Target!;
		request.Complete(image != 0 ? CFRetain(image) : 0);
	}

	private static bool CopyPixels(nint image, ref byte[] buffer, out int width, out int height)
	{
		width = 0;
		height = 0;
		int imageWidth = (int)CGImageGetWidth(image);
		int imageHeight = (int)CGImageGetHeight(image);
		int stride = (int)CGImageGetBytesPerRow(image);
		if (imageWidth <= 0 || imageHeight <= 0 || CGImageGetBitsPerPixel(image) != 32 || stride < imageWidth * 4)
			return false;
		nint data = CGDataProviderCopyData(CGImageGetDataProvider(image));
		if (data == 0)
			return false;
		try
		{
			nint source = CFDataGetBytePtr(data);
			if (source == 0 || (long)CFDataGetLength(data) < (long)stride * imageHeight)
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
			CFRelease(data);
		}
	}

	private static bool EnsureLoaded()
	{
		if (_unavailable)
			return false;
		if (_framework != 0)
			return true;
		try
		{
			_framework = NativeLibrary.Load("/System/Library/Frameworks/ScreenCaptureKit.framework/ScreenCaptureKit");
			nint system = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
			_stackBlock = NativeLibrary.GetExport(system, "_NSConcreteStackBlock");
			_descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
			_descriptor->Size = (nuint)sizeof(BlockLiteral);
			_descriptor->Copy = (nint)(delegate* unmanaged<nint, nint, void>)&CopyBlock;
			_descriptor->Dispose = (nint)(delegate* unmanaged<nint, void>)&DisposeBlock;
			return true;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			_unavailable = true;
			return false;
		}
	}

	private static nint CreateBlock(CaptureRequest request, nint invoke)
	{
		GCHandle handle = GCHandle.Alloc(request);
		BlockLiteral block = new()
		{
			Isa = _stackBlock,
			Flags = BlockHasCopyDispose,
			Invoke = invoke,
			Descriptor = _descriptor,
			Context = GCHandle.ToIntPtr(handle)
		};
		nint copied = BlockCopy((nint)(&block));
		if (copied != 0)
			return copied;
		handle.Free();
		throw new InvalidOperationException("The screen capture callback could not be allocated");
	}

	[UnmanagedCallersOnly]
	private static void CopyBlock(nint destination, nint source)
	{
	}

	[UnmanagedCallersOnly]
	private static void DisposeBlock(nint block) => GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Free();

	[LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")]
	private static partial nint BlockCopy(nint block);

	[LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")]
	private static partial void BlockRelease(nint block);

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
	private static partial nint CFRetain(nint value);

	[LibraryImport(CoreFoundation)]
	private static partial void CFRelease(nint value);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_getClass(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC)]
	private static partial nint objc_retain(nint value);

	[LibraryImport(ObjectiveC)]
	private static partial void objc_release(nint value);

	[LibraryImport(ObjectiveC)]
	private static partial nint objc_autoreleasePoolPush();

	[LibraryImport(ObjectiveC)]
	private static partial void objc_autoreleasePoolPop(nint pool);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendObject(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendIndex(nint receiver, nint selector, nuint index);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nuint SendReturnsNUInt(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial uint SendReturnsUInt(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendNUInt(nint receiver, nint selector, nuint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendUInt(nint receiver, nint selector, uint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendBoolBoolBlock(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool first, [MarshalAs(UnmanagedType.I1)] bool second, nint block);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendObjectObjectBlock(nint receiver, nint selector, nint first, nint second, nint block);
}
