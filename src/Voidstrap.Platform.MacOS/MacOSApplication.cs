using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSApplication
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static nint _appKit;

	public static bool Activate()
	{
		if (!OperatingSystem.IsMacOS())
			return false;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			if (_appKit == 0)
				_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			nint application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
			if (application == 0)
				return false;
			SendBool(application, sel_registerName("activateIgnoringOtherApps:"), true);
			nint windows = Send(application, sel_registerName("windows"));
			nint count = windows == 0 ? 0 : Send(windows, sel_registerName("count"));
			for (nint index = 0; index < count; index++)
			{
				nint window = SendIndex(windows, sel_registerName("objectAtIndex:"), (nuint)index);
				if (window == 0 || !SendReturnsBool(window, sel_registerName("isVisible")) || !SendReturnsBool(window, sel_registerName("canBecomeKeyWindow")))
					continue;
				SendObject(window, sel_registerName("makeKeyAndOrderFront:"), 0);
				break;
			}
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

	public static int ApplyWindowShadows()
	{
		if (!OperatingSystem.IsMacOS())
			return 0;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			if (_appKit == 0)
				_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			nint application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
			nint windows = application == 0 ? 0 : Send(application, sel_registerName("windows"));
			if (windows == 0)
				return 0;
			int applied = 0;
			nint count = Send(windows, sel_registerName("count"));
			for (nint index = 0; index < count; index++)
			{
				nint window = SendIndex(windows, sel_registerName("objectAtIndex:"), (nuint)index);
				if (window == 0 || !SendReturnsBool(window, sel_registerName("isVisible")))
					continue;
				SendBool(window, sel_registerName("setHasShadow:"), true);
				Send(window, sel_registerName("invalidateShadow"));
				applied++;
			}
			return applied;
		}
		catch
		{
			return 0;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

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
	private static partial nint SendIndex(nint receiver, nint selector, nuint index);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendReturnsBool(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
}
