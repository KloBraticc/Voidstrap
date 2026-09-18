using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Voidstrap.UI.Elements.Controls;

public static class SidebarGroup
{
	private const double FallbackRowHeight = 40.0;

	private const double ChildSpacing = 4.0;

	private static readonly Duration RevealDuration = new Duration(TimeSpan.FromMilliseconds(250));

	private static readonly ConditionalWeakTable<NavigationItem, List<NavigationItem>> Children = new ConditionalWeakTable<NavigationItem, List<NavigationItem>>();

	private static readonly Duration FillDuration = new Duration(TimeSpan.FromMilliseconds(187));

	private static readonly RoutedEventHandler HeaderClickHandler = OnHeaderClick;

	private static readonly MouseButtonEventHandler ChildButtonHandler = OnChildButton;

	public static readonly DependencyProperty IconOnlyProperty = DependencyProperty.RegisterAttached(
		"IconOnly", typeof(bool), typeof(SidebarGroup), new PropertyMetadata(false));

	public static readonly DependencyProperty IsHeaderProperty = DependencyProperty.RegisterAttached(
		"IsHeader", typeof(bool), typeof(SidebarGroup), new PropertyMetadata(false, OnIsHeaderChanged));

	public static readonly DependencyProperty HeaderProperty = DependencyProperty.RegisterAttached(
		"Header", typeof(NavigationItem), typeof(SidebarGroup), new PropertyMetadata(null, OnHeaderChanged));

	public static readonly DependencyProperty IsChildProperty = DependencyProperty.RegisterAttached(
		"IsChild", typeof(bool), typeof(SidebarGroup), new PropertyMetadata(false));

