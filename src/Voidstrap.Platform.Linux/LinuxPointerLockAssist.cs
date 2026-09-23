using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxPointerLockAssist
{
	private const int GenericEvent = 35;
	private const int XIAllMasterDevices = 1;
	private const int XIRawButtonPress = 15;
	private const int XIRawButtonRelease = 16;
	private const int XFixesCursorNotify = 1;
	private const ulong XFixesDisplayCursorNotifyMask = 1;
	private const int RightButton = 3;
	private const uint Button3Mask = 1 << 10;
	private const short PollIn = 1;
	private const int EventSize = 192;
	private const int LoggedEngagements = 3;

	private static readonly object Gate = new();
	private static CancellationTokenSource? _cancellation;
	private static Action<string>? _log;

	public static bool IsXWaylandSession => OperatingSystem.IsLinux()
		&& !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
		&& !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

	public static void SetActive(bool active, Action<string>? log = null)
	{
		lock (Gate)
		{
			if (!active)
			{
				_cancellation?.Cancel();
				_cancellation?.Dispose();
				_cancellation = null;
				return;
			}

			if (_cancellation is not null || !IsXWaylandSession)
				return;

			_log = log;
			_cancellation = new CancellationTokenSource();
			Thread thread = new(Run)
			{
				IsBackground = true,
				Name = "Voidstrap pointer lock assist"
			};
			thread.Start(_cancellation.Token);
		}
	}

	private sealed class Session
	{
		public nint Display;
		public nint Root;
		public bool RightHeld;
		public bool Hidden;
		public ulong CursorSerial = ulong.MaxValue;
		public bool CursorBlank;
		public int RightClickEngagements;
		public int HiddenCursorEngagements;
	}

	private static void Run(object? state)
	{
		CancellationToken token = state is CancellationToken value ? value : CancellationToken.None;
		Session session = new();
		nint maskBuffer = 0;
		nint eventBuffer = 0;
		try
		{
			session.Display = XOpenDisplay(0);
			if (session.Display == 0)
				return;

			nint display = session.Display;
			session.Root = XDefaultRootWindow(display);
			int major = 2;
			int minor = 2;
			if (XQueryExtension(display, "XInputExtension", out int opcode, out _, out _) == 0
				|| XIQueryVersion(display, ref major, ref minor) != 0
				|| XFixesQueryExtension(display, out int fixesEventBase, out _) == 0)
			{
				_log?.Invoke("XInput2 or XFixes is unavailable, the camera lock assist stays off");
				return;
			}

			maskBuffer = Marshal.AllocHGlobal(3);
			Marshal.WriteByte(maskBuffer, 0, 0);
			Marshal.WriteByte(maskBuffer, 1, (byte)(1 << (XIRawButtonPress & 7)));
			Marshal.WriteByte(maskBuffer, 2, (byte)(1 << (XIRawButtonRelease & 7)));
			XIEventMask mask = new() { DeviceId = XIAllMasterDevices, MaskLength = 3, Mask = maskBuffer };
			if (XISelectEvents(display, session.Root, ref mask, 1) != 0)
				return;
			XFixesSelectCursorInput(display, session.Root, XFixesDisplayCursorNotifyMask);
			XFlush(display);

			_log?.Invoke("Camera lock assist is running for Sober on XWayland");
			eventBuffer = Marshal.AllocHGlobal(EventSize);
			PollDescriptor descriptor = new() { Descriptor = XConnectionNumber(display), Events = PollIn };
			while (!token.IsCancellationRequested)
			{
				if (XPending(display) == 0)
				{
					descriptor.Returned = 0;
					_ = poll(ref descriptor, 1, 500);
					if (XPending(display) == 0)
					{
						if (session.RightHeld && !IsRightButtonDown(session))
							session.RightHeld = false;
						Evaluate(session);
					}
					continue;
				}

				XNextEvent(display, eventBuffer);
				int type = Marshal.ReadInt32(eventBuffer);
				if (type == fixesEventBase + XFixesCursorNotify)
				{
					Evaluate(session);
					continue;
				}

				if (type != GenericEvent
					|| Marshal.ReadInt32(eventBuffer, 32) != opcode
					|| XGetEventData(display, eventBuffer) == 0)
					continue;

				try
				{
					int eventType = Marshal.ReadInt32(eventBuffer, 36);
					nint data = Marshal.ReadIntPtr(eventBuffer, 48);
					if (data == 0 || Marshal.ReadInt32(data, 56) != RightButton)
						continue;

					if (eventType == XIRawButtonPress)
						session.RightHeld = true;
					else if (eventType == XIRawButtonRelease)
						session.RightHeld = false;
					else
						continue;
				}
				finally
				{
					XFreeEventData(display, eventBuffer);
				}

				Evaluate(session);
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			_log?.Invoke("The X11 input libraries are missing, the camera lock assist stays off: " + ex.Message);
		}
		catch (Exception ex)
		{
			_log?.Invoke("The camera lock assist stopped: " + ex.Message);
		}
		finally
		{
			try
			{
				if (session.Display != 0)
				{
					if (session.Hidden)
						SetHidden(session, false);
					XCloseDisplay(session.Display);
				}
			}
			catch (Exception)
			{
			}

			if (eventBuffer != 0)
				Marshal.FreeHGlobal(eventBuffer);
			if (maskBuffer != 0)
				Marshal.FreeHGlobal(maskBuffer);

			int total = session.RightClickEngagements + session.HiddenCursorEngagements;
			if (total > 0)
				_log?.Invoke("Camera lock assist stopped after locking the cursor " + total + " times, "
					+ session.RightClickEngagements + " for right click and " + session.HiddenCursorEngagements + " for a hidden Sober cursor");
		}
	}

	private static void Evaluate(Session session)
	{
		bool focused = IsSoberFocused();
		bool blank = focused && IsCurrentCursorBlank(session);
		bool wanted = focused && (session.RightHeld || blank);
		if (wanted == session.Hidden)
			return;

		SetHidden(session, wanted);
		if (!wanted)
			return;

		int count;
		string reason;
		if (session.RightHeld && !blank)
		{
			count = ++session.RightClickEngagements;
			reason = "right click is held";
		}
		else
		{
			count = ++session.HiddenCursorEngagements;
			reason = "Sober hid its cursor";
		}

		if (session.RightClickEngagements + session.HiddenCursorEngagements <= LoggedEngagements)
			_log?.Invoke("Hid the cursor so XWayland locks it for Sober, " + reason + " (" + count + ")");
	}

	private static void SetHidden(Session session, bool hidden)
	{
		if (hidden)
			XFixesHideCursor(session.Display, session.Root);
		else
			XFixesShowCursor(session.Display, session.Root);
		XFlush(session.Display);
		session.Hidden = hidden;
	}

	private static bool IsCurrentCursorBlank(Session session)
	{
		nint image = XFixesGetCursorImage(session.Display);
		if (image == 0)
			return false;

		try
		{
			ulong serial = (ulong)Marshal.ReadInt64(image, 16);
			if (serial == session.CursorSerial)
				return session.CursorBlank;

			int width = (ushort)Marshal.ReadInt16(image, 4);
			int height = (ushort)Marshal.ReadInt16(image, 6);
			nint pixels = Marshal.ReadIntPtr(image, 24);
			bool blank = pixels != 0 && width > 0 && height > 0;
			for (int index = 0; blank && index < width * height; index++)
			{
				if ((Marshal.ReadInt64(pixels, index * 8) & 0xFF000000L) != 0)
					blank = false;
			}

			session.CursorSerial = serial;
			session.CursorBlank = blank;
			return blank;
		}
		finally
		{
			XFree(image);
		}
	}

	private static bool IsRightButtonDown(Session session)
	{
		return XQueryPointer(session.Display, session.Root, out _, out _, out _, out _, out _, out _, out uint buttons) != 0
			&& (buttons & Button3Mask) != 0;
	}

	private static bool IsSoberFocused()
	{
		nint active = LinuxWindowInterop.GetActiveTopLevelWindow();
		if (active != 0 && LinuxWindowInterop.IsSoberRuntimeWindow(active))
			return true;

		nint focused = LinuxWindowInterop.GetFocusedWindow();
		return focused != 0 && LinuxWindowInterop.IsSoberRuntimeWindow(focused);
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
	private static partial int XFree(nint data);

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
	private static partial int XQueryPointer(
		nint display,
		nint window,
		out nint rootReturn,
		out nint childReturn,
		out int rootX,
		out int rootY,
		out int windowX,
		out int windowY,
		out uint mask);

	[LibraryImport("libXi.so.6")]
	private static partial int XIQueryVersion(nint display, ref int major, ref int minor);

	[LibraryImport("libXi.so.6")]
	private static partial int XISelectEvents(nint display, nint window, ref XIEventMask masks, int count);

	[LibraryImport("libXfixes.so.3")]
	private static partial int XFixesQueryExtension(nint display, out int eventBase, out int errorBase);

	[LibraryImport("libXfixes.so.3")]
	private static partial void XFixesSelectCursorInput(nint display, nint window, ulong eventMask);

	[LibraryImport("libXfixes.so.3")]
	private static partial nint XFixesGetCursorImage(nint display);

	[LibraryImport("libXfixes.so.3")]
	private static partial void XFixesHideCursor(nint display, nint window);

	[LibraryImport("libXfixes.so.3")]
	private static partial void XFixesShowCursor(nint display, nint window);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int poll(ref PollDescriptor descriptors, nuint count, int timeout);
}
