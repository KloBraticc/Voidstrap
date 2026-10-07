using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSClipboard
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static readonly object Gate = new();
	private static nint _appKit;
	private static nint _stringType;

	public static bool IsAvailable
	{
		get
		{
			lock (Gate)
				return EnsureLoaded();
		}
	}

	public static long ChangeCount
	{
		get
		{
			lock (Gate)
			{
				if (!EnsureLoaded())
					return -1;
				nint pool = objc_autoreleasePoolPush();
				try
				{
					return Send(Pasteboard(), sel_registerName("changeCount"));
				}
				finally
				{
					objc_autoreleasePoolPop(pool);
				}
			}
		}
	}

	public static string? GetText()
	{
		lock (Gate)
		{
			if (!EnsureLoaded())
				return null;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				nint value = SendObject(Pasteboard(), sel_registerName("stringForType:"), _stringType);
				if (value == 0)
					return string.Empty;
				nint utf8 = Send(value, sel_registerName("UTF8String"));
				return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
			}
			catch
			{
				return null;
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
		}
	}

	public static bool SetText(string? text)
	{
		lock (Gate)
		{
			if (!EnsureLoaded())
				return false;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				nint pasteboard = Pasteboard();
				nint value = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), text ?? string.Empty);
				if (pasteboard == 0 || value == 0)
					return false;
				Send(pasteboard, sel_registerName("clearContents"));
				return SetString(pasteboard, sel_registerName("setString:forType:"), value, _stringType);
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
	}

	private static nint Pasteboard() => Send(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));

	private static bool EnsureLoaded()
	{
		if (!OperatingSystem.IsMacOS())
			return false;
		if (_stringType != 0)
			return true;
		try
		{
			if (_appKit == 0)
				_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			_stringType = Marshal.ReadIntPtr(NativeLibrary.GetExport(_appKit, "NSPasteboardTypeString"));
			return _stringType != 0;
		}
		catch
		{
			return false;
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

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint SendString(nint receiver, nint selector, string argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SetString(nint receiver, nint selector, nint value, nint type);
}
