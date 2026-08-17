using System;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.UI;

public static class RoundedWindowChrome
{
	public const double CornerRadius = 8.0;

	private static bool _installed;

	public static void Install()
	{
		if (_installed || Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		_installed = true;
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
	}

	public static void Prepare(Window window)
	{
		if (window == null || Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		try
		{
			if (window.WindowStyle != WindowStyle.None)
			{
				window.WindowStyle = WindowStyle.None;
			}
			if (!window.AllowsTransparency)
			{
				window.AllowsTransparency = true;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RoundedWindowChrome::Prepare", "Could not enable transparency: " + ex.Message);
		}
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}
		ConstrainContentWidth(window);
		ApplyStartupLocation(window);
		ApplyClip(window);
		LinuxTitleBar.Apply(window);
		window.SizeChanged -= OnWindowSizeChanged;
		window.SizeChanged += OnWindowSizeChanged;
		window.StateChanged -= OnWindowStateChanged;
		window.StateChanged += OnWindowStateChanged;
		window.Closed -= OnWindowClosed;
		window.Closed += OnWindowClosed;
		if (window.ResizeMode == ResizeMode.CanResize || window.ResizeMode == ResizeMode.CanResizeWithGrip)
		{
			WindowEdgeResizer.Attach(window);
		}
	}

	private static void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
	{
		ApplyClip(sender as Window);
	}

	private static void OnWindowStateChanged(object? sender, EventArgs e)
	{
		ApplyClip(sender as Window);
	}

	private static void OnWindowClosed(object? sender, EventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}
		window.SizeChanged -= OnWindowSizeChanged;
		window.StateChanged -= OnWindowStateChanged;
		window.Closed -= OnWindowClosed;
	}

	private static void ApplyStartupLocation(Window window)
	{
		try
		{
			if (window.WindowStartupLocation == WindowStartupLocation.Manual)
			{
				return;
			}
			if (window.WindowState == System.Windows.WindowState.Maximized)
			{
				return;
			}
			double width = window.ActualWidth > 0.0 ? window.ActualWidth : window.Width;
			double height = window.ActualHeight > 0.0 ? window.ActualHeight : window.Height;
			if (double.IsNaN(width) || double.IsNaN(height) || width <= 0.0 || height <= 0.0)
			{
				return;
			}

			double left;
			double top;
			Window? owner = window.Owner;
			if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner
				&& owner != null && owner.ActualWidth > 0.0 && !double.IsNaN(owner.Left) && !double.IsNaN(owner.Top))
			{
				left = owner.Left + (owner.ActualWidth - width) / 2.0;
				top = owner.Top + (owner.ActualHeight - height) / 2.0;
			}
			else
			{
				(double screenWidth, double screenHeight) = Voidstrap.Utility.ScreenMetrics.GetPrimary();
				left = (screenWidth - width) / 2.0;
				top = (screenHeight - height) / 2.0;
			}

			(double boundsWidth, double boundsHeight) = Voidstrap.Utility.ScreenMetrics.GetPrimary();
			left = Math.Max(0.0, Math.Min(left, boundsWidth - width));
			top = Math.Max(0.0, Math.Min(top, boundsHeight - height));

			window.Left = left;
			window.Top = top;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RoundedWindowChrome::ApplyStartupLocation", "Could not position window: " + ex.Message);
		}
	}

	private static void ConstrainContentWidth(Window window)
	{
		try
		{
			if (window.SizeToContent != SizeToContent.Height && window.SizeToContent != SizeToContent.Manual)
			{
				return;
			}
			double target = window.Width;
			if (double.IsNaN(target) || target <= 0.0)
			{
				target = window.ActualWidth;
			}
			if (double.IsNaN(target) || target <= 0.0)
			{
				return;
			}
			if (window.Content is FrameworkElement root && (double.IsNaN(root.MaxWidth) || root.MaxWidth > target))
			{
				root.MaxWidth = target;
			}
		}
		catch
		{
		}
	}

	private static void ApplyClip(Window? window)
	{
		if (window == null)
		{
			return;
		}
		if (!window.AllowsTransparency)
		{
			window.Clip = null;
			return;
		}
		double width = window.ActualWidth;
		double height = window.ActualHeight;
		if (window.WindowState == System.Windows.WindowState.Maximized || width <= 0.0 || height <= 0.0)
		{
			window.Clip = null;
			return;
		}
		RectangleGeometry geometry = new(new Rect(0.0, 0.0, width, height), CornerRadius, CornerRadius);
		geometry.Freeze();
		window.Clip = geometry;
	}
}
