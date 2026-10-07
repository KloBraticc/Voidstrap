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

	private sealed class PositionTracker
	{
		private readonly Window _window;
		private long _lastSync;
		private MacOSWindow.Rect _pressFrame;
		private (double X, double Y) _pressMouse;
		private bool _pressed;

		public PositionTracker(Window window)
		{
			_window = window;
			_window.PreviewMouseMove += OnPreviewMouseMove;
			_window.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
			_window.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
			_window.Activated += OnActivated;
			_window.Closed += OnClosed;
		}

		private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
				Sync(false);
		}

		private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			nint native = ResolveNativeWindow(_window);
			_pressed = native != 0;
			if (!_pressed)
				return;
			_pressFrame = MacOSWindow.GetFrame(native);
			_pressMouse = MacOSWindow.MouseLocation();
		}

		private void OnPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (_pressed)
			{
				_pressed = false;
				nint native = ResolveNativeWindow(_window);
				MacOSWindow.Rect frame = native == 0 ? default : MacOSWindow.GetFrame(native);
				bool moved = Math.Abs(frame.X - _pressFrame.X) > 1 || Math.Abs(frame.Y - _pressFrame.Y) > 1;
				bool resized = Math.Abs(frame.Width - _pressFrame.Width) > 1 || Math.Abs(frame.Height - _pressFrame.Height) > 1;
				if (native != 0 && moved && !resized)
				{
					(double mouseX, double mouseY) = MacOSWindow.MouseLocation();
					MacOSWindow.Rect expected = new(_pressFrame.X + mouseX - _pressMouse.X, _pressFrame.Y + mouseY - _pressMouse.Y, frame.Width, frame.Height);
					if (!expected.Matches(frame))
						MacOSWindow.SetFrame(native, expected, false);
				}
			}
			Sync(true);
		}

		private void OnActivated(object? sender, EventArgs e) => Sync(true);

		public void Sync(bool force)
		{
			long now = Environment.TickCount64;
			if (!force && now - _lastSync < 200)
				return;
			_lastSync = now;
			nint native = ResolveNativeWindow(_window);
			if (native == 0 || MacOSWindow.IsMinimized(native))
				return;
			MacOSWindow.Rect frame = MacOSWindow.GetFrame(native);
			double top = MacOSWindow.PrimaryScreenHeight() - (frame.Y + frame.Height);
			if (Math.Abs(_window.Left - frame.X) > 1)
				_window.Left = frame.X;
			if (Math.Abs(_window.Top - top) > 1)
				_window.Top = top;
		}

		private void OnClosed(object? sender, EventArgs e)
		{
			_window.PreviewMouseMove -= OnPreviewMouseMove;
			_window.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
			_window.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
			_window.Activated -= OnActivated;
			_window.Closed -= OnClosed;
			Trackers.Remove(_window);
		}
	}

	private static readonly ConditionalWeakTable<Window, PositionTracker> Trackers = new();

	internal static void TrackPosition(Window window)
	{
		if (Trackers.TryGetValue(window, out _))
			return;
		Trackers.Add(window, new PositionTracker(window));
		nint native = ResolveNativeWindow(window);
		if (native != 0 && window.ResizeMode != ResizeMode.NoResize)
			MacOSWindow.EnableMinimize(native);
	}

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
		if (Trackers.TryGetValue(window, out PositionTracker? tracker))
			tracker.Sync(true);
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
