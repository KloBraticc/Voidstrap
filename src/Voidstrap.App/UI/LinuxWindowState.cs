using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Voidstrap.UI;

internal static class LinuxWindowState
{
	private sealed class RestoreBounds
	{
		public double Left;

		public double Top;

		public double Width;

		public double Height;

		public bool Captured;
	}

	private static readonly ConditionalWeakTable<Window, RestoreBounds> Bounds = new();

	private static readonly ConditionalWeakTable<Window, object> Centered = new();

	private static bool _installed;

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsLinux)
			return;

		_installed = true;
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
		App.Logger?.WriteLine("LinuxWindowState::Install", "Maximized and centered windows now follow the desktop work area");
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not Window window)
			return;

		window.StateChanged -= OnWindowStateChanged;
		window.StateChanged += OnWindowStateChanged;
		window.Closed -= OnWindowClosed;
		window.Closed += OnWindowClosed;
		CenterOnWorkArea(window);
		Apply(window);
	}

	private static void OnWindowStateChanged(object? sender, EventArgs e)
	{
		if (sender is Window window)
			Apply(window);
	}

	private static void OnWindowClosed(object? sender, EventArgs e)
	{
		if (sender is not Window window)
			return;

		window.StateChanged -= OnWindowStateChanged;
		window.Closed -= OnWindowClosed;
		Bounds.Remove(window);
		Centered.Remove(window);
	}

	private static void CenterOnWorkArea(Window window)
	{
		if (window.WindowStartupLocation != WindowStartupLocation.CenterScreen
			|| window.WindowState != System.Windows.WindowState.Normal
			|| Centered.TryGetValue(window, out _))
			return;

		Rect work = LinuxScreenMetrics.WorkArea;

		if (work.Width < 1d || work.Height < 1d)
			return;

		double width = double.IsNaN(window.Width) || window.Width <= 0d ? window.ActualWidth : window.Width;
		double height = double.IsNaN(window.Height) || window.Height <= 0d ? window.ActualHeight : window.Height;

		if (width <= 0d || height <= 0d)
			return;

		Centered.Add(window, new object());
		window.Left = work.Left + Math.Max(0d, (work.Width - width) / 2d);
		window.Top = work.Top + Math.Max(0d, (work.Height - height) / 2d);
	}

	private static void Apply(Window window)
	{
		Rect work = LinuxScreenMetrics.WorkArea;

		if (work.Width < 1d || work.Height < 1d)
			return;

		double width = work.Width;
		double height = work.Height;

		RestoreBounds restore = Bounds.GetValue(window, static _ => new RestoreBounds());

		if (window.WindowState == System.Windows.WindowState.Maximized)
		{
			if (!restore.Captured)
			{
				restore.Left = window.Left;
				restore.Top = window.Top;
				restore.Width = window.Width;
				restore.Height = window.Height;
				restore.Captured = true;
			}

			if (Math.Abs(window.Width - width) < 0.5d
				&& Math.Abs(window.Height - height) < 0.5d
				&& Math.Abs(window.Left - work.Left) < 0.5d
				&& Math.Abs(window.Top - work.Top) < 0.5d)
				return;

			window.MaxWidth = double.PositiveInfinity;
			window.MaxHeight = double.PositiveInfinity;
			window.Left = work.Left;
			window.Top = work.Top;
			window.Width = width;
			window.Height = height;
			return;
		}

		if (!restore.Captured)
			return;

		restore.Captured = false;
		if (!double.IsNaN(restore.Width) && restore.Width > 0d)
			window.Width = restore.Width;

		if (!double.IsNaN(restore.Height) && restore.Height > 0d)
			window.Height = restore.Height;

		if (!double.IsNaN(restore.Left))
			window.Left = restore.Left;

		if (!double.IsNaN(restore.Top))
			window.Top = restore.Top;
	}
}
