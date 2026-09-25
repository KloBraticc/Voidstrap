using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxClassicKeys
{
	private const int KeyPress = 2;
	private const int GenericEvent = 35;
	private const int XIRawKeyPress = 13;
	private const int XIAllMasterDevices = 1;
	private const int GrabModeAsync = 1;
	private const int EventSize = 192;
	private const short PollIn = 1;
	private const int LockMask = 2;
	private const long PropertyChangeMask = 1L << 22;
	private const int Mod2Mask = 16;
	private static readonly uint[] GrabModifiers = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

	private const ulong KeysymEscape = 0xFF1B;

	private static readonly object Gate = new();
	private static nint _display;
	private static nint _root;
	private static int _escapeKeycode;
	private static bool _grabbed;
	private static Thread? _thread;
	private static CancellationTokenSource? _cancellation;
	private static Action? _escape;
	private static Action<ushort>? _keyDown;
	private static Action<string>? _log;

	public static void Start(Action escape, Action<ushort> keyDown, Action<string>? log = null)
	{
		if (!OperatingSystem.IsLinux())
			return;

		lock (Gate)
		{
			if (_thread is not null)
				return;

			_escape = escape;
			_keyDown = keyDown;
			_log = log;
			_cancellation = new CancellationTokenSource();
			_thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Voidstrap classic keys"
			};
			_thread.Start(_cancellation.Token);
		}
	}

	public static void Stop()
	{
		Thread? thread;
		lock (Gate)
		{
			thread = _thread;
			_thread = null;
			_cancellation?.Cancel();
			_cancellation?.Dispose();
			_cancellation = null;
			_escape = null;
			_keyDown = null;
		}

		thread?.Join(TimeSpan.FromSeconds(2));
	}

	public static bool Write(ushort virtualKey, bool down)
	{
		ulong keysym = ToKeysym(virtualKey);
		if (keysym == 0)
			return false;

		lock (Gate)
		{
			if (_display == 0)
				return false;

			try
			{
				int keycode = XKeysymToKeycode(_display, keysym);
				if (keycode == 0)
					return false;

				bool regrab = keysym == KeysymEscape && _grabbed;
				if (regrab)
					SetGrab(false);

				XTestFakeKeyEvent(_display, (uint)keycode, down ? 1 : 0, 0);
				_ = XSync(_display, 0);
				if (regrab)
					SetGrab(true);
				return true;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				_log?.Invoke("Key injection needs libXtst, classic topbar keys are unavailable: " + ex.Message);
				return false;
			}
		}
	}

	private static void Run(object? state)
	{
		CancellationToken token = state is CancellationToken value ? value : CancellationToken.None;
		nint maskBuffer = 0;
		nint eventBuffer = 0;
		try
		{
			LinuxWindowInterop.KeepIgnoringXErrors();
			lock (Gate)
			{
				_display = XOpenDisplay(0);
				if (_display == 0)
					return;
				_root = XDefaultRootWindow(_display);
				_escapeKeycode = XKeysymToKeycode(_display, KeysymEscape);
			}

			nint display = _display;
			int major = 2;
			int minor = 2;
			if (XQueryExtension(display, "XInputExtension", out int opcode, out _, out _) == 0
				|| XIQueryVersion(display, ref major, ref minor) != 0
				|| XTestQueryExtension(display, out _, out _, out _, out _) == 0)
			{
				_log?.Invoke("XInput2 or XTest is unavailable, classic topbar keys stay off");
				return;
			}

			maskBuffer = Marshal.AllocHGlobal(2);
			Marshal.WriteByte(maskBuffer, 0, 0);
			Marshal.WriteByte(maskBuffer, 1, (byte)(1 << (XIRawKeyPress & 7)));
			XIEventMask mask = new() { DeviceId = XIAllMasterDevices, MaskLength = 2, Mask = maskBuffer };
			lock (Gate)
			{
				if (XISelectEvents(display, _root, ref mask, 1) != 0)
					return;
				_ = XSelectInput(display, _root, PropertyChangeMask);
				_ = XFlush(display);
			}

			_log?.Invoke("Classic topbar keys are running for Sober on X11");
			eventBuffer = Marshal.AllocHGlobal(EventSize);
			PollDescriptor descriptor = new() { Descriptor = XConnectionNumber(display), Events = PollIn };
			while (!token.IsCancellationRequested)
			{
				descriptor.Returned = 0;
				_ = poll(ref descriptor, 1, 500);
				lock (Gate)
				{
					while (XPending(display) != 0)
						Dispatch(display, eventBuffer, opcode);
					UpdateGrab();
				}
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			_log?.Invoke("The X11 input libraries are missing, classic topbar keys stay off: " + ex.Message);
		}
		catch (Exception ex)
		{
			_log?.Invoke("Classic topbar keys stopped: " + ex.Message);
		}
		finally
		{
			lock (Gate)
			{
				try
				{
					if (_display != 0)
					{
						if (_grabbed)
							SetGrab(false);
						XCloseDisplay(_display);
					}
				}
				catch (Exception)
				{
				}

				_display = 0;
				_grabbed = false;
			}

			if (eventBuffer != 0)
				Marshal.FreeHGlobal(eventBuffer);
			if (maskBuffer != 0)
				Marshal.FreeHGlobal(maskBuffer);
		}
	}

	private static void Dispatch(nint display, nint eventBuffer, int opcode)
	{
		XNextEvent(display, eventBuffer);
		int type = Marshal.ReadInt32(eventBuffer);
		if (type == KeyPress)
		{
			if (Marshal.ReadInt32(eventBuffer, 84) == _escapeKeycode)
				Raise(_escape);
			return;
		}

		if (type != GenericEvent
			|| Marshal.ReadInt32(eventBuffer, 32) != opcode
			|| Marshal.ReadInt32(eventBuffer, 36) != XIRawKeyPress
			|| XGetEventData(display, eventBuffer) == 0)
			return;

		int keycode;
		try
		{
			nint data = Marshal.ReadIntPtr(eventBuffer, 48);
			if (data == 0)
				return;
			keycode = Marshal.ReadInt32(data, 56);
		}
		finally
		{
			XFreeEventData(display, eventBuffer);
		}

		if (keycode == _escapeKeycode)
			return;

		ushort virtualKey = ToVirtualKey(XkbKeycodeToKeysym(display, (byte)keycode, 0, 0));
		Action<ushort>? keyDown = _keyDown;
		if (virtualKey != 0 && keyDown is not null)
			Raise(() => keyDown(virtualKey));
	}

	private static void Raise(Action? action)
	{
		if (action is null)
			return;
		try
		{
			action();
		}
		catch (Exception ex)
		{
			_log?.Invoke("A classic topbar key could not be handled: " + ex.Message);
		}
	}

	private static void UpdateGrab()
	{
		bool wanted = _escapeKeycode != 0 && IsSoberFocused();
		if (wanted != _grabbed)
			SetGrab(wanted);
	}

	private static void SetGrab(bool grab)
	{
		foreach (uint modifiers in GrabModifiers)
		{
			if (grab)
				_ = XGrabKey(_display, _escapeKeycode, modifiers, _root, 0, GrabModeAsync, GrabModeAsync);
			else
				_ = XUngrabKey(_display, _escapeKeycode, modifiers, _root);
		}
		_ = XSync(_display, 0);
		_grabbed = grab;
	}

	private static bool IsSoberFocused()
	{
		nint active = LinuxWindowInterop.GetActiveTopLevelWindow();
		if (active != 0 && LinuxWindowInterop.IsSoberRuntimeWindow(active))
			return true;

		nint focused = LinuxWindowInterop.GetFocusedWindow();
		return focused != 0 && LinuxWindowInterop.IsSoberRuntimeWindow(focused);
	}

	private static ulong ToKeysym(ushort virtualKey)
	{
		return virtualKey switch
		{
			0x1B => KeysymEscape,
			0x09 => 0xFF09,
			0x0D => 0xFF0D,
			0xC0 => 0x0060,
			0xBF => 0x002F,
			>= 0x41 and <= 0x5A => (ulong)(virtualKey + 0x20),
			>= 0x70 and <= 0x7B => (ulong)(0xFFBE + virtualKey - 0x70),
			0x5B => 0xFFEB,
			0x2C => 0xFF61,
			_ => 0
		};
	}

	private static ushort ToVirtualKey(ulong keysym)
	{
		return keysym switch
		{
			0xFF0D => 0x0D,
			0x0060 => 0xC0,
			0x002F => 0xBF,
			>= 0xFFBE and <= 0xFFC9 => (ushort)(0x70 + keysym - 0xFFBE),
			_ => 0
		};
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XIEventMask
	{
		public int DeviceId;
		public int MaskLength;
		public nint Mask;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PollDescriptor
	{
		public int Descriptor;
		public short Events;
		public short Returned;
	}

	[LibraryImport("libX11.so.6")]
	private static partial nint XOpenDisplay(nint name);

	[LibraryImport("libX11.so.6")]
	private static partial int XCloseDisplay(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XDefaultRootWindow(nint display);

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int XQueryExtension(nint display, string name, out int majorOpcode, out int firstEvent, out int firstError);

	[LibraryImport("libX11.so.6")]
	private static partial int XFlush(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XSync(nint display, int discard);

	[LibraryImport("libX11.so.6")]
	private static partial int XPending(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XNextEvent(nint display, nint eventReturn);

	[LibraryImport("libX11.so.6")]
	private static partial int XConnectionNumber(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetEventData(nint display, nint cookie);

	[LibraryImport("libX11.so.6")]
	private static partial void XFreeEventData(nint display, nint cookie);

	[LibraryImport("libX11.so.6")]
	private static partial int XKeysymToKeycode(nint display, ulong keysym);

	[LibraryImport("libX11.so.6")]
	private static partial ulong XkbKeycodeToKeysym(nint display, byte keycode, int group, int level);

	[LibraryImport("libX11.so.6")]
	private static partial int XSelectInput(nint display, nint window, long eventMask);

	[LibraryImport("libX11.so.6")]
	private static partial int XGrabKey(nint display, int keycode, uint modifiers, nint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);

	[LibraryImport("libX11.so.6")]
	private static partial int XUngrabKey(nint display, int keycode, uint modifiers, nint grabWindow);

	[LibraryImport("libXi.so.6")]
	private static partial int XIQueryVersion(nint display, ref int major, ref int minor);

	[LibraryImport("libXi.so.6")]
	private static partial int XISelectEvents(nint display, nint window, ref XIEventMask masks, int count);

	[LibraryImport("libXtst.so.6")]
	private static partial int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);

	[LibraryImport("libXtst.so.6")]
	private static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, ulong delay);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int poll(ref PollDescriptor descriptors, nuint count, int timeout);
}
