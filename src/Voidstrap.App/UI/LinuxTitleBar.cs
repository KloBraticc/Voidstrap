using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Platform.Linux;
using Wpf.Ui.Controls;

namespace Voidstrap.UI;

internal static class LinuxTitleBar
{
	private static GnomeButtonLayout? _layout;

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
			App.Logger?.WriteLine("LinuxTitleBar::Apply", "Could not read the system button layout: " + ex.Message);
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
			App.Logger?.WriteLine("LinuxTitleBar::OnTitleBarLoaded", "Could not apply the system button layout: " + ex.Message);
		}
	}

	private static void ApplyLayout(TitleBar titleBar)
	{
		_layout ??= GnomeButtonLayout.Parse(Run("gsettings", "get org.gnome.desktop.wm.preferences button-layout"));
		bool onLeft = _layout.OnLeft;
		IReadOnlyList<string> order = _layout.Order;
		if (order.Count == 0)
		{
			return;
		}

		titleBar.ApplyTemplate();
		FrameworkElement? close = FindPart(titleBar, "PART_CloseButton");
		FrameworkElement? maximize = FindPart(titleBar, "PART_MaximizeButton");
		FrameworkElement? restore = FindPart(titleBar, "PART_RestoreButton");
		FrameworkElement? minimize = FindPart(titleBar, "ButtonMinimize");
		if (close == null)
		{
			return;
		}

		if (!order.Contains("minimize"))
		{
			titleBar.ShowMinimize = false;
		}
		if (!order.Contains("maximize"))
		{
			titleBar.ShowMaximize = false;
		}
		if (!order.Contains("close"))
		{
			titleBar.ShowClose = false;
		}

		int column = 1;
		foreach (string button in order)
		{
			switch (button)
			{
				case "minimize":
					SetColumn(minimize, column);
					break;
				case "maximize":
					SetColumn(maximize, column);
					SetColumn(restore, column);
					break;
				case "close":
					SetColumn(close, column);
					break;
				default:
					continue;
			}
			column++;
		}

		if (onLeft)
		{
			MoveButtonsToLeft(titleBar, close);
		}
	}

	private static void MoveButtonsToLeft(TitleBar titleBar, FrameworkElement close)
	{
		try
		{
			if (VisualTreeHelper.GetParent(close) is not Grid buttons)
			{
				return;
			}
			if (VisualTreeHelper.GetParent(buttons) is not Grid row || row.ColumnDefinitions.Count < 2)
			{
				return;
			}
			buttons.HorizontalAlignment = HorizontalAlignment.Left;
			Grid.SetColumn(buttons, 0);
			row.ColumnDefinitions[0].Width = GridLength.Auto;
			row.ColumnDefinitions[1].Width = new GridLength(1.0, GridUnitType.Star);
			foreach (UIElement child in row.Children)
			{
				if (child is ContentPresenter presenter)
				{
					Grid.SetColumn(presenter, 1);
				}
			}
			if (FindPart(titleBar, "TitleGrid") is FrameworkElement title)
			{
				title.HorizontalAlignment = HorizontalAlignment.Right;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTitleBar::MoveButtonsToLeft", "Kept the buttons on the right: " + ex.Message);
		}
	}

	private static void SetColumn(FrameworkElement? element, int column)
	{
		if (element != null)
		{
			Grid.SetColumn(element, column);
		}
	}

	private static FrameworkElement? FindPart(TitleBar titleBar, string name)
	{
		return titleBar.Template?.FindName(name, titleBar) as FrameworkElement;
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

	private static string Run(string fileName, string arguments)
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process == null)
			{
				return string.Empty;
			}
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(2500);
			return output;
		}
		catch
		{
			return string.Empty;
		}
	}
}
