using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Voidstrap.UI;

internal sealed class WindowEdgeResizer
{
	private const double EdgeThickness = 6.0;

	private static readonly Dictionary<Window, WindowEdgeResizer> Attached = [];

	private readonly Window _window;

	private ResizeEdge _edge;

	private bool _resizing;

	private Point _startCursor;

	private double _startLeft;

	private double _startTop;

	private double _startWidth;

	private double _startHeight;

	[Flags]
	private enum ResizeEdge
	{
		None = 0,
		Left = 1,
		Right = 2,
		Top = 4,
		Bottom = 8
	}

	private WindowEdgeResizer(Window window)
	{
		_window = window;
	}

	public static void Attach(Window window)
	{
		if (Attached.ContainsKey(window))
		{
			return;
		}
		WindowEdgeResizer resizer = new(window);
		Attached[window] = resizer;
		window.PreviewMouseLeftButtonDown += resizer.OnPreviewMouseDown;
		window.PreviewMouseMove += resizer.OnPreviewMouseMove;
		window.PreviewMouseLeftButtonUp += resizer.OnPreviewMouseUp;
		window.LostMouseCapture += resizer.OnLostCapture;
		window.Closed += resizer.OnClosed;
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		_window.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
		_window.PreviewMouseMove -= OnPreviewMouseMove;
		_window.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
		_window.LostMouseCapture -= OnLostCapture;
		_window.Closed -= OnClosed;
		Attached.Remove(_window);
	}

	private ResizeEdge HitTest(Point position)
	{
		double width = _window.ActualWidth;
		double height = _window.ActualHeight;
		if (width <= 0.0 || height <= 0.0)
		{
			return ResizeEdge.None;
		}
		ResizeEdge edge = ResizeEdge.None;
		if (position.X <= EdgeThickness)
		{
			edge |= ResizeEdge.Left;
		}
		else if (position.X >= width - EdgeThickness)
		{
			edge |= ResizeEdge.Right;
		}
		if (position.Y <= EdgeThickness)
		{
			edge |= ResizeEdge.Top;
		}
		else if (position.Y >= height - EdgeThickness)
		{
			edge |= ResizeEdge.Bottom;
		}
		return edge;
	}

	private static Cursor? CursorFor(ResizeEdge edge)
	{
		return edge switch
		{
			ResizeEdge.Left => Cursors.SizeWE,
			ResizeEdge.Right => Cursors.SizeWE,
			ResizeEdge.Top => Cursors.SizeNS,
			ResizeEdge.Bottom => Cursors.SizeNS,
			ResizeEdge.Left | ResizeEdge.Top => Cursors.SizeNWSE,
			ResizeEdge.Right | ResizeEdge.Bottom => Cursors.SizeNWSE,
			ResizeEdge.Right | ResizeEdge.Top => Cursors.SizeNESW,
			ResizeEdge.Left | ResizeEdge.Bottom => Cursors.SizeNESW,
			_ => null
		};
	}

	private Point ScreenLogical(MouseEventArgs e)
	{
		Point device = _window.PointToScreen(e.GetPosition(_window));
		PresentationSource? source = PresentationSource.FromVisual(_window);
		if (source?.CompositionTarget != null)
		{
			return source.CompositionTarget.TransformFromDevice.Transform(device);
		}
		return device;
	}

	private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (_window.WindowState != System.Windows.WindowState.Normal)
		{
			return;
		}
		ResizeEdge edge = HitTest(e.GetPosition(_window));
		if (edge == ResizeEdge.None)
		{
			return;
		}
		_edge = edge;
		_resizing = true;
		_startCursor = ScreenLogical(e);
		_startLeft = _window.Left;
		_startTop = _window.Top;
		_startWidth = _window.ActualWidth;
		_startHeight = _window.ActualHeight;
		_window.CaptureMouse();
		e.Handled = true;
	}

	private void OnPreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_resizing)
		{
			if (_window.WindowState == System.Windows.WindowState.Normal)
			{
				Cursor? cursor = CursorFor(HitTest(e.GetPosition(_window)));
				if (cursor != null)
				{
					Mouse.OverrideCursor = cursor;
				}
				else if (Mouse.OverrideCursor != null)
				{
					Mouse.OverrideCursor = null;
				}
			}
			return;
		}
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			EndResize();
			return;
		}
		Point cursor2 = ScreenLogical(e);
		double deltaX = cursor2.X - _startCursor.X;
		double deltaY = cursor2.Y - _startCursor.Y;
		double minWidth = double.IsNaN(_window.MinWidth) || _window.MinWidth <= 0.0 ? 800.0 : Math.Max(_window.MinWidth, 800.0);
		double minHeight = double.IsNaN(_window.MinHeight) || _window.MinHeight <= 0.0 ? 500.0 : Math.Max(_window.MinHeight, 500.0);
		double maxWidth = double.IsNaN(_window.MaxWidth) || _window.MaxWidth <= 0.0 ? double.PositiveInfinity : _window.MaxWidth;
		double maxHeight = double.IsNaN(_window.MaxHeight) || _window.MaxHeight <= 0.0 ? double.PositiveInfinity : _window.MaxHeight;
		if ((_edge & ResizeEdge.Right) != 0)
		{
			_window.Width = Math.Clamp(_startWidth + deltaX, minWidth, maxWidth);
		}
		else if ((_edge & ResizeEdge.Left) != 0)
		{
			double width = Math.Clamp(_startWidth - deltaX, minWidth, maxWidth);
			_window.Left = _startLeft + (_startWidth - width);
			_window.Width = width;
		}
		if ((_edge & ResizeEdge.Bottom) != 0)
		{
			_window.Height = Math.Clamp(_startHeight + deltaY, minHeight, maxHeight);
		}
		else if ((_edge & ResizeEdge.Top) != 0)
		{
			double height = Math.Clamp(_startHeight - deltaY, minHeight, maxHeight);
			_window.Top = _startTop + (_startHeight - height);
			_window.Height = height;
		}
		e.Handled = true;
	}

	private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_resizing)
		{
			EndResize();
			e.Handled = true;
		}
	}

	private void OnLostCapture(object sender, MouseEventArgs e)
	{
		if (_resizing)
		{
			_resizing = false;
			_edge = ResizeEdge.None;
		}
	}

	private void EndResize()
	{
		_resizing = false;
		_edge = ResizeEdge.None;
		if (_window.IsMouseCaptured)
		{
			_window.ReleaseMouseCapture();
		}
		Mouse.OverrideCursor = null;
	}
}
