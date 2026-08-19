using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public readonly record struct LinuxWindowGeometry(
	nint Window,
	int ProcessId,
	int Left,
	int Top,
	int Width,
	int Height,
	bool Valid,
	bool Focused);

public static class LinuxWindowInterop
{
	private const int ShapeInput = 2;
	private const int ShapeSet = 0;
	private const int Unsorted = 0;
	private const int AnyPropertyType = 0;
	private const int Success = 0;

	private static readonly string[] RuntimeClassMarkers = ["sober", "vinegarhq", "roblox"];
	private static readonly object Sync = new();

	private static nint _display;
	private static bool _initialized;
	private static bool _unavailable;
	private static XErrorHandler? _errorHandler;

	public static bool IsAvailable
	{
		get
		{
			if (!OperatingSystem.IsLinux())
				return false;
			return Display != 0;
		}
	}

	private static nint Display
	{
		get
		{
			lock (Sync)
			{
				if (_initialized)
					return _display;
				_initialized = true;
				if (_unavailable || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
				{
					_unavailable = true;
					return 0;
				}

				try
				{
					_errorHandler = IgnoreError;
					XSetErrorHandler(_errorHandler);
					_display = XOpenDisplay(null);
				}
				catch (DllNotFoundException)
				{
					_display = 0;
				}
				catch (EntryPointNotFoundException)
				{
					_display = 0;
				}

				_unavailable = _display == 0;
				return _display;
			}
		}
	}

	public static LinuxWindowGeometry FindRuntimeWindow()
	{
		nint display = Display;
		if (display == 0)
			return default;

		try
		{
			nint focused = GetActiveWindow(display);
			foreach (nint window in EnumerateClientWindows(display))
			{
				if (!IsRuntimeWindow(display, window))
					continue;
				if (!TryGetGeometry(display, window, out int left, out int top, out int width, out int height))
					continue;
				return new LinuxWindowGeometry(
					window,
					GetWindowProcessId(display, window),
					left,
					top,
					width,
					height,
					true,
					focused != 0 && focused == window);
			}
		}
		catch (Exception)
		{
			return default;
		}

		return default;
	}

	public static nint FindOwnWindowByTitle(string title)
	{
		nint display = Display;
		if (display == 0 || string.IsNullOrWhiteSpace(title))
			return 0;

		int processId = Environment.ProcessId;
		try
		{
			foreach (nint window in EnumerateClientWindows(display))
			{
				if (GetWindowProcessId(display, window) != processId)
					continue;
				if (string.Equals(GetWindowTitle(display, window), title, StringComparison.Ordinal))
					return window;
			}
		}
		catch (Exception)
		{
			return 0;
		}

		return 0;
	}

	public static bool IsLiveWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			return XGetGeometry(display, window, out _, out _, out _, out uint width, out uint height, out _, out _) != 0
				&& width > 0
				&& height > 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TrySetClickThrough(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);
			XFlush(display);
			return true;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static IEnumerable<nint> EnumerateClientWindows(nint display)
	{
		nint root = XDefaultRootWindow(display);
		nint atom = XInternAtom(display, "_NET_CLIENT_LIST", true);
		if (atom == 0)
			yield break;

		if (!TryGetProperty(display, root, atom, out nint data, out ulong count, out int format) || format != 32)
		{
			if (data != 0)
				XFree(data);
			yield break;
		}

		try
		{
			for (ulong i = 0; i < count; i++)
			{
				nint window = Marshal.ReadIntPtr(data, (int)i * IntPtr.Size);
				if (window != 0)
					yield return window;
			}
		}
		finally
		{
			XFree(data);
		}
	}

	private static bool IsRuntimeWindow(nint display, nint window)
	{
		if (TryGetClassHint(display, window, out string name, out string className))
		{
			foreach (string marker in RuntimeClassMarkers)
			{
				if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)
					|| className.Contains(marker, StringComparison.OrdinalIgnoreCase))
					return true;
			}
		}

