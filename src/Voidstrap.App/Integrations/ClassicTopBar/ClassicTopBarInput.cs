using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations.ClassicTopBar;

internal static partial class ClassicTopBarInput
{
	public const ushort VkEscape = 0x1B;
	public const ushort VkTab = 0x09;
	public const ushort VkReturn = 0x0D;
	public const ushort VkOemTilde = 0xC0;
	public const ushort VkOemSlash = 0xBF;
	public const ushort VkA = 0x41;
	public const ushort VkR = 0x52;
	public const ushort VkF4 = 0x73;
	public const ushort VkF12 = 0x7B;
	public const ushort VkLeftWindows = 0x5B;
	public const ushort VkSnapshot = 0x2C;

	public const nuint InjectedTag = 0x564F4944;

	private const uint InputKeyboard = 1;
	private const uint KeyEventScanCode = 0x0008;
	private const uint KeyEventKeyUp = 0x0002;
	private const uint KeyEventExtendedKey = 0x0001;
	private const uint MapVirtualKeyToScan = 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct KeyboardInput
	{
		public ushort Vk;
		public ushort Scan;
		public uint Flags;
		public uint Time;
		public nuint ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Input
	{
		public uint Type;
		public KeyboardInput Keyboard;
		public int PadA;
		public int PadB;
	}

	[LibraryImport("user32.dll", EntryPoint = "SendInput")]
	private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

	[LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
	private static partial uint MapVirtualKey(uint code, uint mapType);

	[LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetForegroundWindow(nint hwnd);

	public static void FocusRoblox()
	{
		nint hwnd = RobloxWindowTracker.Current.Hwnd;
		if (hwnd == 0)
		{
			return;
		}
		try
		{
			if (!OperatingSystem.IsWindows())
			{
				Voidstrap.Platform.Linux.LinuxWindowInterop.TryActivateWindow(hwnd);
				return;
			}
			SetForegroundWindow(hwnd);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ClassicTopBarInput", "Roblox could not be focused: " + ex.Message);
		}
	}

	public static void Send(params ushort[] keys)
	{
		foreach (ushort key in keys)
		{
			Tap(key);
		}
	}

	public static void Tap(ushort key)
	{
		Write(key, down: true);
		Write(key, down: false);
	}

	public static void Hold(ushort key)
	{
		Write(key, down: true);
	}

	public static void Release(ushort key)
	{
		Write(key, down: false);
	}

	public static async Task SequenceAsync(CancellationToken token, params (ushort Key, int DelayMilliseconds)[] steps)
	{
		foreach ((ushort key, int delay) in steps)
		{
			token.ThrowIfCancellationRequested();
			Tap(key);
			if (delay > 0)
			{
				await Task.Delay(delay, token).ConfigureAwait(true);
			}
		}
	}

	private static void Write(ushort key, bool down)
	{
		if (!OperatingSystem.IsWindows())
		{
			Voidstrap.Platform.Linux.LinuxClassicKeys.Write(key, down);
			return;
		}
		uint scan = MapVirtualKey(key, MapVirtualKeyToScan);
		uint flags = KeyEventScanCode;
		if (IsExtended(key))
		{
			flags |= KeyEventExtendedKey;
		}
		if (!down)
		{
			flags |= KeyEventKeyUp;
		}
		Input[] inputs =
		{
			new Input
			{
				Type = InputKeyboard,
				Keyboard = new KeyboardInput
				{
					Vk = scan == 0 ? key : (ushort)0,
					Scan = (ushort)scan,
					Flags = scan == 0 ? flags & ~KeyEventScanCode : flags,
					ExtraInfo = InjectedTag
				}
			}
		};
		SendInput(1, inputs, Marshal.SizeOf<Input>());
	}

	private static bool IsExtended(ushort key)
	{
		return key is VkLeftWindows or VkSnapshot;
	}
}
