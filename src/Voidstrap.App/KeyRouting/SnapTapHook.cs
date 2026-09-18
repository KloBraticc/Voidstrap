using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Voidstrap.KeyRouting;

public static unsafe partial class SnapTapHook
{
	private sealed class Session
	{
		public readonly ManualResetEvent StopEvent = new(false);

		public Thread? Worker;

		public volatile uint ThreadId;
	}

	private sealed record Config(List<int[]> Groups, OpposingKeyPriority Priority);

	[StructLayout(LayoutKind.Sequential)]
	private struct KeybdInput
	{
		public ushort Vk;
		public ushort Scan;
		public uint Flags;
		public uint Time;
		public nuint ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KeyboardInput
	{
		public uint Type;
		public KeybdInput Ki;
		private readonly ulong _unionPadding;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Msg
	{
		public nint Hwnd;
		public uint Message;
		public nuint WParam;
		public nint LParam;
		public uint Time;
		public int X;
		public int Y;
		public uint Private;
	}

	private const string LOG_IDENT = "SnapTap";
	private const string MutexName = "Local\\VoidstrapSnapTap";
	private const uint StopMessage = 0x8000 + 0x5354;
	private const uint TimerMessage = 0x0113;
	private const uint RehookIntervalMs = 1000;
	private const uint LlkhfExtended = 0x01;
	private const uint LlkhfInjected = 0x10;
	private const uint InputKeyboard = 1;
	private const uint KeyeventfExtendedKey = 0x01;
	private const uint KeyeventfKeyUp = 0x02;
	private const uint KeyeventfScanCode = 0x08;
	private const uint MapvkVkToVsc = 0;
	private const uint PmNoRemove = 0;
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const nuint InjectedTag = 0x56535450;
	private const int MaxBatch = 16;

	private static readonly Lock _gate = new();
	private static readonly HOOKPROC _hookProc = OnKeyboardEvent;
	private static readonly OpposingKeyResolver _resolver = new();
	private static readonly List<RoutedKey> _routed = new(MaxBatch);
	private static readonly ushort[] _scanCodes = new ushort[256];
	private static readonly bool[] _extended = new bool[256];
	private static readonly char[] _classBuffer = new char[32];
	private static readonly char[] _pathBuffer = new char[1024];
	private static readonly WaitCallback _writeLog = WriteLog;

	private static Session? _session;
	private static volatile Config? _config;
	private static string? _configDescription;
	private static Config? _appliedConfig;
	private static UnhookWindowsHookExSafeHandle? _hook;
	private static HWND _lastForeground;
	private static uint _lastForegroundPid;
	private static bool _robloxFocused;
	private static bool _swapReported;
	private static bool _injectFailureReported;

	public static void ApplyFromSettings()
	{
		if (App.Settings.Prop.SnapTapEnabled)
			Start();
		else
			Stop();
	}

	public static bool Start()
	{
		if (!Voidstrap.Utility.Platform.SupportsInputHooks)
			return false;

		Config config = ReadConfig();
		if (config.Groups.Count == 0)
		{
			App.Logger?.WriteLine(LOG_IDENT, "No valid key groups configured, Snap Tap stays off");
			Stop();
			return false;
		}

		string description = Describe(config);
		lock (_gate)
		{
			if (_session != null)
			{
				if (description != _configDescription)
				{
					_configDescription = description;
					_config = config;
					App.Logger?.WriteLine(LOG_IDENT, "Settings updated: " + description);
				}
				return true;
			}
			_configDescription = description;
			_config = config;
			Session session = new()
			{
				Worker = new Thread(RunSession)
				{
					Name = "SnapTap",
					IsBackground = true,
					Priority = ThreadPriority.Highest
				}
			};
			_session = session;
			session.Worker.Start(session);
		}
		App.Logger?.WriteLine(LOG_IDENT, "Snap Tap starting: " + description);
		return true;
	}

	public static void Stop()
	{
		Session? session;
		lock (_gate)
		{
			session = _session;
			_session = null;
		}
		if (session == null)
			return;

		session.StopEvent.Set();
		uint threadId = session.ThreadId;
		if (threadId != 0)
			_ = PostThreadMessageW(threadId, StopMessage, 0, 0);
		if (session.Worker != null && session.Worker.Join(1000))
			session.StopEvent.Dispose();
		else
			App.Logger?.WriteLine(LOG_IDENT, "Snap Tap thread did not exit in time");
		App.Logger?.WriteLine(LOG_IDENT, "Snap Tap stopped");
	}

	public static void Shutdown()
	{
		Stop();
	}

	private static Config ReadConfig()
	{
		int mode = App.Settings.Prop.SnapTapMode;
		OpposingKeyPriority priority = Enum.IsDefined(typeof(OpposingKeyPriority), mode) ? (OpposingKeyPriority)mode : OpposingKeyPriority.LastPressed;
		return new Config(OpposingKeyResolver.ParseGroups(App.Settings.Prop.SnapTapKeys), priority);
	}

	private static string Describe(Config config)
	{
		return config.Priority + " on " + OpposingKeyResolver.FormatGroups(config.Groups);
	}

	private static void RunSession(object? state)
	{
		var session = (Session)state!;
		Mutex? mutex = null;
		bool owned = false;
		nuint timer = 0;
		try
		{
			mutex = new Mutex(false, MutexName);
			owned = AcquireOwnership(mutex, session.StopEvent);
			if (!owned)
				return;

			PeekMessageW(out _, 0, 0, 0, PmNoRemove);
			_resolver.Reset();
			_appliedConfig = null;
			_lastForeground = HWND.Null;
			_lastForegroundPid = 0;
			_robloxFocused = false;
			_swapReported = false;
			_injectFailureReported = false;
			ApplyPendingConfig();

			_hook = InstallHook();
			if (_hook is null || _hook.IsInvalid)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Keyboard hook could not be installed, error " + Marshal.GetLastWin32Error());
				return;
			}
			timer = SetTimer(0, 0, RehookIntervalMs, 0);

			session.ThreadId = GetCurrentThreadId();
			if (session.StopEvent.WaitOne(0))
				return;

			App.Logger?.WriteLine(LOG_IDENT, "Keyboard hook active, waiting for Roblox focus");
			while (GetMessageW(out Msg msg, 0, 0, 0) > 0)
			{
				if (msg.Message == StopMessage)
					break;
				if (msg.Message == TimerMessage)
					Rehook();
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("SnapTap::RunSession", ex);
		}
		finally
		{
			session.ThreadId = 0;
			if (timer != 0)
				_ = KillTimer(0, timer);
			try
			{
				_hook?.Dispose();
			}
			catch
			{
			}
			_hook = null;
			if (owned)
			{
				try
				{
					mutex!.ReleaseMutex();
				}
				catch
				{
				}
			}
			mutex?.Dispose();
		}
	}

	private static UnhookWindowsHookExSafeHandle InstallHook()
	{
		using var module = new FreeLibrarySafeHandle(GetModuleHandleW(null), ownsHandle: false);
		return PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _hookProc, module, 0);
	}

