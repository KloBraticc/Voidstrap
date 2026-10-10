using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.KeyRouting;

internal static unsafe partial class MacSnapTap
{
	private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const long EventTag = 0x56535450534E4150;
	private static readonly Lock Gate = new();
	private static readonly ushort[] KeyCodes = BuildKeyCodes();
	private static Session? _session;

	public static bool Start(List<int[]> groups, OpposingKeyPriority priority)
	{
		List<int[]> supported = new(groups.Count);
		foreach (int[] group in groups)
		{
			List<int> keys = new(group.Length);
			foreach (int key in group)
			{
				if ((uint)key < KeyCodes.Length && KeyCodes[key] != ushort.MaxValue)
					keys.Add(key);
				else
					Report(OpposingKeyResolver.KeyName(key) + " has no macOS key mapping and is skipped");
			}
			if (keys.Count >= 2)
				supported.Add([.. keys]);
		}
		if (supported.Count == 0 || !MacOSInputAccess.IsGranted)
		{
			Stop();
			Report(supported.Count == 0 ? "No valid macOS key groups configured, Snap Tap stays off" : "Snap Tap needs Accessibility access in System Settings");
			return false;
		}
		lock (Gate)
		{
			if (_session is { } existing)
			{
				if (!existing.Worker.IsAlive)
				{
					existing.Dispose();
					_session = null;
				}
				else if (existing.StopEvent.IsSet)
					return false;
				else
				{
					string description = Describe(supported, priority);
					if (existing.Description != description)
					{
						existing.Description = description;
						Volatile.Write(ref existing.Pending, new Config(supported, priority));
					}
					return true;
				}
			}
			Session session = new(new Config(supported, priority));
			_session = session;
			session.Worker.Start();
			if (session.Started.Wait(3000) && session.Ready)
				return true;
			session.StopEvent.Set();
			if (session.Worker.Join(2000))
			{
				session.Dispose();
				_session = null;
			}
			return false;
		}
	}

	public static void Stop()
	{
		lock (Gate)
		{
			if (_session is not { } session)
				return;
			session.StopEvent.Set();
			if (!session.Worker.Join(2000))
			{
				Report("Snap Tap input thread is still stopping");
				return;
			}
			session.Dispose();
			_session = null;
		}
	}

	private static string Describe(List<int[]> groups, OpposingKeyPriority priority) => priority + ": " + OpposingKeyResolver.FormatGroups(groups);

	private sealed record Config(List<int[]> Groups, OpposingKeyPriority Priority);

	private sealed class Session : IDisposable
	{
		public readonly ManualResetEventSlim StopEvent = new();
		public readonly ManualResetEventSlim Started = new();
		public readonly Thread Worker;
		public Config? Pending;
		public string Description;
		public volatile bool Ready;
		private readonly OpposingKeyResolver _resolver = new();
		private readonly List<RoutedKey> _output = new(256);
		private readonly bool[] _delivered = new bool[256];
		private readonly nint[] _events = new nint[256];
		private ushort[] _keyCodes = (ushort[])KeyCodes.Clone();
		private int[] _virtualKeys = BuildVirtualKeys(KeyCodes);
		private MacOSKeyboardLayout? _layout;
		private nint _tap;
		private int _foregroundPid;
		private int _targetPid;
		private bool _target;
		private bool _disposed;

		public Session(Config config)
		{
			Pending = config;
			Description = Describe(config.Groups, config.Priority);
			Worker = new Thread(Run) { IsBackground = true, Name = "Snap Tap macOS" };
		}

