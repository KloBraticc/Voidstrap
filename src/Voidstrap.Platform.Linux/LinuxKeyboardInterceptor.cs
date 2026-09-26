using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public readonly record struct LinuxKeyEvent(ushort Code, int Value);

public interface ILinuxKeyRouter
{
	bool IsTargetFocused();

	void Route(LinuxKeyEvent input, bool focused, List<LinuxKeyEvent> output);

	void Reset();
}

public enum LinuxInputAccessState
{
	Ready,
	MissingKeyboardAccess,
	MissingUinputAccess,
	NoKeyboards,
	Unsupported
}

public sealed unsafe partial class LinuxKeyboardInterceptor : IDisposable
{
	public const string VirtualDeviceName = "Voidstrap Snap Tap";

	private const int EventSize = 24;
	private const int KeyBitmapBytes = 96;
	private const int MaxKeyCode = 248;
	private const ushort EvSyn = 0;
	private const ushort EvKey = 1;
	private const ushort EvLed = 0x11;
	private const ushort SynReport = 0;
	private const ushort SynDropped = 3;
	private const int LedCount = 3;
	private const int OpenReadWrite = 2;
	private const int OpenReadOnly = 0;
	private const int OpenWriteOnly = 1;
	private const int OpenNonBlocking = 0x800;
	private const int OpenCloseOnExec = 0x80000;
	private const int OpenCreate = 0x40;
	private const short PollIn = 1;
	private const int LockExclusive = 2;
	private const int LockNonBlocking = 4;
	private const int NoDevice = 19;
	private const int Interrupted = 4;
	private const long ScanIntervalMs = 2000;
	private const long FocusIntervalMs = 150;
	private const long OwnershipRetryMs = 2000;

	private const nuint EviocGrab = 0x40044590;
	private const nuint EviocGetKeyState = 0x80604518;
	private const nuint EviocGetKeyBits = 0x80604521;
	private const nuint EviocGetEventBits = 0x80044520;
	private const int EvRelative = 2;
	private const int EvAbsolute = 3;
	private const int ButtonLeft = 0x110;
	private const nuint EviocGetName = 0x81004506;
	private const nuint UiSetEventBit = 0x40045564;
	private const nuint UiSetKeyBit = 0x40045565;
	private const nuint UiSetLedBit = 0x40045569;
	private const nuint UiDeviceSetup = 0x405c5503;
	private const nuint UiDeviceCreate = 0x5501;
	private const nuint UiDeviceDestroy = 0x5502;

	private static readonly ushort[] KeyboardProbeKeys = [30, 31, 32, 17, 44, 57, 28];

	private readonly ILinuxKeyRouter _router;
	private readonly Action<string>? _log;
	private readonly Func<string, bool> _deviceFilter;
	private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
	private readonly List<LinuxKeyEvent> _routed = new(16);
	private readonly List<LinuxKeyEvent> _pending = new(32);
	private readonly bool[] _emitted = new bool[MaxKeyCode + 1];
	private readonly byte[] _eventBuffer = new byte[EventSize * 64];
	private readonly object _gate = new();
	private Thread? _thread;
	private volatile bool _stopping;
	private int _wakeRead = -1;
	private int _wakeWrite = -1;
	private int _uinput = -1;
	private int _lockFile = -1;
	private bool _grabbed;
	private bool _focused;
	private bool _ownershipReported;
	private bool _grabFailureReported;
	private long _grabRetryAt;

	private sealed class Device
	{
		public Device(string path, string name, int descriptor)
		{
			Path = path;
			Name = name;
			Descriptor = descriptor;
		}

		public string Path { get; }
		public string Name { get; }
		public int Descriptor { get; }
		public bool Writable { get; init; }
		public bool[] Down { get; } = new bool[MaxKeyCode + 1];
	}

	public LinuxKeyboardInterceptor(ILinuxKeyRouter router, Action<string>? log = null, Func<string, bool>? deviceFilter = null)
	{
		_router = router;
		_log = log;
		_deviceFilter = deviceFilter ?? (static _ => true);
	}

