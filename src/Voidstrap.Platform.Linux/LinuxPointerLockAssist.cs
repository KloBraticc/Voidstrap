using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxPointerLockAssist
{
	private const int GenericEvent = 35;
	private const int XIAllMasterDevices = 1;
	private const int XIRawButtonPress = 15;
	private const int XIRawButtonRelease = 16;
	private const int XIRawMotion = 17;
	private const long HoldCheckInterval = 8;
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
		public int Locks;
		public bool Holding;
		public int AnchorX;
		public int AnchorY;
		public int Left;
		public int Top;
		public int Width;
		public int Height;
		public long LastHoldCheck;
		public int Recenters;
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
			Marshal.WriteByte(maskBuffer, 2, (byte)((1 << (XIRawButtonRelease & 7)) | (1 << (XIRawMotion & 7))));
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
					if (eventType == XIRawMotion)
					{
						KeepInside(session);
						continue;
					}

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
					+ session.RightClickEngagements + " for right click and " + session.HiddenCursorEngagements + " for a hidden Sober cursor, "
					+ session.Locks + " held inside the window");
		}
	}

	private static void Evaluate(Session session)
	{
		nint sober = FindFocusedSober();
		bool focused = sober != 0;
		bool blank = focused && IsCurrentCursorBlank(session);
		bool wanted = focused && (session.RightHeld || blank);
		if (wanted == session.Hidden)
			return;

		SetHidden(session, wanted);
		if (!wanted)
			return;

		if (LockInPlace(session, sober))
		{
			session.Locks++;
			if (session.Locks <= LoggedEngagements)
				_log?.Invoke("Locked the cursor inside Sober so it cannot drift out of the window while the camera turns (" + session.Locks + ")");
		}

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
		if (!hidden)
			session.Holding = false;
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

	private static nint FindFocusedSober()
	{
		nint active = LinuxWindowInterop.GetActiveTopLevelWindow();
		if (active != 0 && LinuxWindowInterop.IsSoberRuntimeWindow(active))
			return active;

		nint focused = LinuxWindowInterop.GetFocusedWindow();
		return focused != 0 && LinuxWindowInterop.IsSoberRuntimeWindow(focused) ? focused : 0;
	}

	private static bool LockInPlace(Session session, nint sober)
	{
		_ = XSync(session.Display, 0);
		if (XQueryPointer(session.Display, session.Root, out _, out nint child, out int rootX, out int rootY, out _, out _, out _) == 0 || child == 0)
			return false;

		if (!LinuxWindowInterop.IsSameOrDescendantWindow(sober, child) && !LinuxWindowInterop.IsSameOrDescendantWindow(child, sober))
			return false;

		_ = XWarpPointer(session.Display, 0, session.Root, 0, 0, 0, 0, rootX, rootY);
		_ = XFlush(session.Display);
		if (LinuxWindowInterop.TryGetWindowGeometry(sober, out int left, out int top, out int width, out int height))
		{
			session.Holding = true;
			session.AnchorX = rootX;
			session.AnchorY = rootY;
			session.Left = left;
			session.Top = top;
			session.Width = width;
			session.Height = height;
		}
		return true;
	}

	private static void KeepInside(Session session)
	{
		if (!session.Holding || !session.Hidden)
			return;

		long now = Environment.TickCount64;
		if (now - session.LastHoldCheck < HoldCheckInterval)
			return;
		session.LastHoldCheck = now;

		if (XQueryPointer(session.Display, session.Root, out _, out _, out int x, out int y, out _, out _, out _) == 0)
			return;

		int margin = Math.Max(8, Math.Min(64, Math.Min(session.Width, session.Height) / 4));
		if (IsInside(session, x, y, margin))
			return;

		bool anchorSafe = IsInside(session, session.AnchorX, session.AnchorY, margin * 2);
		int targetX = anchorSafe ? session.AnchorX : session.Left + session.Width / 2;
		int targetY = anchorSafe ? session.AnchorY : session.Top + session.Height / 2;
		_ = XWarpPointer(session.Display, 0, session.Root, 0, 0, 0, 0, targetX, targetY);
		_ = XFlush(session.Display);
		session.Recenters++;
		if (session.Recenters <= LoggedEngagements)
			_log?.Invoke("Kept the hidden cursor inside Sober while the camera turned (" + session.Recenters + ")");
	}

	private static bool IsInside(Session session, int x, int y, int margin)
	{
		return x >= session.Left + margin
			&& y >= session.Top + margin
			&& x < session.Left + session.Width - margin
			&& y < session.Top + session.Height - margin;
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
	private static partial int XWarpPointer(nint display, nint sourceWindow, nint destinationWindow, int sourceX, int sourceY, uint sourceWidth, uint sourceHeight, int destinationX, int destinationY);

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
