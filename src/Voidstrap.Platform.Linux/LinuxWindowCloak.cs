using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxWindowInterop
{
	private const int CreateNotifyEvent = 16;
	private const int MapNotifyEvent = 19;
	private const int ReparentNotifyEvent = 21;
	private const int PropertyNotifyEvent = 28;
	private const nint StructureNotifyEventMask = 1 << 17;
	private const nint PropertyChangeEventMask = 1 << 22;
	private const nint AtomPropertyType = 4;
	private const nint CardinalPropertyType = 6;
	private const int ReplacePropertyMode = 0;
	private const int XEventBufferSize = 192;

	public static SoberWindowCloak? StartSoberCloak()
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) || Display == 0)
			return null;

		try
		{
			nint display = XOpenDisplay(null);
			return display == 0 ? null : new SoberWindowCloak(display);
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}
	}

	public static bool TryCloakWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			CloakWindow(display, window);
			nint frame = ResolveFrameWindow(display, window);
			if (frame != 0 && frame != window)
				CloakWindow(display, frame);
			_ = XFlush(display);
			return true;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static bool TryUncloakWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0 || !IsLiveWindow(window))
			return false;

		try
		{
			nint opacity = XInternAtom(display, "_NET_WM_WINDOW_OPACITY", false);
			nint frame = ResolveFrameWindow(display, window);
			foreach (nint target in frame != 0 && frame != window ? new[] { window, frame } : new[] { window })
			{
				_ = XDeleteProperty(display, target, opacity);
				XShapeCombineMask(display, target, ShapeBounding, 0, 0, 0, ShapeSet);
				XShapeCombineMask(display, target, ShapeInput, 0, 0, 0, ShapeSet);
			}

			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint root = XDefaultRootWindow(display);
			foreach (string hint in new[] { "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER" })
			{
				XClientMessage message = new()
				{
					type = ClientMessage,
					serial = 0,
					send_event = 1,
					display = display,
					window = window,
					message_type = state,
					format = 32,
					data0 = NetWmStateRemove,
					data1 = XInternAtom(display, hint, false),
					data2 = 0,
					data3 = 1,
					data4 = 0
				};
				XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
			}
			_ = XFlush(display);
			return true;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static void CloakWindow(nint display, nint window)
	{
		_ = XChangeProperty(display, window, XInternAtom(display, "_NET_WM_WINDOW_OPACITY", false), CardinalPropertyType, 32, ReplacePropertyMode, [0], 1);
		XShapeCombineRectangles(display, window, ShapeBounding, 0, 0, 0, 0, ShapeSet, Unsorted);
		XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);
	}

	private static void CloakNewWindow(nint display, nint window)
	{
		CloakWindow(display, window);
		_ = XChangeProperty(display, window, XInternAtom(display, "_NET_WM_USER_TIME", false), CardinalPropertyType, 32, ReplacePropertyMode, [0], 1);
		_ = XChangeProperty(
			display,
			window,
			XInternAtom(display, "_NET_WM_STATE", false),
			AtomPropertyType,
			32,
			ReplacePropertyMode,
			[XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false), XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false)],
			2);
	}

	public sealed class SoberWindowCloak : IDisposable
	{
		private readonly nint _display;
		private readonly Thread _thread;
		private readonly HashSet<nint> _watched = [];
		private readonly ConcurrentDictionary<nint, byte> _cloaked = new();
		private volatile bool _stopping;

		internal SoberWindowCloak(nint display)
		{
			_display = display;
			_ = XSelectInput(display, XDefaultRootWindow(display), SubstructureNotifyMask);
			_ = XFlush(display);
			_thread = new Thread(Run) { IsBackground = true, Name = "Voidstrap Sober cloak" };
			_thread.Start();
		}

		public IReadOnlyCollection<nint> CloakedWindows => _cloaked.Keys.ToArray();

		public void Dispose()
		{
			_stopping = true;
			if (Thread.CurrentThread != _thread)
				_thread.Join(1000);
		}

		private void Run()
		{
			nint buffer = Marshal.AllocHGlobal(XEventBufferSize);
			try
			{
				nint root = XDefaultRootWindow(_display);
				while (!_stopping)
				{
					while (!_stopping && XPending(_display) > 0)
					{
						_ = XNextEvent(_display, buffer);
						Handle(buffer, root);
					}
					Thread.Sleep(4);
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
				_ = XCloseDisplay(_display);
			}
		}

		private void Handle(nint xEvent, nint root)
		{
			int type = Marshal.ReadInt32(xEvent) & 0x7f;
			switch (type)
			{
				case CreateNotifyEvent:
				{
					nint parent = Marshal.ReadIntPtr(xEvent, 32);
					nint created = Marshal.ReadIntPtr(xEvent, 40);
					if (parent != root || created == 0)
						return;
					_watched.Add(created);
					_ = XSelectInput(_display, created, PropertyChangeEventMask | StructureNotifyEventMask);
					TryCloak(created);
					break;
				}
				case PropertyNotifyEvent:
				{
					nint window = Marshal.ReadIntPtr(xEvent, 32);
					if (_watched.Contains(window) && !_cloaked.ContainsKey(window))
						TryCloak(window);
					break;
				}
				case ReparentNotifyEvent:
				{
					nint child = Marshal.ReadIntPtr(xEvent, 40);
					nint frame = Marshal.ReadIntPtr(xEvent, 48);
					if (frame != 0 && frame != root && _cloaked.ContainsKey(child))
					{
						CloakWindow(_display, frame);
						_ = XFlush(_display);
					}
					break;
				}
				case MapNotifyEvent:
				{
					nint mapped = Marshal.ReadIntPtr(xEvent, 40);
					if (_cloaked.ContainsKey(mapped))
					{
						CloakWindow(_display, mapped);
						_ = XFlush(_display);
					}
					break;
				}
			}
		}

		private void TryCloak(nint window)
		{
			bool hasClass = TryGetClassHint(_display, window, out _, out _);
			if (!IsSoberWindow(_display, window))
			{
				if (hasClass)
				{
					_watched.Remove(window);
					_ = XSelectInput(_display, window, 0);
				}
				return;
			}

			CloakNewWindow(_display, window);
			_cloaked[window] = 0;
			_ = XFlush(_display);
		}
	}

	[LibraryImport("libX11.so.6")]
	private static partial int XSelectInput(nint display, nint window, nint eventMask);

	[LibraryImport("libX11.so.6")]
	private static partial int XPending(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XNextEvent(nint display, nint xEvent);

	[LibraryImport("libX11.so.6")]
	private static partial int XCloseDisplay(nint display);
}
