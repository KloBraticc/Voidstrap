using System;
using System.Collections.Generic;
using Voidstrap.Platform.Linux;

namespace Voidstrap.KeyRouting;

internal static class LinuxSnapTap
{
	private const string LOG_IDENT = "SnapTap";

	private static readonly object Gate = new();
	private static readonly ushort[] VirtualToEvdev = BuildVirtualToEvdev();
	private static readonly int[] EvdevToVirtual = BuildEvdevToVirtual(VirtualToEvdev);

	private static LinuxKeyboardInterceptor? _interceptor;
	private static Router? _router;

	public static LinuxInputAccessState Access => LinuxKeyboardInterceptor.CheckAccess();

	public static bool Start(List<int[]> groups, OpposingKeyPriority priority)
	{
		List<int[]> supported = FilterSupported(groups);
		if (supported.Count == 0)
		{
			App.Logger?.WriteLine(LOG_IDENT, "None of the configured Snap Tap keys exist on Linux keyboards, Snap Tap stays off");
			Stop();
			return false;
		}

		lock (Gate)
		{
			if (_interceptor != null && _router != null)
			{
				_router.Configure(supported, priority);
				return true;
			}

			LinuxInputAccessState access = LinuxKeyboardInterceptor.CheckAccess();
			if (access != LinuxInputAccessState.Ready)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Snap Tap cannot start: " + DescribeAccess(access));
				return false;
			}

			_router = new Router();
			_router.Configure(supported, priority);
			_interceptor = new LinuxKeyboardInterceptor(_router, message => App.Logger?.WriteLine(LOG_IDENT, message));
			if (_interceptor.Start())
				return true;

			_interceptor.Dispose();
			_interceptor = null;
			_router = null;
			return false;
		}
	}

	public static void Stop()
	{
		LinuxKeyboardInterceptor? interceptor;
		lock (Gate)
		{
			interceptor = _interceptor;
			_interceptor = null;
			_router = null;
		}
		interceptor?.Dispose();
	}

	public static string DescribeAccess(LinuxInputAccessState access)
	{
		return access switch
		{
			LinuxInputAccessState.Ready => "Keyboard access is ready",
			LinuxInputAccessState.MissingKeyboardAccess => "Voidstrap cannot read your keyboard yet",
			LinuxInputAccessState.MissingUinputAccess => "Voidstrap cannot create its virtual keyboard yet",
			LinuxInputAccessState.NoKeyboards => "No keyboard was found",
			_ => "This system does not provide the Linux input devices Snap Tap needs"
		};
	}

	private static List<int[]> FilterSupported(List<int[]> groups)
	{
		List<int[]> supported = new(groups.Count);
		foreach (int[] group in groups)
		{
			List<int> keys = new(group.Length);
			foreach (int key in group)
			{
				if ((uint)key < VirtualToEvdev.Length && VirtualToEvdev[key] != 0)
					keys.Add(key);
				else
					App.Logger?.WriteLine(LOG_IDENT, OpposingKeyResolver.KeyName(key) + " has no Linux key mapping and is skipped");
			}
			if (keys.Count >= 2)
				supported.Add([.. keys]);
		}
		return supported;
	}

	private sealed class Router : ILinuxKeyRouter
	{
		private sealed record Settings(List<int[]> Groups, OpposingKeyPriority Priority);

		private readonly OpposingKeyResolver _resolver = new();
		private readonly List<RoutedKey> _routed = new(16);
		private volatile Settings? _pending;
		private bool _focused;
		private bool _swapReported;

		public void Configure(List<int[]> groups, OpposingKeyPriority priority)
		{
			_pending = new Settings(groups, priority);
		}

		public bool IsTargetFocused()
		{
			bool focused = LinuxWindowInterop.IsRuntimeWindowActive();
			if (focused != _focused)
				_swapReported = false;
			_focused = focused;
			return focused;
		}

		public void Route(LinuxKeyEvent input, bool focused, List<LinuxKeyEvent> output)
		{
			ApplyPending();
			int key = input.Code < EvdevToVirtual.Length ? EvdevToVirtual[input.Code] : 0;
			if (key == 0 || !_resolver.IsMapped(key))
			{
				output.Add(input);
				return;
			}

			_routed.Clear();
			if (_resolver.Route(key, input.Value != 0, focused, _routed) == KeyRouteDecision.Forward)
			{
				output.Add(input);
				return;
			}

			int released = -1;
			int pressed = -1;
			foreach (RoutedKey routed in _routed)
			{
				ushort code = VirtualToEvdev[routed.VirtualKey];
				if (code == 0)
					continue;
				int value = routed.Down ? (input.Value == 2 && routed.VirtualKey == key ? 2 : 1) : 0;
				output.Add(new LinuxKeyEvent(code, value));
				if (value == 0)
					released = routed.VirtualKey;
				else if (value == 1)
					pressed = routed.VirtualKey;
			}

			if (!_swapReported && released >= 0 && pressed >= 0)
			{
				_swapReported = true;
				App.Logger?.WriteLine(LOG_IDENT, "Snap Tap switched " + OpposingKeyResolver.KeyName(released) + " to " + OpposingKeyResolver.KeyName(pressed));
			}
		}

		public void Reset()
		{
			ApplyPending();
			_resolver.Reset();
		}

		private void ApplyPending()
		{
			Settings? settings = _pending;
			if (settings == null)
				return;
			_pending = null;
			_resolver.Configure(settings.Groups, settings.Priority);
			_resolver.Reset();
			App.Logger?.WriteLine(LOG_IDENT, "Snap Tap on Linux uses " + settings.Priority + " on " + OpposingKeyResolver.FormatGroups(settings.Groups));
		}
	}

	private static ushort[] BuildVirtualToEvdev()
	{
		ushort[] map = new ushort[256];
		ushort[] letters = [30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44];
		for (int index = 0; index < letters.Length; index++)
			map['A' + index] = letters[index];
		map['0'] = 11;
		for (int digit = 1; digit <= 9; digit++)
			map['0' + digit] = (ushort)(1 + digit);

		map[0x08] = 14;
		map[0x09] = 15;
		map[0x0D] = 28;
		map[0x14] = 58;
		map[0x1B] = 1;
		map[0x20] = 57;
		map[0x21] = 104;
		map[0x22] = 109;
		map[0x23] = 107;
		map[0x24] = 102;
		map[0x25] = 105;
		map[0x26] = 103;
		map[0x27] = 106;
		map[0x28] = 108;
		map[0x2D] = 110;
		map[0x2E] = 111;

		ushort[] keypad = [82, 79, 80, 81, 75, 76, 77, 71, 72, 73];
		for (int index = 0; index < keypad.Length; index++)
			map[0x60 + index] = keypad[index];
		map[0x6A] = 55;
		map[0x6B] = 78;
		map[0x6D] = 74;
		map[0x6E] = 83;
		map[0x6F] = 98;

		ushort[] functions = [59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88];
		for (int index = 0; index < functions.Length; index++)
			map[0x70 + index] = functions[index];

		map[0xBA] = 39;
		map[0xBB] = 13;
		map[0xBC] = 51;
		map[0xBD] = 12;
		map[0xBE] = 52;
		map[0xBF] = 53;
		map[0xC0] = 41;
		map[0xDB] = 26;
		map[0xDC] = 43;
		map[0xDD] = 27;
		map[0xDE] = 40;
		return map;
	}

	private static int[] BuildEvdevToVirtual(ushort[] virtualToEvdev)
	{
		int[] map = new int[256];
		for (int key = 0; key < virtualToEvdev.Length; key++)
		{
			ushort code = virtualToEvdev[key];
			if (code != 0 && map[code] == 0)
				map[code] = key;
		}
		return map;
	}
}
