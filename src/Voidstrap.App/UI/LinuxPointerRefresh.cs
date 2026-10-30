using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxPointerRefresh
{
    internal static void Attach(Window window)
    {
#if CROSSPLAT
		if (!OperatingSystem.IsLinux() || window is null || States.TryGetValue(window, out _))
			return;

		State state = new(window);
		States.Add(window, state);
		state.Schedule();
#endif
    }

    internal static void Schedule(Window window)
    {
#if CROSSPLAT
		if (OperatingSystem.IsLinux() && window is not null && States.TryGetValue(window, out State? state))
			state.Schedule();
#endif
    }

#if CROSSPLAT
	private static readonly ConditionalWeakTable<Window, State> States = new();

	private static readonly FieldInfo? InputSubscriptionField = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowHost).GetField(
		"_inputSubscription",
		BindingFlags.Instance | BindingFlags.NonPublic);

	private const int SettleChecks = 12;

	private const int FirstShowChecks = 60;

	private const double PositionTolerance = 2.0;

	private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(100);

	private sealed class State
	{
		private readonly WeakReference<Window> _window;
		private DispatcherTimer? _timer;
		private int _remaining;
		private bool _shown;

		internal State(Window window)
		{
			_window = new WeakReference<Window>(window);
			window.LocationChanged += OnWindowChanged;
			window.SizeChanged += OnWindowSizeChanged;
			window.Activated += OnWindowChanged;
			window.ContentRendered += OnContentRendered;
			window.StateChanged += OnWindowChanged;
			window.Closed += OnClosed;
		}

		private void OnContentRendered(object? sender, EventArgs e)
		{
			if (_shown)
			{
				Schedule();
				return;
			}
			_shown = true;
			Schedule(FirstShowChecks);
		}

		private void OnWindowChanged(object? sender, EventArgs e)
		{
			Schedule();
		}

		private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
		{
			Schedule();
		}

		private void OnClosed(object? sender, EventArgs e)
		{
			if (sender is Window window)
			{
				window.LocationChanged -= OnWindowChanged;
				window.SizeChanged -= OnWindowSizeChanged;
				window.Activated -= OnWindowChanged;
				window.ContentRendered -= OnContentRendered;
				window.StateChanged -= OnWindowChanged;
				window.Closed -= OnClosed;
			}
			_timer?.Stop();
			_timer = null;
		}

		internal void Schedule(int checks = SettleChecks)
		{
			if (!_window.TryGetTarget(out Window? window))
				return;

			_remaining = Math.Max(_remaining, checks);
			if (_timer is null)
			{
				_timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher)
				{
					Interval = CheckInterval
				};
				_timer.Tick += OnTick;
			}
			if (!_timer.IsEnabled)
				_timer.Start();
		}

		private void OnTick(object? sender, EventArgs e)
		{
			if (--_remaining <= 0)
				_timer?.Stop();
			if (_window.TryGetTarget(out Window? window))
				Refresh(window);
			else
				_timer?.Stop();
		}

		private void Refresh(Window window)
		{
			try
			{
				if (!window.IsVisible || window.WindowState == System.Windows.WindowState.Minimized)
					return;
				if (!System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(window, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host) || host is null)
					return;

				nint handle = LinuxWindowMode.ResolveNativeWindow(window);
				if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPointerInWindow(handle, out int x, out int y, out bool buttonsHeld) || buttonsHeld)
					return;

				Silk.NET.Maths.Vector2D<int> size = host.SilkWindow?.Size ?? default;
				if (x < 0 || y < 0 || x >= size.X || y >= size.Y)
					return;

				double scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
				if (scale <= 0.0 || double.IsNaN(scale))
					scale = 1.0;
				Point known = System.Windows.Input.Mouse.GetPosition(window);
				if (Math.Abs(known.X * scale - x) <= PositionTolerance && Math.Abs(known.Y * scale - y) <= PositionTolerance)
					return;

				Silk.NET.Input.IMouse? mouse = FindMouse(host);
				if (mouse is null || !RaiseMove(mouse, new System.Numerics.Vector2(x, y)))
					_timer?.Stop();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxPointerRefresh", "The pointer position could not be refreshed: " + ex.Message);
				_timer?.Stop();
			}
		}
	}

	private static Silk.NET.Input.IMouse? FindMouse(System.Windows.Media.ProGPU.ProGpuWpfWindowHost host)
	{
		object? subscription = InputSubscriptionField?.GetValue(host);
		FieldInfo? contextField = subscription?.GetType().GetField("_inputContext", BindingFlags.Instance | BindingFlags.NonPublic);
		if (contextField?.GetValue(subscription) is not Silk.NET.Input.IInputContext context || context.Mice.Count == 0)
			return null;
		return context.Mice[0];
	}

	private static bool RaiseMove(Silk.NET.Input.IMouse mouse, System.Numerics.Vector2 position)
	{
		for (Type? type = mouse.GetType(); type is not null; type = type.BaseType)
		{
			FieldInfo? field = type.GetField("MouseMove", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is null)
				continue;
			if (field.GetValue(mouse) is not Action<Silk.NET.Input.IMouse, System.Numerics.Vector2> handler)
				return false;
			handler(mouse, position);
			return true;
		}
		return false;
	}
#endif
}
