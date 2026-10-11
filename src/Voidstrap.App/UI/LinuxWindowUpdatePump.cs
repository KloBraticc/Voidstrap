using System;
using System.Collections.Generic;
using System.Diagnostics;
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

	private static readonly TimeSpan FallbackInterval = TimeSpan.FromMilliseconds(16);

	private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(16);

	private static readonly FieldInfo? NativeLoopRunningField = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowHost)
		.GetField("_isNativeLoopRunning", BindingFlags.Instance | BindingFlags.NonPublic);

	private static readonly MethodInfo? DisposeDeferredNativeWindow = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowHost)
		.GetMethod("DisposeDeferredNativeWindowIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);

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
		private static bool _updating;
		private static bool _updateQueued;
		private static readonly HashSet<Pump> Pending = [];
		private long _lastOwnerTick;
		private bool _stopped;

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
			else
				Unsubscribe();
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
			if (_stopped || !_window.IsVisible)
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

				if (_updating)
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
			App.Logger?.WriteLine("LinuxWindowUpdatePump", _window.GetType().Name + " uses a fallback pump while no native owner loop is running");
		}

		private void OnFallbackTick(object? sender, EventArgs e)
		{
			if (!_window.IsVisible)
			{
				_fallback?.Stop();
				return;
			}

			if (HasRecentOwnerTick())
				return;

			QueueUpdate();
		}

		private bool HasRecentOwnerTick() => _lastOwnerTick != 0
			&& Stopwatch.GetElapsedTime(_lastOwnerTick) < ActiveInterval
			&& _owners.Exists(RunsNativeLoop);

		private void OnOwnerUpdateTick(object? sender, EventArgs e)
		{
			_lastOwnerTick = Stopwatch.GetTimestamp();
			QueueUpdate();
		}

		private void QueueUpdate()
		{
			if (_stopped || _host is null || !_window.IsVisible || RunsNativeLoop(_host)
				|| _window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)
				return;
			Pending.Add(this);
			ScheduleUpdates();
		}

		private static void ScheduleUpdates()
		{
			if (_updating || _updateQueued || Pending.Count == 0)
				return;
			foreach (Pump pump in Pending)
			{
				_updateQueued = true;
				pump._window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FlushUpdates));
				break;
			}
		}

		private static void FlushUpdates()
		{
			_updateQueued = false;
			if (_updating)
				return;
			Pump[] batch = [.. Pending];
			Pending.Clear();
			_updating = true;
			try
			{
				foreach (Pump pump in batch)
				{
					Pending.Remove(pump);
					pump.Update();
				}
			}
			finally
			{
				_updating = false;
				ScheduleUpdates();
			}
		}

		private void Update()
		{
			if (_stopped || _host is null || !_window.IsVisible || RunsNativeLoop(_host))
				return;
			bool ownsFrame = false;
			try
			{
				if (_host.SilkWindow is { } nativeWindow)
					nativeWindow.IsEventDriven = false;
				NativeLoopRunningField?.SetValue(_host, true);
				ownsFrame = NativeLoopRunningField is not null;
				_host.DoEvents();
			}
			catch (Exception ex)
			{
				Stop("the window update failed, " + ex.Message);
			}
			finally
			{
				try
				{
					if (ownsFrame)
					{
						NativeLoopRunningField!.SetValue(_host, false);
						DisposeDeferredNativeWindow?.Invoke(_host, null);
					}
				}
				catch (Exception ex)
				{
					Stop("the native frame could not finish, " + ex.Message);
				}
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
			Pending.Remove(this);
			_lastOwnerTick = 0;
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
