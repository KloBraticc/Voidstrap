using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxWindowUpdatePump
{
	internal static void Attach(Window window)
	{
#if CROSSPLAT
		if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) || Pumps.TryGetValue(window, out _))
			return;

		Pumps.Add(window, new Pump(window));
#endif
	}

#if CROSSPLAT
	private static readonly ConditionalWeakTable<Window, Pump> Pumps = new();

	private static readonly EventInfo? UpdateTickEvent = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowHost)
		.GetEvent("UpdateTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly MethodInfo? AddUpdateTick = UpdateTickEvent?.GetAddMethod(true);

	private static readonly MethodInfo? RemoveUpdateTick = UpdateTickEvent?.GetRemoveMethod(true);

	private static readonly TimeSpan FallbackInterval = TimeSpan.FromMilliseconds(33);

	private static readonly FieldInfo? NativeLoopRunningField = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowHost)
		.GetField("_isNativeLoopRunning", BindingFlags.Instance | BindingFlags.NonPublic);

	private static bool RunsNativeLoop(System.Windows.Media.ProGPU.ProGpuWpfWindowHost host)
	{
		return NativeLoopRunningField is null || NativeLoopRunningField.GetValue(host) is true;
	}

	private sealed class Pump
	{
		private readonly Window _window;
		private readonly List<System.Windows.Media.ProGPU.ProGpuWpfWindowHost> _owners = [];
		private readonly EventHandler _ownerTick;
		private System.Windows.Media.ProGPU.ProGpuWpfWindowHost? _host;
		private DispatcherTimer? _fallback;
		private bool _updating;
		private bool _stopped;
		private bool _eventsContinued;

		internal Pump(Window window)
		{
			_window = window;
			_ownerTick = OnOwnerUpdateTick;
			window.Loaded += OnLoaded;
			window.IsVisibleChanged += OnVisibleChanged;
			window.Activated += OnActivated;
			window.PreviewMouseDown += OnPreviewMouseDown;
			window.Closed += OnClosed;
		}

		private void OnLoaded(object sender, RoutedEventArgs e) => Subscribe();

		private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (e.NewValue is true)
				Subscribe();
		}

		private void OnActivated(object? sender, EventArgs e)
		{
			Subscribe();
		}

		private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
		{
			Subscribe();
		}

		private void Subscribe()
		{
			if (_stopped)
				return;

			try
			{
				if (_host is null && (!System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(_window, out _host) || _host is null))
					return;

				if (AddUpdateTick is null || RemoveUpdateTick is null)
				{
					StartFallback();
					return;
				}

				Application? application = Application.Current;
				if (application is null)
					return;

				for (int index = _owners.Count - 1; index >= 0; index--)
				{
					if (RunsNativeLoop(_owners[index]))
						continue;
					RemoveUpdateTick.Invoke(_owners[index], [_ownerTick]);
					_owners.RemoveAt(index);
				}

				if (NativeLoopRunningField is not null && RunsNativeLoop(_host))
					return;

				StartFallback();

				foreach (Window other in application.Windows)
				{
					if (ReferenceEquals(other, _window)
						|| !System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(other, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? owner)
						|| owner is null
						|| ReferenceEquals(owner, _host)
						|| _owners.Contains(owner)
						|| !RunsNativeLoop(owner))
						continue;

					AddUpdateTick.Invoke(owner, [_ownerTick]);
					_owners.Add(owner);
				}
			}
			catch (Exception ex)
			{
				Stop("the window hosts could not be read, " + ex.Message);
			}
		}

		private void StartFallback()
		{
			if (_fallback is not null)
			{
				if (!_fallback.IsEnabled)
					_fallback.Start();
				return;
			}

			_fallback = new DispatcherTimer(DispatcherPriority.Input, _window.Dispatcher) { Interval = FallbackInterval };
			_fallback.Tick += OnFallbackTick;
			_fallback.Start();
			App.Logger?.WriteLine("LinuxWindowUpdatePump", "The window host has no update tick, " + _window.GetType().Name + " is pumped on a timer");
		}

		private void OnFallbackTick(object? sender, EventArgs e)
		{
			if (!_window.IsVisible)
			{
				_fallback?.Stop();
				return;
			}

			if (_owners.Exists(RunsNativeLoop))
				return;

			OnOwnerUpdateTick(sender, e);
		}

		private void OnOwnerUpdateTick(object? sender, EventArgs e)
		{
			if (_updating || _stopped || _host is null || !_window.IsVisible)
				return;

			_updating = true;
			try
			{
				bool overlay = Voidstrap.Integrations.Overlays.LinuxOverlaySurface.IsOverlayWindow(_window);
				if (overlay && !_owners.Exists(RunsNativeLoop))
				{
					if (!_eventsContinued)
					{
						_host.SilkWindow?.ContinueEvents();
						_eventsContinued = true;
					}
					_host.SilkWindow?.DoEvents();
				}
				_host.SilkWindow?.DoUpdate();
			}
			catch (Exception ex)
			{
				Stop("the window update failed, " + ex.Message);
			}
			finally
			{
				_updating = false;
			}
		}

		private void Stop(string reason)
		{
			if (_stopped)
				return;

			_stopped = true;
			Unsubscribe();
			App.Logger?.WriteLine("LinuxWindowUpdatePump", "Stopped pumping " + _window.GetType().Name + " because " + reason);
		}

		private void Unsubscribe()
		{
			foreach (System.Windows.Media.ProGPU.ProGpuWpfWindowHost owner in _owners)
			{
				try
				{
					RemoveUpdateTick?.Invoke(owner, [_ownerTick]);
				}
				catch (Exception)
				{
				}
			}

			_owners.Clear();

			if (_fallback is not null)
			{
				_fallback.Stop();
				_fallback.Tick -= OnFallbackTick;
				_fallback = null;
			}
		}

		private void OnClosed(object? sender, EventArgs e)
		{
			_stopped = true;
			Unsubscribe();
			_window.Loaded -= OnLoaded;
			_window.IsVisibleChanged -= OnVisibleChanged;
			_window.Activated -= OnActivated;
			_window.PreviewMouseDown -= OnPreviewMouseDown;
			_window.Closed -= OnClosed;
			Pumps.Remove(_window);
		}
	}
#endif
}