	public bool IsGrabbed => _grabbed;

	public static LinuxInputAccessState CheckAccess()
	{
		if (!OperatingSystem.IsLinux())
			return LinuxInputAccessState.Unsupported;
		if (!File.Exists("/dev/uinput") && !File.Exists("/dev/input/uinput"))
			return LinuxInputAccessState.Unsupported;

		try
		{
			int probe = open(UinputPath(), OpenWriteOnly | OpenNonBlocking | OpenCloseOnExec, 0);
			if (probe < 0)
				return LinuxInputAccessState.MissingUinputAccess;
			_ = close(probe);

			bool anyKeyboard = false;
			foreach (string path in EnumerateEventNodes())
			{
				int descriptor = open(path, OpenReadOnly | OpenNonBlocking | OpenCloseOnExec, 0);
				if (descriptor < 0)
				{
					if (IsLikelyKeyboardNode(path))
						return LinuxInputAccessState.MissingKeyboardAccess;
					continue;
				}
				try
				{
					if (IsKeyboard(descriptor) && ReadName(descriptor) != VirtualDeviceName)
						anyKeyboard = true;
				}
				finally
				{
					_ = close(descriptor);
				}
			}
			return anyKeyboard ? LinuxInputAccessState.Ready : LinuxInputAccessState.NoKeyboards;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return LinuxInputAccessState.Unsupported;
		}
	}