	public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
		"IsExpanded", typeof(bool), typeof(SidebarGroup), new PropertyMetadata(false));

	public static readonly DependencyProperty HasActiveChildProperty = DependencyProperty.RegisterAttached(
		"HasActiveChild", typeof(bool), typeof(SidebarGroup), new PropertyMetadata(false));

	private static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
		"Reveal", typeof(double), typeof(SidebarGroup), new PropertyMetadata(0.0, OnRevealChanged));

	public static bool GetIsHeader(DependencyObject element) => (bool)element.GetValue(IsHeaderProperty);

	public static void SetIsHeader(DependencyObject element, bool value) => element.SetValue(IsHeaderProperty, value);

	public static NavigationItem? GetHeader(DependencyObject element) => (NavigationItem?)element.GetValue(HeaderProperty);

	public static void SetHeader(DependencyObject element, NavigationItem? value) => element.SetValue(HeaderProperty, value);

	public static bool GetIsChild(DependencyObject element) => (bool)element.GetValue(IsChildProperty);

	public static void SetIsChild(DependencyObject element, bool value) => element.SetValue(IsChildProperty, value);

	public static bool GetIsExpanded(DependencyObject element) => (bool)element.GetValue(IsExpandedProperty);

	public static void SetIsExpanded(DependencyObject element, bool value) => element.SetValue(IsExpandedProperty, value);

	public static bool GetIconOnly(DependencyObject element) => (bool)element.GetValue(IconOnlyProperty);

	public static void SetIconOnly(DependencyObject element, bool value) => element.SetValue(IconOnlyProperty, value);

	public static bool GetHasActiveChild(DependencyObject element) => (bool)element.GetValue(HasActiveChildProperty);

	public static void SetHasActiveChild(DependencyObject element, bool value) => element.SetValue(HasActiveChildProperty, value);

	public static void Expand(NavigationItem header, bool expanded, bool animate)
	{
		SetIsExpanded(header, expanded);
		double target = expanded ? 1.0 : 0.0;
		if (!animate)
		{
			header.BeginAnimation(RevealProperty, null);
			header.SetValue(RevealProperty, target);
			ApplyReveal(header, target);
			return;
		}
		double from = (double)header.GetValue(RevealProperty);
		DoubleAnimation animation = new DoubleAnimation(from, target, RevealDuration)
		{
			EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
		};
		header.BeginAnimation(RevealProperty, animation);
	}

	public static void RefreshActiveChild(NavigationItem header)
	{
		if (!Children.TryGetValue(header, out List<NavigationItem>? children))
			return;
		foreach (NavigationItem child in children)
			UpdateFill(child);
		bool active = children.Any(child => child.IsActive);
		if (GetHasActiveChild(header) == active)
			return;
		SetHasActiveChild(header, active);
		if (active && !GetIsExpanded(header) && !GetIconOnly(header))
			Expand(header, true, animate: true);
	}

	private static void OnIsHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not NavigationItem header)
			return;
		header.RemoveHandler(ButtonBase.ClickEvent, HeaderClickHandler);
		if ((bool)e.NewValue)
			header.AddHandler(ButtonBase.ClickEvent, HeaderClickHandler);
	}

	private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not NavigationItem child)
			return;
		if (e.OldValue is NavigationItem oldHeader && Children.TryGetValue(oldHeader, out List<NavigationItem>? oldChildren))
			oldChildren.Remove(child);
		DetachFill(child);
		if (e.NewValue is not NavigationItem header)
		{
			SetIsChild(child, false);
			ShowChild(child, double.PositiveInfinity, 1.0, true);
			return;
		}
		List<NavigationItem> children = Children.GetOrCreateValue(header);
		if (!children.Contains(child))
			children.Add(child);
		SetIsChild(child, true);
		AttachFill(child);
		ApplyReveal(header, (double)header.GetValue(RevealProperty));
	}

	private static void AttachFill(NavigationItem child)
	{
		child.Background = new SolidColorBrush(Colors.Transparent);
		child.MouseEnter += OnChildMouse;
		child.MouseLeave += OnChildMouse;
		child.LostMouseCapture += OnChildMouse;
		child.AddHandler(UIElement.MouseLeftButtonDownEvent, ChildButtonHandler, true);
		child.AddHandler(UIElement.MouseLeftButtonUpEvent, ChildButtonHandler, true);
	}

	private static void DetachFill(NavigationItem child)
	{
		child.MouseEnter -= OnChildMouse;
		child.MouseLeave -= OnChildMouse;
		child.LostMouseCapture -= OnChildMouse;
		child.RemoveHandler(UIElement.MouseLeftButtonDownEvent, ChildButtonHandler);
		child.RemoveHandler(UIElement.MouseLeftButtonUpEvent, ChildButtonHandler);
		child.ClearValue(Control.BackgroundProperty);
	}

	private static void OnChildMouse(object sender, MouseEventArgs e)
	{
		if (sender is NavigationItem child)
			UpdateFill(child);
	}

	private static void OnChildButton(object sender, MouseButtonEventArgs e)
	{
		if (sender is NavigationItem child)
			UpdateFill(child);
	}

	private static void UpdateFill(NavigationItem child)
	{
		if (child.Background is not SolidColorBrush brush || brush.IsFrozen)
			return;
		Color hover = child.TryFindResource("SubtleFillColorSecondary") is Color secondary ? secondary : Color.FromArgb(15, 255, 255, 255);
		Color target;
		if (child.IsPressed)
			target = child.TryFindResource("SubtleFillColorTertiary") is Color tertiary ? tertiary : Color.FromArgb(11, 255, 255, 255);
		else if (child.IsMouseOver || child.IsActive)
			target = hover;
		else
			target = Color.FromArgb(0, hover.R, hover.G, hover.B);
		ColorAnimation animation = new ColorAnimation(target, FillDuration)
		{
			EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
		};
		brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
	}

	private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is NavigationItem header)
			ApplyReveal(header, (double)e.NewValue);
	}

	private static void ApplyReveal(NavigationItem header, double reveal)
	{
		if (!Children.TryGetValue(header, out List<NavigationItem>? children) || children.Count == 0)
			return;
		double rowHeight = header.ActualHeight > 0 ? header.ActualHeight + ChildSpacing : FallbackRowHeight;
		double shown = reveal * rowHeight * children.Count;
		bool open = GetIsExpanded(header);
		for (int i = 0; i < children.Count; i++)
		{
			double height = reveal >= 1.0
				? double.PositiveInfinity
				: Math.Clamp(shown - i * rowHeight, 0.0, rowHeight);
			ShowChild(children[i], height, Math.Clamp(reveal, 0.0, 1.0), open);
		}
	}

	private static void ShowChild(NavigationItem child, double height, double opacity, bool interactive)
	{
		child.MaxHeight = height;
		child.Opacity = opacity;
		child.IsHitTestVisible = interactive;
		child.Focusable = interactive;
		KeyboardNavigation.SetIsTabStop(child, interactive);
	}

	private static void OnHeaderClick(object sender, RoutedEventArgs e)
	{
		if (sender is not NavigationItem header || !ReferenceEquals(e.OriginalSource, header))
			return;
		if (GetIconOnly(header))
		{
			OpenFlyout(header);
			return;
		}
		Expand(header, !GetIsExpanded(header), animate: true);
	}

	private static void OpenFlyout(NavigationItem header)
	{
		if (!Children.TryGetValue(header, out List<NavigationItem>? children) || children.Count == 0)
			return;
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			PlacementTarget = header,
			Placement = PlacementMode.Right
		};
		foreach (NavigationItem child in children)
		{
			if (child.Visibility != Visibility.Visible)
				continue;
			MenuItem item = new MenuItem { Header = LabelOf(child), Tag = child };
			item.Click += OnFlyoutItemClick;
			menu.Items.Add(item);
		}
		menu.Closed += OnFlyoutClosed;
		menu.IsOpen = true;
	}

	private static string LabelOf(NavigationItem item)
	{
		return item.Content switch
		{
			string text => text,
			Panel panel => panel.Children.OfType<TextBlock>().Select(block => block.Text).FirstOrDefault() ?? "",
			_ => item.Content?.ToString() ?? ""
		};
	}

	private static void OnFlyoutItemClick(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Tag: NavigationItem child })
			child.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, child));
	}

	private static void OnFlyoutClosed(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.ContextMenu menu)
			return;
		menu.Closed -= OnFlyoutClosed;
		foreach (MenuItem item in menu.Items.OfType<MenuItem>())
			item.Click -= OnFlyoutItemClick;
	}
}
