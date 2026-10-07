using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSWindow
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const nuint MiniaturizableMask = 1 << 2;

	[StructLayout(LayoutKind.Sequential)]
	public struct Rect
	{
		public double X;
		public double Y;
		public double Width;
		public double Height;

		public Rect(double x, double y, double width, double height)
		{
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}

		public readonly bool Matches(Rect other) => Math.Abs(X - other.X) < 2 && Math.Abs(Y - other.Y) < 2 && Math.Abs(Width - other.Width) < 2 && Math.Abs(Height - other.Height) < 2;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public double X;
		public double Y;
	}

	public static void Minimize(nint window)
	{
		if (window == 0)
			return;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nuint mask = SendMask(window, sel_registerName("styleMask"));
			if ((mask & MiniaturizableMask) == 0)
				SetMask(window, sel_registerName("setStyleMask:"), mask | MiniaturizableMask);
			SendObject(window, sel_registerName("miniaturize:"), 0);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static bool IsMinimized(nint window) => window != 0 && SendReturnsBool(window, sel_registerName("isMiniaturized"));

	public static Rect GetFrame(nint window) => ReadRect(window, sel_registerName("frame"));

	public static Rect GetWorkArea(nint window)
	{
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint screen = Send(window, sel_registerName("screen"));
			if (screen == 0)
				screen = Send(objc_getClass("NSScreen"), sel_registerName("mainScreen"));
			return screen == 0 ? default : ReadRect(screen, sel_registerName("visibleFrame"));
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static void SetFrame(nint window, Rect frame, bool animate)
	{
		if (window == 0 || frame.Width <= 0 || frame.Height <= 0)
			return;
		SetFrameNative(window, sel_registerName("setFrame:display:animate:"), frame, true, animate);
	}

	public static (double X, double Y) MouseLocation()
	{
		Point point = SendPoint(objc_getClass("NSEvent"), sel_registerName("mouseLocation"));
		return (point.X, point.Y);
	}

	private static Rect ReadRect(nint receiver, nint selector)
	{
		if (receiver == 0)
			return default;
		if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
			return SendRect(receiver, selector);
		SendRectStret(out Rect rect, receiver, selector);
		return rect;
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
	private static partial nuint SendMask(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetMask(nint receiver, nint selector, nuint mask);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendReturnsBool(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial Rect SendRect(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend_stret")]
	private static partial void SendRectStret(out Rect rect, nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial Point SendPoint(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetFrameNative(nint receiver, nint selector, Rect frame, [MarshalAs(UnmanagedType.I1)] bool display, [MarshalAs(UnmanagedType.I1)] bool animate);
}
