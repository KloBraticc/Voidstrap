using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Wpf.Ui.Controls;

public static class CaptionButtonState
{
    private static readonly IEasingFunction FadeEase = CreateEase();

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(100));

    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered",
        typeof(bool),
        typeof(CaptionButtonState),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed",
        typeof(bool),
        typeof(CaptionButtonState),
        new PropertyMetadata(false, OnStateChanged));

    public static bool GetIsHovered(DependencyObject element)
    {
        return (bool)element.GetValue(IsHoveredProperty);
    }

    public static void SetIsHovered(DependencyObject element, bool value)
    {
        element.SetValue(IsHoveredProperty, value);
    }

    public static bool GetIsPressed(DependencyObject element)
    {
        return (bool)element.GetValue(IsPressedProperty);
    }

    public static void SetIsPressed(DependencyObject element, bool value)
    {
        element.SetValue(IsPressedProperty, value);
    }

    public static void Apply(Control button, bool animate)
    {
        if (button.Template?.FindName("HoverLayer", button) is not UIElement layer)
            return;

        bool pressed = GetIsPressed(button) || button is System.Windows.Controls.Primitives.ButtonBase { IsPressed: true };
        double target = pressed ? 0.8 : GetIsHovered(button) ? 1.0 : 0.0;

        if (!animate || !button.IsVisible)
        {
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Opacity = target;
            return;
        }

        layer.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(target, FadeDuration) { EasingFunction = FadeEase },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Control button)
            Apply(button, true);
    }

    private static QuadraticEase CreateEase()
    {
        QuadraticEase ease = new() { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();
        return ease;
    }
}
