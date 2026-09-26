using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public static unsafe partial class LinuxWindowShadow
{
	private const double BaseMargin = 32.0;
	private const int MaxRestackAttempts = 4;
	private const int MaxPlacementAttempts = 3;
	private const int EventSize = 192;
	private const int OpenNonBlocking = 0x800;
	private const int OpenCloseOnExec = 0x80000;
	private const short PollIn = 1;
	private const int Interrupted = 4;

	private const int ConfigureNotify = 22;
	private const int MapNotify = 19;
	private const int UnmapNotify = 18;
	private const int DestroyNotify = 17;
	private const int ReparentNotify = 21;
	private const int PropertyNotify = 28;
	private const int ClientMessage = 33;

	private const nint StructureNotifyMask = 1 << 17;
	private const nint PropertyChangeMask = 1 << 22;
	private const nint SubstructureNotifyMask = 1 << 19;
	private const nint SubstructureRedirectMask = 1 << 20;

	private const nuint CwBackPixel = 1 << 1;
	private const nuint CwBorderPixel = 1 << 3;
	private const nuint CwEventMask = 1 << 11;
	private const nuint CwColormap = 1 << 13;

	private const nint AtomType = 4;
	private const nint CardinalType = 6;
	private const nint WindowType = 33;
	private const nint WmHintsAtom = 35;
	private const nint WmNormalHintsAtom = 40;
	private const nint WmSizeHintsType = 41;
	private const nint WmClassAtom = 67;
	private const nint WmTransientForAtom = 68;

	private const int QueuedAlready = 0;
	private const int IsViewable = 2;
	private const int InputOutput = 1;
	private const int TrueColor = 4;
	private const int ZPixmap = 2;
	private const int LsbFirst = 0;
	private const int ShapeInput = 2;
	private const int ShapeSet = 0;
	private const int Unsorted = 0;
	private const int PropModeReplace = 0;
	private const nint RestackBelow = 1;
	private const nint SourcePager = 2;
	private const nint StateRemove = 0;
	private const nint StateAdd = 1;
	private const long AllDesktops = 0xFFFFFFFF;

	private static readonly ShadowStyle FocusedStyle = new(8.0, 0.32, 1.5, 0.2);
	private static readonly ShadowStyle UnfocusedStyle = new(6.0, 0.18, 1.5, 0.1);

	private static readonly object StartSync = new();
	private static readonly ConcurrentQueue<Request> Requests = new();
	private static Thread? _thread;
	private static volatile bool _disabled;
	private static int _wakeRead = -1;
	private static int _wakeWrite = -1;
	private static Action<string>? _log;

	public static void Track(nint window, int cornerRadius, double scale, bool suppressed, Action<string>? log = null)
	{
		if (!OperatingSystem.IsLinux() || window == 0 || _disabled)
			return;
		if (log != null)
			_log = log;
		if (!EnsureStarted())
			return;
		double safeScale = scale > 0.0 && double.IsFinite(scale) ? scale : 1.0;
		Requests.Enqueue(new Request(window, Math.Max(0, cornerRadius), safeScale, suppressed, false));
		Wake();
	}

	public static void Forget(nint window)
	{
		if (window == 0 || _thread is null || _disabled)
			return;
		Requests.Enqueue(new Request(window, 0, 1.0, true, true));
		Wake();
	}

	private static bool EnsureStarted()
	{
		lock (StartSync)
		{
			if (_disabled)
				return false;
			if (_thread != null)
				return true;
			if (Environment.GetEnvironmentVariable("VOIDSTRAP_WINDOW_SHADOW") == "0")
			{
				Disable("VOIDSTRAP_WINDOW_SHADOW is set to 0");
				return false;
			}
			if (LinuxSteamOS.Current.IsGamescopeSession)
			{
				Disable("gamescope shows each window on its own, so companion shadow windows would appear as extra windows");
				return false;
			}
			if (!LinuxWindowInterop.IsAvailable)
			{
				Disable("the X display is unavailable");
				return false;
			}

			try
			{
				int* descriptors = stackalloc int[2];
				if (pipe2(descriptors, OpenNonBlocking | OpenCloseOnExec) != 0)
				{
					Disable("the wake pipe could not be created");
					return false;
				}
				_wakeRead = descriptors[0];
				_wakeWrite = descriptors[1];
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				Disable(ex.Message);
				return false;
			}

			_thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Voidstrap window shadows"
			};
			_thread.Start();
			return true;
		}
	}

	private static void Disable(string reason)
	{
		if (_disabled)
			return;
		_disabled = true;
		_log?.Invoke("Window shadows are off because " + reason);
	}

	private static void Wake()
	{
		int descriptor = _wakeWrite;
		if (descriptor < 0)
			return;
		byte signal = 1;
		_ = write(descriptor, &signal, 1);
	}

	private static void Run()
	{
		ShadowHost? host = null;
		try
		{
			host = ShadowHost.Open(out string reason);
			if (host is null)
			{
				Disable(reason);
				return;
			}
			_log?.Invoke("Voidstrap draws its own window shadows because " + host.WindowManager + " leaves frameless windows without one");
			host.Run();
		}
		catch (Exception ex)
		{
			Disable("the shadow thread stopped, " + ex.Message);
		}
		finally
		{
			host?.Dispose();
			Requests.Clear();
		}
	}

	private readonly record struct Request(nint Window, int Radius, double Scale, bool Suppressed, bool Forget);

	private readonly record struct ShadowStyle(
		double AmbientBlur,
		double AmbientOpacity,
		double ContactBlur,
		double ContactOpacity);

	private readonly record struct PaintKey(
		int Width,
		int Height,
		int OffsetX,
		int OffsetY,
		int TargetWidth,
		int TargetHeight,
		int Radius,
		int Margin,
		double Scale,
		bool Focused);

	private readonly record struct ShadowRect(int X, int Y, int Width, int Height)
	{
		public bool IsEmpty => Width <= 0 || Height <= 0;

		public long Area => IsEmpty ? 0 : (long)Width * Height;

		public ShadowRect Intersect(ShadowRect other)
		{
			int left = Math.Max(X, other.X);
			int top = Math.Max(Y, other.Y);
			int right = Math.Min(X + Width, other.X + other.Width);
			int bottom = Math.Min(Y + Height, other.Y + other.Height);
			return right > left && bottom > top ? new ShadowRect(left, top, right - left, bottom - top) : default;
		}
	}

	private readonly struct ShadowLayer
	{
		private readonly double _centerX;
		private readonly double _centerY;
		private readonly double _halfWidth;
		private readonly double _halfHeight;
		private readonly double _radius;
		private readonly double _spread;
		private readonly double _opacity;

		public ShadowLayer(double left, double top, double width, double height, double radius, double blur, double opacity)
		{
			_halfWidth = width / 2.0;
			_halfHeight = height / 2.0;
			_centerX = left + _halfWidth;
			_centerY = top + _halfHeight;
			_radius = Math.Min(radius, Math.Min(_halfWidth, _halfHeight));
			_spread = Math.Max(blur, 0.5) * Math.Sqrt(2.0);
			_opacity = opacity;
		}

		public double Alpha(double x, double y)
		{
			double qx = Math.Abs(x - _centerX) - (_halfWidth - _radius);
			double qy = Math.Abs(y - _centerY) - (_halfHeight - _radius);
			double ox = Math.Max(qx, 0.0);
			double oy = Math.Max(qy, 0.0);
			double distance = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0.0) - _radius;
			return _opacity * 0.5 * (1.0 - Erf(distance / _spread));
		}

		private static double Erf(double value)
		{
			double sign = value < 0.0 ? -1.0 : 1.0;
			double x = Math.Abs(value);
			double t = 1.0 / (1.0 + 0.3275911 * x);
			double poly = ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t;
			return sign * (1.0 - poly * Math.Exp(-x * x));
		}
	}

	private sealed class Shadow
	{
		public Shadow(nint target)
		{
			Target = target;
		}

		public nint Target { get; }
		public nint Window;
		public int Radius;
		public double Scale = 1.0;
		public bool CallerSuppressed;
		public bool TargetMapped;
		public bool StateSuppressed;
		public bool TargetAbove;
		public long TargetDesktop = -1;
		public long? TargetOpacity;
		public ShadowRect TargetBounds;
		public bool NeedsGeometry = true;
		public bool NeedsState = true;
		public bool NeedsDesktop = true;
		public bool NeedsOpacity = true;
		public bool MapRequested;
		public bool Mapped;
		public bool ShadowAbove;
		public long ShadowDesktop = -1;
		public long? ShadowOpacity;
		public bool OpacityApplied;
		public ShadowRect Placed;
		public bool VerifyPlacement;
		public int PlacementAttempts;
		public PaintKey Painted;
		public int RestackAttempts;
	}

	private sealed class ShadowHost : IDisposable
	{
		private readonly nint _display;
		private readonly nint _root;
		private readonly int _screen;
		private readonly Dictionary<nint, Shadow> _byTarget = new();
		private readonly Dictionary<nint, Shadow> _byWindow = new();
		private readonly Dictionary<long, List<ShadowRect>> _workAreas = new();
		private readonly nint _netSupported;
		private readonly nint _netRestackWindow;
		private readonly nint _netClientList;
		private readonly nint _netClientListStacking;
		private readonly nint _netActiveWindow;
		private readonly nint _netCurrentDesktop;
		private readonly nint _netWorkarea;
		private readonly nint _netWmState;
		private readonly nint _netWmStateHidden;
		private readonly nint _netWmStateFullscreen;
		private readonly nint _netWmStateMaximizedVert;
		private readonly nint _netWmStateMaximizedHorz;
		private readonly nint _netWmStateAbove;
		private readonly nint _netWmStateSticky;
		private readonly nint _netWmStateSkipTaskbar;
		private readonly nint _netWmStateSkipPager;
		private readonly nint _netWmDesktop;
		private readonly nint _netWmWindowOpacity;
		private readonly nint _netWmWindowType;
		private readonly nint _netWmWindowTypeUtility;
		private readonly nint _netWmUserTime;
		private readonly nint _netWmName;
		private readonly nint _utf8String;
		private readonly nint _motifWmHints;
		private readonly nint _comptonShadow;
		private nint _visual;
		private nint _colormap;
		private nint _gc;
		private bool _swapBytes;
		private nint _activeWindow;
		private long _currentDesktop;
		private bool _stackDirty;
		private bool _activeDirty;
		private bool _desktopDirty;

		private ShadowHost(nint display)
		{
			_display = display;
			_screen = XDefaultScreen(display);
			_root = XRootWindow(display, _screen);
			_netSupported = Intern("_NET_SUPPORTED");
			_netRestackWindow = Intern("_NET_RESTACK_WINDOW");
			_netClientList = Intern("_NET_CLIENT_LIST");
			_netClientListStacking = Intern("_NET_CLIENT_LIST_STACKING");
			_netActiveWindow = Intern("_NET_ACTIVE_WINDOW");
			_netCurrentDesktop = Intern("_NET_CURRENT_DESKTOP");
			_netWorkarea = Intern("_NET_WORKAREA");
			_netWmState = Intern("_NET_WM_STATE");
			_netWmStateHidden = Intern("_NET_WM_STATE_HIDDEN");
			_netWmStateFullscreen = Intern("_NET_WM_STATE_FULLSCREEN");
			_netWmStateMaximizedVert = Intern("_NET_WM_STATE_MAXIMIZED_VERT");
			_netWmStateMaximizedHorz = Intern("_NET_WM_STATE_MAXIMIZED_HORZ");
			_netWmStateAbove = Intern("_NET_WM_STATE_ABOVE");
			_netWmStateSticky = Intern("_NET_WM_STATE_STICKY");
			_netWmStateSkipTaskbar = Intern("_NET_WM_STATE_SKIP_TASKBAR");
			_netWmStateSkipPager = Intern("_NET_WM_STATE_SKIP_PAGER");
			_netWmDesktop = Intern("_NET_WM_DESKTOP");
			_netWmWindowOpacity = Intern("_NET_WM_WINDOW_OPACITY");
			_netWmWindowType = Intern("_NET_WM_WINDOW_TYPE");
			_netWmWindowTypeUtility = Intern("_NET_WM_WINDOW_TYPE_UTILITY");
			_netWmUserTime = Intern("_NET_WM_USER_TIME");
			_netWmName = Intern("_NET_WM_NAME");
			_utf8String = Intern("UTF8_STRING");
			_motifWmHints = Intern("_MOTIF_WM_HINTS");
			_comptonShadow = Intern("_COMPTON_SHADOW");
			WindowManager = ReadWindowManagerName();
		}

		public string WindowManager { get; }

		public static ShadowHost? Open(out string reason)
		{
			nint display = XOpenDisplay(null);
			if (display == 0)
			{
				reason = "the X display could not be opened";
				return null;
			}

			ShadowHost host = new(display);
			reason = host.Prepare();
			if (reason.Length == 0)
				return host;
			host.Dispose();
			return null;
		}

		private string Prepare()
		{
			nint selection = Intern("_NET_WM_CM_S" + _screen.ToString(CultureInfo.InvariantCulture));
			if (XGetSelectionOwner(_display, selection) == 0)
				return "no compositing manager is running";
			if (Array.IndexOf(ReadLongs(_root, _netSupported, AtomType), _netRestackWindow) < 0)
				return WindowManager + " cannot restack windows";

			XVisualInfo info;
			if (XMatchVisualInfo(_display, _screen, 32, TrueColor, &info) == 0 || info.Visual == 0)
				return "the display has no 32 bit visual";
			_visual = info.Visual;
			_colormap = XCreateColormap(_display, _root, _visual, 0);
			if (_colormap == 0)
				return "the shadow colormap could not be created";
			_swapBytes = (XImageByteOrder(_display) == LsbFirst) != BitConverter.IsLittleEndian;

			_ = XSelectInput(_display, _root, PropertyChangeMask);
			_currentDesktop = ReadCardinal(_root, _netCurrentDesktop) ?? 0;
			_activeWindow = ReadWindow(_root, _netActiveWindow);
			return "";
		}

		public void Run()
		{
			byte* buffer = stackalloc byte[EventSize];
			byte* scratch = stackalloc byte[64];
			PollDescriptor* descriptors = stackalloc PollDescriptor[2];
			int connection = XConnectionNumber(_display);
			while (true)
			{
				DrainRequests();
				while (XPending(_display) > 0)
				{
					new Span<byte>(buffer, EventSize).Clear();
					_ = XNextEvent(_display, buffer);
					Handle(buffer);
				}
				Refresh();
				_ = XFlush(_display);
				if (XEventsQueued(_display, QueuedAlready) > 0 || !Requests.IsEmpty)
					continue;

				descriptors[0] = new PollDescriptor { Descriptor = connection, Events = PollIn };
				descriptors[1] = new PollDescriptor { Descriptor = _wakeRead, Events = PollIn };
				if (poll(descriptors, 2, -1) < 0)
				{
					int error = Marshal.GetLastPInvokeError();
					if (error != Interrupted)
						throw new IOException("waiting for window events failed with error " + error.ToString(CultureInfo.InvariantCulture));
					continue;
				}
				if ((descriptors[1].ReturnedEvents & PollIn) != 0)
				{
					while (read(_wakeRead, scratch, 64) > 0)
					{
					}
				}
			}
		}

		private void DrainRequests()
		{
			while (Requests.TryDequeue(out Request request))
			{
				if (request.Forget)
				{
					if (_byTarget.TryGetValue(request.Window, out Shadow? gone))
						Destroy(gone, true);
					continue;
				}

				if (!_byTarget.TryGetValue(request.Window, out Shadow? shadow))
				{
					shadow = Adopt(request.Window);
					if (shadow is null)
						continue;
				}
				shadow.Radius = request.Radius;
				shadow.Scale = request.Scale;
				shadow.CallerSuppressed = request.Suppressed;
			}
		}

		private Shadow? Adopt(nint target)
		{
			_ = XSelectInput(_display, target, StructureNotifyMask | PropertyChangeMask);
			XWindowAttributes attributes;
			if (XGetWindowAttributes(_display, target, &attributes) == 0 || attributes.OverrideRedirect != 0)
			{
				_ = XSelectInput(_display, target, 0);
				return null;
			}

			Shadow shadow = new(target)
			{
				TargetMapped = attributes.MapState == IsViewable
			};
			_byTarget[target] = shadow;
			return shadow;
		}

		private void Handle(byte* buffer)
		{
			int type = *(int*)buffer;
			switch (type)
			{
				case ConfigureNotify:
				{
					nint window = *(nint*)(buffer + 40);
					if (_byTarget.TryGetValue(window, out Shadow? shadow))
					{
						shadow.NeedsGeometry = true;
						shadow.RestackAttempts = 0;
						_stackDirty = true;
					}
					else if (_byWindow.TryGetValue(window, out shadow))
					{
						shadow.VerifyPlacement = true;
					}
					break;
				}
				case MapNotify:
				{
					nint window = *(nint*)(buffer + 40);
					if (_byTarget.TryGetValue(window, out Shadow? shadow))
					{
						shadow.TargetMapped = true;
						shadow.NeedsGeometry = true;
						shadow.NeedsState = true;
						shadow.NeedsDesktop = true;
						shadow.RestackAttempts = 0;
					}
					else if (_byWindow.TryGetValue(window, out shadow))
					{
						shadow.Mapped = true;
						shadow.VerifyPlacement = true;
						shadow.PlacementAttempts = 0;
						shadow.RestackAttempts = 0;
						_stackDirty = true;
					}
					break;
				}
				case UnmapNotify:
				{
					nint window = *(nint*)(buffer + 40);
					if (_byTarget.TryGetValue(window, out Shadow? shadow))
					{
						shadow.TargetMapped = false;
					}
					else if (_byWindow.TryGetValue(window, out shadow))
					{
						shadow.Mapped = false;
						shadow.MapRequested = false;
					}
					break;
				}
				case DestroyNotify:
				{
					nint window = *(nint*)(buffer + 40);
					if (_byTarget.TryGetValue(window, out Shadow? shadow))
					{
						Destroy(shadow, false);
					}
					else if (_byWindow.TryGetValue(window, out shadow))
					{
						_byWindow.Remove(window);
						shadow.Window = 0;
						shadow.Mapped = false;
						shadow.MapRequested = false;
						shadow.Painted = default;
					}
					break;
				}
				case ReparentNotify:
				{
					if (_byTarget.TryGetValue(*(nint*)(buffer + 40), out Shadow? shadow))
						shadow.NeedsGeometry = true;
					break;
				}
				case PropertyNotify:
				{
					nint window = *(nint*)(buffer + 32);
					nint atom = *(nint*)(buffer + 40);
					if (window == _root)
					{
						HandleRootProperty(atom);
					}
					else if (_byTarget.TryGetValue(window, out Shadow? shadow))
					{
						if (atom == _netWmState)
							shadow.NeedsState = true;
						else if (atom == _netWmDesktop)
							shadow.NeedsDesktop = true;
						else if (atom == _netWmWindowOpacity)
							shadow.NeedsOpacity = true;
					}
					break;
				}
			}
		}

		private void HandleRootProperty(nint atom)
		{
			if (atom == _netClientListStacking)
			{
				_stackDirty = true;
			}
			else if (atom == _netActiveWindow)
			{
				_activeDirty = true;
			}
			else if (atom != _netClientList)
			{
				_workAreas.Clear();
				_desktopDirty = true;
			}
		}

		private void Refresh()
		{
			if (_activeDirty)
			{
				_activeDirty = false;
				_activeWindow = ReadWindow(_root, _netActiveWindow);
			}
			if (_desktopDirty)
			{
				_desktopDirty = false;
				_currentDesktop = ReadCardinal(_root, _netCurrentDesktop) ?? 0;
			}

			if (_byTarget.Count > 0)
			{
				foreach (Shadow shadow in _byTarget.Values.ToArray())
					Update(shadow);
			}

			if (_stackDirty)
			{
				_stackDirty = false;
				Restack();
			}
		}

		private void Update(Shadow shadow)
		{
			if (shadow.NeedsGeometry)
			{
				shadow.NeedsGeometry = false;
				if (!TryReadBounds(shadow.Target, out ShadowRect bounds))
				{
					Destroy(shadow, false);
					return;
				}
				shadow.TargetBounds = bounds;
			}
			if (shadow.NeedsState)
			{
				shadow.NeedsState = false;
				ReadState(shadow);
			}
			if (shadow.NeedsDesktop)
			{
				shadow.NeedsDesktop = false;
				shadow.TargetDesktop = ReadCardinal(shadow.Target, _netWmDesktop) ?? -1;
			}
			if (shadow.NeedsOpacity)
			{
				shadow.NeedsOpacity = false;
				shadow.TargetOpacity = ReadCardinal(shadow.Target, _netWmWindowOpacity);
			}

			ShadowRect target = shadow.TargetBounds;
			int margin = (int)Math.Ceiling(BaseMargin * shadow.Scale);
			ShadowRect full = new(target.X - margin, target.Y - margin, target.Width + margin * 2, target.Height + margin * 2);
			ShadowRect placed = target.IsEmpty ? default : Clip(full, target, shadow.TargetDesktop);
			bool visible = shadow.TargetMapped && !shadow.CallerSuppressed && !shadow.StateSuppressed && !placed.IsEmpty;
			if (!visible)
			{
				Withdraw(shadow);
				return;
			}
			if (shadow.Window == 0 && !Create(shadow, placed))
				return;

			if (placed != shadow.Placed)
			{
				_ = XMoveResizeWindow(_display, shadow.Window, placed.X, placed.Y, (uint)placed.Width, (uint)placed.Height);
				shadow.Placed = placed;
				shadow.PlacementAttempts = 0;
				shadow.VerifyPlacement = false;
			}
			else if (shadow.VerifyPlacement && shadow.Mapped)
			{
				shadow.VerifyPlacement = false;
				if (shadow.PlacementAttempts < MaxPlacementAttempts
					&& TryReadBounds(shadow.Window, out ShadowRect actual)
					&& actual != placed)
				{
					shadow.PlacementAttempts++;
					_ = XMoveResizeWindow(_display, shadow.Window, placed.X, placed.Y, (uint)placed.Width, (uint)placed.Height);
				}
			}

			PaintKey key = new(
				placed.Width,
				placed.Height,
				placed.X - full.X,
				placed.Y - full.Y,
				target.Width,
				target.Height,
				shadow.Radius,
				margin,
				shadow.Scale,
				_activeWindow == shadow.Target);
			if (key != shadow.Painted && Paint(shadow, key))
				shadow.Painted = key;

			SyncOpacity(shadow);
			if (!shadow.MapRequested)
			{
				PrepareMap(shadow, placed);
				_ = XMapWindow(_display, shadow.Window);
				shadow.MapRequested = true;
				shadow.RestackAttempts = 0;
				return;
			}
			if (shadow.Mapped)
			{
				SyncDesktop(shadow);
				SyncAbove(shadow);
			}
		}

		private void ReadState(Shadow shadow)
		{
			nint[] states = ReadLongs(shadow.Target, _netWmState, AtomType);
			bool vertical = Array.IndexOf(states, _netWmStateMaximizedVert) >= 0;
			bool horizontal = Array.IndexOf(states, _netWmStateMaximizedHorz) >= 0;
			shadow.StateSuppressed = Array.IndexOf(states, _netWmStateHidden) >= 0
				|| Array.IndexOf(states, _netWmStateFullscreen) >= 0
				|| (vertical && horizontal);
			shadow.TargetAbove = Array.IndexOf(states, _netWmStateAbove) >= 0;
		}

		private ShadowRect Clip(ShadowRect full, ShadowRect target, long desktop)
		{
			List<ShadowRect> areas = GetWorkAreas(desktop);
			if (areas.Count == 0)
				return full;

			ShadowRect best = default;
			long bestTarget = 0;
			long bestFull = 0;
			foreach (ShadowRect area in areas)
			{
				long targetOverlap = target.Intersect(area).Area;
				long fullOverlap = full.Intersect(area).Area;
				if (targetOverlap > bestTarget || (targetOverlap == bestTarget && fullOverlap > bestFull))
				{
					best = area;
					bestTarget = targetOverlap;
					bestFull = fullOverlap;
				}
			}
			return bestFull > 0 ? full.Intersect(best) : default;
		}

		private List<ShadowRect> GetWorkAreas(long desktop)
		{
			long index = desktop < 0 || desktop >= AllDesktops ? _currentDesktop : desktop;
			if (_workAreas.TryGetValue(index, out List<ShadowRect>? cached))
				return cached;

			List<ShadowRect> areas = new();
			nint perMonitor = XInternAtom(_display, "_GTK_WORKAREAS_D" + index.ToString(CultureInfo.InvariantCulture), 1);
			if (perMonitor != 0)
				AddRectangles(areas, ReadLongs(_root, perMonitor, CardinalType), 0, int.MaxValue);
			if (areas.Count == 0)
			{
				nint[] shared = ReadLongs(_root, _netWorkarea, CardinalType);
				int start = shared.Length >= (index + 1) * 4 ? (int)index * 4 : 0;
				AddRectangles(areas, shared, start, 1);
			}
			_workAreas[index] = areas;
			return areas;
		}

		private static void AddRectangles(List<ShadowRect> areas, nint[] values, int start, int limit)
		{
			for (int offset = start; offset + 3 < values.Length && areas.Count < limit; offset += 4)
			{
				ShadowRect area = new((int)values[offset], (int)values[offset + 1], (int)values[offset + 2], (int)values[offset + 3]);
				if (!area.IsEmpty)
					areas.Add(area);
			}
		}

		private bool Create(Shadow shadow, ShadowRect placed)
		{
			XSetWindowAttributes attributes = default;
			attributes.BackgroundPixel = 0;
			attributes.BorderPixel = 0;
			attributes.Colormap = _colormap;
			attributes.EventMask = StructureNotifyMask;
			nint window = XCreateWindow(
				_display,
				_root,
				placed.X,
				placed.Y,
				(uint)placed.Width,
				(uint)placed.Height,
				0,
				32,
				InputOutput,
				_visual,
				CwBackPixel | CwBorderPixel | CwEventMask | CwColormap,
				&attributes);
			if (window == 0)
				return false;

			SetLongs(window, _netWmWindowType, AtomType, [_netWmWindowTypeUtility]);
			SetLongs(window, _motifWmHints, _motifWmHints, [2, 0, 0, 0, 0]);
			SetLongs(window, WmHintsAtom, WmHintsAtom, [1, 0, 0, 0, 0, 0, 0, 0, 0]);
			SetLongs(window, _netWmUserTime, CardinalType, [0]);
			SetLongs(window, _comptonShadow, CardinalType, [0]);
			SetText(window, _netWmName, _utf8String, "Voidstrap window shadow");
			SetLongs(window, WmTransientForAtom, WindowType, [shadow.Target]);
			CopyProperty(shadow.Target, window, WmClassAtom);
			XShapeCombineRectangles(_display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);

			shadow.Window = window;
			shadow.Placed = placed;
			shadow.Painted = default;
			shadow.Mapped = false;
			shadow.MapRequested = false;
			shadow.OpacityApplied = false;
			shadow.ShadowOpacity = null;
			_byWindow[window] = shadow;
			return true;
		}

		private void PrepareMap(Shadow shadow, ShadowRect placed)
		{
			SetLongs(shadow.Window, WmNormalHintsAtom, WmSizeHintsType,
				[1 | 2 | 4 | 8, placed.X, placed.Y, placed.Width, placed.Height, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

			List<nint> states = [_netWmStateSkipTaskbar, _netWmStateSkipPager];
			if (shadow.TargetAbove)
				states.Add(_netWmStateAbove);
			if (shadow.TargetDesktop >= AllDesktops)
				states.Add(_netWmStateSticky);
			SetLongs(shadow.Window, _netWmState, AtomType, states.ToArray());
			shadow.ShadowAbove = shadow.TargetAbove;

			if (shadow.TargetDesktop >= 0)
				SetLongs(shadow.Window, _netWmDesktop, CardinalType, [(nint)shadow.TargetDesktop]);
			else
				_ = XDeleteProperty(_display, shadow.Window, _netWmDesktop);
			shadow.ShadowDesktop = shadow.TargetDesktop;
		}

		private void SyncDesktop(Shadow shadow)
		{
			if (shadow.TargetDesktop < 0 || shadow.TargetDesktop == shadow.ShadowDesktop)
				return;
			SendMessage(shadow.Window, _netWmDesktop, (nint)shadow.TargetDesktop, SourcePager, 0, 0, 0);
			shadow.ShadowDesktop = shadow.TargetDesktop;
			_stackDirty = true;
		}

		private void SyncAbove(Shadow shadow)
		{
			if (shadow.TargetAbove == shadow.ShadowAbove)
				return;
			SendMessage(shadow.Window, _netWmState, shadow.TargetAbove ? StateAdd : StateRemove, _netWmStateAbove, 0, SourcePager, 0);
			shadow.ShadowAbove = shadow.TargetAbove;
			shadow.RestackAttempts = 0;
			_stackDirty = true;
		}

		private void SyncOpacity(Shadow shadow)
		{
			if (shadow.OpacityApplied && shadow.ShadowOpacity == shadow.TargetOpacity)
				return;
			if (shadow.TargetOpacity is long opacity)
				SetLongs(shadow.Window, _netWmWindowOpacity, CardinalType, [(nint)opacity]);
			else
				_ = XDeleteProperty(_display, shadow.Window, _netWmWindowOpacity);
			shadow.ShadowOpacity = shadow.TargetOpacity;
			shadow.OpacityApplied = true;
		}

		private void Withdraw(Shadow shadow)
		{
			if (shadow.Window != 0 && shadow.MapRequested)
				_ = XWithdrawWindow(_display, shadow.Window, _screen);
			shadow.MapRequested = false;
			shadow.Mapped = false;
		}

		private void Destroy(Shadow shadow, bool release)
		{
			if (shadow.Window != 0)
			{
				_byWindow.Remove(shadow.Window);
				_ = XDestroyWindow(_display, shadow.Window);
				shadow.Window = 0;
			}
			_byTarget.Remove(shadow.Target);
			if (release)
				_ = XSelectInput(_display, shadow.Target, 0);
		}

		private void Restack()
		{
			nint[] stacking = ReadLongs(_root, _netClientListStacking, WindowType);
			if (stacking.Length == 0)
				return;

			foreach (Shadow shadow in _byTarget.Values)
			{
				if (!shadow.Mapped || shadow.Window == 0)
					continue;
				int own = Array.IndexOf(stacking, shadow.Window);
				int target = Array.IndexOf(stacking, shadow.Target);
				if (own < 0 || target < 0)
					continue;
				if (own == target + 1)
				{
					shadow.RestackAttempts = 0;
					continue;
				}
				if (shadow.RestackAttempts >= MaxRestackAttempts)
					continue;
				shadow.RestackAttempts++;
				SendMessage(shadow.Window, _netRestackWindow, SourcePager, shadow.Target, RestackBelow, 0, 0);
			}
		}

		private bool Paint(Shadow shadow, PaintKey key)
		{
			nuint length = (nuint)key.Width * (nuint)key.Height;
			uint* pixels = (uint*)NativeMemory.AllocZeroed(length, sizeof(uint));
			try
			{
				Render(pixels, key, _swapBytes);
				nint image = XCreateImage(_display, _visual, 32, ZPixmap, 0, (nint)pixels, (uint)key.Width, (uint)key.Height, 32, 0);
				if (image == 0)
					return false;
				nint pixmap = XCreatePixmap(_display, shadow.Window, (uint)key.Width, (uint)key.Height, 32);
				if (pixmap == 0)
				{
					*(nint*)((byte*)image + 16) = 0;
					_ = XDestroyImage(image);
					return false;
				}
				if (_gc == 0)
					_gc = XCreateGC(_display, pixmap, 0, 0);
				_ = XPutImage(_display, pixmap, _gc, image, 0, 0, 0, 0, (uint)key.Width, (uint)key.Height);
				*(nint*)((byte*)image + 16) = 0;
				_ = XDestroyImage(image);
				_ = XSetWindowBackgroundPixmap(_display, shadow.Window, pixmap);
				_ = XFreePixmap(_display, pixmap);
				_ = XClearWindow(_display, shadow.Window);
				return true;
			}
			finally
			{
				NativeMemory.Free(pixels);
			}
		}

		private static void Render(uint* pixels, PaintKey key, bool swapBytes)
		{
			ShadowStyle style = key.Focused ? FocusedStyle : UnfocusedStyle;
			int radius = Math.Min(key.Radius, Math.Min(key.TargetWidth, key.TargetHeight) / 2);
			double margin = key.Margin;
			ShadowLayer ambient = new(
				margin,
				margin,
				key.TargetWidth,
				key.TargetHeight,
				radius,
				style.AmbientBlur * key.Scale,
				style.AmbientOpacity);
			ShadowLayer contact = new(
				margin,
				margin,
				key.TargetWidth,
				key.TargetHeight,
				radius,
				style.ContactBlur * key.Scale,
				style.ContactOpacity);

			for (int row = 0; row < key.Height; row++)
			{
				int shadowY = row + key.OffsetY;
				int targetY = shadowY - key.Margin;
				bool crossesTarget = targetY >= 0 && targetY < key.TargetHeight;
				int insideStart = int.MaxValue;
				int insideEnd = int.MinValue;
				if (crossesTarget)
				{
					int inset = RowInset(targetY, key.TargetHeight, radius);
					insideStart = key.Margin + inset - key.OffsetX;
					insideEnd = key.Margin + key.TargetWidth - inset - key.OffsetX;
				}

				uint* line = pixels + (nint)row * key.Width;
				double y = shadowY + 0.5;
				for (int column = 0; column < key.Width; column++)
				{
					if (column >= insideStart && column < insideEnd)
					{
						column = insideEnd - 1;
						continue;
					}
					double x = column + key.OffsetX + 0.5;
					double coverage = 1.0 - (1.0 - ambient.Alpha(x, y)) * (1.0 - contact.Alpha(x, y));
					uint alpha = (uint)Math.Clamp((int)(coverage * 255.0 + 0.5), 0, 255);
					if (alpha == 0)
						continue;
					uint pixel = alpha << 24;
					line[column] = swapBytes ? BinaryPrimitives.ReverseEndianness(pixel) : pixel;
				}
			}
		}

		private static int RowInset(int y, int height, int radius)
		{
			int edge = Math.Min(y, height - 1 - y);
			if (edge >= radius)
				return 0;
			double distance = radius - edge - 0.5;
			return (int)Math.Round(radius - Math.Sqrt((double)radius * radius - distance * distance));
		}

		private bool TryReadBounds(nint window, out ShadowRect bounds)
		{
			bounds = default;
			if (XGetGeometry(_display, window, out _, out _, out _, out uint width, out uint height, out _, out _) == 0)
				return false;
			if (XTranslateCoordinates(_display, window, _root, 0, 0, out int x, out int y, out _) == 0)
				return false;
			bounds = new ShadowRect(x, y, (int)width, (int)height);
			return true;
		}

		private void SendMessage(nint window, nint messageType, nint first, nint second, nint third, nint fourth, nint fifth)
		{
			byte* message = stackalloc byte[EventSize];
			new Span<byte>(message, EventSize).Clear();
			*(int*)message = ClientMessage;
			*(int*)(message + 16) = 1;
			*(nint*)(message + 24) = _display;
			*(nint*)(message + 32) = window;
			*(nint*)(message + 40) = messageType;
			*(int*)(message + 48) = 32;
			*(nint*)(message + 56) = first;
			*(nint*)(message + 64) = second;
			*(nint*)(message + 72) = third;
			*(nint*)(message + 80) = fourth;
			*(nint*)(message + 88) = fifth;
			_ = XSendEvent(_display, _root, 0, SubstructureRedirectMask | SubstructureNotifyMask, message);
		}

		private nint Intern(string name)
		{
			return XInternAtom(_display, name, 0);
		}

		private nint[] ReadLongs(nint window, nint property, nint type)
		{
			nint data = 0;
			try
			{
				if (XGetWindowProperty(_display, window, property, 0, 4096, 0, type, out _, out int format, out nuint count, out _, out data) != 0
					|| data == 0
					|| format != 32
					|| count == 0)
					return [];
				nint[] values = new nint[(int)count];
				Marshal.Copy(data, values, 0, values.Length);
				return values;
			}
			finally
			{
				if (data != 0)
					_ = XFree(data);
			}
		}

		private long? ReadCardinal(nint window, nint property)
		{
			nint[] values = ReadLongs(window, property, CardinalType);
			return values.Length > 0 ? (long)(nuint)values[0] & 0xFFFFFFFF : null;
		}

		private nint ReadWindow(nint window, nint property)
		{
			nint[] values = ReadLongs(window, property, WindowType);
			return values.Length > 0 ? values[0] : 0;
		}

		private string ReadWindowManagerName()
		{
			nint check = ReadWindow(_root, Intern("_NET_SUPPORTING_WM_CHECK"));
			if (check == 0)
				return "the window manager";
			nint data = 0;
			try
			{
				if (XGetWindowProperty(_display, check, _netWmName, 0, 256, 0, _utf8String, out _, out int format, out nuint count, out _, out data) != 0
					|| data == 0
					|| format != 8
					|| count == 0)
					return "the window manager";
				string name = Encoding.UTF8.GetString((byte*)data, (int)count).Trim();
				return name.Length > 0 ? name : "the window manager";
			}
			finally
			{
				if (data != 0)
					_ = XFree(data);
			}
		}

		private void SetLongs(nint window, nint property, nint type, nint[] values)
		{
			fixed (nint* data = values)
				_ = XChangeProperty(_display, window, property, type, 32, PropModeReplace, data, values.Length);
		}

		private void SetText(nint window, nint property, nint type, string text)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text);
			fixed (byte* data = bytes)
				_ = XChangeProperty(_display, window, property, type, 8, PropModeReplace, data, bytes.Length);
		}

		private void CopyProperty(nint source, nint destination, nint property)
		{
			nint data = 0;
			try
			{
				if (XGetWindowProperty(_display, source, property, 0, 1024, 0, 0, out nint type, out int format, out nuint count, out _, out data) != 0
					|| data == 0
					|| type == 0
					|| count == 0)
					return;
				_ = XChangeProperty(_display, destination, property, type, format, PropModeReplace, (void*)data, (int)count);
			}
			finally
			{
				if (data != 0)
					_ = XFree(data);
			}
		}

		public void Dispose()
		{
			foreach (Shadow shadow in _byTarget.Values.ToArray())
				Destroy(shadow, true);
			_ = XCloseDisplay(_display);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PollDescriptor
	{
		public int Descriptor;
		public short Events;
		public short ReturnedEvents;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XVisualInfo
	{
		public nint Visual;
		public nuint VisualId;
		public int Screen;
		public int Depth;
		public int Class;
		public nuint RedMask;
		public nuint GreenMask;
		public nuint BlueMask;
		public int ColormapSize;
		public int BitsPerRgb;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XSetWindowAttributes
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
	private struct XWindowAttributes
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

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XOpenDisplay(string? name);

	[LibraryImport("libX11.so.6")]
	private static partial int XCloseDisplay(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XConnectionNumber(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XDefaultScreen(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XRootWindow(nint display, int screen);

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

	[LibraryImport("libX11.so.6")]
	private static partial nint XGetSelectionOwner(nint display, nint selection);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetWindowProperty(
		nint display,
		nint window,
		nint property,
		nint offset,
		nint length,
		int delete,
		nint requestedType,
		out nint actualType,
		out int actualFormat,
		out nuint itemCount,
		out nuint bytesAfter,
		out nint data);

	[LibraryImport("libX11.so.6")]
	private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, void* data, int count);

	[LibraryImport("libX11.so.6")]
	private static partial int XDeleteProperty(nint display, nint window, nint property);

	[LibraryImport("libX11.so.6")]
	private static partial int XFree(nint data);

	[LibraryImport("libX11.so.6")]
	private static partial int XMatchVisualInfo(nint display, int screen, int depth, int visualClass, XVisualInfo* info);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreateColormap(nint display, nint window, nint visual, int alloc);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreateWindow(
		nint display,
		nint parent,
		int x,
		int y,
		uint width,
		uint height,
		uint borderWidth,
		int depth,
		uint windowClass,
		nint visual,
		nuint valueMask,
		XSetWindowAttributes* attributes);

	[LibraryImport("libX11.so.6")]
	private static partial int XDestroyWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XMapWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XWithdrawWindow(nint display, nint window, int screen);

	[LibraryImport("libX11.so.6")]
	private static partial int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

	[LibraryImport("libX11.so.6")]
	private static partial int XSelectInput(nint display, nint window, nint mask);

	[LibraryImport("libX11.so.6")]
	private static partial int XPending(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XEventsQueued(nint display, int mode);

	[LibraryImport("libX11.so.6")]
	private static partial int XNextEvent(nint display, void* eventReturn);

	[LibraryImport("libX11.so.6")]
	private static partial int XSendEvent(nint display, nint window, int propagate, nint mask, void* eventSend);

	[LibraryImport("libX11.so.6")]
	private static partial int XFlush(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetWindowAttributes(nint display, nint window, XWindowAttributes* attributes);

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
	private static partial int XTranslateCoordinates(
		nint display,
		nint source,
		nint destination,
		int sourceX,
		int sourceY,
		out int destinationX,
		out int destinationY,
		out nint child);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreatePixmap(nint display, nint drawable, uint width, uint height, uint depth);

	[LibraryImport("libX11.so.6")]
	private static partial int XFreePixmap(nint display, nint pixmap);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreateGC(nint display, nint drawable, nuint valueMask, nint values);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreateImage(
		nint display,
		nint visual,
		uint depth,
		int format,
		int offset,
		nint data,
		uint width,
		uint height,
		int bitmapPad,
		int bytesPerLine);

	[LibraryImport("libX11.so.6")]
	private static partial int XPutImage(
		nint display,
		nint drawable,
		nint gc,
		nint image,
		int sourceX,
		int sourceY,
		int destinationX,
		int destinationY,
		uint width,
		uint height);

	[LibraryImport("libX11.so.6")]
	private static partial int XDestroyImage(nint image);

	[LibraryImport("libX11.so.6")]
	private static partial int XSetWindowBackgroundPixmap(nint display, nint window, nint pixmap);

	[LibraryImport("libX11.so.6")]
	private static partial int XClearWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XImageByteOrder(nint display);

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

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int pipe2(int* descriptors, int flags);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int poll(PollDescriptor* descriptors, nuint count, int timeout);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial nint read(int descriptor, void* buffer, nuint count);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial nint write(int descriptor, void* buffer, nuint count);
}
