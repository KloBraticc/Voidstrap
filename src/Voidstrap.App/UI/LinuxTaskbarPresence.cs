using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxTaskbarPresence
{
	private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500.0);

	private static DispatcherTimer? _timer;

	private static bool _hidden;

	public static void HideWhileSessionRuns()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Application? application = Application.Current;
		if (application is null)
			return;

		if (!application.Dispatcher.CheckAccess())
		{
			application.Dispatcher.BeginInvoke(new Action(HideWhileSessionRuns));
			return;
		}

		if (_hidden)
		{
			Sweep();
			return;
		}

		_hidden = true;
		IReadOnlyList<string> changed = Sweep();
		App.Logger.WriteLine(
			"LinuxTaskbarPresence::HideWhileSessionRuns",
			changed.Count > 0
				? "Voidstrap is hidden from the taskbar while Roblox runs: " + string.Join(", ", changed)
				: "Voidstrap has no taskbar windows to hide, watching for new ones while Roblox runs");

		_timer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = SweepInterval
		};
		_timer.Tick += OnSweepTick;
		_timer.Start();
	}

	public static void RestoreAfterSession()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Application? application = Application.Current;
		if (application is null)
			return;

		if (!application.Dispatcher.CheckAccess())
		{
			application.Dispatcher.BeginInvoke(new Action(RestoreAfterSession));
			return;
		}

		StopTimer();

		if (!_hidden)
			return;

		_hidden = false;
		IReadOnlyList<string> restored = Sweep();
		if (restored.Count > 0)
			App.Logger.WriteLine("LinuxTaskbarPresence::RestoreAfterSession", "Voidstrap is back on the taskbar: " + string.Join(", ", restored));
	}

	public static void StopForShutdown()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Application? application = Application.Current;
		if (application is null)
			return;

		if (!application.Dispatcher.CheckAccess())
		{
			application.Dispatcher.BeginInvoke(new Action(StopForShutdown));
			return;
		}

		StopTimer();
		_hidden = false;
	}

	private static void StopTimer()
	{
		DispatcherTimer? timer = _timer;
		if (timer is null)
			return;

		_timer = null;
		timer.Stop();
		timer.Tick -= OnSweepTick;
	}

	private static void OnSweepTick(object? sender, EventArgs e)
	{
		IReadOnlyList<string> changed = Sweep();
		if (changed.Count > 0)
			App.Logger.WriteLine("LinuxTaskbarPresence::Sweep", "Hidden from the taskbar: " + string.Join(", ", changed));
	}

	private static IReadOnlyList<string> Sweep()
	{
		try
		{
			Application? application = Application.Current;
			if (application is null)
				return Array.Empty<string>();

			HashSet<nint> handles = new(Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnManagedWindows());
			foreach (Window window in application.Windows.OfType<Window>())
			{
				nint handle = ResolveHandle(window);
				if (handle != 0)
					handles.Add(handle);

				if (!string.IsNullOrWhiteSpace(window.Title))
				{
					foreach (nint native in Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnManagedWindowsByTitle(window.Title))
						handles.Add(native);
				}
			}

			return Voidstrap.Platform.Linux.LinuxWindowInterop.ApplyTaskbarVisibility(handles.ToArray(), string.Empty, _hidden);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxTaskbarPresence::Sweep", "The taskbar state could not be updated: " + ex.Message);
			return Array.Empty<string>();
		}
	}

	private static nint ResolveHandle(Window window)
	{
		try
		{
			nint handle = new WindowInteropHelper(window).Handle;
			if (handle != 0 && Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle))
				return handle;
		}
		catch (Exception)
		{
		}

		return string.IsNullOrWhiteSpace(window.Title)
			? 0
			: Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(window.Title);
	}
}
