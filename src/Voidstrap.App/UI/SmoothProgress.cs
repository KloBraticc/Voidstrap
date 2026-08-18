using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class SmoothProgress
{
	private const string GlowName = "GlowRect";

	private const double GlowWidth = 200.0;

	private const double MarqueePixelsPerSecond = 260.0;

	private static readonly TimeSpan ValueTransition = TimeSpan.FromMilliseconds(220);

	private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
		"State",
		typeof(BarState),
		typeof(SmoothProgress));

	private sealed class BarState
	{
		public bool Suppress;

		public double Displayed;

		public double MarqueeWidth = -1.0;

		public bool IndeterminateHooked;
	}

	private static readonly DependencyPropertyDescriptor IndeterminateDescriptor =
		DependencyPropertyDescriptor.FromProperty(ProgressBar.IsIndeterminateProperty, typeof(ProgressBar));

	private static bool _installed;

	public static void Install()
	{
		if (_installed)
		{
			return;
		}
		_installed = true;
		EventManager.RegisterClassHandler(typeof(ProgressBar), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged));
	}

	private static BarState EnsureAttached(ProgressBar bar)
	{
		if (bar.GetValue(StateProperty) is BarState existing)
		{
			return existing;
		}
		BarState state = new() { Displayed = bar.Value };
		bar.SetValue(StateProperty, state);
		bar.ValueChanged += OnValueChanged;
		bar.IsVisibleChanged += OnIsVisibleChanged;
		HookIndeterminate(bar, state);
		return state;
	}

	private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (sender is ProgressBar bar)
		{
			StartMarquee(bar, EnsureAttached(bar));
		}
	}

	private static void HookIndeterminate(ProgressBar bar, BarState state)
	{
		if (state.IndeterminateHooked)
		{
			return;
		}
		IndeterminateDescriptor.AddValueChanged(bar, OnIndeterminateChanged);
		state.IndeterminateHooked = true;
	}

	private static void UnhookIndeterminate(ProgressBar bar, BarState state)
	{
		if (!state.IndeterminateHooked)
		{
			return;
		}
		IndeterminateDescriptor.RemoveValueChanged(bar, OnIndeterminateChanged);
		state.IndeterminateHooked = false;
	}

	private static void OnIndeterminateChanged(object? sender, EventArgs e)
	{
		if (sender is not ProgressBar bar || bar.GetValue(StateProperty) is not BarState state)
		{
			return;
		}
		state.MarqueeWidth = -1.0;
		if (!bar.IsIndeterminate)
		{
			StopMarquee(bar);
			return;
		}
		bar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestartMarquee(bar)));
	}

	private static void RestartMarquee(ProgressBar bar)
	{
		if (bar.GetValue(StateProperty) is not BarState state || !bar.IsIndeterminate)
		{
			return;
		}
		state.MarqueeWidth = -1.0;
		StartMarquee(bar, state);
		if (state.MarqueeWidth < 0.0)
		{
			bar.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RestartMarquee(bar)));
		}
	}

	private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (sender is not ProgressBar bar || bar.GetValue(StateProperty) is not BarState state)
		{
			return;
		}
		if (bar.IsVisible)
		{
			HookIndeterminate(bar, state);
			RestartMarquee(bar);
			return;
		}
		UnhookIndeterminate(bar, state);
		StopMarquee(bar);
		state.MarqueeWidth = -1.0;
		bar.BeginAnimation(RangeBase.ValueProperty, null);
		state.Suppress = false;
	}

	private static TranslateTransform? ResolveGlow(ProgressBar bar, out double trackWidth)
	{
		trackWidth = 0.0;
		FrameworkElement? glow;
		try
		{
			glow = bar.Template?.FindName(GlowName, bar) as FrameworkElement;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
		if (glow == null)
		{
			return null;
		}
		trackWidth = glow.Parent is FrameworkElement host ? host.ActualWidth : bar.ActualWidth;
		if (glow.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
		{
			transform = new TranslateTransform();
			glow.RenderTransform = transform;
		}
		return transform;
	}

	private static void StartMarquee(ProgressBar bar, BarState state)
	{
		if (!bar.IsIndeterminate)
		{
			StopMarquee(bar);
			state.MarqueeWidth = -1.0;
			return;
		}

		TranslateTransform? transform = ResolveGlow(bar, out double trackWidth);
		if (transform == null || trackWidth <= 0.0)
		{
			return;
		}
		if (Math.Abs(state.MarqueeWidth - trackWidth) < 0.5)
		{
			return;
		}
		state.MarqueeWidth = trackWidth;

		double distance = trackWidth + GlowWidth;
		DoubleAnimation animation = new()
		{
			From = -GlowWidth,
			To = trackWidth,
			Duration = TimeSpan.FromSeconds(Math.Clamp(distance / MarqueePixelsPerSecond, 0.9, 4.0)),
			RepeatBehavior = RepeatBehavior.Forever
		};
		animation.Freeze();
		transform.BeginAnimation(TranslateTransform.XProperty, animation);
	}

	private static void StopMarquee(ProgressBar bar)
	{
		try
		{
			if (bar.Template?.FindName(GlowName, bar) is FrameworkElement glow && glow.RenderTransform is TranslateTransform transform && !transform.IsFrozen)
			{
				transform.BeginAnimation(TranslateTransform.XProperty, null);
				transform.X = 0.0;
			}
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (sender is not ProgressBar bar || bar.GetValue(StateProperty) is not BarState state)
		{
			return;
		}
		if (state.Suppress)
		{
			return;
		}
		if (bar.IsIndeterminate)
		{
			state.Displayed = e.NewValue;
			return;
		}
		AnimateTo(bar, state, state.Displayed, e.NewValue);
	}

	private static void AnimateTo(ProgressBar bar, BarState state, double from, double to)
	{
		if (double.IsNaN(from) || double.IsNaN(to) || to <= from || to - from < 0.5)
		{
			state.Displayed = to;
			return;
		}

		DoubleAnimation animation = new()
		{
			From = from,
			To = to,
			Duration = ValueTransition,
			EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.HoldEnd
		};
		animation.Completed += (s, e) => OnTransitionCompleted(bar, state, to);
		state.Suppress = true;
		state.Displayed = to;
		bar.BeginAnimation(RangeBase.ValueProperty, animation);
	}

	private static void OnTransitionCompleted(ProgressBar bar, BarState state, double reached)
	{
		if (bar.GetValue(StateProperty) is not BarState current || !ReferenceEquals(current, state))
		{
			return;
		}
		bar.BeginAnimation(RangeBase.ValueProperty, null);
		state.Suppress = false;
		state.Displayed = reached;
		double pending = bar.Value;
		if (!bar.IsIndeterminate && pending - reached >= 0.5)
		{
			AnimateTo(bar, state, reached, pending);
		}
		else
		{
			state.Displayed = pending;
		}
	}

}
