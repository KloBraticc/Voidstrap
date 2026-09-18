using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Voidstrap.UI.Elements.Controls;

public class AnimatedPopup : Popup
{
	private static readonly CoerceValueCallback? BaseCoerceIsOpen = IsOpenProperty.GetMetadata(typeof(Popup)).CoerceValueCallback;

	private static readonly Duration OpenDuration = new Duration(TimeSpan.FromMilliseconds(170));

	private static readonly Duration CloseDuration = new Duration(TimeSpan.FromMilliseconds(120));

	private static readonly IEasingFunction OpenEase = CreateEase(EasingMode.EaseOut);

	private static readonly IEasingFunction CloseEase = CreateEase(EasingMode.EaseIn);

	private readonly TranslateTransform _offset = new TranslateTransform();

	private AnimationClock? _clock;

	private bool _closing;

	private bool _finishingClose;

	private Window? _dismissWindow;

	public event EventHandler? Closing;

	public bool LightDismiss { get; set; }

	public bool IsClosing => _closing;

	static AnimatedPopup()
	{
		IsOpenProperty.OverrideMetadata(typeof(AnimatedPopup), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, null, CoerceIsOpen));
	}

	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);
		AttachDismiss();
		if (!_closing)
		{
			Animate(true, true);
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		DetachDismiss();
		base.OnClosed(e);
	}

	private static object CoerceIsOpen(DependencyObject d, object baseValue)
	{
		AnimatedPopup popup = (AnimatedPopup)d;
		if (baseValue is true)
		{
			if (popup._closing)
			{
				popup._closing = false;
				popup.Animate(true, false);
			}
			else if (!popup.IsOpen)
			{
				popup.HideChild();
			}
			return BaseCoerceIsOpen != null ? BaseCoerceIsOpen(d, baseValue) : baseValue;
		}
		if (popup._finishingClose || !popup.IsOpen || !SystemParameters.ClientAreaAnimation || popup.Child is not UIElement)
		{
			return baseValue;
		}
		if (!popup._closing)
		{
			popup._closing = true;
			popup.Closing?.Invoke(popup, EventArgs.Empty);
			popup.Animate(false, false);
		}
		return true;
	}

	private void HideChild()
	{
		if (!SystemParameters.ClientAreaAnimation || Child is not UIElement child)
		{
			return;
		}
		ReleaseClock();
		AttachOffset(child);
		child.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, TimeSpan.Zero));
		_offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8d, TimeSpan.Zero));
	}

	private void Animate(bool opening, bool fromStart)
	{
		ReleaseClock();
		if (Child is not UIElement child)
		{
			return;
		}
		AttachOffset(child);
		if (!SystemParameters.ClientAreaAnimation)
		{
			child.BeginAnimation(OpacityProperty, null);
			_offset.BeginAnimation(TranslateTransform.YProperty, null);
			return;
		}
		Duration duration = opening ? OpenDuration : CloseDuration;
		IEasingFunction ease = opening ? OpenEase : CloseEase;
		DoubleAnimation fade = new DoubleAnimation(opening ? 1d : 0d, duration) { EasingFunction = ease };
		DoubleAnimation slide = new DoubleAnimation(opening ? 0d : -4d, duration) { EasingFunction = ease };
		if (fromStart)
		{
			fade.From = 0d;
			slide.From = -8d;
		}
		_offset.BeginAnimation(TranslateTransform.YProperty, slide);
		_clock = fade.CreateClock();
		_clock.Completed += Clock_Completed;
		child.ApplyAnimationClock(OpacityProperty, _clock);
	}

	private void Clock_Completed(object? sender, EventArgs e)
	{
		ReleaseClock();
		if (!_closing)
		{
			return;
		}
		_closing = false;
		_finishingClose = true;
		try
		{
			CoerceValue(IsOpenProperty);
		}
		finally
		{
			_finishingClose = false;
		}
	}

	private void AttachDismiss()
	{
		if (!LightDismiss || _dismissWindow != null)
		{
			return;
		}
		_dismissWindow = Window.GetWindow(this);
		if (_dismissWindow != null)
		{
			_dismissWindow.PreviewMouseDown += DismissWindow_PreviewMouseDown;
		}
	}

	private void DetachDismiss()
	{
		if (_dismissWindow == null)
		{
			return;
		}
		_dismissWindow.PreviewMouseDown -= DismissWindow_PreviewMouseDown;
		_dismissWindow = null;
	}

	private void DismissWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (!IsOpen || _closing || e.OriginalSource is not DependencyObject source || IsWithin(source, Child) || IsWithin(source, PlacementTarget))
		{
			return;
		}
		IsOpen = false;
	}

	private static bool IsWithin(DependencyObject source, DependencyObject? root)
	{
		if (root == null)
		{
			return false;
		}
		for (DependencyObject? current = source; current != null; current = LogicalTreeHelper.GetParent(current) ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null))
		{
			if (ReferenceEquals(current, root))
			{
				return true;
			}
		}
		return false;
	}

	private void AttachOffset(UIElement child)
	{
		if (ReferenceEquals(child.RenderTransform, Transform.Identity))
		{
			child.RenderTransform = _offset;
		}
	}

	private void ReleaseClock()
	{
		if (_clock == null)
		{
			return;
		}
		_clock.Completed -= Clock_Completed;
		_clock = null;
	}

	private static CubicEase CreateEase(EasingMode mode)
	{
		CubicEase ease = new CubicEase { EasingMode = mode };
		ease.Freeze();
		return ease;
	}
}