	private static void Rehook()
	{
		UnhookWindowsHookExSafeHandle replacement = InstallHook();
		if (replacement.IsInvalid)
		{
			replacement.Dispose();
			return;
		}
		UnhookWindowsHookExSafeHandle? previous = _hook;
		_hook = replacement;
		try
		{
			previous?.Dispose();
		}
		catch
		{
		}
	}

	private static bool AcquireOwnership(Mutex mutex, WaitHandle stop)
	{
		WaitHandle[] handles = [stop, mutex];
		bool logged = false;
		while (true)
		{
			try
			{
				int index = WaitHandle.WaitAny(handles, logged ? Timeout.Infinite : 0);
				if (index != WaitHandle.WaitTimeout)
					return index == 1;
			}
			catch (AbandonedMutexException ex) when (ex.MutexIndex == 1)
			{
				return true;
			}
			App.Logger?.WriteLine(LOG_IDENT, "Another Voidstrap process owns Snap Tap, taking over when it exits");
			logged = true;
		}
	}

	private static void ApplyPendingConfig()
	{
		Config? config = _config;
		if (config == null || ReferenceEquals(config, _appliedConfig))
			return;
		_resolver.Configure(config.Groups, config.Priority);
		_appliedConfig = config;
	}

	private static LRESULT OnKeyboardEvent(int code, WPARAM wParam, LPARAM lParam)
	{
		if (code >= 0)
		{
			try
			{
				var data = (KBDLLHOOKSTRUCT*)lParam.Value;
				uint flags = (uint)data->flags;
				uint message = (uint)wParam.Value;
				bool down = message == PInvoke.WM_KEYDOWN || message == PInvoke.WM_SYSKEYDOWN;
				bool up = message == PInvoke.WM_KEYUP || message == PInvoke.WM_SYSKEYUP;
				if ((flags & LlkhfInjected) == 0 && (down || up))
				{
					ApplyPendingConfig();
					int key = (int)data->vkCode;
					if (_resolver.IsMapped(key))
					{
						_scanCodes[key] = (ushort)data->scanCode;
						_extended[key] = (flags & LlkhfExtended) != 0;
						_routed.Clear();
						if (_resolver.Route(key, down, IsRobloxForeground(), _routed) == KeyRouteDecision.Swallow)
						{
							if (_routed.Count == 0 || Inject(_routed))
								return (LRESULT)1;
							_resolver.Reset();
						}
					}
				}
			}
			catch
			{
			}
		}
		return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
	}

