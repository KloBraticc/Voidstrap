using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSApplication
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static nint _appKit;

	public static string Describe()
	{
		if (!OperatingSystem.IsMacOS())
			return "";
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
			if (application == 0)
				return "no application";
			nint key = Send(application, sel_registerName("keyWindow"));
			nint windows = Send(application, sel_registerName("windows"));
			nint count = windows == 0 ? 0 : Send(windows, sel_registerName("count"));
			nint responder = key == 0 ? 0 : Send(key, sel_registerName("firstResponder"));
			nint content = key == 0 ? 0 : Send(key, sel_registerName("contentView"));
			string state = $"active={SendReturnsBool(application, sel_registerName("isActive"))} windows={count} key={(key == 0 ? "none" : "set")} responder={(responder == 0 ? "none" : responder == content ? "content" : responder == key ? "window" : "other")}";
			for (nint index = 0; index < count; index++)
			{
				nint window = SendIndex(windows, sel_registerName("objectAtIndex:"), (nuint)index);
				if (window == 0)
					continue;
				NSRect frame = Frame(window);
				state += $" [{index}: {frame.X:0},{frame.Y:0} {frame.Width:0}x{frame.Height:0} visible={SendReturnsBool(window, sel_registerName("isVisible"))} mini={SendReturnsBool(window, sel_registerName("isMiniaturized"))} zoomed={SendReturnsBool(window, sel_registerName("isZoomed"))}]";
			}
			return state;
		}
		catch (Exception ex)
		{
			return ex.Message;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

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
				nint content = Send(window, sel_registerName("contentView"));
				if (content != 0)
					SendObject(window, sel_registerName("makeFirstResponder:"), content);
				break;
			}
			return Send(application, sel_registerName("keyWindow")) != 0;
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

	public static bool SetDockIcon(string? pngPath)
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
			nint image = 0;
			if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
			{
				nint path = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), pngPath);
				image = SendObject(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithContentsOfFile:"), path);
				if (image == 0)
					return false;
				Send(image, sel_registerName("autorelease"));
			}
			SendObject(application, sel_registerName("setApplicationIconImage:"), image);
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

	public static bool SetDockVisible(bool visible)
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
			bool changed = SendPolicy(application, sel_registerName("setActivationPolicy:"), visible ? 0 : 1);
			if (changed && visible)
				SendBool(application, sel_registerName("activateIgnoringOtherApps:"), true);
			return changed;
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

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendPolicy(nint receiver, nint selector, nint policy);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC)]
	private static partial nint objc_autoreleasePoolPush();

	[LibraryImport(ObjectiveC)]
	private static partial void objc_autoreleasePoolPop(nint pool);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[StructLayout(LayoutKind.Sequential)]
	private struct NSRect
	{
		public double X;
		public double Y;
		public double Width;
		public double Height;
	}

	private static NSRect Frame(nint window)
	{
		if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
			return SendFrame(window, sel_registerName("frame"));
		SendFrameStret(out NSRect frame, window, sel_registerName("frame"));
		return frame;
	}

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial NSRect SendFrame(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend_stret")]
	private static partial void SendFrameStret(out NSRect frame, nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint SendString(nint receiver, nint selector, string argument);

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
