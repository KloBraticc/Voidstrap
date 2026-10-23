using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Voidstrap.UI.Elements.Settings.Pages;

internal sealed class PageSidebar
{
	private const double MinWidth = 64.0;

	private const double MaxWidth = 320.0;

	private const double DefaultWidth = 225.0;

	private const double IconThreshold = 150.0;

	private const double CollapsedWidth = 72.0;

	private readonly FrameworkElement _sidebar;

	private readonly FrameworkElement _resizer;

	private readonly IInputElement _origin;

	private readonly Action<bool> _setIconsOnly;

	private readonly Action<double> _saveWidth;

	private double _expandedWidth = DefaultWidth;

	private bool _resizing;

	private double _resizeStartX;

	private double _resizeStartWidth;

	public bool IsCollapsed { get; private set; }

	public PageSidebar(FrameworkElement sidebar, FrameworkElement resizer, IInputElement origin, Action<bool> setIconsOnly, Action<double> saveWidth)
	{
		_sidebar = sidebar;
		_resizer = resizer;
		_origin = origin;
		_setIconsOnly = setIconsOnly;
		_saveWidth = saveWidth;
	}

	public void Apply(double width)
	{
		if (double.IsNaN(width) || width <= 0.0)
			width = DefaultWidth;
		width = Math.Clamp(width, MinWidth, MaxWidth);
		if (Math.Abs(width - DefaultWidth) <= 12.0)
			width = DefaultWidth;
		_sidebar.Width = width;
		bool iconsOnly = width < IconThreshold;
		if (!iconsOnly)
			_expandedWidth = width;
		IsCollapsed = iconsOnly;
		_setIconsOnly(iconsOnly);
	}

	public void Toggle()
	{
		StopResizing();
		_sidebar.BeginAnimation(FrameworkElement.WidthProperty, null);
		double from = double.IsNaN(_sidebar.Width) ? DefaultWidth : _sidebar.Width;
		double to = IsCollapsed ? Math.Max(IconThreshold, _expandedWidth) : CollapsedWidth;
		if (Math.Abs(to - from) <= 1.0)
		{
			Apply(to);
			Save();
			return;
		}
		if (!IsCollapsed)
			_setIconsOnly(false);
		DoubleAnimation slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		};
		slide.Completed += ToggleAnimation_Completed;
		_sidebar.BeginAnimation(FrameworkElement.WidthProperty, slide);
	}

	private void ToggleAnimation_Completed(object? sender, EventArgs e)
	{
		Apply(IsCollapsed ? Math.Max(IconThreshold, _expandedWidth) : CollapsedWidth);
		Save();
	}

	public void ResizerDown(MouseButtonEventArgs e)
	{
		_sidebar.BeginAnimation(FrameworkElement.WidthProperty, null);
		if (e.ClickCount > 1)
		{
			StopResizing();
			Apply(DefaultWidth);
			Save();
			e.Handled = true;
			return;
		}
		_resizing = true;
		_resizeStartX = e.GetPosition(_origin).X;
		_resizeStartWidth = double.IsNaN(_sidebar.Width) ? DefaultWidth : _sidebar.Width;
		_resizer.CaptureMouse();
		e.Handled = true;
	}

	public void ResizerMove(MouseEventArgs e)
	{
		if (_resizing)
			Apply(_resizeStartWidth + e.GetPosition(_origin).X - _resizeStartX);
	}

	public void ResizerUp()
	{
		if (!_resizing)
			return;
		StopResizing();
		Save();
	}

	private void StopResizing()
	{
		if (!_resizing)
			return;
		_resizing = false;
		try
		{
			_resizer.ReleaseMouseCapture();
		}
		catch
		{
		}
	}

	private void Save()
	{
		_saveWidth(_sidebar.Width);
	}
}