	private static bool Inject(List<RoutedKey> keys)
	{
		int count = Math.Min(keys.Count, MaxBatch);
		KeyboardInput* inputs = stackalloc KeyboardInput[count];
		int released = -1;
		int pressed = -1;
		for (int i = 0; i < count; i++)
		{
			RoutedKey routed = keys[i];
			int key = routed.VirtualKey;
			ushort scan = _scanCodes[key];
			if (scan == 0)
				scan = (ushort)MapVirtualKeyW((uint)key, MapvkVkToVsc);
			uint keyFlags = KeyeventfScanCode;
			if (_extended[key])
				keyFlags |= KeyeventfExtendedKey;
			if (routed.Down)
				pressed = key;
			else
			{
				keyFlags |= KeyeventfKeyUp;
				released = key;
			}
			inputs[i] = new KeyboardInput
			{
				Type = InputKeyboard,
				Ki = new KeybdInput
				{
					Vk = (ushort)key,
					Scan = scan,
					Flags = keyFlags,
					ExtraInfo = InjectedTag
				}
			};
		}

		uint sent = SendInput((uint)count, inputs, sizeof(KeyboardInput));
		if (sent != (uint)count)
		{
			if (!_injectFailureReported)
			{
				_injectFailureReported = true;
				Report("Windows rejected the Snap Tap key event, error " + Marshal.GetLastWin32Error() + ". Run Voidstrap with the same rights as Roblox");
			}
			return sent != 0;
		}

		if (!_swapReported && released >= 0 && pressed >= 0)
		{
			_swapReported = true;
			Report("Snap Tap switched " + OpposingKeyResolver.KeyName(released) + " to " + OpposingKeyResolver.KeyName(pressed));
		}
		return true;
	}

	private static bool IsRobloxForeground()
	{
		HWND foreground = PInvoke.GetForegroundWindow();
		if (foreground == HWND.Null)
		{
			_lastForeground = HWND.Null;
			_lastForegroundPid = 0;
			return SetFocused(false);
		}
		PInvoke.GetWindowThreadProcessId(foreground, out uint pid);
		if (foreground == _lastForeground && pid == _lastForegroundPid)
			return _robloxFocused;
		_lastForeground = foreground;
		_lastForegroundPid = pid;
		return SetFocused(IsRobloxWindow(foreground, pid));
	}

	private static bool SetFocused(bool focused)
	{
		if (focused != _robloxFocused)
		{
			_robloxFocused = focused;
			_swapReported = false;
			Report(focused ? "Roblox focused, Snap Tap engaged" : "Roblox unfocused, Snap Tap idle");
		}
		return focused;
	}

	private static void Report(string message)
	{
		ThreadPool.UnsafeQueueUserWorkItem(_writeLog, message);
	}

	private static void WriteLog(object? message)
	{
		App.Logger?.WriteLine(LOG_IDENT, (string)message!);
	}

	private static bool IsRobloxWindow(HWND hwnd, uint pid)
	{
		if (pid == 0)
			return false;
		int classLength;
		fixed (char* buffer = _classBuffer)
			classLength = GetClassNameW(hwnd, buffer, _classBuffer.Length);
		if (!_classBuffer.AsSpan(0, Math.Max(0, classLength)).SequenceEqual("WINDOWSCLIENT"))
			return false;

		nint process = OpenProcess(ProcessQueryLimitedInformation, 0, pid);
		if (process == 0)
			return false;
		try
		{
			uint length = (uint)_pathBuffer.Length;
			fixed (char* buffer = _pathBuffer)
			{
				if (QueryFullProcessImageNameW(process, 0, buffer, &length) == 0)
					return false;
			}
			ReadOnlySpan<char> path = _pathBuffer.AsSpan(0, (int)length);
			ReadOnlySpan<char> name = path[(path.LastIndexOf('\\') + 1)..];
			return name.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase)
				&& name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			_ = CloseHandle(process);
		}
	}

	[LibraryImport("user32.dll", SetLastError = true)]
	private static partial uint SendInput(uint count, KeyboardInput* inputs, int size);

	[LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
	private static partial uint MapVirtualKeyW(uint code, uint mapType);

	[LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
	private static partial int GetMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax);

	[LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
	private static partial int PeekMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

	[LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
	private static partial int PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

	[LibraryImport("user32.dll")]
	private static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint timerProc);

	[LibraryImport("user32.dll")]
	private static partial int KillTimer(nint hwnd, nuint id);

	[LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
	private static partial int GetClassNameW(nint hwnd, char* className, int maxCount);

	[LibraryImport("kernel32.dll")]
	private static partial uint GetCurrentThreadId();

	[LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint GetModuleHandleW(string? moduleName);

	[LibraryImport("kernel32.dll")]
	private static partial nint OpenProcess(uint access, int inherit, uint processId);

	[LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
	private static partial int QueryFullProcessImageNameW(nint process, uint flags, char* exeName, uint* size);

	[LibraryImport("kernel32.dll")]
	private static partial int CloseHandle(nint handle);
}