		private void Run()
		{
			nint source = 0;
			nint mode = 0;
			nint loop = 0;
			GCHandle handle = GCHandle.Alloc(this);
			bool ownsMutex = false;
			Mutex? mutex = null;
			try
			{
				mutex = new Mutex(false, "VoidstrapSnapTap");
				_layout = new MacOSKeyboardLayout();
				_tap = CGEventTapCreate(1, 0, 0, (1UL << 10) | (1UL << 11), &OnEvent, GCHandle.ToIntPtr(handle));
				if (_tap == 0)
				{
					Report("macOS could not create the Snap Tap keyboard filter. Check Accessibility access and restart Voidstrap");
					return;
				}
				CGEventTapEnable(_tap, false);
				source = CFMachPortCreateRunLoopSource(0, _tap, 0);
				mode = CFStringCreateWithCString(0, "kCFRunLoopDefaultMode", 0x08000100);
				loop = CFRunLoopGetCurrent();
				if (source == 0 || mode == 0 || loop == 0)
					return;
				CFRunLoopAddSource(loop, source, mode);
				Ready = true;
				Started.Set();
				while (!StopEvent.IsSet && !ownsMutex)
				{
					try { ownsMutex = mutex.WaitOne(500); }
					catch (AbandonedMutexException) { ownsMutex = true; }
				}
				if (!ownsMutex || StopEvent.IsSet)
					return;
				Report("Snap Tap keyboard filter is ready on macOS");
				while (!StopEvent.IsSet)
				{
					ApplyPending();
					RefreshLayout(0);
					bool access = MacOSInputAccess.IsGranted;
					if (!access || IsSecureEventInputEnabled())
						Release(false);
					else
						UpdateFocus();
					if (CGEventTapIsEnabled(_tap) != access)
						CGEventTapEnable(_tap, access);
					_ = CFRunLoopRunInMode(mode, 0.5, false);
				}
			}
			catch (Exception ex)
			{
				Report("Snap Tap input thread stopped: " + ex.Message);
			}
			finally
			{
				if (_tap != 0)
				{
					CGEventTapEnable(_tap, false);
					try { Release(true); }
					catch (Exception ex) { Report("Snap Tap key cleanup failed: " + ex.Message); }
					CFMachPortInvalidate(_tap);
				}
				if (source != 0)
				{
					if (loop != 0 && mode != 0)
						CFRunLoopRemoveSource(loop, source, mode);
					CFRelease(source);
				}
				if (mode != 0)
					CFRelease(mode);
				if (_tap != 0)
					CFRelease(_tap);
				if (ownsMutex)
					mutex?.ReleaseMutex();
				mutex?.Dispose();
				_layout?.Dispose();
				handle.Free();
				Ready = false;
				Started.Set();
			}
		}

		private void ApplyPending()
		{
			Config? config = Interlocked.Exchange(ref Pending, null);
			if (config == null)
				return;
			Release(true);
			_resolver.Configure(config.Groups, config.Priority);
			_resolver.Reset();
			Report("Snap Tap on macOS uses " + config.Priority + " on " + OpposingKeyResolver.FormatGroups(config.Groups));
		}

		private bool UpdateFocus()
		{
			int pid = MacOSProcessPolicy.FrontmostProcessId();
			if (pid != _foregroundPid)
			{
				Release(false);
				_foregroundPid = pid;
				_target = IsRoblox(pid);
				_targetPid = _target ? pid : 0;
				Report(_target ? "Roblox focused, Snap Tap engaged" : "Roblox unfocused, Snap Tap idle");
			}
			return _target;
		}

		private void RefreshLayout(uint keyboardType)
		{
			if (_layout == null || !_layout.Refresh(KeyCodes, keyboardType, out ushort[] keys))
				return;
			Release(true);
			_keyCodes = keys;
			_virtualKeys = BuildVirtualKeys(keys);
			Report("Snap Tap keyboard layout updated");
		}

