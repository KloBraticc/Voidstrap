using System;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class MacDockPresence
{
	private const double MinimumWindowSize = 50;

	private static DispatcherTimer? _timer;
	private static bool? _dockVisible;

	internal static void Install()
	{
		if (!OperatingSystem.IsMacOS() || _timer != null)
			return;
		_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
		_timer.Tick += OnTick;
		_timer.Start();
		if (Application.Current != null)
			Application.Current.Exit += OnExit;
	}

	private static void OnTick(object? sender, EventArgs e)
	{
		bool wanted = HasVisibleWindow();
		if (_dockVisible == wanted)
			return;
		if (!OperatingSystem.IsMacOS() || !Voidstrap.Platform.MacOS.MacOSApplication.SetDockVisible(wanted))
			return;
		_dockVisible = wanted;
		App.Logger?.WriteLine("MacDockPresence", wanted ? "A window is open, Voidstrap is in the Dock" : "No window is open, Voidstrap left the Dock and stays in the menu bar");
		if (wanted)
			Voidstrap.Utility.Branding.ApplyDockIcon();
	}

	private static bool HasVisibleWindow()
	{
		Application? application = Application.Current;
		if (application == null)
			return true;
		foreach (Window window in application.Windows)
		{
			if (window.IsVisible && window.ShowInTaskbar && window.ActualWidth >= MinimumWindowSize && window.ActualHeight >= MinimumWindowSize)
				return true;
		}
		return false;
	}

	private static void OnExit(object? sender, ExitEventArgs e)
	{
		if (_timer != null)
		{
			_timer.Stop();
			_timer.Tick -= OnTick;
			_timer = null;
		}
		if (Application.Current != null)
			Application.Current.Exit -= OnExit;
	}
}
