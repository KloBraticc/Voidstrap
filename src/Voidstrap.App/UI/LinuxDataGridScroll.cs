using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Voidstrap.UI;

public static class LinuxDataGridScroll
{
	private const int LinesPerNotch = 3;
	private const double FallbackRowHeight = 32.0;

	private static bool _installed;

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		_installed = true;
		EventManager.RegisterClassHandler(
			typeof(DataGrid),
			UIElement.PreviewMouseWheelEvent,
			new MouseWheelEventHandler(OnPreviewMouseWheel),
			true);
	}

	private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (e.Handled || e.Delta == 0 || sender is not DataGrid grid)
		{
			return;
		}

		if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
		{
			return;
		}

		ScrollViewer? viewer = FindScrollViewer(grid);
		if (viewer is null || viewer.ScrollableHeight <= 0.0)
		{
			return;
		}

		bool up = e.Delta > 0;
		double offset = viewer.VerticalOffset;
		if (up ? offset <= 0.0 : offset >= viewer.ScrollableHeight)
		{
			return;
		}

		double step = ResolveStep(grid);
		double target = up ? offset - step : offset + step;
		target = Math.Max(0.0, Math.Min(viewer.ScrollableHeight, target));
		viewer.ScrollToVerticalOffset(target);
		e.Handled = true;
	}

	private static double ResolveStep(DataGrid grid)
	{
		if (VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Item)
		{
			return LinesPerNotch;
		}

		double rowHeight = grid.RowHeight;
		if (double.IsNaN(rowHeight) || rowHeight <= 0.0)
		{
			rowHeight = grid.MinRowHeight > 0.0 ? grid.MinRowHeight : FallbackRowHeight;
		}

		return LinesPerNotch * rowHeight;
	}

	private static ScrollViewer? FindScrollViewer(DependencyObject root)
	{
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int index = 0; index < count; index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, index);
			if (child is ScrollViewer viewer)
			{
				return viewer;
			}

			ScrollViewer? nested = FindScrollViewer(child);
			if (nested is not null)
			{
				return nested;
			}
		}

		return null;
	}
}
