using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public readonly record struct MacOSScreenWindow(int Number, double Left, double Top, double Width, double Height);

public static partial class MacOSOverlayWindow
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	private const nint StatusWindowLevel = 25;
	private const nuint CanJoinAllSpaces = 1 << 0;
	private const nuint Stationary = 1 << 4;
	private const nuint IgnoresCycle = 1 << 6;
	private const nuint FullScreenAuxiliary = 1 << 8;
	private const uint OnScreenOnly = 1 << 0;
	private const uint ExcludeDesktopElements = 1 << 4;
	private const int NumberSInt32Type = 3;
	private static readonly object Gate = new();
	private static nint _appKit;
	private static nint _ownerPidKey;
	private static nint _layerKey;
	private static nint _boundsKey;
	private static nint _numberKey;

	public static void Configure(nint window, bool clickThrough)
	{
		if (!OperatingSystem.IsMacOS() || window == 0)
			return;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			EnsureAppKit();
			SendLong(window, sel_registerName("setLevel:"), StatusWindowLevel);
			SendULong(window, sel_registerName("setCollectionBehavior:"), CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
			SendBool(window, sel_registerName("setIgnoresMouseEvents:"), clickThrough);
			SendBool(window, sel_registerName("setOpaque:"), false);
			SendBool(window, sel_registerName("setHasShadow:"), false);
			SendBool(window, sel_registerName("setHidesOnDeactivate:"), false);
			nint clear = Send(objc_getClass("NSColor"), sel_registerName("clearColor"));
			if (clear != 0)
				SendObject(window, sel_registerName("setBackgroundColor:"), clear);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static void SetClickThrough(nint window, bool clickThrough)
	{
		if (OperatingSystem.IsMacOS() && window != 0)
			SendBool(window, sel_registerName("setIgnoresMouseEvents:"), clickThrough);
	}

	public static void ShowWithoutActivating(nint window)
	{
		if (OperatingSystem.IsMacOS() && window != 0)
			Send(window, sel_registerName("orderFrontRegardless"));
	}

	public static void Hide(nint window)
	{
		if (OperatingSystem.IsMacOS() && window != 0)
			SendObject(window, sel_registerName("orderOut:"), 0);
	}

	public static void MoveTo(nint window, double left, double top, double width, double height)
	{
		if (!OperatingSystem.IsMacOS() || window == 0 || width <= 0 || height <= 0)
			return;
		double primaryHeight = MacOSWindow.PrimaryScreenHeight();
		if (primaryHeight <= 0)
			return;
		MacOSWindow.SetFrame(window, new MacOSWindow.Rect(left, primaryHeight - top - height, width, height), false);
	}

	public static bool ActivateProcess(int processId)
	{
		if (!OperatingSystem.IsMacOS() || processId <= 0)
			return false;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			EnsureAppKit();
			nint app = SendInt(objc_getClass("NSRunningApplication"), sel_registerName("runningApplicationWithProcessIdentifier:"), processId);
			return app != 0 && SendULongReturnsBool(app, sel_registerName("activateWithOptions:"), 0);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static MacOSScreenWindow? FindLargestWindow(int processId)
	{
		if (!OperatingSystem.IsMacOS() || processId <= 0)
			return null;
		lock (Gate)
		{
			if (!EnsureWindowKeys())
				return null;
			nint list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
			if (list == 0)
				return null;
			try
			{
				MacOSScreenWindow? best = null;
				nint count = CFArrayGetCount(list);
				for (nint index = 0; index < count; index++)
				{
					nint info = CFArrayGetValueAtIndex(list, index);
					if (info == 0 || ReadInt(info, _ownerPidKey) != processId || ReadInt(info, _layerKey) != 0)
						continue;
					nint boundsValue = CFDictionaryGetValue(info, _boundsKey);
					if (boundsValue == 0 || !CGRectMakeWithDictionaryRepresentation(boundsValue, out MacOSWindow.Rect bounds) || bounds.Width < 64 || bounds.Height < 64)
						continue;
					if (best is null || bounds.Width * bounds.Height > best.Value.Width * best.Value.Height)
						best = new MacOSScreenWindow(ReadInt(info, _numberKey), bounds.X, bounds.Y, bounds.Width, bounds.Height);
				}
				return best;
			}
			finally
			{
				CFRelease(list);
			}
		}
	}

	public static int FrontmostWindowOwner()
	{
		if (!OperatingSystem.IsMacOS())
			return 0;
		lock (Gate)
		{
			if (!EnsureWindowKeys())
				return 0;
			nint list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
			if (list == 0)
				return 0;
			try
			{
				nint count = CFArrayGetCount(list);
				for (nint index = 0; index < count; index++)
				{
					nint info = CFArrayGetValueAtIndex(list, index);
					if (info != 0 && ReadInt(info, _layerKey) == 0)
						return ReadInt(info, _ownerPidKey);
				}
				return 0;
			}
			finally
			{
				CFRelease(list);
			}
		}
	}

	private static int ReadInt(nint dictionary, nint key)
	{
		nint number = CFDictionaryGetValue(dictionary, key);
		return number != 0 && CFNumberGetValue(number, NumberSInt32Type, out int value) ? value : int.MinValue;
	}

	private static bool EnsureWindowKeys()
	{
		if (_boundsKey != 0)
			return true;
		try
		{
			nint graphics = NativeLibrary.Load(CoreGraphics);
			_ownerPidKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(graphics, "kCGWindowOwnerPID"));
			_layerKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(graphics, "kCGWindowLayer"));
			_numberKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(graphics, "kCGWindowNumber"));
			_boundsKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(graphics, "kCGWindowBounds"));
			return _boundsKey != 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			return false;
		}
	}

	private static void EnsureAppKit()
	{
		if (_appKit == 0)
			_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
	}

	[LibraryImport(CoreGraphics)]
	private static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

	[LibraryImport(CoreGraphics)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool CGRectMakeWithDictionaryRepresentation(nint dictionary, out MacOSWindow.Rect rect);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFArrayGetCount(nint array);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFDictionaryGetValue(nint dictionary, nint key);

	[LibraryImport(CoreFoundation)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool CFNumberGetValue(nint number, int type, out int value);

	[LibraryImport(CoreFoundation)]
	private static partial void CFRelease(nint value);

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
	private static partial nint SendInt(nint receiver, nint selector, int argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendLong(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendULong(nint receiver, nint selector, nuint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendULongReturnsBool(nint receiver, nint selector, nuint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);
}
