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

public static partial class LinuxWindowInterop
{
	private const nint CwOverrideRedirect = 1 << 9;

	private const int ShapeBounding = 0;

	private const int ShapeInput = 2;
	private const int ShapeSet = 0;
	private const int Unsorted = 0;
	private const int AnyPropertyType = 0;
	private const int Success = 0;
	private const int IsUnmapped = 0;
	private const int IsViewable = 2;
	private const int ZPixmap = 2;
	private const int LsbFirst = 0;
	private const int IpcCreate = 512;
	private const int OwnerReadWrite = 384;
	private const int IpcRemove = 0;

	private static readonly string[] RuntimeClassMarkers = ["sober", "vinegarhq", "roblox"];
	private static readonly object Sync = new();

	private static nint _display;
	private static bool _initialized;
	private static bool _unavailable;
	private static int _compositeState;
	private static int _shapeState;
	private static nint _captureWindow;
	private static nint _capturePixmap;
	private static int _captureWidth;
	private static int _captureHeight;
	private static int _sharedImageState;
	private static nint _sharedImage;
	private static XShmSegmentInfo _sharedSegment;
	private static int _sharedWidth;
	private static int _sharedHeight;
	private static nint _sharedVisual;
	private static int _sharedDepth;
	private static XErrorHandler? _errorHandler;
	private static readonly HashSet<nint> PreparedOverlayWindows = [];

	public static bool IsAvailable
	{
		get
		{
			if (!OperatingSystem.IsLinux())
				return false;
			return Display != 0;
		}
	}

	public static bool HasActiveX11Compositor
	{
		get
		{
			if (!OperatingSystem.IsLinux())
				return false;

			lock (Sync)
			{
				nint display = Display;
				if (display == 0)
					return false;

				try
				{
					int screen = XDefaultScreen(display);
					if (screen < 0)
						return false;
					string selectionName = "_NET_WM_CM_S" + screen.ToString(System.Globalization.CultureInfo.InvariantCulture);
					nint selection = XInternAtom(display, selectionName, true);
					return selection != 0 && XGetSelectionOwner(display, selection) != 0;
				}
				catch (DllNotFoundException)
				{
					return false;
				}
				catch (EntryPointNotFoundException)
				{
					return false;
				}
				catch (Exception)
				{
					return false;
				}
			}
		}
	}

	public static bool TrySetApplicationIdentity(nint window, string resourceName, string resourceClass, nint[] iconData)
	{
		lock (Sync)
			return TrySetApplicationIdentityCore(window, resourceName, resourceClass, iconData);
	}

