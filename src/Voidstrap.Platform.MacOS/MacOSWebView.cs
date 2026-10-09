using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed unsafe partial class MacOSWebView : IDisposable
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const string HandlerName = "voidstrap";
	private const nint PolicyCancel = 0;
	private const nint PolicyAllow = 1;

	private static readonly Dictionary<nint, MacOSWebView> Views = [];
	private static nint _delegateClass;

	private readonly Func<string, bool> _isAllowed;
	private nint _view;
	private nint _delegate;
	private nint _controller;
	private bool _disposed;

	public event Action<string>? MessageReceived;

	public event Action? NavigationFinished;

	public MacOSWebView(nint window, string file, string folder, IEnumerable<string> scripts, double cornerRadius, Func<string, bool> isAllowed)
	{
		if (!OperatingSystem.IsMacOS() || window == 0)
			throw new InvalidOperationException("A web panel requires a native window");
		_isAllowed = isAllowed;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");
			nint content = Send(window, Sel("contentView"));
			if (content == 0 || !EnsureDelegateClass())
				throw new InvalidOperationException("The web panel could not be prepared");
			_delegate = Send(Send(_delegateClass, Sel("alloc")), Sel("init"));
			Views[_delegate] = this;

			nint configuration = Send(Send(objc_getClass("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
			_controller = Send(Send(configuration, Sel("userContentController")), Sel("retain"));
			foreach (string source in scripts)
			{
				nint script = CreateUserScript(Send(objc_getClass("WKUserScript"), Sel("alloc")), Sel("initWithSource:injectionTime:forMainFrameOnly:"), NSString(source), 0, true);
				SendObject(_controller, Sel("addUserScript:"), script);
				Send(script, Sel("release"));
			}
			SendTwoObjects(_controller, Sel("addScriptMessageHandler:name:"), _delegate, NSString(HandlerName));
			nint preferences = Send(configuration, Sel("preferences"));
			SetBool(preferences, Sel("setJavaScriptCanOpenWindowsAutomatically:"), false);

			_view = SendRectObject(Send(objc_getClass("WKWebView"), Sel("alloc")), Sel("initWithFrame:configuration:"), new MacOSWindow.Rect(0, 0, 1, 1), configuration);
			Send(configuration, Sel("release"));
			if (_view == 0)
				throw new InvalidOperationException("The web panel could not be created");
			SendTwoObjects(_view, Sel("setValue:forKey:"), SendBoolResult(objc_getClass("NSNumber"), Sel("numberWithBool:"), false), NSString("drawsBackground"));
			if (RespondsTo(_view, "setUnderPageBackgroundColor:"))
				SendObject(_view, Sel("setUnderPageBackgroundColor:"), Send(objc_getClass("NSColor"), Sel("clearColor")));
			SendObject(_view, Sel("setNavigationDelegate:"), _delegate);
			SetBool(_view, Sel("setHidden:"), true);
			if (cornerRadius > 0)
			{
				SetBool(_view, Sel("setWantsLayer:"), true);
				nint layer = Send(_view, Sel("layer"));
				if (layer != 0)
				{
					SetDouble(layer, Sel("setCornerRadius:"), cornerRadius);
					SetBool(layer, Sel("setMasksToBounds:"), true);
				}
			}
			SendObject(content, Sel("addSubview:"), _view);

			nint fileUrl = SendObject(objc_getClass("NSURL"), Sel("fileURLWithPath:"), NSString(file));
			nint folderUrl = SendObjectBool(objc_getClass("NSURL"), Sel("fileURLWithPath:isDirectory:"), NSString(folder), true);
			SendTwoObjects(_view, Sel("loadFileURL:allowingReadAccessToURL:"), fileUrl, folderUrl);
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

	public void SetFrame(double x, double y, double width, double height, bool visible)
	{
		if (_disposed || _view == 0)
			return;
		nint parent = Send(_view, Sel("superview"));
		if (parent == 0)
			return;
		double top = y;
		if (!SendReturnsBool(parent, Sel("isFlipped")))
			top = MacOSWindow.GetFrameForView(parent).Height - y - height;
		SetRect(_view, Sel("setFrame:"), new MacOSWindow.Rect(x, top, Math.Max(1, width), Math.Max(1, height)));
		SetBool(_view, Sel("setHidden:"), !visible || width < 1 || height < 1);
	}

	public void Evaluate(string script)
	{
		if (_disposed || _view == 0 || string.IsNullOrEmpty(script))
			return;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			SendTwoObjects(_view, Sel("evaluateJavaScript:completionHandler:"), NSString(script), 0);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static bool IsLeftMouseDown()
	{
		return OperatingSystem.IsMacOS() && (Send(objc_getClass("NSEvent"), Sel("pressedMouseButtons")) & 1) != 0;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (_controller != 0)
		{
			SendObject(_controller, Sel("removeScriptMessageHandlerForName:"), NSString(HandlerName));
			Send(_controller, Sel("release"));
			_controller = 0;
		}
		if (_view != 0)
		{
			SendObject(_view, Sel("setNavigationDelegate:"), 0);
			Send(_view, Sel("stopLoading"));
			Send(_view, Sel("removeFromSuperview"));
			Send(_view, Sel("release"));
			_view = 0;
		}
		if (_delegate != 0)
		{
			Views.Remove(_delegate);
			Send(_delegate, Sel("release"));
			_delegate = 0;
		}
		MessageReceived = null;
		NavigationFinished = null;
		GC.SuppressFinalize(this);
	}

	private static bool EnsureDelegateClass()
	{
		if (_delegateClass != 0)
			return true;
		_delegateClass = objc_getClass("VoidstrapWebPanelDelegate");
		if (_delegateClass != 0)
			return true;
		nint created = objc_allocateClassPair(objc_getClass("NSObject"), "VoidstrapWebPanelDelegate", 0);
		if (created == 0)
			return false;
		class_addMethod(created, Sel("userContentController:didReceiveScriptMessage:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&OnScriptMessage, "v@:@@");
		class_addMethod(created, Sel("webView:decidePolicyForNavigationAction:decisionHandler:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&OnDecidePolicy, "v@:@@@?");
		class_addMethod(created, Sel("webView:didFinishNavigation:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&OnFinished, "v@:@@");
		objc_registerClassPair(created);
		_delegateClass = created;
		return true;
	}

	[UnmanagedCallersOnly]
	private static void OnScriptMessage(nint self, nint selector, nint controller, nint message)
	{
		try
		{
			if (!Views.TryGetValue(self, out MacOSWebView? view))
				return;
			nint body = Send(message, Sel("body"));
			if (body == 0 || !IsKind(body, "NSString"))
				return;
			string? text = Marshal.PtrToStringUTF8(Send(body, Sel("UTF8String")));
			if (!string.IsNullOrEmpty(text))
				view.MessageReceived?.Invoke(text);
		}
		catch (Exception)
		{
		}
	}

	[UnmanagedCallersOnly]
	private static void OnDecidePolicy(nint self, nint selector, nint webView, nint action, nint handler)
	{
		nint policy = PolicyCancel;
		try
		{
			if (Views.TryGetValue(self, out MacOSWebView? view))
			{
				nint url = Send(Send(action, Sel("request")), Sel("URL"));
				string? address = url == 0 ? null : Marshal.PtrToStringUTF8(Send(Send(url, Sel("absoluteString")), Sel("UTF8String")));
				if (!string.IsNullOrEmpty(address) && view._isAllowed(address))
					policy = PolicyAllow;
			}
		}
		catch (Exception)
		{
			policy = PolicyCancel;
		}
		if (handler != 0)
			((delegate* unmanaged<nint, nint, void>)(*(nint*)(handler + 16)))(handler, policy);
	}

	[UnmanagedCallersOnly]
	private static void OnFinished(nint self, nint selector, nint webView, nint navigation)
	{
		try
		{
			if (Views.TryGetValue(self, out MacOSWebView? view))
				view.NavigationFinished?.Invoke();
		}
		catch (Exception)
		{
		}
	}

	private static bool IsKind(nint value, string className) => SendObjectReturnsBool(value, Sel("isKindOfClass:"), objc_getClass(className));

	private static bool RespondsTo(nint value, string selector) => SendObjectReturnsBool(value, Sel("respondsToSelector:"), Sel(selector));

	private static nint Sel(string name) => sel_registerName(name);

	private static nint NSString(string value) => SendString(objc_getClass("NSString"), Sel("stringWithUTF8String:"), value);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_getClass(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

	[LibraryImport(ObjectiveC)]
	private static partial void objc_registerClassPair(nint cls);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);

	[LibraryImport(ObjectiveC)]
	private static partial nint objc_autoreleasePoolPush();

	[LibraryImport(ObjectiveC)]
	private static partial void objc_autoreleasePoolPop(nint pool);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendObject(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendTwoObjects(nint receiver, nint selector, nint first, nint second);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendObjectBool(nint receiver, nint selector, nint argument, [MarshalAs(UnmanagedType.I1)] bool flag);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendBoolResult(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint CreateUserScript(nint receiver, nint selector, nint source, nint injectionTime, [MarshalAs(UnmanagedType.I1)] bool mainFrameOnly);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendRectObject(nint receiver, nint selector, MacOSWindow.Rect frame, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetRect(nint receiver, nint selector, MacOSWindow.Rect frame);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetDouble(nint receiver, nint selector, double value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendReturnsBool(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendObjectReturnsBool(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint SendString(nint receiver, nint selector, string text);
}
