using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSUrlEvents
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const uint GetUrlEvent = 0x4755524C;
	private static nint _appKit;
	private static nint _manager;
	private static nint _notifications;
	private static nint _receiver;
	private static Action<string>? _received;

	public static unsafe void Install(Action<string> received)
	{
		if (!OperatingSystem.IsMacOS() || _receiver != 0)
			return;

		_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
		try
		{
			nint receiverClass = objc_getClass("VoidstrapUrlReceiver");
			if (receiverClass == 0)
			{
				receiverClass = objc_allocateClassPair(objc_getClass("NSObject"), "VoidstrapUrlReceiver", 0);
				if (receiverClass == 0)
					throw new InvalidOperationException("The macOS URL receiver could not be created");
				if (!class_addMethod(receiverClass, sel_registerName("receiveUrl:reply:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&ReceiveUrl, "v@:@@") ||
					!class_addMethod(receiverClass, sel_registerName("willFinishLaunching:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WillFinishLaunching, "v@:@"))
				{
					objc_disposeClassPair(receiverClass);
					throw new InvalidOperationException("The macOS URL receiver could not be configured");
				}
				objc_registerClassPair(receiverClass);
			}

			_received = received;
			_receiver = Send(Send(receiverClass, sel_registerName("alloc")), sel_registerName("init"));
			_manager = Send(objc_getClass("NSAppleEventManager"), sel_registerName("sharedAppleEventManager"));
			if (_receiver == 0 || _manager == 0)
				throw new InvalidOperationException("The macOS URL event manager is unavailable");
			_notifications = Send(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
			nint notification = Marshal.ReadIntPtr(NativeLibrary.GetExport(_appKit, "NSApplicationWillFinishLaunchingNotification"));
			AddObserver(_notifications, sel_registerName("addObserver:selector:name:object:"), _receiver, sel_registerName("willFinishLaunching:"), notification, 0);
			BindHandler();
		}
		catch
		{
			Shutdown();
			throw;
		}
	}

	private static void BindHandler() => SetHandler(_manager, sel_registerName("setEventHandler:andSelector:forEventClass:andEventID:"), _receiver, sel_registerName("receiveUrl:reply:"), GetUrlEvent, GetUrlEvent);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void WillFinishLaunching(nint receiver, nint selector, nint notification)
	{
		try
		{
			BindHandler();
		}
		catch
		{
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void ReceiveUrl(nint receiver, nint selector, nint descriptor, nint reply)
	{
		try
		{
			nint parameter = SendKeyword(descriptor, sel_registerName("paramDescriptorForKeyword:"), 0x2D2D2D2D);
			nint value = Send(parameter, sel_registerName("stringValue"));
			nint length = Send(value, sel_registerName("length"));
			if (length <= 0 || length > 32768)
				return;
			string? url = Marshal.PtrToStringUTF8(Send(value, sel_registerName("UTF8String")));
			if (!string.IsNullOrWhiteSpace(url))
				_received?.Invoke(url);
		}
		catch
		{
		}
	}

	public static void Shutdown()
	{
		if (_notifications != 0)
		{
			RemoveObserver(_notifications, sel_registerName("removeObserver:"), _receiver);
			_notifications = 0;
		}
		if (_manager != 0)
		{
			RemoveHandler(_manager, sel_registerName("removeEventHandlerForEventClass:andEventID:"), GetUrlEvent, GetUrlEvent);
			_manager = 0;
		}
		if (_receiver != 0)
		{
			objc_release(_receiver);
			_receiver = 0;
		}
		_received = null;
		if (_appKit != 0)
		{
			NativeLibrary.Free(_appKit);
			_appKit = 0;
		}
	}

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_getClass(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool class_addMethod(nint cls, nint name, nint implementation, string types);

	[LibraryImport(ObjectiveC)]
	private static partial void objc_disposeClassPair(nint cls);

	[LibraryImport(ObjectiveC)]
	private static partial void objc_registerClassPair(nint cls);

	[LibraryImport(ObjectiveC)]
	private static partial void objc_release(nint receiver);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendKeyword(nint receiver, nint selector, uint keyword);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetHandler(nint receiver, nint selector, nint handler, nint handlerSelector, uint eventClass, uint eventId);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void AddObserver(nint receiver, nint selector, nint observer, nint observerSelector, nint name, nint sender);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void RemoveObserver(nint receiver, nint selector, nint observer);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void RemoveHandler(nint receiver, nint selector, uint eventClass, uint eventId);
}