	public bool Start()
	{
		lock (_gate)
		{
			if (_thread != null)
				return true;
			int* descriptors = stackalloc int[2];
			if (pipe2(descriptors, OpenNonBlocking | OpenCloseOnExec) != 0)
			{
				Log("Snap Tap could not create its wake pipe");
				return false;
			}
			_wakeRead = descriptors[0];
			_wakeWrite = descriptors[1];
			_stopping = false;
			_thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Voidstrap Snap Tap",
				Priority = ThreadPriority.Highest
			};
			_thread.Start();
			return true;
		}
	}

	public void Dispose()
	{
		Thread? thread;
		lock (_gate)
		{
			thread = _thread;
			_thread = null;
			_stopping = true;
		}
		if (thread == null)
			return;
		Wake();
		if (!thread.Join(2000))
			Log("Snap Tap input thread did not stop in time");
		CloseDescriptor(ref _wakeRead);
		CloseDescriptor(ref _wakeWrite);
	}

	private void Run()
	{
		try
		{
			if (!AcquireOwnership())
				return;
			if (!CreateVirtualKeyboard())
				return;
			Log("Snap Tap virtual keyboard ready, waiting for Sober focus");
			Loop();
		}
		catch (Exception ex)
		{
			Log("Snap Tap input thread stopped: " + ex.Message);
		}
		finally
		{
			Release();
		}
	}

	private bool AcquireOwnership()
	{
		string directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath();
		string path = Path.Combine(directory, "voidstrap-snaptap.lock");
		_lockFile = open(path, OpenReadWrite | OpenCreate | OpenCloseOnExec, 384);
		if (_lockFile < 0)
		{
			Log("Snap Tap could not open its lock file, continuing without it");
			return true;
		}
		while (!_stopping)
		{
			if (flock(_lockFile, LockExclusive | LockNonBlocking) == 0)
				return true;
			if (!_ownershipReported)
			{
				_ownershipReported = true;
				Log("Another Voidstrap process owns Snap Tap, taking over when it exits");
			}
			WaitForWake(OwnershipRetryMs);
		}
		return false;
	}

	private bool CreateVirtualKeyboard()
	{
		_uinput = open(UinputPath(), OpenReadWrite | OpenNonBlocking | OpenCloseOnExec, 0);
		if (_uinput < 0)
		{
			Log("Snap Tap cannot open /dev/uinput, error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture));
			return false;
		}
		_ = ioctl(_uinput, UiSetEventBit, EvKey);
		_ = ioctl(_uinput, UiSetEventBit, EvSyn);
		_ = ioctl(_uinput, UiSetEventBit, EvLed);
		for (int code = 1; code <= MaxKeyCode; code++)
			_ = ioctl(_uinput, UiSetKeyBit, code);
		for (int led = 0; led < LedCount; led++)
			_ = ioctl(_uinput, UiSetLedBit, led);

		byte* setup = stackalloc byte[92];
		new Span<byte>(setup, 92).Clear();
		*(ushort*)setup = 0x06;
		*(ushort*)(setup + 2) = 0x5654;
		*(ushort*)(setup + 4) = 0x5354;
		*(ushort*)(setup + 6) = 1;
		byte[] name = Encoding.ASCII.GetBytes(VirtualDeviceName);
		for (int index = 0; index < name.Length && index < 79; index++)
			setup[8 + index] = name[index];
		if (ioctl(_uinput, UiDeviceSetup, setup) < 0 || ioctl(_uinput, UiDeviceCreate, 0) < 0)
		{
			Log("Snap Tap could not create its virtual keyboard, error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture));
			CloseDescriptor(ref _uinput);
			return false;
		}
		return true;
	}

	private void Loop()
	{
		long nextScan = 0;
		long nextFocus = 0;
		PollDescriptor* descriptors = stackalloc PollDescriptor[64];
		string[] paths = new string[64];
		while (!_stopping)
		{
			long now = Environment.TickCount64;
			if (now >= nextScan)
			{
				nextScan = now + ScanIntervalMs;
				ScanDevices();
			}
			if (now >= nextFocus)
			{
				nextFocus = now + FocusIntervalMs;
				UpdateFocus();
			}

			int count = 0;
			descriptors[count++] = new PollDescriptor { Descriptor = _wakeRead, Events = PollIn };
			descriptors[count++] = new PollDescriptor { Descriptor = _uinput, Events = PollIn };
			foreach (Device device in _devices.Values)
			{
				if (count >= 64)
					break;
				paths[count] = device.Path;
				descriptors[count++] = new PollDescriptor { Descriptor = device.Descriptor, Events = PollIn };
			}

			int ready = poll(descriptors, (nuint)count, (int)FocusIntervalMs);
			if (ready < 0)
			{
				if (Marshal.GetLastPInvokeError() == Interrupted)
					continue;
				throw new IOException("waiting for keyboard input failed");
			}
			if (ready == 0)
				continue;

			if ((descriptors[0].ReturnedEvents & PollIn) != 0)
				DrainWake();
			if ((descriptors[1].ReturnedEvents & PollIn) != 0)
				ForwardLeds();
			for (int index = 2; index < count; index++)
			{
				if (descriptors[index].ReturnedEvents == 0)
					continue;
				if (_devices.TryGetValue(paths[index], out Device? device))
					ReadDevice(device);
			}
		}
	}

	private void UpdateFocus()
	{
		bool focused;
		try
		{
			focused = _router.IsTargetFocused();
		}
		catch (Exception)
		{
			focused = false;
		}
		if (focused != _focused)
		{
			_focused = focused;
			Log(focused ? "Sober focused, Snap Tap engaged" : "Sober unfocused, Snap Tap idle");
		}

		if (focused && !_grabbed && _devices.Count > 0 && Environment.TickCount64 >= _grabRetryAt && PhysicalKeysIdle())
			Grab(true);
		else if (!focused && _grabbed && PhysicalKeysIdle() && !AnyEmitted())
			Grab(false);
	}

	private void Grab(bool grab)
	{
		if (!grab)
			ReleaseEmitted();
		int grabbed = 0;
		foreach (Device device in _devices.Values)
		{
			if (ioctl(device.Descriptor, EviocGrab, grab ? 1 : 0) == 0)
				grabbed++;
			if (grab)
				DiscardPending(device);
		}
		_grabbed = grab && grabbed > 0;
		_router.Reset();
		Array.Clear(_emitted);
		if (grab && grabbed == 0)
		{
			_grabRetryAt = Environment.TickCount64 + ScanIntervalMs;
			if (!_grabFailureReported)
			{
				_grabFailureReported = true;
				Log("Snap Tap could not take over the keyboard, another program may be holding it");
			}
			return;
		}
		_grabFailureReported = false;
		Log(grab ? "Keyboard taken over for Snap Tap on " + grabbed.ToString(CultureInfo.InvariantCulture) + " device(s)" : "Keyboard handed back to the desktop");
	}

	private void ScanDevices()
	{
		HashSet<string> present = new(StringComparer.Ordinal);
		foreach (string path in EnumerateEventNodes())
		{
			present.Add(path);
			if (_devices.ContainsKey(path))
				continue;
			bool writable = true;
			int descriptor = open(path, OpenReadWrite | OpenNonBlocking | OpenCloseOnExec, 0);
			if (descriptor < 0)
			{
				writable = false;
				descriptor = open(path, OpenReadOnly | OpenNonBlocking | OpenCloseOnExec, 0);
			}
			if (descriptor < 0)
				continue;
			string name = ReadName(descriptor);
			if (name == VirtualDeviceName || !IsKeyboard(descriptor) || !_deviceFilter(name))
			{
				_ = close(descriptor);
				continue;
			}
			Device device = new(path, name, descriptor) { Writable = writable };
			_devices[path] = device;
			if (_grabbed)
			{
				if (PhysicalKeysIdle(device))
				{
					_ = ioctl(descriptor, EviocGrab, 1);
					DiscardPending(device);
				}
			}
			Log("Snap Tap watching keyboard " + name);
		}

		foreach (string path in _devices.Keys.Where(path => !present.Contains(path)).ToList())
			RemoveDevice(path);
	}

	private void RemoveDevice(string path)
	{
		if (!_devices.Remove(path, out Device? device))
			return;
		_ = close(device.Descriptor);
		Log("Snap Tap stopped watching keyboard " + device.Name);
	}

	private void ReadDevice(Device device)
	{
		while (true)
		{
			nint read;
			fixed (byte* buffer = _eventBuffer)
				read = LibcRead(device.Descriptor, buffer, (nuint)_eventBuffer.Length);
			if (read < 0)
			{
				int error = Marshal.GetLastPInvokeError();
				if (error == NoDevice)
					RemoveDevice(device.Path);
				return;
			}
			if (read == 0)
				return;
			if (!_grabbed)
			{
				if (read < _eventBuffer.Length)
					return;
				continue;
			}

			for (int offset = 0; offset + EventSize <= read; offset += EventSize)
			{
				ushort type = BitConverter.ToUInt16(_eventBuffer, offset + 16);
				ushort code = BitConverter.ToUInt16(_eventBuffer, offset + 18);
				int value = BitConverter.ToInt32(_eventBuffer, offset + 20);
				if (type == EvKey && code <= MaxKeyCode)
				{
					if (value == 1)
						device.Down[code] = true;
					else if (value == 0)
						device.Down[code] = false;
					_routed.Clear();
					_router.Route(new LinuxKeyEvent(code, value), _focused, _routed);
					_pending.AddRange(_routed);
				}
				else if (type == EvSyn && code == SynReport)
				{
					Flush();
				}
				else if (type == EvSyn && code == SynDropped)
				{
					_pending.Clear();
					ReleaseEmitted();
					_router.Reset();
					SyncKeyState(device);
				}
			}
			if (read < _eventBuffer.Length)
				return;
		}
	}

	private void Flush()
	{
		if (_pending.Count == 0)
			return;
		int total = _pending.Count + 1;
		byte* buffer = stackalloc byte[EventSize * Math.Min(total, 64)];
		int written = 0;
		foreach (LinuxKeyEvent key in _pending)
		{
			if (written == 63)
				break;
			if (key.Code == 0 || key.Code > MaxKeyCode)
				continue;
			if (key.Value == 1)
				_emitted[key.Code] = true;
			else if (key.Value == 0)
				_emitted[key.Code] = false;
			WriteEvent(buffer + written * EventSize, EvKey, key.Code, key.Value);
			written++;
		}
		_pending.Clear();
		if (written == 0)
			return;
		WriteEvent(buffer + written * EventSize, EvSyn, SynReport, 0);
		written++;
		_ = LibcWrite(_uinput, buffer, (nuint)(written * EventSize));
	}

	private void ReleaseEmitted()
	{
		if (_uinput < 0)
			return;
		byte* buffer = stackalloc byte[EventSize * 2];
		for (ushort code = 1; code <= MaxKeyCode; code++)
		{
			if (!_emitted[code])
				continue;
			_emitted[code] = false;
			WriteEvent(buffer, EvKey, code, 0);
			WriteEvent(buffer + EventSize, EvSyn, SynReport, 0);
			_ = LibcWrite(_uinput, buffer, EventSize * 2);
		}
	}

	private bool AnyEmitted()
	{
		foreach (bool down in _emitted)
		{
			if (down)
				return true;
		}
		return false;
	}

	private void ForwardLeds()
	{
		byte[] buffer = new byte[EventSize * 16];
		byte* message = stackalloc byte[EventSize * 2];
		while (true)
		{
			nint read;
			fixed (byte* data = buffer)
				read = LibcRead(_uinput, data, (nuint)buffer.Length);
			if (read <= 0)
				return;
			for (int offset = 0; offset + EventSize <= read; offset += EventSize)
			{
				ushort type = BitConverter.ToUInt16(buffer, offset + 16);
				if (type != EvLed)
					continue;
				ushort code = BitConverter.ToUInt16(buffer, offset + 18);
				int value = BitConverter.ToInt32(buffer, offset + 20);
				WriteEvent(message, EvLed, code, value);
				WriteEvent(message + EventSize, EvSyn, SynReport, 0);
				foreach (Device device in _devices.Values)
				{
					if (device.Writable)
						_ = LibcWrite(device.Descriptor, message, EventSize * 2);
				}
			}
			if (read < buffer.Length)
				return;
		}
	}

	private bool PhysicalKeysIdle()
	{
		foreach (Device device in _devices.Values)
		{
			if (_grabbed ? !TrackedKeysIdle(device) : !PhysicalKeysIdle(device))
				return false;
		}
		return true;
	}

	private static bool TrackedKeysIdle(Device device)
	{
		foreach (bool down in device.Down)
		{
			if (down)
				return false;
		}
		return true;
	}

	private static bool PhysicalKeysIdle(Device device)
	{
		SyncKeyState(device);
		return TrackedKeysIdle(device);
	}

	private static void SyncKeyState(Device device)
	{
		byte* state = stackalloc byte[KeyBitmapBytes];
		new Span<byte>(state, KeyBitmapBytes).Clear();
		if (ioctl(device.Descriptor, EviocGetKeyState, state) < 0)
		{
			Array.Clear(device.Down);
			return;
		}
		for (int code = 0; code <= MaxKeyCode; code++)
			device.Down[code] = (state[code / 8] & (1 << (code % 8))) != 0;
	}

	private void DiscardPending(Device device)
	{
		fixed (byte* buffer = _eventBuffer)
		{
			while (LibcRead(device.Descriptor, buffer, (nuint)_eventBuffer.Length) > 0)
			{
			}
		}
	}

	private void Release()
	{
		try
		{
			if (_grabbed)
				Grab(false);
		}
		catch (Exception)
		{
		}
		foreach (Device device in _devices.Values.ToList())
		{
			_ = ioctl(device.Descriptor, EviocGrab, 0);
			_ = close(device.Descriptor);
		}
		_devices.Clear();
		if (_uinput >= 0)
		{
			_ = ioctl(_uinput, UiDeviceDestroy, 0);
			CloseDescriptor(ref _uinput);
		}
		CloseDescriptor(ref _lockFile);
		Log("Snap Tap released the keyboard");
	}

	private void Wake()
	{
		int descriptor = _wakeWrite;
		if (descriptor < 0)
			return;
		byte signal = 1;
		_ = LibcWrite(descriptor, &signal, 1);
	}

	private void DrainWake()
	{
		byte* buffer = stackalloc byte[32];
		while (LibcRead(_wakeRead, buffer, 32) > 0)
		{
		}
	}

	private void WaitForWake(long milliseconds)
	{
		PollDescriptor descriptor = new() { Descriptor = _wakeRead, Events = PollIn };
		if (poll(&descriptor, 1, (int)milliseconds) > 0)
			DrainWake();
	}

	private void Log(string message)
	{
		_log?.Invoke(message);
	}

	private static void WriteEvent(byte* target, ushort type, ushort code, int value)
	{
		new Span<byte>(target, EventSize).Clear();
		*(ushort*)(target + 16) = type;
		*(ushort*)(target + 18) = code;
		*(int*)(target + 20) = value;
	}

	private static void CloseDescriptor(ref int descriptor)
	{
		if (descriptor >= 0)
			_ = close(descriptor);
		descriptor = -1;
	}

	private static string UinputPath()
	{
		return File.Exists("/dev/uinput") ? "/dev/uinput" : "/dev/input/uinput";
	}

	private static IEnumerable<string> EnumerateEventNodes()
	{
		if (!Directory.Exists("/dev/input"))
			return [];
		return Directory.EnumerateFiles("/dev/input", "event*")
			.OrderBy(path => int.TryParse(Path.GetFileName(path).AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : int.MaxValue);
	}

	private static bool IsLikelyKeyboardNode(string path)
	{
		try
		{
			string capabilities = File.ReadAllText("/sys/class/input/" + Path.GetFileName(path) + "/device/capabilities/key").Trim();
			string[] words = capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Length == 0)
				return false;
			ulong lowest = ulong.Parse(words[^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			string relative = File.ReadAllText("/sys/class/input/" + Path.GetFileName(path) + "/device/capabilities/rel").Trim();
			return (lowest & (1UL << 30)) != 0 && (lowest & (1UL << 57)) != 0 && relative == "0";
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsKeyboard(int descriptor)
	{
		uint types = 0;
		if (ioctl(descriptor, EviocGetEventBits, &types) < 0)
			return false;
		if ((types & (1u << EvRelative)) != 0 || (types & (1u << EvAbsolute)) != 0)
			return false;

		byte* bits = stackalloc byte[KeyBitmapBytes];
		new Span<byte>(bits, KeyBitmapBytes).Clear();
		if (ioctl(descriptor, EviocGetKeyBits, bits) < 0)
			return false;
		if ((bits[ButtonLeft / 8] & (1 << (ButtonLeft % 8))) != 0)
			return false;
		foreach (ushort key in KeyboardProbeKeys)
		{
			if ((bits[key / 8] & (1 << (key % 8))) == 0)
				return false;
		}
		return true;
	}

	private static string ReadName(int descriptor)
	{
		byte* name = stackalloc byte[256];
		new Span<byte>(name, 256).Clear();
		int length = ioctl(descriptor, EviocGetName, name);
		if (length <= 0)
			return string.Empty;
		return Encoding.UTF8.GetString(name, Math.Min(length, 256)).TrimEnd('\0');
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PollDescriptor
	{
		public int Descriptor;
		public short Events;
		public short ReturnedEvents;
	}

	[LibraryImport("libc.so.6", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int open(string path, int flags, int mode);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int close(int descriptor);

	[LibraryImport("libc.so.6", EntryPoint = "read", SetLastError = true)]
	private static partial nint LibcRead(int descriptor, void* buffer, nuint count);

	[LibraryImport("libc.so.6", EntryPoint = "write", SetLastError = true)]
	private static partial nint LibcWrite(int descriptor, void* buffer, nuint count);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int ioctl(int descriptor, nuint request, nint argument);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int ioctl(int descriptor, nuint request, void* argument);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int poll(PollDescriptor* descriptors, nuint count, int timeout);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int pipe2(int* descriptors, int flags);

	[LibraryImport("libc.so.6", SetLastError = true)]
	private static partial int flock(int descriptor, int operation);
}