		public nint Handle(nint proxy, uint type, nint input)
		{
			if (type is 0xFFFFFFFE or 0xFFFFFFFF)
			{
				Release(false);
				if (!StopEvent.IsSet && MacOSInputAccess.IsGranted)
					CGEventTapEnable(_tap, true);
				return input;
			}
			if (input == 0 || type is not (10 or 11) || CGEventGetIntegerValueField(input, 42) == EventTag)
				return input;
			ApplyPending();
			bool focused = UpdateFocus();
			if (focused)
				RefreshLayout((uint)CGEventGetIntegerValueField(input, 10));
			long code = CGEventGetIntegerValueField(input, 9);
			int key = (ulong)code < (ulong)_virtualKeys.Length ? _virtualKeys[code] : 0;
			if (key == 0 || !_resolver.IsMapped(key))
				return input;
			if (StopEvent.IsSet || !focused || IsSecureEventInputEnabled())
				return input;
			_output.Clear();
			if (_resolver.Route(key, type == 10, true, _output) == KeyRouteDecision.Forward)
				return input;
			int count = 0;
			try
			{
				foreach (RoutedKey routed in _output)
				{
					nint copy = CGEventCreateCopy(input);
					if (copy == 0)
					{
						Release(true);
						return input;
					}
					_events[count++] = copy;
					CGEventSetType(copy, routed.Down ? 10U : 11U);
					CGEventSetIntegerValueField(copy, 9, _keyCodes[routed.VirtualKey]);
					CGEventSetIntegerValueField(copy, 42, EventTag);
					if (!routed.Down || routed.VirtualKey != key)
						CGEventSetIntegerValueField(copy, 8, 0);
				}
				for (int index = 0; index < count; index++)
				{
					CGEventTapPostEvent(proxy, _events[index]);
					_delivered[_output[index].VirtualKey] = _output[index].Down;
				}
				return 0;
			}
			finally
			{
				for (int index = 0; index < count; index++)
					CFRelease(_events[index]);
			}
		}

		private void Release(bool restorePhysical)
		{
			bool restore = restorePhysical && _targetPid != 0 && MacOSProcessPolicy.FrontmostProcessId() == _targetPid && !IsSecureEventInputEnabled();
			for (int key = 0; key < _delivered.Length; key++)
			{
				bool down = restore && _resolver.IsMapped(key) && CGEventSourceKeyState(1, _keyCodes[key]);
				if (_targetPid != 0 && _delivered[key] != down)
				{
					nint input = CGEventCreateKeyboardEvent(0, _keyCodes[key], down);
					if (input != 0)
					{
						CGEventSetIntegerValueField(input, 42, EventTag);
						CGEventPostToPid(_targetPid, input);
						CFRelease(input);
					}
				}
				_delivered[key] = false;
			}
			_resolver.Reset();
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			StopEvent.Dispose();
			Started.Dispose();
			_disposed = true;
			GC.SuppressFinalize(this);
		}

