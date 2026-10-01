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

	private static readonly HashSet<nint> HiddenNativeHelpers = [];

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
		nint[] helpers = HiddenNativeHelpers.Where(Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow).ToArray();
		HiddenNativeHelpers.Clear();
		IReadOnlyList<string> restored = Voidstrap.Platform.Linux.LinuxWindowInterop.ApplyTaskbarVisibility(helpers, string.Empty, false);
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
		HiddenNativeHelpers.Clear();
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
			if (application is null || !_hidden)
				return Array.Empty<string>();

			HashSet<nint> known = [];
			HashSet<string> taskbarTitles = new(StringComparer.Ordinal);
			List<nint> helpers = [];
			foreach (Window window in application.Windows.OfType<Window>())
			{
				if (window.ShowInTaskbar && !string.IsNullOrWhiteSpace(window.Title))
					taskbarTitles.Add(window.Title);

				nint handle = ResolveHandle(window);
				if (handle == 0 || !known.Add(handle))
					continue;

				if (!window.ShowInTaskbar)
					helpers.Add(handle);
			}

			if (!Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed)
			{
				foreach (nint native in Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnManagedWindows())
				{
					if (!known.Add(native) || taskbarTitles.Contains(Voidstrap.Platform.Linux.LinuxWindowInterop.ReadWindowTitle(native)))
						continue;

					helpers.Add(native);
					HiddenNativeHelpers.Add(native);
				}
			}

			return Voidstrap.Platform.Linux.LinuxWindowInterop.ApplyTaskbarVisibility(helpers, string.Empty, true);
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
			nint native = LinuxWindowMode.ResolveExactNativeWindow(window);
			if (native != 0 && Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(native))
				return native;

			nint handle = new WindowInteropHelper(window).Handle;
			if (handle != 0 && Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle))
				return handle;
		}
		catch (Exception)
		{
		}

		return 0;
	}
}
