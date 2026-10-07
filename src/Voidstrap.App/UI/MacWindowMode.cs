using System;
using System.Runtime.CompilerServices;
using System.Windows;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.UI;

internal static class MacWindowMode
{
	private sealed class State
	{
		public MacOSWindow.Rect Restore;
		public bool Maximized;
		public long ChangedAt;
	}

	private static readonly ConditionalWeakTable<Window, State> States = new();

	internal static nint ResolveNativeWindow(Window window)
	{
#if CROSSPLAT
		if (System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(window, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host)
			&& host?.SilkWindow?.Native?.Cocoa is { } cocoa
			&& cocoa != 0)
			return cocoa;
#endif
		return 0;
	}

	internal static bool IsMaximized(Window window)
	{
		return States.TryGetValue(window, out State? state) && state.Maximized;
	}

	internal static void Minimize(Window window)
	{
		nint native = ResolveNativeWindow(window);
		if (native == 0)
		{
			App.Logger?.WriteLine("MacWindowMode::Minimize", "The native window could not be found");
			return;
		}
		MacOSWindow.Minimize(native);
	}

	internal static void ToggleMaximize(Window window)
	{
		if (window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
			return;
		nint native = ResolveNativeWindow(window);
		if (native == 0)
		{
			App.Logger?.WriteLine("MacWindowMode::ToggleMaximize", "The native window could not be found");
			return;
		}
		State state = States.GetValue(window, CreateState);
		MacOSWindow.Rect frame = MacOSWindow.GetFrame(native);
		MacOSWindow.Rect work = MacOSWindow.GetWorkArea(native);
		bool maximize = !state.Maximized && !frame.Matches(work);
		if (maximize)
		{
			state.Restore = frame;
			MacOSWindow.SetFrame(native, work, true);
		}
		else
		{
			MacOSWindow.Rect restore = state.Restore.Width > 0 ? state.Restore : Centered(work, Math.Min(1071, work.Width * 0.85), Math.Min(690, work.Height * 0.85));
			MacOSWindow.SetFrame(native, restore, true);
		}
		SetMaximized(window, state, maximize);
	}

	internal static bool RestoreForDrag(Window window, Point pointer)
	{
		if (!States.TryGetValue(window, out State? state) || !state.Maximized)
			return false;
		nint native = ResolveNativeWindow(window);
		if (native == 0)
			return false;
		MacOSWindow.Rect frame = MacOSWindow.GetFrame(native);
		MacOSWindow.Rect work = MacOSWindow.GetWorkArea(native);
		MacOSWindow.Rect restore = state.Restore.Width > 0 ? state.Restore : Centered(work, Math.Min(1071, work.Width * 0.85), Math.Min(690, work.Height * 0.85));
		(double mouseX, double mouseY) = MacOSWindow.MouseLocation();
		double ratio = frame.Width > 0 ? Math.Clamp((mouseX - frame.X) / frame.Width, 0, 1) : 0.5;
		double offsetFromTop = Math.Max(8, frame.Y + frame.Height - mouseY);
		MacOSWindow.Rect moved = new(mouseX - restore.Width * ratio, mouseY + offsetFromTop - restore.Height, restore.Width, restore.Height);
		MacOSWindow.SetFrame(native, moved, false);
		SetMaximized(window, state, false);
		return true;
	}

	internal static void SyncAfterResize(Window window)
	{
		if (!States.TryGetValue(window, out State? state) || !state.Maximized || Environment.TickCount64 - state.ChangedAt < 700)
			return;
		nint native = ResolveNativeWindow(window);
		if (native == 0 || MacOSWindow.IsMinimized(native))
			return;
		if (!MacOSWindow.GetFrame(native).Matches(MacOSWindow.GetWorkArea(native)))
			SetMaximized(window, state, false);
	}

	private static void SetMaximized(Window window, State state, bool maximized)
	{
		state.Maximized = maximized;
		state.ChangedAt = Environment.TickCount64;
		LinuxTitleBar.RefreshMaximized(window, maximized);
	}

	private static State CreateState(Window window) => new();

	private static MacOSWindow.Rect Centered(MacOSWindow.Rect work, double width, double height)
	{
		return new MacOSWindow.Rect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
	}
}
