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

	private nint _nativeWindow;

	private System.Windows.Threading.DispatcherTimer? _pollTimer;

	private EventHandler? _pollHandler;

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
		StopPointerPolling();
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
		Point device;
		if (Voidstrap.Utility.Platform.IsLinux
			&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPointerPosition(out int pointerX, out int pointerY))
		{
			device = new Point(pointerX, pointerY);
		}
		else
		{
			device = _window.PointToScreen(e.GetPosition(_window));
		}

		PresentationSource? source = PresentationSource.FromVisual(_window);
		if (source?.CompositionTarget != null)
		{
			return source.CompositionTarget.TransformFromDevice.Transform(device);
		}
		return device;
	}

	private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (_window.WindowState != System.Windows.WindowState.Normal || LinuxWindowMode.IsFullscreen(_window) || LinuxWindowMode.IsMaximized(_window))
		{
			return;
		}
		ResizeEdge edge = HitTest(e.GetPosition(_window));
		if (edge == ResizeEdge.None)
		{
			return;
		}
		if (Voidstrap.Utility.Platform.IsLinux && TryBeginWindowManagerResize(edge))
		{
			Mouse.OverrideCursor = null;
			e.Handled = true;
			return;
		}
		_edge = edge;
		_resizing = true;
		_startCursor = ScreenLogical(e);
		_startLeft = _window.Left;
		_startTop = _window.Top;
		_startWidth = _window.ActualWidth;
		_startHeight = _window.ActualHeight;
		_nativeWindow = ResolveNativeWindow();
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			StartPointerPolling();
		}
		else
		{
			_window.CaptureMouse();
		}

		e.Handled = true;
	}

	private void OnPreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_resizing)
		{
			if (_window.WindowState == System.Windows.WindowState.Normal && !LinuxWindowMode.IsFullscreen(_window) && !LinuxWindowMode.IsMaximized(_window))
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
		ApplyResize(ScreenLogical(e));
		e.Handled = true;
	}

	private void ApplyResize(Point cursor2)
	{
		double deltaX = cursor2.X - _startCursor.X;
		double deltaY = cursor2.Y - _startCursor.Y;
		double minWidth = double.IsNaN(_window.MinWidth) || _window.MinWidth <= 0.0 ? 320.0 : _window.MinWidth;
		double minHeight = double.IsNaN(_window.MinHeight) || _window.MinHeight <= 0.0 ? 240.0 : _window.MinHeight;
		double maxWidth = double.IsNaN(_window.MaxWidth) || _window.MaxWidth <= 0.0 ? double.PositiveInfinity : _window.MaxWidth;
		double maxHeight = double.IsNaN(_window.MaxHeight) || _window.MaxHeight <= 0.0 ? double.PositiveInfinity : _window.MaxHeight;
		minWidth = Math.Min(minWidth, maxWidth);
		minHeight = Math.Min(minHeight, maxHeight);
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

		CommitNativeGeometry();
	}

	private bool TryBeginWindowManagerResize(ResizeEdge edge)
	{
#if CROSSPLAT
		int direction = edge switch
		{
			ResizeEdge.Left | ResizeEdge.Top => 0,
			ResizeEdge.Top => 1,
			ResizeEdge.Right | ResizeEdge.Top => 2,
			ResizeEdge.Right => 3,
			ResizeEdge.Right | ResizeEdge.Bottom => 4,
			ResizeEdge.Bottom => 5,
			ResizeEdge.Left | ResizeEdge.Bottom => 6,
			ResizeEdge.Left => 7,
			_ => -1
		};
		if (direction < 0
			|| !System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(_window, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host)
			|| host?.SilkWindow?.Native?.X11 is not { } x11
			|| x11.Display == 0
			|| x11.Window == 0)
		{
			return false;
		}
		return Voidstrap.Platform.Linux.LinuxWindowInterop.TryBeginInteractiveResize(x11.Display, (nint)x11.Window, direction);
#else
		return false;
#endif
	}

	private nint ResolveNativeWindow()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return 0;
		}

		try
		{
			return LinuxWindowMode.ResolveNativeWindow(_window);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("WindowEdgeResizer::ResolveNativeWindow", "The native window could not be resolved: " + ex.Message);
			return 0;
		}
	}

	private void CommitNativeGeometry()
	{
		if (_nativeWindow == 0)
		{
			return;
		}

		try
		{
			double scale = 1.0;
			PresentationSource? source = PresentationSource.FromVisual(_window);
			if (source?.CompositionTarget != null)
			{
				scale = source.CompositionTarget.TransformToDevice.M11;
			}

			if (scale <= 0.0 || double.IsNaN(scale))
			{
				scale = 1.0;
			}

			int left = (int)Math.Round(_window.Left * scale);
			int top = (int)Math.Round(_window.Top * scale);
			int width = (int)Math.Round(_window.Width * scale);
			int height = (int)Math.Round(_window.Height * scale);
			Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(_nativeWindow, left, top, width, height);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("WindowEdgeResizer::CommitNativeGeometry", "The window geometry could not be applied: " + ex.Message);
		}
	}

	private void StartPointerPolling()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || _pollTimer != null)
		{
			return;
		}

		_pollHandler = OnPollTick;
		_pollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input, _window.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(16.0)
		};
		_pollTimer.Tick += _pollHandler;
		_pollTimer.Start();
	}

	private void StopPointerPolling()
	{
		if (_pollTimer == null)
		{
			return;
		}

		_pollTimer.Stop();
		if (_pollHandler != null)
		{
			_pollTimer.Tick -= _pollHandler;
		}

		_pollTimer = null;
		_pollHandler = null;
	}

	private void OnPollTick(object? sender, EventArgs e)
	{
		if (!_resizing)
		{
			StopPointerPolling();
			return;
		}

		if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPointerPosition(out int x, out int y, out bool pressed))
		{
			return;
		}

		if (!pressed)
		{
			EndResize();
			return;
		}

		Point device = new(x, y);
		PresentationSource? source = PresentationSource.FromVisual(_window);
		Point logical = source?.CompositionTarget != null
			? source.CompositionTarget.TransformFromDevice.Transform(device)
			: device;
		ApplyResize(logical);
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
		_nativeWindow = 0;
		StopPointerPolling();
		if (_window.IsMouseCaptured)
		{
			_window.ReleaseMouseCapture();
		}
		Mouse.OverrideCursor = null;
	}
}
