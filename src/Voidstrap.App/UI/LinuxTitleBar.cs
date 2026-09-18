using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Voidstrap.UI;

internal static class LinuxTitleBar
{
	public static void Apply(Window window)
	{
		if (window == null || !OperatingSystem.IsLinux())
		{
			return;
		}
		try
		{
			TitleBar? titleBar = FindTitleBar(window);
			if (titleBar == null)
			{
				return;
			}
			if (titleBar.IsLoaded)
			{
				ApplyLayout(titleBar);
				return;
			}
			titleBar.Loaded -= OnTitleBarLoaded;
			titleBar.Loaded += OnTitleBarLoaded;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTitleBar::Apply", "Could not apply the Windows caption controls: " + ex.Message);
		}
	}

	private static void OnTitleBarLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not TitleBar titleBar)
		{
			return;
		}
		titleBar.Loaded -= OnTitleBarLoaded;
		try
		{
			ApplyLayout(titleBar);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTitleBar::OnTitleBarLoaded", "Could not apply the Windows caption controls: " + ex.Message);
		}
	}

	private static void ApplyLayout(TitleBar titleBar)
	{
		titleBar.ApplyTemplate();
		OverrideMaximizeAction(titleBar);
		EnableFullWidthDrag(titleBar);
		Window? window = Window.GetWindow(titleBar);
		if (window != null)
			ApplyMaximizedVisual(titleBar, LinuxWindowMode.IsMaximized(window));
		App.Logger?.WriteLine("LinuxTitleBar::ApplyLayout", "Applied the Windows Wpf.Ui caption controls");
	}

	internal static void RefreshMaximized(Window window, bool maximized)
	{
		TitleBar? titleBar = FindTitleBar(window);
		if (titleBar != null)
			ApplyMaximizedVisual(titleBar, maximized);
	}

	private static void ApplyMaximizedVisual(TitleBar titleBar, bool maximized)
	{
		titleBar.ApplyTemplate();
		if (titleBar.Template?.FindName("PART_MaximizeButton", titleBar) is FrameworkElement maximize)
			maximize.Visibility = titleBar.ShowMaximize && !maximized ? Visibility.Visible : Visibility.Collapsed;
		if (titleBar.Template?.FindName("PART_RestoreButton", titleBar) is FrameworkElement restore)
			restore.Visibility = titleBar.ShowMaximize && maximized ? Visibility.Visible : Visibility.Collapsed;
	}


	private static void EnableFullWidthDrag(TitleBar titleBar)
	{
		titleBar.PreviewMouseLeftButtonDown -= OnTitleBarPressed;
		titleBar.PreviewMouseLeftButtonDown += OnTitleBarPressed;
	}

	private static void OnTitleBarPressed(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (sender is not TitleBar titleBar || e.ButtonState != System.Windows.Input.MouseButtonState.Pressed)
		{
			return;
		}

		if (IsInteractive(e.OriginalSource as DependencyObject))
		{
			return;
		}

		Window? window = Window.GetWindow(titleBar);
		if (window == null)
		{
			return;
		}

		if (e.ClickCount == 2)
		{
			ToggleMaximize(window);
			e.Handled = true;
			return;
		}

		if (LinuxWindowMode.IsFullscreen(window) || LinuxWindowMode.IsCompositorMaximized(window))
		{
			return;
		}
		if (window.WindowState == System.Windows.WindowState.Maximized)
			window.WindowState = System.Windows.WindowState.Normal;

		try
		{
			window.DragMove();
			e.Handled = true;
		}
		catch (InvalidOperationException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTitleBar::OnTitleBarPressed", "The window could not be moved: " + ex.Message);
		}
	}

	private static bool IsInteractive(DependencyObject? source)
	{
		while (source != null)
		{
			if (source is System.Windows.Controls.Primitives.ButtonBase
				or System.Windows.Controls.Primitives.TextBoxBase
				or ComboBox
				or System.Windows.Controls.Primitives.Thumb
				or System.Windows.Controls.Primitives.ScrollBar
				or System.Windows.Controls.PasswordBox
				or System.Windows.Controls.Slider)
			{
				return true;
			}

			source = source is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(source)
				: LogicalTreeHelper.GetParent(source);
		}

		return false;
	}

	private static void ToggleMaximize(Window window)
	{
		LinuxWindowMode.ToggleMaximize(window);
	}

	private static void OverrideMaximizeAction(TitleBar titleBar)
	{
		titleBar.MaximizeActionOverride = OnTitleBarMaximizeRequested;
	}

	private static void OnTitleBarMaximizeRequested(TitleBar titleBar, Window window)
	{
		if (window != null)
		{
			ToggleMaximize(window);
		}
	}

	private static TitleBar? FindTitleBar(DependencyObject root)
	{
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is TitleBar titleBar)
			{
				return titleBar;
			}
			TitleBar? nested = FindTitleBar(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}
}