	private static bool TrySetApplicationIdentityCore(nint window, string resourceName, string resourceClass, nint[] iconData)
	{
		nint display = Display;
		if (display == 0 || window == 0 || string.IsNullOrWhiteSpace(resourceName) || string.IsNullOrWhiteSpace(resourceClass))
			return false;

		nint namePointer = 0;
		nint classPointer = 0;
		try
		{
			namePointer = Marshal.StringToHGlobalAnsi(resourceName);
			classPointer = Marshal.StringToHGlobalAnsi(resourceClass);
			XClassHint hint = new()
			{
				ResourceName = namePointer,
				ResourceClass = classPointer
			};
			if (XSetClassHint(display, window, ref hint) == 0)
				return false;
			_ = XFlush(display);

			if (iconData.Length > 2)
			{
				nint iconAtom = XInternAtom(display, "_NET_WM_ICON", false);
				nint cardinalAtom = XInternAtom(display, "CARDINAL", false);
				if (iconAtom != 0 && cardinalAtom != 0)
					XChangeProperty(display, window, iconAtom, cardinalAtom, 32, 0, iconData, iconData.Length);
			}

			_ = XFlush(display);
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
		finally
		{
			if (namePointer != 0)
				Marshal.FreeHGlobal(namePointer);
			if (classPointer != 0)
				Marshal.FreeHGlobal(classPointer);
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
					_ = XInitThreads();
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

	public static LinuxWindowGeometry FindRuntimeWindow(nint preferredWindow = 0)
	{
		nint display = Display;
		if (display == 0)
			return default;

		try
		{
			nint focused = GetActiveWindow(display);
			LinuxWindowGeometry best = default;
			long bestScore = long.MinValue;
			if (preferredWindow != 0 && TryMeasureRuntimeWindow(display, preferredWindow, focused, out LinuxWindowGeometry preferred))
			{
				best = preferred;
				bestScore = ScoreRuntimeWindow(display, preferred);
			}
			foreach (nint window in EnumerateClientWindows(display))
			{
				if (window == preferredWindow || !TryMeasureRuntimeWindow(display, window, focused, out LinuxWindowGeometry measured))
					continue;
				long score = ScoreRuntimeWindow(display, measured);
				if (score <= bestScore)
					continue;
				best = measured;
				bestScore = score;
			}
			return best;
		}
		catch (Exception)
		{
			return default;
		}
	}

	private static bool TryMeasureRuntimeWindow(nint display, nint window, nint focused, out LinuxWindowGeometry geometry)
	{
		geometry = default;
		int processId = GetWindowProcessId(display, window);
		if (XGetWindowAttributes(display, window, out XWindowAttributes attributes) == 0
			|| attributes.MapState != IsViewable
			|| processId == Environment.ProcessId
			|| !IsRuntimeWindow(display, window)
			|| !TryGetGeometry(display, window, out int left, out int top, out int width, out int height))
			return false;
		geometry = new LinuxWindowGeometry(
			window,
			processId,
			left,
			top,
			width,
			height,
			true,
			focused != 0 && focused == window);
		return true;
	}

	private static long ScoreRuntimeWindow(nint display, LinuxWindowGeometry geometry)
	{
		long score = Math.Min((long)geometry.Width * geometry.Height, 999_999_999L);
		if (IsSoberWindow(display, geometry.Window))
			score += 4_000_000_000_000L;
		else if (TryGetClassHint(display, geometry.Window, out string name, out string className))
		{
			if (name.Contains("roblox", StringComparison.OrdinalIgnoreCase)
				|| className.Contains("roblox", StringComparison.OrdinalIgnoreCase))
				score += 3_000_000_000_000L;
			else if (name.Contains("vinegar", StringComparison.OrdinalIgnoreCase)
				|| className.Contains("vinegar", StringComparison.OrdinalIgnoreCase))
				score += 2_000_000_000_000L;
		}
		if (geometry.Focused)
			score += 500_000_000_000L;
		return score;
	}

	public static bool IsSoberRuntimeWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;
		try
		{
			return IsSoberWindow(display, window);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsSoberWindow(nint display, nint window)
	{
		if (TryGetClassHint(display, window, out string name, out string className)
			&& (name.Contains("sober", StringComparison.OrdinalIgnoreCase)
				|| className.Contains("sober", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("vinegar", StringComparison.OrdinalIgnoreCase)
				|| className.Contains("vinegar", StringComparison.OrdinalIgnoreCase)))
			return true;
		string title = GetWindowTitle(display, window);
		if (!title.Contains("Sober", StringComparison.OrdinalIgnoreCase)
			&& !title.Contains("Roblox", StringComparison.OrdinalIgnoreCase))
			return false;
		return IsSoberProcessIdentity(GetWindowProcessId(display, window));
	}

	public static nint GetFocusedWindow()
	{
		nint display = Display;
		if (display == 0)
			return 0;

		try
		{
			XGetInputFocus(display, out nint focused, out _);
			return focused != 0 ? focused : GetActiveWindow(display);
		}
		catch (Exception)
		{
			return 0;
		}
	}

	public static nint GetActiveTopLevelWindow()
	{
		nint display = Display;
		if (display == 0)
			return 0;
		try
		{
			return GetActiveWindow(display);
		}
		catch (Exception)
		{
			return 0;
		}
	}

	public static bool IsSameOrDescendantWindow(nint window, nint ancestor)
	{
		if (window == 0 || ancestor == 0)
			return false;
		if (window == ancestor)
			return true;

		nint display = Display;
		if (display == 0)
			return false;

		try
		{
			nint current = window;
			for (int depth = 0; depth < 16; depth++)
			{
				if (!XQueryTree(display, current, out nint root, out nint parent, out nint children, out _))
					return false;
				if (children != 0)
					_ = XFree(children);
				if (parent == ancestor)
					return true;
				if (parent == 0 || parent == root || parent == current)
					return false;
				current = parent;
			}
		}
		catch (Exception)
		{
		}

		return false;
	}

	public static bool TryActivateWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint active = XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
			nint root = XDefaultRootWindow(display);
			if (active == 0 || root == 0)
				return false;

			XClientMessage message = new()
			{
				type = ClientMessage,
				serial = 0,
				send_event = 1,
				display = display,
				window = window,
				message_type = active,
				format = 32,
				data0 = 1,
				data1 = 0,
				data2 = 0,
				data3 = 0,
				data4 = 0
			};
			XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
			_ = XRaiseWindow(display, window);
			_ = XFlush(display);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
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

			return FindOwnWindowInTree(display, XDefaultRootWindow(display), title, processId, 0);
		}
		catch (Exception)
		{
			return 0;
		}
	}

	public static IReadOnlyList<nint> FindOwnManagedWindowsByTitle(string title)
	{
		nint display = Display;
		if (display == 0 || string.IsNullOrWhiteSpace(title))
			return Array.Empty<nint>();

		int processId = Environment.ProcessId;
		List<nint> found = new();
		try
		{
			foreach (nint window in EnumerateClientWindows(display))
			{
				if (GetWindowProcessId(display, window) != processId)
					continue;
				if (string.Equals(GetWindowTitle(display, window), title, StringComparison.Ordinal))
					found.Add(window);
			}
		}
		catch (Exception)
		{
			return found;
		}

		return found;
	}

	public static IReadOnlyList<nint> FindOwnManagedWindows()
	{
		nint display = Display;
		if (display == 0)
			return Array.Empty<nint>();

		int processId = Environment.ProcessId;
		List<nint> found = new();
		try
		{
			foreach (nint window in EnumerateClientWindows(display))
			{
				if (GetWindowProcessId(display, window) == processId)
					found.Add(window);
			}
		}
		catch (Exception)
		{
		}

		return found;
	}

	public static IReadOnlyList<nint> FindSoberWindows()
	{
		nint display = Display;
		if (display == 0)
			return Array.Empty<nint>();

		List<nint> found = new();
		try
		{
			foreach (nint window in EnumerateClientWindows(display))
			{
				if (IsSoberWindow(display, window))
					found.Add(window);
			}
		}
		catch (Exception)
		{
		}

		return found;
	}

	private const int MaxWindowTreeDepth = 6;

	private static nint FindOwnWindowInTree(nint display, nint parent, string title, int processId, int depth)
	{
		if (depth > MaxWindowTreeDepth || parent == 0)
			return 0;

		if (!XQueryTree(display, parent, out _, out _, out nint children, out uint count) || children == 0)
			return 0;

		try
		{
			for (uint i = 0; i < count; i++)
			{
				nint window = Marshal.ReadIntPtr(children, (int)i * IntPtr.Size);

				if (window == 0)
					continue;

				if (string.Equals(GetWindowTitle(display, window), title, StringComparison.Ordinal))
				{
					int owner = GetWindowProcessId(display, window);

					if (owner == processId || owner == 0)
						return window;
				}

				nint nested = FindOwnWindowInTree(display, window, title, processId, depth + 1);

				if (nested != 0)
					return nested;
			}
		}
		finally
		{
			_ = XFree(children);
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

	private const uint Button1Mask = 1 << 8;

	public static bool TryGetWindowGeometry(nint window, out int x, out int y, out int width, out int height)
	{
		x = 0;
		y = 0;
		width = 0;
		height = 0;
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			return TryGetGeometry(display, window, out x, out y, out width, out height);
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

	public static bool TryWindowHasColorVariation(nint window, int width, int height)
	{
		nint display = Display;
		if (display == 0 || window == 0 || width <= 0 || height <= 0)
			return false;
		lock (Sync)
			return TryWindowTreeHasColorVariation(display, window, width, height, 0);
	}

	public static bool TryCaptureWindowBgra32(nint window, byte[] destination, out int width, out int height)
	{
		return TryCaptureWindow32(window, destination, true, out width, out height);
	}

	public static bool TryCaptureWindowBgrx32(nint window, byte[] destination, out int width, out int height)
	{
		return TryCaptureWindow32(window, destination, false, out width, out height);
	}

	private static bool TryCaptureWindow32(nint window, byte[] destination, bool forceOpaqueAlpha, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (window == 0 || destination == null)
			return false;

		lock (Sync)
		{
			nint display = Display;
			if (display == 0)
				return false;

			try
			{
				if (XGetWindowAttributes(display, window, out XWindowAttributes attributes) == 0
					|| attributes.MapState != IsViewable
					|| attributes.Width <= 0
					|| attributes.Height <= 0)
					return false;

				width = attributes.Width;
				height = attributes.Height;
				long required = (long)width * height * 4;
				if (required > int.MaxValue || destination.LongLength < required)
					return false;
				ReadVisualMasks(attributes.Visual, out ulong visualRedMask, out ulong visualGreenMask, out ulong visualBlueMask);

				if (TryGetCompositePixmap(display, window, width, height, out nint pixmap)
					&& TryCaptureDrawableBgra32(
						display,
						pixmap,
						destination,
						width,
						height,
						attributes.Visual,
						attributes.Depth,
						visualRedMask,
						visualGreenMask,
						visualBlueMask,
						forceOpaqueAlpha))
					return true;

				ReleaseCapturePixmap(display, window);

				return TryCaptureDrawableBgra32(
					display,
					window,
					destination,
					width,
					height,
					attributes.Visual,
					attributes.Depth,
					visualRedMask,
					visualGreenMask,
					visualBlueMask,
					forceOpaqueAlpha);
			}
			catch (DllNotFoundException)
			{
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	public static void ReleaseWindowCapture(nint window)
	{
		lock (Sync)
		{
			nint display = Display;
			if (display != 0)
			{
				ReleaseCapturePixmap(display, window);
				ReleaseSharedImage(display);
			}
		}
	}

	public static void RefreshWindowCapturePixmap(nint window)
	{
		if (window == 0)
			return;
		lock (Sync)
		{
			nint display = Display;
			if (display != 0)
				ReleaseCapturePixmap(display, window);
		}
	}

	public static bool TryCreateWindowDamage(nint window, out nint damage, out int eventType)
	{
		damage = 0;
		eventType = 0;
		if (window == 0)
			return false;

		lock (Sync)
		{
			nint display = Display;
			if (display == 0)
				return false;
			try
			{
				if (XDamageQueryExtension(display, out int eventBase, out _) == 0)
					return false;
				damage = XDamageCreate(display, window, 3);
				if (damage == 0)
					return false;
				eventType = eventBase;
				XDamageSubtract(display, damage, 0, 0);
				_ = XFlush(display);
				return true;
			}
			catch (DllNotFoundException)
			{
				damage = 0;
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				damage = 0;
				return false;
			}
		}
	}

	public static bool TryConsumeWindowDamage(nint damage, int eventType)
	{
		if (damage == 0 || eventType <= 0)
			return false;
		lock (Sync)
		{
			nint display = Display;
			if (display == 0)
				return false;
			try
			{
				bool changed = false;
				while (XCheckTypedEvent(display, eventType, out _) != 0)
					changed = true;
				if (changed)
				{
					XDamageSubtract(display, damage, 0, 0);
					_ = XFlush(display);
				}
				return changed;
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
	}

	public static void DestroyWindowDamage(nint damage, int eventType)
	{
		if (damage == 0)
			return;
		lock (Sync)
		{
			nint display = Display;
			if (display == 0)
				return;
			try
			{
				XDamageDestroy(display, damage);
				if (eventType > 0)
				{
					while (XCheckTypedEvent(display, eventType, out _) != 0)
					{
					}
				}
				_ = XFlush(display);
			}
			catch (DllNotFoundException)
			{
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	private static bool TryGetCompositePixmap(nint display, nint window, int width, int height, out nint pixmap)
	{
		if (_capturePixmap != 0
			&& _captureWindow == window
			&& _captureWidth == width
			&& _captureHeight == height)
		{
			pixmap = _capturePixmap;
			return true;
		}

		ReleaseCapturePixmap(display, 0);
		pixmap = 0;
		if (_compositeState == 0)
		{
			try
			{
				_compositeState = XCompositeQueryExtension(display, out _, out _) != 0 ? 1 : -1;
			}
			catch (DllNotFoundException)
			{
				_compositeState = -1;
			}
			catch (EntryPointNotFoundException)
			{
				_compositeState = -1;
			}
		}
		if (_compositeState < 0)
			return false;

		try
		{
			pixmap = XCompositeNameWindowPixmap(display, window);
			if (pixmap == 0)
				return false;
			_captureWindow = window;
			_capturePixmap = pixmap;
			_captureWidth = width;
			_captureHeight = height;
			return true;
		}
		catch (DllNotFoundException)
		{
			_compositeState = -1;
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			_compositeState = -1;
			return false;
		}
	}

	private static void ReleaseCapturePixmap(nint display, nint window)
	{
		if (_capturePixmap == 0 || (window != 0 && _captureWindow != window))
			return;
		_ = XFreePixmap(display, _capturePixmap);
		_captureWindow = 0;
		_capturePixmap = 0;
		_captureWidth = 0;
		_captureHeight = 0;
	}

	private static void ReadVisualMasks(nint visual, out ulong redMask, out ulong greenMask, out ulong blueMask)
	{
		redMask = 0;
		greenMask = 0;
		blueMask = 0;
		if (visual == 0)
			return;
		XVisualMasks masks = Marshal.PtrToStructure<XVisualMasks>(visual);
		redMask = masks.RedMask;
		greenMask = masks.GreenMask;
		blueMask = masks.BlueMask;
	}

	private static bool TryCaptureDrawableBgra32(
		nint display,
		nint drawable,
		byte[] destination,
		int width,
		int height,
		nint visual,
		int depth,
		ulong visualRedMask,
		ulong visualGreenMask,
		ulong visualBlueMask,
		bool forceOpaqueAlpha)
	{
		if (TryCaptureDrawableSharedBgra32(
			display,
			drawable,
			destination,
			width,
			height,
			visual,
			depth,
			visualRedMask,
			visualGreenMask,
			visualBlueMask,
			forceOpaqueAlpha))
			return true;

		nint image = 0;
		try
		{
			image = XGetImage(display, drawable, 0, 0, (uint)width, (uint)height, nuint.MaxValue, ZPixmap);
			if (image == 0)
				return false;

			return CopyXImageBgra32(
				Marshal.PtrToStructure<XImageInfo>(image),
				destination,
				width,
				height,
				visualRedMask,
				visualGreenMask,
				visualBlueMask,
				forceOpaqueAlpha);
		}
		finally
		{
			if (image != 0)
				_ = XDestroyImage(image);
		}
	}

	private static bool TryCaptureDrawableSharedBgra32(
		nint display,
		nint drawable,
		byte[] destination,
		int width,
		int height,
		nint visual,
		int depth,
		ulong visualRedMask,
		ulong visualGreenMask,
		ulong visualBlueMask,
		bool forceOpaqueAlpha)
	{
		if (_sharedImageState == 0)
		{
			try
			{
				_sharedImageState = XShmQueryExtension(display) != 0 ? 1 : -1;
			}
			catch (DllNotFoundException)
			{
				_sharedImageState = -1;
			}
			catch (EntryPointNotFoundException)
			{
				_sharedImageState = -1;
			}
		}
		if (_sharedImageState < 0)
			return false;
		if ((_sharedImage == 0
				|| _sharedWidth != width
				|| _sharedHeight != height
				|| _sharedVisual != visual
				|| _sharedDepth != depth)
			&& !TryCreateSharedImage(display, visual, depth, width, height))
			return false;
		try
		{
			if (XShmGetImage(display, drawable, _sharedImage, 0, 0, nuint.MaxValue) == 0)
			{
				ReleaseSharedImage(display);
				return false;
			}
			return CopyXImageBgra32(
				Marshal.PtrToStructure<XImageInfo>(_sharedImage),
				destination,
				width,
				height,
				visualRedMask,
				visualGreenMask,
				visualBlueMask,
				forceOpaqueAlpha);
		}
		catch (DllNotFoundException)
		{
			_sharedImageState = -1;
			ReleaseSharedImage(display);
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			_sharedImageState = -1;
			ReleaseSharedImage(display);
			return false;
		}
	}

	private static bool TryCreateSharedImage(nint display, nint visual, int depth, int width, int height)
	{
		ReleaseSharedImage(display);
		XShmSegmentInfo segment = new()
		{
			Shmid = -1
		};
		nint image = 0;
		bool attached = false;
		try
		{
			image = XShmCreateImage(display, visual, (uint)depth, ZPixmap, 0, ref segment, (uint)width, (uint)height);
			if (image == 0)
				return false;
			XImageInfo info = Marshal.PtrToStructure<XImageInfo>(image);
			long length = (long)info.BytesPerLine * info.Height;
			if (info.BytesPerLine <= 0 || info.Height < height || length <= 0 || length > int.MaxValue)
				return false;
			segment.Shmid = ShmGet(0, (nuint)length, IpcCreate | OwnerReadWrite);
			if (segment.Shmid < 0)
				return false;
			segment.ShmAddress = ShmAt(segment.Shmid, 0, 0);
			if (segment.ShmAddress == -1)
				return false;
			segment.ReadOnly = 0;
			info.Data = segment.ShmAddress;
			Marshal.StructureToPtr(info, image, false);
			if (XShmAttach(display, ref segment) == 0)
				return false;
			attached = true;
			_ = XSync(display, 0);
			_ = ShmControl(segment.Shmid, IpcRemove, 0);
			_sharedImage = image;
			_sharedSegment = segment;
			_sharedWidth = width;
			_sharedHeight = height;
			_sharedVisual = visual;
			_sharedDepth = depth;
			return true;
		}
		finally
		{
			if (_sharedImage == 0)
			{
				if (attached)
					XShmDetach(display, ref segment);
				if (image != 0)
				{
					XImageInfo info = Marshal.PtrToStructure<XImageInfo>(image);
					info.Data = 0;
					Marshal.StructureToPtr(info, image, false);
					_ = XDestroyImage(image);
				}
				if (segment.ShmAddress != 0 && segment.ShmAddress != -1)
					_ = ShmDetach(segment.ShmAddress);
				if (segment.Shmid >= 0)
					_ = ShmControl(segment.Shmid, IpcRemove, 0);
			}
		}
	}

	private static void ReleaseSharedImage(nint display)
	{
		if (_sharedImage == 0)
			return;
		XShmDetach(display, ref _sharedSegment);
		_ = XSync(display, 0);
		XImageInfo info = Marshal.PtrToStructure<XImageInfo>(_sharedImage);
		info.Data = 0;
		Marshal.StructureToPtr(info, _sharedImage, false);
		_ = XDestroyImage(_sharedImage);
		if (_sharedSegment.ShmAddress != 0 && _sharedSegment.ShmAddress != -1)
			_ = ShmDetach(_sharedSegment.ShmAddress);
		_sharedImage = 0;
		_sharedSegment = default;
		_sharedWidth = 0;
		_sharedHeight = 0;
		_sharedVisual = 0;
		_sharedDepth = 0;
	}

	private static bool CopyXImageBgra32(
		XImageInfo info,
		byte[] destination,
		int width,
		int height,
		ulong visualRedMask,
		ulong visualGreenMask,
		ulong visualBlueMask,
		bool forceOpaqueAlpha)
	{
		if (info.Data == 0
			|| info.Width < width
			|| info.Height < height
			|| info.BytesPerLine <= 0
			|| info.BitsPerPixel <= 0)
			return false;

		int bytesPerPixel = (info.BitsPerPixel + 7) / 8;
		if (bytesPerPixel is < 2 or > 8 || info.BytesPerLine < width * bytesPerPixel)
			return false;

		ulong redMask = info.RedMask != 0 ? info.RedMask : visualRedMask;
		ulong greenMask = info.GreenMask != 0 ? info.GreenMask : visualGreenMask;
		ulong blueMask = info.BlueMask != 0 ? info.BlueMask : visualBlueMask;
		if (bytesPerPixel == 4
			&& info.ByteOrder == LsbFirst
			&& redMask == 0x00FF0000
			&& greenMask == 0x0000FF00
			&& blueMask == 0x000000FF)
		{
			int destinationStride = width * 4;
			if (info.BytesPerLine == destinationStride)
			{
				Marshal.Copy(info.Data, destination, 0, checked(destinationStride * height));
			}
			else
			{
				for (int y = 0; y < height; y++)
				{
					int destinationOffset = y * destinationStride;
					Marshal.Copy(info.Data + y * info.BytesPerLine, destination, destinationOffset, destinationStride);
				}
			}
			if (forceOpaqueAlpha)
			{
				Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan(0, checked(destinationStride * height)));
				for (int index = 0; index < pixels.Length; index++)
					pixels[index] |= 0xFF000000u;
			}
			return true;
		}

		if (redMask == 0 || greenMask == 0 || blueMask == 0)
			return false;

		int output = 0;
		for (int y = 0; y < height; y++)
		{
			int row = y * info.BytesPerLine;
			for (int x = 0; x < width; x++)
			{
				int source = row + x * bytesPerPixel;
				ulong pixel = ReadNativePixel(info.Data, source, bytesPerPixel, info.ByteOrder == LsbFirst);
				destination[output++] = NormalizePixelComponent(pixel, blueMask);
				destination[output++] = NormalizePixelComponent(pixel, greenMask);
				destination[output++] = NormalizePixelComponent(pixel, redMask);
				destination[output++] = 255;
			}
		}
		return true;
	}

	private static ulong ReadNativePixel(nint data, int offset, int byteCount, bool leastSignificantByteFirst)
	{
		ulong pixel = 0;
		if (leastSignificantByteFirst)
		{
			for (int index = 0; index < byteCount; index++)
				pixel |= (ulong)Marshal.ReadByte(data, offset + index) << (index * 8);
			return pixel;
		}

		for (int index = 0; index < byteCount; index++)
			pixel = (pixel << 8) | Marshal.ReadByte(data, offset + index);
		return pixel;
	}

	private static byte NormalizePixelComponent(ulong pixel, ulong mask)
	{
		int shift = 0;
		ulong shiftedMask = mask;
		while ((shiftedMask & 1) == 0)
		{
			shiftedMask >>= 1;
			shift++;
		}
		ulong value = (pixel & mask) >> shift;
		return (byte)((value * 255 + shiftedMask / 2) / shiftedMask);
	}

	private static bool TryWindowTreeHasColorVariation(nint display, nint window, int width, int height, int depth)
	{
		if (TryDrawableHasColorVariation(display, window, width, height))
			return true;
		if (depth >= 4 || !XQueryTree(display, window, out _, out _, out nint children, out uint count) || children == 0)
			return false;
		try
		{
			for (uint index = 0; index < count; index++)
			{
				nint child = Marshal.ReadIntPtr(children, (int)index * IntPtr.Size);
				if (child == 0 || XGetGeometry(display, child, out _, out _, out _, out uint childWidth, out uint childHeight, out _, out _) == 0)
					continue;
				if (TryWindowTreeHasColorVariation(display, child, (int)childWidth, (int)childHeight, depth + 1))
					return true;
			}
			return false;
		}
		finally
		{
			_ = XFree(children);
		}
	}

	private static bool TryDrawableHasColorVariation(nint display, nint drawable, int width, int height)
	{
		nint image = 0;
		try
		{
			_ = XSync(display, 0);
			image = XGetImage(display, drawable, 0, 0, (uint)width, (uint)height, nuint.MaxValue, 2);
			if (image == 0)
				return false;
			XImageInfo info = Marshal.PtrToStructure<XImageInfo>(image);
			if (info.Data == 0 || info.BytesPerLine <= 0 || info.BitsPerPixel <= 0)
				return false;
			int bytesPerPixel = Math.Max(1, (info.BitsPerPixel + 7) / 8);
			int sampleWidth = Math.Min(width, info.Width);
			int sampleHeight = Math.Min(height, info.Height);
			HashSet<ulong> colors = new();
			for (int y = 2; y < sampleHeight - 2; y += 3)
			{
				for (int x = 2; x < sampleWidth - 2; x += 3)
				{
					int offset = y * info.BytesPerLine + x * bytesPerPixel;
					ulong pixel = 0;
					for (int component = 0; component < Math.Min(bytesPerPixel, 4); component++)
						pixel |= (ulong)Marshal.ReadByte(info.Data, offset + component) << (component * 8);
					colors.Add(pixel);
					if (colors.Count >= 16)
						return true;
				}
			}
			return false;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
		finally
		{
			if (image != 0)
				_ = XDestroyImage(image);
		}
	}

	public static bool TryMoveResize(nint window, int x, int y, int width, int height)
	{
		nint display = Display;
		if (display == 0 || window == 0 || width <= 0 || height <= 0)
			return false;

		try
		{
			nint frame = ResolveFrameWindow(display, window);
			if (frame != window && frame != 0)
			{
				_ = XMoveResizeWindow(display, window, 0, 0, (uint)width, (uint)height);
				_ = XMoveResizeWindow(display, frame, x, y, (uint)width, (uint)height);
			}
			else
			{
				_ = XMoveResizeWindow(display, window, x, y, (uint)width, (uint)height);
			}

			_ = XFlush(display);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryRequestGeometry(nint window, int x, int y, int width, int height)
	{
		nint display = Display;
		if (display == 0 || window == 0 || width <= 0 || height <= 0)
			return false;

		try
		{
			_ = XMoveResizeWindow(display, window, x, y, (uint)width, (uint)height);
			_ = XFlush(display);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryBeginInteractiveResize(nint display, nint window, int direction)
	{
		if (display == 0 || window == 0 || direction < 0 || direction > 7)
			return false;

		try
		{
			nint root = XDefaultRootWindow(display);
			nint moveResize = XInternAtom(display, "_NET_WM_MOVERESIZE", false);
			if (root == 0 || moveResize == 0 || !IsWindowManagerFeatureSupported(display, root, moveResize))
				return false;
			if (XQueryPointer(display, root, out _, out _, out int rootX, out int rootY, out _, out _, out _) == 0)
				return false;

			_ = XUngrabPointer(display, 0);
			XClientMessage message = new()
			{
				type = ClientMessage,
				serial = 0,
				send_event = 1,
				display = display,
				window = window,
				message_type = moveResize,
				format = 32,
				data0 = rootX,
				data1 = rootY,
				data2 = direction,
				data3 = 1,
				data4 = 1
			};
			bool sent = XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message) != 0;
			_ = XFlush(display);
			return sent;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsWindowManagerFeatureSupported(nint display, nint root, nint feature)
	{
		nint data = 0;
		try
		{
			nint supported = XInternAtom(display, "_NET_SUPPORTED", true);
			if (supported == 0 || !TryGetProperty(display, root, supported, out data, out ulong count, out int format) || format != 32)
				return false;
			for (ulong i = 0; i < count; i++)
			{
				if (Marshal.ReadIntPtr(data, (int)i * IntPtr.Size) == feature)
					return true;
			}
			return false;
		}
		finally
		{
			if (data != 0)
				_ = XFree(data);
		}
	}

	public static nint GetFrameWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return 0;

		try
		{
			return ResolveFrameWindow(display, window);
		}
		catch (Exception)
		{
			return window;
		}
	}

	public static bool IsReparentedWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint frame = ResolveFrameWindow(display, window);
			return frame != 0 && frame != window;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static nint ResolveFrameWindow(nint display, nint window)
	{
		nint current = window;
		for (int depth = 0; depth < 16; depth++)
		{
			if (!XQueryTree(display, current, out nint root, out nint parent, out nint children, out _))
				return current;
			if (children != 0)
				_ = XFree(children);
			if (parent == 0 || parent == root)
				return current;
			current = parent;
		}

		return current;
	}

	public static bool TrySetWindowOpacity(nint window, double opacity)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint atom = XInternAtom(display, "_NET_WM_WINDOW_OPACITY", false);
			if (atom == 0)
				return false;

			if (opacity >= 1.0)
			{
				_ = XDeleteProperty(display, window, atom);
			}
			else
			{
				nint cardinal = XInternAtom(display, "CARDINAL", false);
				nint value = (nint)(uint)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * uint.MaxValue);
				XChangeProperty(display, window, atom, cardinal, 32, 0, [value], 1);
			}

			_ = XFlush(display);
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
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TrySetWindowBackground(nint window, uint rgb)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			_ = XSetWindowBackground(display, window, (nuint)(rgb & 0xFFFFFF));
			_ = XFlush(display);
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
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TrySetUndecorated(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint hints = XInternAtom(display, "_MOTIF_WM_HINTS", false);
			if (hints == 0)
				return false;

			nint[] value = new nint[5];
			value[0] = 2;
			value[1] = 0;
			value[2] = 0;
			value[3] = 0;
			value[4] = 0;
			XChangeProperty(display, window, hints, hints, 32, 0, value, value.Length);
			_ = XFlush(display);
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
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryGetPointerPosition(out int x, out int y)
	{
		return TryGetPointerPosition(out x, out y, out _);
	}

	public static bool TryGetPointerPosition(out int x, out int y, out bool primaryPressed)
	{
		x = 0;
		y = 0;
		primaryPressed = false;
		nint display = Display;
		if (display == 0)
			return false;

		try
		{
			nint root = XDefaultRootWindow(display);
			if (root == 0)
				return false;

			if (XQueryPointer(display, root, out _, out _, out int rootX, out int rootY, out _, out _, out uint mask) == 0)
				return false;

			x = rootX;
			y = rootY;
			primaryPressed = (mask & Button1Mask) != 0;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryGetRootBounds(out int width, out int height)
	{
		width = 0;
		height = 0;
		nint display = Display;
		if (display == 0)
			return false;

		try
		{
			nint root = XDefaultRootWindow(display);
			if (root == 0)
				return false;
			if (XGetGeometry(display, root, out _, out _, out _, out uint rootWidth, out uint rootHeight, out _, out _) == 0)
				return false;
			if (rootWidth == 0 || rootHeight == 0)
				return false;

			width = (int)rootWidth;
			height = (int)rootHeight;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryGetPrimaryMonitorBounds(out int left, out int top, out int width, out int height)
	{
		left = 0;
		top = 0;
		width = 0;
		height = 0;
		nint display = Display;
		if (display == 0)
			return false;

		nint monitors = 0;
		try
		{
			monitors = XRRGetMonitors(display, XDefaultRootWindow(display), 1, out int count);
			if (monitors == 0 || count <= 0)
				return false;

			int chosen = 0;
			for (int index = 0; index < count; index++)
			{
				nint entry = monitors + index * MonitorInfoSize;
				if (Marshal.ReadInt32(entry, 8) != 0)
				{
					chosen = index;
					break;
				}
				if (Marshal.ReadInt32(entry, 20) == 0 && Marshal.ReadInt32(entry, 24) == 0)
					chosen = index;
			}

			nint monitor = monitors + chosen * MonitorInfoSize;
			left = Marshal.ReadInt32(monitor, 20);
			top = Marshal.ReadInt32(monitor, 24);
			width = Marshal.ReadInt32(monitor, 28);
			height = Marshal.ReadInt32(monitor, 32);
			return width > 0 && height > 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
		finally
		{
			if (monitors != 0)
				XRRFreeMonitors(monitors);
		}
	}

	public static bool TryGetPrimaryScreen(out int left, out int top, out int width, out int height, out int workLeft, out int workTop, out int workWidth, out int workHeight)
	{
		workLeft = 0;
		workTop = 0;
		workWidth = 0;
		workHeight = 0;
		if (!TryGetPrimaryMonitorBounds(out left, out top, out width, out height))
			return false;

		workLeft = left;
		workTop = top;
		workWidth = width;
		workHeight = height;
		if (TryGetWorkArea(out int areaLeft, out int areaTop, out int areaWidth, out int areaHeight))
		{
			int right = Math.Min(left + width, areaLeft + areaWidth);
			int bottom = Math.Min(top + height, areaTop + areaHeight);
			int clippedLeft = Math.Max(left, areaLeft);
			int clippedTop = Math.Max(top, areaTop);
			if (right - clippedLeft >= width / 2 && bottom - clippedTop >= height / 2)
			{
				workLeft = clippedLeft;
				workTop = clippedTop;
				workWidth = right - clippedLeft;
				workHeight = bottom - clippedTop;
			}
		}
		return true;
	}

	public static bool TryGetScreenBounds(out int width, out int height)
	{
		width = 0;
		height = 0;
		nint display = Display;

		if (display == 0)
			return false;

		try
		{
			nint root = XDefaultRootWindow(display);

			if (!TryGetGeometry(display, root, out _, out _, out int rootWidth, out int rootHeight))
				return false;

			width = rootWidth;
			height = rootHeight;
			return width > 0 && height > 0;
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

	public static bool TryGetWorkArea(out int left, out int top, out int width, out int height)
	{
		left = 0;
		top = 0;
		width = 0;
		height = 0;
		nint display = Display;
		if (display == 0)
			return false;

		nint data = 0;
		try
		{
			nint root = XDefaultRootWindow(display);
			nint atom = XInternAtom(display, "_NET_WORKAREA", true);
			if (root == 0 || atom == 0)
				return false;
			if (!TryGetProperty(display, root, atom, out data, out ulong count, out int format) || format != 32 || count < 4)
				return false;

			left = (int)Marshal.ReadIntPtr(data, 0);
			top = (int)Marshal.ReadIntPtr(data, IntPtr.Size);
			width = (int)Marshal.ReadIntPtr(data, IntPtr.Size * 2);
			height = (int)Marshal.ReadIntPtr(data, IntPtr.Size * 3);
			return width > 0 && height > 0;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			if (data != 0)
				_ = XFree(data);
		}
	}

	public static bool TrySetClickThrough(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0 || !HasShapeExtension(display))
			return false;

		try
		{
			XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);
			_ = XSync(display, 0);
			return TryGetInputShapeRectangleCount(window, out int count) && count == 0;
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

	private static bool HasShapeExtension(nint display)
	{
		if (_shapeState != 0)
			return _shapeState > 0;
		try
		{
			_shapeState = XShapeQueryExtension(display, out _, out _) != 0 ? 1 : -1;
		}
		catch (DllNotFoundException)
		{
			_shapeState = -1;
		}
		catch (EntryPointNotFoundException)
		{
			_shapeState = -1;
		}
		return _shapeState > 0;
	}

	public static bool TrySetBoundingRectangles(nint window, IReadOnlyList<(int X, int Y, int Width, int Height)> rectangles)
	{
		nint display = Display;
		if (display == 0 || window == 0 || !HasShapeExtension(display))
			return false;

		try
		{
			List<XRectangle> nativeRectangles = new(rectangles.Count);
			foreach ((int x, int y, int width, int height) in rectangles)
			{
				if (width <= 0 || height <= 0)
					continue;
				nativeRectangles.Add(new XRectangle(
					(short)Math.Clamp(x, short.MinValue, short.MaxValue),
					(short)Math.Clamp(y, short.MinValue, short.MaxValue),
					(ushort)Math.Clamp(width, 1, ushort.MaxValue),
					(ushort)Math.Clamp(height, 1, ushort.MaxValue)));
			}

			if (nativeRectangles.Count == 0)
				return false;

			int size = Marshal.SizeOf<XRectangle>();
			nint buffer = Marshal.AllocHGlobal(size * nativeRectangles.Count);
			try
			{
				for (int index = 0; index < nativeRectangles.Count; index++)
					Marshal.StructureToPtr(nativeRectangles[index], buffer + index * size, false);
				XShapeCombineRectangles(display, window, ShapeBounding, 0, 0, buffer, nativeRectangles.Count, ShapeSet, Unsorted);
				_ = XFlush(display);
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}

			return TryGetBoundingShapeRectangleCount(window, out int applied) && applied > 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TrySetInputRectangles(nint window, IReadOnlyList<(int X, int Y, int Width, int Height)> rectangles)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			List<XRectangle> nativeRectangles = new(rectangles.Count);
			foreach ((int x, int y, int width, int height) in rectangles)
			{
				if (width <= 0 || height <= 0)
					continue;
				nativeRectangles.Add(new XRectangle(
					(short)Math.Clamp(x, short.MinValue, short.MaxValue),
					(short)Math.Clamp(y, short.MinValue, short.MaxValue),
					(ushort)Math.Clamp(width, 1, ushort.MaxValue),
					(ushort)Math.Clamp(height, 1, ushort.MaxValue)));
			}

			if (nativeRectangles.Count == 0)
				return TrySetClickThrough(window);

			int size = Marshal.SizeOf<XRectangle>();
			nint buffer = Marshal.AllocHGlobal(size * nativeRectangles.Count);
			try
			{
				for (int index = 0; index < nativeRectangles.Count; index++)
					Marshal.StructureToPtr(nativeRectangles[index], buffer + index * size, false);
				XShapeCombineRectangles(display, window, ShapeInput, 0, 0, buffer, nativeRectangles.Count, ShapeSet, Unsorted);
				_ = XFlush(display);
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
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

	public static bool TrySetRoundedCorners(nint window, int width, int height, int radius)
	{
		nint display = Display;
		if (display == 0 || window == 0 || width <= 0 || height <= 0)
			return false;

		int limit = Math.Min(width, height) / 2;
		if (radius > limit)
			radius = limit;

		if (radius <= 0)
			return TryClearShape(window);

		try
		{
			List<XRectangle> rectangles = new(radius * 2 + 1);

			for (int y = 0; y < radius; y++)
			{
				double dy = radius - y - 0.5;
				double dx = radius - Math.Sqrt(radius * radius - dy * dy);
				int inset = (int)Math.Round(dx);
				int rowWidth = width - inset * 2;
				if (rowWidth <= 0)
					continue;

				rectangles.Add(new XRectangle((short)inset, (short)y, (ushort)rowWidth, 1));
				rectangles.Add(new XRectangle((short)inset, (short)(height - y - 1), (ushort)rowWidth, 1));
			}

			int middleHeight = height - radius * 2;
			if (middleHeight > 0)
				rectangles.Add(new XRectangle(0, (short)radius, (ushort)width, (ushort)middleHeight));
			if (width < short.MaxValue)
				rectangles.Add(new XRectangle((short)width, 0, (ushort)(short.MaxValue - width), (ushort)short.MaxValue));
			if (height < short.MaxValue)
				rectangles.Add(new XRectangle(0, (short)height, (ushort)width, (ushort)(short.MaxValue - height)));

			if (rectangles.Count == 0)
				return false;

			int size = Marshal.SizeOf<XRectangle>();
			nint buffer = Marshal.AllocHGlobal(size * rectangles.Count);
			try
			{
				for (int i = 0; i < rectangles.Count; i++)
					Marshal.StructureToPtr(rectangles[i], buffer + i * size, false);

				XShapeCombineRectangles(display, window, ShapeBounding, 0, 0, buffer, rectangles.Count, ShapeSet, Unsorted);
				_ = XFlush(display);
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}

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

	public static bool TryClearShape(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			XShapeCombineMask(display, window, ShapeBounding, 0, 0, 0, ShapeSet);
			_ = XFlush(display);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryResetInputShape(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			XShapeCombineMask(display, window, ShapeInput, 0, 0, 0, ShapeSet);
			_ = XFlush(display);
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

	public static bool TryGetBoundingShapeRectangleCount(nint window, out int count)
	{
		return TryGetShapeRectangleCount(window, ShapeBounding, out count);
	}

	public static bool TryGetBoundingShapeBounds(nint window, out int x, out int y, out int width, out int height, out long area)
	{
		x = 0;
		y = 0;
		width = 0;
		height = 0;
		area = 0;
		nint display = Display;
		if (display == 0 || window == 0 || !HasShapeExtension(display) || !IsLiveWindow(window))
			return false;

		nint rectangles = 0;
		try
		{
			_ = XSync(display, 0);
			rectangles = XShapeGetRectangles(display, window, ShapeBounding, out int count, out _);
			if (rectangles == 0 || count <= 0)
				return false;

			int size = Marshal.SizeOf<XRectangle>();
			int left = int.MaxValue;
			int top = int.MaxValue;
			int right = int.MinValue;
			int bottom = int.MinValue;
			for (int index = 0; index < count; index++)
			{
				XRectangle rectangle = Marshal.PtrToStructure<XRectangle>(rectangles + index * size);
				if (rectangle.Width == 0 || rectangle.Height == 0)
					continue;
				left = Math.Min(left, rectangle.X);
				top = Math.Min(top, rectangle.Y);
				right = Math.Max(right, rectangle.X + rectangle.Width);
				bottom = Math.Max(bottom, rectangle.Y + rectangle.Height);
				area += (long)rectangle.Width * rectangle.Height;
			}
			if (left == int.MaxValue || right <= left || bottom <= top)
				return false;
			x = left;
			y = top;
			width = right - left;
			height = bottom - top;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			if (rectangles != 0)
				_ = XFree(rectangles);
		}
	}

	public static bool TryGetInputShapeRectangleCount(nint window, out int count)
	{
		return TryGetShapeRectangleCount(window, ShapeInput, out count);
	}

	private static bool TryGetShapeRectangleCount(nint window, int kind, out int count)
	{
		count = 0;
		nint display = Display;
		if (display == 0 || window == 0 || !HasShapeExtension(display) || !IsLiveWindow(window))
			return false;

		nint rectangles = 0;
		try
		{
			_ = XSync(display, 0);
			rectangles = XShapeGetRectangles(display, window, kind, out count, out _);
			_ = XSync(display, 0);
			return count >= 0 && IsLiveWindow(window);
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
		finally
		{
			if (rectangles != 0)
				_ = XFree(rectangles);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XRectangle
	{
		public short X;
		public short Y;
		public ushort Width;
		public ushort Height;

		public XRectangle(short x, short y, ushort width, ushort height)
		{
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}
	}

	public static bool TrySetOverrideRedirect(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			XSetWindowAttributes attributes = default;
			attributes.OverrideRedirect = 1;

			_ = XUnmapWindow(display, window);
			_ = XSync(display, 0);
			ReparentToRoot(display, window);
			XChangeWindowAttributes(display, window, CwOverrideRedirect, ref attributes);
			_ = XSync(display, 0);
			_ = XMapWindow(display, window);
			_ = XSync(display, 0);
			return IsLiveWindow(window) && IsOverrideRedirectWindow(window);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void ReparentToRoot(nint display, nint window)
	{
		if (!XQueryTree(display, window, out nint root, out nint parent, out nint children, out _))
			return;
		if (children != 0)
			_ = XFree(children);
		if (parent == 0 || parent == root || XTranslateCoordinates(display, window, root, 0, 0, out int rootX, out int rootY, out _) == 0)
			return;
		_ = XReparentWindow(display, window, root, rootX, rootY);
		_ = XSync(display, 0);
	}

	public static bool TryPrepareUnmappedOverlayWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			if (XGetWindowAttributes(display, window, out XWindowAttributes current) == 0 || current.MapState != IsUnmapped)
				return false;

			XSetWindowAttributes attributes = default;
			attributes.OverrideRedirect = 1;
			XChangeWindowAttributes(display, window, CwOverrideRedirect, ref attributes);

			nint wmHints = XInternAtom(display, "WM_HINTS", false);
			if (wmHints != 0)
			{
				nint[] hints = new nint[9];
				if (TryGetProperty(display, window, wmHints, out nint data, out ulong count, out int format) && format == 32)
				{
					for (int i = 0; i < (int)Math.Min(count, 9UL); i++)
						hints[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size);
				}
				if (data != 0)
					_ = XFree(data);
				hints[0] |= 1;
				hints[1] = 0;
				XChangeProperty(display, window, wmHints, wmHints, 32, 0, hints, hints.Length);
			}

			nint type = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
			nint notification = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", false);
			if (type != 0 && notification != 0)
				XChangeProperty(display, window, type, 4, 32, 0, [notification], 1);

			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint above = XInternAtom(display, "_NET_WM_STATE_ABOVE", false);
			nint skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false);
			nint skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false);
			if (state != 0 && above != 0 && skipTaskbar != 0 && skipPager != 0)
				XChangeProperty(display, window, state, 4, 32, 0, [above, skipTaskbar, skipPager], 3);

			_ = XSync(display, 0);
			bool prepared = IsOverrideRedirectWindow(window);
			if (prepared)
			{
				lock (Sync)
					PreparedOverlayWindows.Add(window);
			}
			return prepared;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryPrepareOverlayWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			if (!IsLiveWindow(window))
				return false;
			if (!IsOverrideRedirectWindow(window) && !TrySetOverrideRedirect(window))
				TrySetUndecorated(window);
			if (!IsLiveWindow(window))
				return false;

			nint type = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
			nint notification = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", false);
			if (type != 0 && notification != 0)
				XChangeProperty(display, window, type, 4, 32, 0, [notification], 1);

			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint above = XInternAtom(display, "_NET_WM_STATE_ABOVE", false);
			nint skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false);
			nint skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false);
			if (state != 0 && above != 0 && skipTaskbar != 0 && skipPager != 0)
				XChangeProperty(display, window, state, 4, 32, 0, [above, skipTaskbar, skipPager], 3);

			_ = XRaiseWindow(display, window);
			_ = XFlush(display);
			lock (Sync)
				PreparedOverlayWindows.Add(window);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool IsPreparedOverlayWindow(nint window)
	{
		if (window == 0)
			return false;
		bool recorded;
		lock (Sync)
			recorded = PreparedOverlayWindows.Contains(window);
		if (!recorded)
			return false;
		if (IsOverrideRedirectWindow(window))
			return true;
		lock (Sync)
			PreparedOverlayWindows.Remove(window);
		return false;
	}

	public static void ForgetPreparedOverlayWindow(nint window)
	{
		if (window == 0)
			return;
		lock (Sync)
			PreparedOverlayWindows.Remove(window);
	}

	public static bool IsOverrideRedirectWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;
		try
		{
			return XGetWindowAttributes(display, window, out XWindowAttributes attributes) != 0
				&& attributes.OverrideRedirect != 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TrySetOverlayWindowType(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint typeAtom = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
			nint notification = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", false);
			if (typeAtom == 0 || notification == 0)
				return false;

			XChangeProperty(display, window, typeAtom, 4, 32, 0, [notification], 1);

			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false);
			nint skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false);

			if (state != 0 && skipTaskbar != 0 && skipPager != 0)
				XChangeProperty(display, window, state, 4, 32, 0, [skipTaskbar, skipPager], 2);

			_ = XFlush(display);
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

	public static bool TrySetAlwaysOnTop(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			if (IsOverrideRedirectWindow(window))
			{
				_ = XRaiseWindow(display, window);
				_ = XFlush(display);
				return true;
			}

			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint above = XInternAtom(display, "_NET_WM_STATE_ABOVE", false);
			if (state == 0 || above == 0)
				return false;

			nint root = XDefaultRootWindow(display);
			XClientMessage message = new()
			{
				type = ClientMessage,
				serial = 0,
				send_event = 1,
				display = display,
				window = window,
				message_type = state,
				format = 32,
				data0 = NetWmStateAdd,
				data1 = above,
				data2 = 0,
				data3 = 1,
				data4 = 0
			};
			XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
			_ = XRaiseWindow(display, window);
			_ = XFlush(display);
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

	public static bool TryRaiseWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			_ = XRaiseWindow(display, window);
			_ = XFlush(display);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool TryFocusWindow(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			_ = XSetInputFocus(display, window, 1, 0);
			_ = XRaiseWindow(display, window);
			_ = XFlush(display);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool IsFullscreen(nint window)
	{
		return HasWindowStates(window, true, "_NET_WM_STATE_FULLSCREEN");
	}

	public static bool IsMaximized(nint window)
	{
		return HasWindowStates(window, true, "_NET_WM_STATE_MAXIMIZED_HORZ", "_NET_WM_STATE_MAXIMIZED_VERT");
	}

	public static bool TrySetFullscreen(nint window)
	{
		return TrySetFullscreen(window, true);
	}

	public static bool TrySetFullscreen(nint window, bool fullscreen)
	{
		return TryChangeWindowState(window, fullscreen, "_NET_WM_STATE_FULLSCREEN", null);
	}

	public static bool TrySetMaximized(nint window, bool maximized)
	{
		return TryChangeWindowState(window, maximized, "_NET_WM_STATE_MAXIMIZED_HORZ", "_NET_WM_STATE_MAXIMIZED_VERT");
	}

	private static bool HasWindowStates(nint window, bool requireAll, params string[] names)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		nint data = 0;
		try
		{
			nint state = XInternAtom(display, "_NET_WM_STATE", true);
			if (state == 0 || !TryGetProperty(display, window, state, out data, out ulong count, out int format) || format != 32)
				return false;
			HashSet<nint> current = [];
			for (ulong i = 0; i < count; i++)
				current.Add(Marshal.ReadIntPtr(data, (int)i * IntPtr.Size));
			foreach (string name in names)
			{
				nint atom = XInternAtom(display, name, true);
				bool present = atom != 0 && current.Contains(atom);
				if (requireAll && !present)
					return false;
				if (!requireAll && present)
					return true;
			}
			return requireAll;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
		finally
		{
			if (data != 0)
				_ = XFree(data);
		}
	}

	private static bool TryChangeWindowState(nint window, bool enabled, string firstName, string? secondName)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint first = XInternAtom(display, firstName, false);
			nint second = string.IsNullOrEmpty(secondName) ? 0 : XInternAtom(display, secondName, false);
			if (state == 0 || first == 0 || (!string.IsNullOrEmpty(secondName) && second == 0))
				return false;

			nint root = XDefaultRootWindow(display);
			if (root == 0)
				return false;
			XClientMessage message = new()
			{
				type = ClientMessage,
				serial = 0,
				send_event = 1,
				display = display,
				window = window,
				message_type = state,
				format = 32,
				data0 = enabled ? NetWmStateAdd : NetWmStateRemove,
				data1 = first,
				data2 = second,
				data3 = 1,
				data4 = 0
			};
			XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
			_ = XFlush(display);
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

	public static bool TrySetHiddenFromShell(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false);
			nint skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false);
			if (state == 0 || skipTaskbar == 0 || skipPager == 0)
				return false;

			nint root = XDefaultRootWindow(display);

			foreach (nint hint in new[] { skipTaskbar, skipPager })
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
					data0 = NetWmStateAdd,
					data1 = hint,
					data2 = 0,
					data3 = 1,
					data4 = 0
				};
				XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
			}

			_ = XFlush(display);
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

	public static IReadOnlyList<string> ApplyTaskbarVisibility(IReadOnlyList<nint> windows, string windowClass, bool hidden)
	{
		nint display = Display;
		if (display == 0 || windows == null || windows.Count == 0)
			return Array.Empty<string>();

		try
		{
			nint state = XInternAtom(display, "_NET_WM_STATE", false);
			nint skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", false);
			nint skipPager = XInternAtom(display, "_NET_WM_STATE_SKIP_PAGER", false);
			if (state == 0 || skipTaskbar == 0 || skipPager == 0)
				return Array.Empty<string>();

			nint root = XDefaultRootWindow(display);
			int processId = Environment.ProcessId;
			List<string> changed = new();

			foreach (nint window in windows)
			{
				if (window == 0)
					continue;

				int owner = GetWindowProcessId(display, window);
				if (owner != 0 && owner != processId)
					continue;

				if (!string.IsNullOrWhiteSpace(windowClass) && !MatchesWindowClass(display, window, windowClass))
					continue;

				if (IsOverrideRedirectWindow(window))
					continue;

				if (HasWindowState(display, window, state, skipTaskbar) == hidden)
					continue;

				if (XGetWindowAttributes(display, window, out XWindowAttributes attributes) != 0 && attributes.MapState == IsUnmapped)
				{
					SetWithdrawnWindowStates(display, window, state, hidden, skipTaskbar, skipPager);
					changed.Add("0x" + window.ToString("x") + " " + GetWindowTitle(display, window));
					continue;
				}

				foreach (nint hint in new[] { skipTaskbar, skipPager })
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
						data0 = hidden ? NetWmStateAdd : NetWmStateRemove,
						data1 = hint,
						data2 = 0,
						data3 = 1,
						data4 = 0
					};
					XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
				}

				changed.Add("0x" + window.ToString("x") + " " + GetWindowTitle(display, window));
			}

			if (changed.Count > 0)
				_ = XFlush(display);

			return changed;
		}
		catch (DllNotFoundException)
		{
			return Array.Empty<string>();
		}
		catch (EntryPointNotFoundException)
		{
			return Array.Empty<string>();
		}
	}

	private static bool MatchesWindowClass(nint display, nint window, string windowClass)
	{
		nint atom = XInternAtom(display, "WM_CLASS", true);
		if (atom == 0)
			return false;

		if (!TryGetProperty(display, window, atom, out nint data, out ulong count, out int format) || format != 8)
		{
			if (data != 0)
				_ = XFree(data);
			return false;
		}

		try
		{
			byte[] bytes = new byte[(int)count];
			Marshal.Copy(data, bytes, 0, bytes.Length);
			foreach (string part in System.Text.Encoding.UTF8.GetString(bytes).Split('\0'))
			{
				if (string.Equals(part, windowClass, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
		finally
		{
			_ = XFree(data);
		}
	}

	private static void SetWithdrawnWindowStates(nint display, nint window, nint state, bool add, params nint[] hints)
	{
		List<nint> atoms = [];
		if (TryGetProperty(display, window, state, out nint data, out ulong count, out int format) && format == 32)
		{
			for (ulong i = 0; i < count; i++)
				atoms.Add(Marshal.ReadIntPtr(data, (int)i * IntPtr.Size));
		}
		if (data != 0)
			_ = XFree(data);

		foreach (nint hint in hints)
		{
			atoms.Remove(hint);
			if (add)
				atoms.Add(hint);
		}

		XChangeProperty(display, window, state, 4, 32, 0, [.. atoms], atoms.Count);
	}

	private static bool HasWindowState(nint display, nint window, nint state, nint hint)
	{
		if (!TryGetProperty(display, window, state, out nint data, out ulong count, out int format) || format != 32)
		{
			if (data != 0)
				_ = XFree(data);
			return false;
		}

		try
		{
			for (ulong i = 0; i < count; i++)
			{
				if (Marshal.ReadIntPtr(data, (int)i * IntPtr.Size) == hint)
					return true;
			}

			return false;
		}
		finally
		{
			_ = XFree(data);
		}
	}

	public static bool TrySetInvisible(nint window)
	{
		nint display = Display;
		if (display == 0 || window == 0)
			return false;

		try
		{
			XShapeCombineRectangles(display, window, ShapeBounding, 0, 0, 0, 0, ShapeSet, Unsorted);
			XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);
			_ = XFlush(display);
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

	[LibraryImport("libX11.so.6")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint childCount);

	private static IEnumerable<nint> EnumerateClientWindows(nint display)
	{
		nint root = XDefaultRootWindow(display);
		nint atom = XInternAtom(display, "_NET_CLIENT_LIST", true);
		nint data = 0;
		ulong count = 0;
		int format = 0;
		if (atom == 0 || !TryGetProperty(display, root, atom, out data, out count, out format) || format != 32)
		{
			if (data != 0)
				_ = XFree(data);
			List<nint> clients = [];
			CollectTreeClients(display, root, XInternAtom(display, "WM_STATE", true), clients, 0);
			foreach (nint client in clients)
				yield return client;
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
			_ = XFree(data);
		}
	}

	private static void CollectTreeClients(nint display, nint parent, nint wmState, List<nint> clients, int depth)
	{
		if (depth > 3 || !XQueryTree(display, parent, out _, out _, out nint children, out uint count) || children == 0)
			return;

		try
		{
			for (uint i = 0; i < count; i++)
			{
				nint window = Marshal.ReadIntPtr(children, (int)i * IntPtr.Size);
				if (window == 0)
					continue;

				nint stateData = 0;
				bool managed = wmState != 0 && TryGetProperty(display, window, wmState, out stateData, out _, out _);
				if (stateData != 0)
					_ = XFree(stateData);
				if (managed || depth == 0 && TryGetClassHint(display, window, out _, out _))
					clients.Add(window);
				else
					CollectTreeClients(display, window, wmState, clients, depth + 1);
			}
		}
		finally
		{
			_ = XFree(children);
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
		if (!title.Contains("Roblox", StringComparison.OrdinalIgnoreCase)
			&& !title.Contains("Sober", StringComparison.OrdinalIgnoreCase))
			return false;
		return IsSoberProcessIdentity(GetWindowProcessId(display, window));
	}

	private static bool IsSoberProcessIdentity(int processId)
	{
		if (processId <= 0)
			return false;
		string processDirectory = "/proc/" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		try
		{
			string name = File.ReadAllText(Path.Combine(processDirectory, "comm")).Trim();
			if (IsSoberExecutableName(name))
				return true;
		}
		catch (Exception)
		{
		}
		try
		{
			string? executable = new FileInfo(Path.Combine(processDirectory, "exe")).LinkTarget;
			if (IsSoberExecutableName(Path.GetFileName(executable)))
				return true;
		}
		catch (Exception)
		{
		}
		try
		{
			string commandLine = File.ReadAllText(Path.Combine(processDirectory, "cmdline"));
			int separator = commandLine.IndexOf('\0');
			string executable = separator >= 0 ? commandLine[..separator] : commandLine;
			if (IsSoberExecutableName(Path.GetFileName(executable)))
				return true;
		}
		catch (Exception)
		{
		}
		try
		{
			string environment = File.ReadAllText(Path.Combine(processDirectory, "environ"));
			if (environment.Split('\0', StringSplitOptions.RemoveEmptyEntries)
				.Any(value => string.Equals(value, "FLATPAK_ID=org.vinegarhq.Sober", StringComparison.OrdinalIgnoreCase)))
				return true;
		}
		catch (Exception)
		{
		}
		try
		{
			string cgroup = File.ReadAllText(Path.Combine(processDirectory, "cgroup"));
			return cgroup.Contains("org.vinegarhq.Sober", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsSoberExecutableName(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;
		string name = Path.GetFileName(value.Trim());
		if (name.EndsWith(" (deleted)", StringComparison.Ordinal))
			name = name[..^10];
		return string.Equals(name, "sober", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "org.vinegarhq.Sober", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(Path.GetFileNameWithoutExtension(name), "sober", StringComparison.OrdinalIgnoreCase);
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
				_ = XFree(data);
			return 0;
		}

		try
		{
			return Marshal.ReadIntPtr(data);
		}
		finally
		{
			_ = XFree(data);
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
				_ = XFree(data);
			return 0;
		}

		try
		{
			return (int)Marshal.ReadIntPtr(data);
		}
		finally
		{
			_ = XFree(data);
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
					_ = XFree(data);
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
				_ = XFree(legacy);
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
				_ = XFree(hint.ResourceName);
			if (hint.ResourceClass != 0)
				_ = XFree(hint.ResourceClass);
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

	public static void KeepIgnoringXErrors()
	{
		try
		{
			_errorHandler ??= IgnoreError;
			XSetErrorHandler(_errorHandler);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int XErrorHandler(nint display, nint errorEvent);

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XSetWindowAttributes
	{
		public nint BackgroundPixmap;
		public nint BackgroundPixel;
		public nint BorderPixmap;
		public nint BorderPixel;
		public int BitGravity;
		public int WinGravity;
		public int BackingStore;
		public nint BackingPlanes;
		public nint BackingPixel;
		public int SaveUnder;
		public nint EventMask;
		public nint DoNotPropagateMask;
		public int OverrideRedirect;
		public nint Colormap;
		public nint Cursor;
	}

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XVisualMasks
	{
		public nint ExtensionData;
		public nuint VisualId;
		public int Class;
		public nuint RedMask;
		public nuint GreenMask;
		public nuint BlueMask;
		public int BitsPerRgb;
		public int MapEntries;
	}

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XWindowAttributes
	{
		public int X;
		public int Y;
		public int Width;
		public int Height;
		public int BorderWidth;
		public int Depth;
		public nint Visual;
		public nint Root;
		public int Class;
		public int BitGravity;
		public int WinGravity;
		public int BackingStore;
		public nint BackingPlanes;
		public nint BackingPixel;
		public int SaveUnder;
		public nint Colormap;
		public int MapInstalled;
		public int MapState;
		public nint AllEventMasks;
		public nint YourEventMask;
		public nint DoNotPropagateMask;
		public int OverrideRedirect;
		public nint Screen;
	}

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XImageInfo
	{
		public int Width;
		public int Height;
		public int XOffset;
		public int Format;
		public nint Data;
		public int ByteOrder;
		public int BitmapUnit;
		public int BitmapBitOrder;
		public int BitmapPad;
		public int Depth;
		public int BytesPerLine;
		public int BitsPerPixel;
		public nuint RedMask;
		public nuint GreenMask;
		public nuint BlueMask;
	}

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XShmSegmentInfo
	{
		public nint ShmSegment;
		public int Shmid;
		public nint ShmAddress;
		public int ReadOnly;
	}

	[LibraryImport("libX11.so.6")]
	private static partial int XChangeWindowAttributes(nint display, nint window, nint valueMask, ref XSetWindowAttributes attributes);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);

	[LibraryImport("libX11.so.6")]
	private static partial int XUnmapWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XMapWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XReparentWindow(nint display, nint window, nint parent, int x, int y);

	[LibraryImport("libX11.so.6")]
	private static partial int XSync(nint display, int discard);

	[LibraryImport("libX11.so.6")]
	private static partial nint XSetErrorHandler(XErrorHandler handler);

	[StructLayout(LayoutKind.Sequential)]
	private partial struct XClassHint
	{
		public nint ResourceName;
		public nint ResourceClass;
	}

	[LibraryImport("libX11.so.6")]
	private static partial int XInitThreads();

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XOpenDisplay(string? display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XDefaultRootWindow(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XDefaultScreen(nint display);

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

	[LibraryImport("libX11.so.6")]
	private static partial nint XGetSelectionOwner(nint display, nint selection);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetWindowProperty(
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

	[LibraryImport("libX11.so.6")]
	private static partial int XGetGeometry(
		nint display,
		nint drawable,
		out nint root,
		out int x,
		out int y,
		out uint width,
		out uint height,
		out uint borderWidth,
		out uint depth);

	[LibraryImport("libX11.so.6")]
	private static partial nint XGetImage(nint display, nint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

	[LibraryImport("libX11.so.6")]
	private static partial int XDestroyImage(nint image);

	[LibraryImport("libX11.so.6")]
	private static partial int XFreePixmap(nint display, nint pixmap);

	[LibraryImport("libXext.so.6")]
	private static partial int XShmQueryExtension(nint display);

	[LibraryImport("libXext.so.6")]
	private static partial nint XShmCreateImage(
		nint display,
		nint visual,
		uint depth,
		int format,
		nint data,
		ref XShmSegmentInfo segment,
		uint width,
		uint height);

	[LibraryImport("libXext.so.6")]
	private static partial int XShmAttach(nint display, ref XShmSegmentInfo segment);

	[LibraryImport("libXext.so.6")]
	private static partial int XShmDetach(nint display, ref XShmSegmentInfo segment);

	[LibraryImport("libXext.so.6")]
	private static partial int XShmGetImage(nint display, nint drawable, nint image, int x, int y, nuint planeMask);

	[LibraryImport("libc.so.6", EntryPoint = "shmget")]
	private static partial int ShmGet(nint key, nuint size, int flags);

	[LibraryImport("libc.so.6", EntryPoint = "shmat")]
	private static partial nint ShmAt(int shmid, nint address, int flags);

	[LibraryImport("libc.so.6", EntryPoint = "shmdt")]
	private static partial int ShmDetach(nint address);

	[LibraryImport("libc.so.6", EntryPoint = "shmctl")]
	private static partial int ShmControl(int shmid, int command, nint buffer);

	[LibraryImport("libXcomposite.so.1")]
	private static partial int XCompositeQueryExtension(nint display, out int eventBase, out int errorBase);

	[LibraryImport("libXcomposite.so.1")]
	private static partial nint XCompositeNameWindowPixmap(nint display, nint window);

	[LibraryImport("libXdamage.so.1")]
	private static partial int XDamageQueryExtension(nint display, out int eventBase, out int errorBase);

	[LibraryImport("libXdamage.so.1")]
	private static partial nint XDamageCreate(nint display, nint drawable, int level);

	[LibraryImport("libXdamage.so.1")]
	private static partial void XDamageDestroy(nint display, nint damage);

	[LibraryImport("libXdamage.so.1")]
	private static partial void XDamageSubtract(nint display, nint damage, nint repair, nint parts);

	[LibraryImport("libX11.so.6")]
	private static partial int XTranslateCoordinates(
		nint display,
		nint sourceWindow,
		nint destinationWindow,
		int sourceX,
		int sourceY,
		out int destinationX,
		out int destinationY,
		out nint child);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetClassHint(nint display, nint window, ref XClassHint hint);

	[LibraryImport("libX11.so.6")]
	private static partial int XSetClassHint(nint display, nint window, ref XClassHint hint);

	[LibraryImport("libX11.so.6")]
	private static partial int XFetchName(nint display, nint window, out nint name);

	[LibraryImport("libX11.so.6")]
	private static partial int XFree(nint data);

	[LibraryImport("libX11.so.6")]
	private static partial int XFlush(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XCheckTypedEvent(nint display, int eventType, out XClientMessage eventReturn);

	[LibraryImport("libX11.so.6")]
	private static partial int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

	[LibraryImport("libX11.so.6")]
	private static partial int XUngrabPointer(nint display, nint time);

	private const int ClientMessage = 33;

	private const nint NetWmStateAdd = 1;

	private const nint NetWmStateRemove = 0;

	private const nint SubstructureRedirectMask = 1 << 20;

	private const nint SubstructureNotifyMask = 1 << 19;

	[StructLayout(LayoutKind.Sequential, Size = 192)]
	private partial struct XClientMessage
	{
		public int type;
		public nint serial;
		public int send_event;
		public nint display;
		public nint window;
		public nint message_type;
		public int format;
		public nint data0;
		public nint data1;
		public nint data2;
		public nint data3;
		public nint data4;
	}

	[LibraryImport("libX11.so.6")]
	private static partial int XSendEvent(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, nint eventMask, ref XClientMessage send_event);

	[LibraryImport("libX11.so.6")]
	private static partial int XRaiseWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XSetInputFocus(nint display, nint focus, int revertTo, nuint time);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetInputFocus(nint display, out nint focus, out int revertTo);

	[LibraryImport("libX11.so.6")]
	private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, [In] nint[] data, int count);

	[LibraryImport("libX11.so.6")]
	private static partial int XDeleteProperty(nint display, nint window, nint property);

	[LibraryImport("libX11.so.6")]
	private static partial int XSetWindowBackground(nint display, nint window, nuint pixel);

	private const int MonitorInfoSize = 56;

	[LibraryImport("libXrandr.so.2")]
	private static partial nint XRRGetMonitors(nint display, nint window, int getActive, out int count);

	[LibraryImport("libXrandr.so.2")]
	private static partial void XRRFreeMonitors(nint monitors);

	[LibraryImport("libX11.so.6")]
	private static partial int XQueryPointer(
		nint display,
		nint window,
		out nint root,
		out nint child,
		out int rootX,
		out int rootY,
		out int windowX,
		out int windowY,
		out uint mask);


	[LibraryImport("libXext.so.6")]
	private static partial int XShapeQueryExtension(nint display, out int eventBase, out int errorBase);

	[LibraryImport("libXext.so.6")]
	private static partial void XShapeCombineRectangles(
		nint display,
		nint window,
		int kind,
		int xOffset,
		int yOffset,
		nint rectangles,
		int count,
		int operation,
		int ordering);

	[LibraryImport("libXext.so.6")]
	private static partial void XShapeCombineMask(
		nint display,
		nint window,
		int kind,
		int xOffset,
		int yOffset,
		nint bitmap,
		int operation);

	[LibraryImport("libXext.so.6")]
	private static partial nint XShapeGetRectangles(
		nint display,
		nint window,
		int kind,
		out int count,
		out int ordering);
}
