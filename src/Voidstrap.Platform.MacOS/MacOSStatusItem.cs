using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed class MacOSStatusMenuItem
{
	public string Label { get; init; } = string.Empty;

	public bool Enabled { get; init; } = true;

	public bool IsSeparator { get; init; }

	public bool IsChecked { get; init; }

	public Action? Activated { get; init; }

	public List<MacOSStatusMenuItem> Children { get; } = [];
}

public static unsafe partial class MacOSStatusItem
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const double VariableLength = -1;
	private const double IconPoints = 18;

	private static readonly Dictionary<nint, Action> Actions = [];
	private static nint _targetClass;
	private static nint _target;
	private static nint _statusItem;
	private static nint _menu;
	private static nint _nextTag = 1;

	public static event Action? MenuOpening;

	public static bool IsVisible => _statusItem != 0;

	public static bool Show(string tooltip, string? iconPath)
	{
		if (!OperatingSystem.IsMacOS())
			return false;
		if (_statusItem != 0)
			return true;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			if (!EnsureTarget())
				return false;
			nint bar = Send(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
			nint item = SendDouble(bar, Sel("statusItemWithLength:"), VariableLength);
			if (item == 0)
				return false;
			_statusItem = Send(item, Sel("retain"));
			nint button = Send(_statusItem, Sel("button"));
			nint image = LoadImage(iconPath);
			if (button != 0)
			{
				if (image != 0)
					SendObject(button, Sel("setImage:"), image);
				else
					SendObject(button, Sel("setTitle:"), NSString("Voidstrap"));
				SendObject(button, Sel("setToolTip:"), NSString(tooltip));
			}
			_menu = Send(Send(objc_getClass("NSMenu"), Sel("alloc")), Sel("init"));
			SetBool(_menu, Sel("setAutoenablesItems:"), false);
			SendObject(_menu, Sel("setDelegate:"), _target);
			SendObject(_statusItem, Sel("setMenu:"), _menu);
			return true;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static void Hide()
	{
		if (_statusItem == 0)
			return;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint bar = Send(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
			SendObject(bar, Sel("removeStatusItem:"), _statusItem);
			Send(_statusItem, Sel("release"));
			if (_menu != 0)
				Send(_menu, Sel("release"));
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
		_statusItem = 0;
		_menu = 0;
		Actions.Clear();
	}

	public static void SetMenu(IReadOnlyList<MacOSStatusMenuItem> items)
	{
		if (_menu == 0)
			return;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			Actions.Clear();
			Fill(_menu, items);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	private static void Fill(nint menu, IReadOnlyList<MacOSStatusMenuItem> items)
	{
		Send(menu, Sel("removeAllItems"));
		foreach (MacOSStatusMenuItem entry in items)
		{
			if (entry.IsSeparator)
			{
				SendObject(menu, Sel("addItem:"), Send(objc_getClass("NSMenuItem"), Sel("separatorItem")));
				continue;
			}
			nint item = InitMenuItem(Send(objc_getClass("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"), NSString(entry.Label), entry.Children.Count == 0 ? Sel("itemClicked:") : 0, NSString(string.Empty));
			SetBool(item, Sel("setEnabled:"), entry.Enabled);
			if (entry.IsChecked)
				SetIndex(item, Sel("setState:"), 1);
			if (entry.Children.Count > 0)
			{
				nint submenu = Send(Send(objc_getClass("NSMenu"), Sel("alloc")), Sel("init"));
				SetBool(submenu, Sel("setAutoenablesItems:"), false);
				Fill(submenu, entry.Children);
				SendObject(item, Sel("setSubmenu:"), submenu);
				Send(submenu, Sel("release"));
			}
			else if (entry.Activated != null)
			{
				nint tag = _nextTag++;
				Actions[tag] = entry.Activated;
				SendObject(item, Sel("setTarget:"), _target);
				SetIndex(item, Sel("setTag:"), tag);
			}
			SendObject(menu, Sel("addItem:"), item);
			Send(item, Sel("release"));
		}
	}

	private static bool EnsureTarget()
	{
		if (_target != 0)
			return true;
		_targetClass = objc_getClass("VoidstrapStatusTarget");
		if (_targetClass == 0)
		{
			nint created = objc_allocateClassPair(objc_getClass("NSObject"), "VoidstrapStatusTarget", 0);
			if (created == 0)
				return false;
			class_addMethod(created, Sel("itemClicked:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnItemClicked, "v@:@");
			class_addMethod(created, Sel("menuWillOpen:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnMenuWillOpen, "v@:@");
			class_addMethod(created, Sel("menuDidClose:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnMenuWillOpen, "v@:@");
			objc_registerClassPair(created);
			_targetClass = created;
		}
		_target = Send(Send(_targetClass, Sel("alloc")), Sel("init"));
		return _target != 0;
	}

	[UnmanagedCallersOnly]
	private static void OnItemClicked(nint self, nint selector, nint sender)
	{
		try
		{
			nint tag = SendIndexResult(sender, Sel("tag"));
			if (Actions.TryGetValue(tag, out Action? action))
				action();
		}
		catch (Exception)
		{
		}
	}

	[UnmanagedCallersOnly]
	private static void OnMenuWillOpen(nint self, nint selector, nint menu)
	{
		try
		{
			MenuOpening?.Invoke();
		}
		catch (Exception)
		{
		}
	}

	private static nint LoadImage(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return 0;
		nint image = SendObject(Send(objc_getClass("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), NSString(path));
		if (image == 0)
			return 0;
		SetSize(image, Sel("setSize:"), new Size { Width = IconPoints, Height = IconPoints });
		return Send(image, Sel("autorelease"));
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Size
	{
		public double Width;
		public double Height;
	}

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

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint SendString(nint receiver, nint selector, string argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendDouble(nint receiver, nint selector, double argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendIndexResult(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetIndex(nint receiver, nint selector, nint value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetSize(nint receiver, nint selector, Size size);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint InitMenuItem(nint receiver, nint selector, nint title, nint action, nint keyEquivalent);
}