		string title = GetWindowTitle(display, window);
		return title.Contains("Roblox", StringComparison.OrdinalIgnoreCase)
			|| title.Contains("Sober", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryGetGeometry(nint display, nint window, out int left, out int top, out int width, out int height)
	{
		left = 0;
		top = 0;
		width = 0;
		height = 0;
		if (XGetGeometry(display, window, out nint root, out _, out _, out uint rawWidth, out uint rawHeight, out _, out _) == 0)
			return false;
		if (XTranslateCoordinates(display, window, root, 0, 0, out int rootX, out int rootY, out _) == 0)
			return false;

		left = rootX;
		top = rootY;
		width = (int)rawWidth;
		height = (int)rawHeight;
		return width > 0 && height > 0;
	}

	private static nint GetActiveWindow(nint display)
	{
		nint atom = XInternAtom(display, "_NET_ACTIVE_WINDOW", true);
		if (atom == 0)
			return 0;
		nint root = XDefaultRootWindow(display);
		if (!TryGetProperty(display, root, atom, out nint data, out ulong count, out int format) || format != 32 || count == 0)
		{
			if (data != 0)
				XFree(data);
			return 0;
		}

		try
		{
			return Marshal.ReadIntPtr(data);
		}
		finally
		{
			XFree(data);
		}
	}

	private static int GetWindowProcessId(nint display, nint window)
	{
		nint atom = XInternAtom(display, "_NET_WM_PID", true);
		if (atom == 0)
			return 0;
		if (!TryGetProperty(display, window, atom, out nint data, out ulong count, out int format) || format != 32 || count == 0)
		{
			if (data != 0)
				XFree(data);
			return 0;
		}

		try
		{
			return (int)Marshal.ReadIntPtr(data);
		}
		finally
		{
			XFree(data);
		}
	}

	private static string GetWindowTitle(nint display, nint window)
	{
		nint atom = XInternAtom(display, "_NET_WM_NAME", true);
		if (atom != 0 && TryGetProperty(display, window, atom, out nint data, out ulong count, out _))
		{
			try
			{
				if (data != 0 && count > 0)
					return ReadUtf8(data, (int)count);
			}
			finally
			{
				if (data != 0)
					XFree(data);
			}
		}

		if (XFetchName(display, window, out nint legacy) != 0 && legacy != 0)
		{
			try
			{
				return Marshal.PtrToStringUTF8(legacy) ?? string.Empty;
			}
			finally
			{
				XFree(legacy);
			}
		}

		return string.Empty;
	}

	private static bool TryGetClassHint(nint display, nint window, out string name, out string className)
	{
		name = string.Empty;
		className = string.Empty;
		XClassHint hint = default;
		if (XGetClassHint(display, window, ref hint) == 0)
			return false;

		try
		{
			name = hint.ResourceName == 0 ? string.Empty : Marshal.PtrToStringUTF8(hint.ResourceName) ?? string.Empty;
			className = hint.ResourceClass == 0 ? string.Empty : Marshal.PtrToStringUTF8(hint.ResourceClass) ?? string.Empty;
			return true;
		}
		finally
		{
			if (hint.ResourceName != 0)
				XFree(hint.ResourceName);
			if (hint.ResourceClass != 0)
				XFree(hint.ResourceClass);
		}
	}

	private static string ReadUtf8(nint data, int length)
	{
		if (length <= 0)
			return string.Empty;
		byte[] buffer = new byte[length];
		Marshal.Copy(data, buffer, 0, length);
		return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
	}

	private static bool TryGetProperty(nint display, nint window, nint property, out nint data, out ulong count, out int format)
	{
		data = 0;
		count = 0;
		format = 0;
		int status = XGetWindowProperty(
			display,
			window,
			property,
			0,
			4096,
			false,
			AnyPropertyType,
			out _,
			out format,
			out count,
			out _,
			out data);
		return status == Success && data != 0 && count > 0;
	}

	private static int IgnoreError(nint display, nint errorEvent)
	{
		return 0;
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int XErrorHandler(nint display, nint errorEvent);

	[DllImport("libX11.so.6")]
	private static extern nint XSetErrorHandler(XErrorHandler handler);

	[StructLayout(LayoutKind.Sequential)]
	private struct XClassHint
	{
		public nint ResourceName;
		public nint ResourceClass;
	}

	[DllImport("libX11.so.6")]
	private static extern nint XOpenDisplay(string? display);

	[DllImport("libX11.so.6")]
	private static extern nint XDefaultRootWindow(nint display);

	[DllImport("libX11.so.6")]
	private static extern nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

	[DllImport("libX11.so.6")]
	private static extern int XGetWindowProperty(
		nint display,
		nint window,
		nint property,
		long offset,
		long length,
		[MarshalAs(UnmanagedType.Bool)] bool delete,
		nint requestedType,
		out nint actualType,
		out int actualFormat,
		out ulong itemCount,
		out ulong bytesAfter,
		out nint property_return);

	[DllImport("libX11.so.6")]
	private static extern int XGetGeometry(
		nint display,
		nint drawable,
		out nint root,
		out int x,
		out int y,
		out uint width,
		out uint height,
		out uint borderWidth,
		out uint depth);

	[DllImport("libX11.so.6")]
	private static extern int XTranslateCoordinates(
		nint display,
		nint sourceWindow,
		nint destinationWindow,
		int sourceX,
		int sourceY,
		out int destinationX,
		out int destinationY,
		out nint child);

	[DllImport("libX11.so.6")]
	private static extern int XGetClassHint(nint display, nint window, ref XClassHint hint);

	[DllImport("libX11.so.6")]
	private static extern int XFetchName(nint display, nint window, out nint name);

	[DllImport("libX11.so.6")]
	private static extern int XFree(nint data);

	[DllImport("libX11.so.6")]
	private static extern int XFlush(nint display);

	[DllImport("libXext.so.6")]
	private static extern void XShapeCombineRectangles(
		nint display,
		nint window,
		int kind,
		int xOffset,
		int yOffset,
		nint rectangles,
		int count,
		int operation,
		int ordering);
}