		public void Recover()
		{
			StopEvent.Set();
			try { Release(true); }
			catch { }
			Report("Snap Tap stopped after an input error, normal keyboard input is restored");
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static nint OnEvent(nint proxy, uint type, nint input, nint userInfo)
	{
		Session? session = null;
		try
		{
			session = (Session)GCHandle.FromIntPtr(userInfo).Target!;
			return session.Handle(proxy, type, input);
		}
		catch
		{
			try { session?.Recover(); }
			catch { }
			return input;
		}
	}

	private static bool IsRoblox(int pid)
	{
		if (pid <= 0)
			return false;
		byte* path = stackalloc byte[4096];
		if (proc_pidpath(pid, path, 4096) <= 0)
			return false;
		string? executable = Marshal.PtrToStringUTF8((nint)path);
		return executable != null && executable.EndsWith("/Roblox.app/Contents/MacOS/RobloxPlayer", StringComparison.OrdinalIgnoreCase);
	}

	private static void Report(string message)
	{
		ThreadPool.QueueUserWorkItem(WriteLog, message);
	}

	private static void WriteLog(object? message) => App.Logger?.WriteLine("SnapTap", (string)message!);

	internal static ushort[] BuildKeyCodes()
	{
		ushort[] map = new ushort[256];
		Array.Fill(map, ushort.MaxValue);
		ushort[] letters = [0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6];
		ushort[] digits = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];
		ushort[] keypad = [82, 83, 84, 85, 86, 87, 88, 89, 91, 92];
		ushort[] functions = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90];
		for (int i = 0; i < letters.Length; i++) map['A' + i] = letters[i];
		for (int i = 0; i < digits.Length; i++) map['0' + i] = digits[i];
		for (int i = 0; i < keypad.Length; i++) map[0x60 + i] = keypad[i];
		for (int i = 0; i < functions.Length; i++) map[0x70 + i] = functions[i];
		map[0x08] = 51; map[0x09] = 48; map[0x0D] = 36; map[0x1B] = 53; map[0x20] = 49;
		map[0x21] = 116; map[0x22] = 121; map[0x23] = 119; map[0x24] = 115;
		map[0x25] = 123; map[0x26] = 126; map[0x27] = 124; map[0x28] = 125;
		map[0x2D] = 114; map[0x2E] = 117;
		map[0x6A] = 67; map[0x6B] = 69; map[0x6D] = 78; map[0x6E] = 65; map[0x6F] = 75;
		map[0xBA] = 41; map[0xBB] = 24; map[0xBC] = 43; map[0xBD] = 27; map[0xBE] = 47;
		map[0xBF] = 44; map[0xC0] = 50; map[0xDB] = 33; map[0xDC] = 42; map[0xDD] = 30; map[0xDE] = 39;
		map[0xE2] = 10;
		return map;
	}

	private static int[] BuildVirtualKeys(ushort[] keyCodes)
	{
		int[] map = new int[128];
		for (int key = 0; key < keyCodes.Length; key++)
			if (keyCodes[key] < map.Length && map[keyCodes[key]] == 0)
				map[keyCodes[key]] = key;
		return map;
	}

	[LibraryImport(CoreGraphics)] private static partial nint CGEventTapCreate(uint tap, uint place, uint options, ulong mask, delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint> callback, nint userInfo);
	[LibraryImport(CoreGraphics)] private static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.I1)] bool enable);
	[LibraryImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static partial bool CGEventTapIsEnabled(nint tap);
	[LibraryImport(CoreGraphics)] private static partial long CGEventGetIntegerValueField(nint input, uint field);
	[LibraryImport(CoreGraphics)] private static partial void CGEventSetIntegerValueField(nint input, uint field, long value);
	[LibraryImport(CoreGraphics)] private static partial void CGEventSetType(nint input, uint type);
	[LibraryImport(CoreGraphics)] private static partial nint CGEventCreateCopy(nint input);
	[LibraryImport(CoreGraphics)] private static partial nint CGEventCreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
	[LibraryImport(CoreGraphics)] private static partial void CGEventTapPostEvent(nint proxy, nint input);
	[LibraryImport(CoreGraphics)] private static partial void CGEventPostToPid(int pid, nint input);
	[LibraryImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static partial bool CGEventSourceKeyState(int state, ushort key);
	[LibraryImport(CoreFoundation)] private static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);
	[LibraryImport(CoreFoundation)] private static partial void CFMachPortInvalidate(nint port);
	[LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
	[LibraryImport(CoreFoundation)] private static partial nint CFRunLoopGetCurrent();
	[LibraryImport(CoreFoundation)] private static partial void CFRunLoopAddSource(nint loop, nint source, nint mode);
	[LibraryImport(CoreFoundation)] private static partial void CFRunLoopRemoveSource(nint loop, nint source, nint mode);
	[LibraryImport(CoreFoundation)] private static partial int CFRunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
	[LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
	[LibraryImport("/usr/lib/libproc.dylib")] private static partial int proc_pidpath(int pid, byte* path, uint size);
	[LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")] [return: MarshalAs(UnmanagedType.I1)] private static partial bool IsSecureEventInputEnabled();
}
