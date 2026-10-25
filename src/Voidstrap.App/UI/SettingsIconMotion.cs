using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Voidstrap.UI;

internal sealed class SettingsIconMotion : IDisposable
{
	private static readonly Duration PressDuration = new(TimeSpan.FromMilliseconds(150));

	private static readonly Duration SpinDuration = new(TimeSpan.FromMilliseconds(650));

	private static readonly KeySpline PressSpline = new(0.167, 0.167, 1, 1);

	private static readonly KeySpline SpinSpline = new(0, 0, 0, 1);

	private readonly FrameworkElement _item;

	private RotateTransform? _rotation;

	private bool _pressed;

	private bool _disposed;

	public SettingsIconMotion(FrameworkElement item)
	{
		_item = item;
		_item.PreviewMouseLeftButtonDown += OnPressed;
		_item.PreviewMouseLeftButtonUp += OnReleased;
		_item.MouseLeave += OnLeft;
		_item.LostMouseCapture += OnCaptureLost;
	}

	private void OnPressed(object sender, MouseButtonEventArgs e)
	{
		RotateTransform? rotation = EnsureRotation();
		if (rotation == null)
			return;
		_pressed = true;
		DoubleAnimationUsingKeyFrames press = new() { FillBehavior = FillBehavior.HoldEnd };
		press.KeyFrames.Add(new SplineDoubleKeyFrame(-20, KeyTime.FromTimeSpan(PressDuration.TimeSpan), PressSpline));
		rotation.BeginAnimation(RotateTransform.AngleProperty, press, HandoffBehavior.SnapshotAndReplace);
	}

	private void OnReleased(object sender, MouseButtonEventArgs e) => Spin();

	private void OnLeft(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed)
			return;
		Spin();
	}

	private void OnCaptureLost(object sender, MouseEventArgs e) => Spin();

	private void Spin()
	{
		if (!_pressed || _rotation == null)
			return;
		_pressed = false;
		DoubleAnimationUsingKeyFrames spin = new() { FillBehavior = FillBehavior.Stop };
		spin.KeyFrames.Add(new SplineDoubleKeyFrame(360, KeyTime.FromTimeSpan(SpinDuration.TimeSpan), SpinSpline));
		_rotation.Angle = 0;
		_rotation.BeginAnimation(RotateTransform.AngleProperty, spin, HandoffBehavior.SnapshotAndReplace);
	}

	private RotateTransform? EnsureRotation()
	{
		if (_rotation != null)
			return _rotation;
		FrameworkElement? icon = FindIcon(_item);
		if (icon == null)
			return null;
		_rotation = new RotateTransform();
		icon.RenderTransformOrigin = new Point(0.5, 0.5);
		icon.RenderTransform = _rotation;
		return _rotation;
	}

	private static FrameworkElement? FindIcon(DependencyObject parent)
	{
		int count = VisualTreeHelper.GetChildrenCount(parent);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is Wpf.Ui.Controls.SymbolIcon icon)
				return icon;
			FrameworkElement? found = FindIcon(child);
			if (found != null)
				return found;
		}
		return null;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_item.PreviewMouseLeftButtonDown -= OnPressed;
		_item.PreviewMouseLeftButtonUp -= OnReleased;
		_item.MouseLeave -= OnLeft;
		_item.LostMouseCapture -= OnCaptureLost;
		_rotation?.BeginAnimation(RotateTransform.AngleProperty, null);
		GC.SuppressFinalize(this);
	}
}
