using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed partial class MacOSWindowBackdrop : IDisposable
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private nint _window;
	private nint _view;
	private nint _background;
	private bool _opaque;
	private bool _disposed;

	public MacOSWindowBackdrop(nint window)
	{
		if (!OperatingSystem.IsMacOS() || window == 0 || !GetBool(objc_getClass("NSThread"), Selector("isMainThread")))
			throw new InvalidOperationException("A backdrop requires a native window on the main thread");
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint content = Send(window, Selector("contentView"));
			nint parent = content == 0 ? 0 : Send(content, Selector("superview"));
			if (parent == 0)
				throw new InvalidOperationException("The window content has no native container");
			_window = Send(window, Selector("retain"));
			_opaque = GetBool(window, Selector("isOpaque"));
			_background = Send(Send(window, Selector("backgroundColor")), Selector("retain"));
			_view = SendRect(Send(objc_getClass("NSVisualEffectView"), Selector("alloc")), Selector("initWithFrame:"), MacOSWindow.GetFrameForView(content));
			if (_view == 0)
				throw new InvalidOperationException("The native backdrop could not be created");
			SetInteger(_view, Selector("setAutoresizingMask:"), 2 | 16);
			SetInteger(_view, Selector("setBlendingMode:"), 0);
			SetInteger(_view, Selector("setState:"), 0);
			AddSubview(parent, Selector("addSubview:positioned:relativeTo:"), _view, -1, content);
			SetBool(window, Selector("setOpaque:"), false);
			SetObject(window, Selector("setBackgroundColor:"), Send(objc_getClass("NSColor"), Selector("clearColor")));
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

	public void Apply(int material, bool dark)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		nint pool = objc_autoreleasePoolPush();
		try
		{
			SetInteger(_view, Selector("setMaterial:"), material);
			nint name = CreateString(objc_getClass("NSString"), Selector("stringWithUTF8String:"), dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
			nint appearance = SendObject(objc_getClass("NSAppearance"), Selector("appearanceNamed:"), name);
			SetObject(_view, Selector("setAppearance:"), appearance);
			SetBool(_view, Selector("setNeedsDisplay:"), true);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (_view != 0)
		{
			Send(_view, Selector("removeFromSuperview"));
			Send(_view, Selector("release"));
			_view = 0;
		}
		if (_window != 0)
		{
			SetBool(_window, Selector("setOpaque:"), _opaque);
			SetObject(_window, Selector("setBackgroundColor:"), _background);
			Send(_window, Selector("release"));
			_window = 0;
		}
		if (_background != 0)
		{
			Send(_background, Selector("release"));
			_background = 0;
		}
		GC.SuppressFinalize(this);
	}

	private static nint Selector(string name) => sel_registerName(name);

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
	private static partial void SetObject(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetInteger(nint receiver, nint selector, nint value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool GetBool(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendRect(nint receiver, nint selector, MacOSWindow.Rect frame);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void AddSubview(nint receiver, nint selector, nint view, nint position, nint relative);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint CreateString(nint receiver, nint selector, string text);
}
