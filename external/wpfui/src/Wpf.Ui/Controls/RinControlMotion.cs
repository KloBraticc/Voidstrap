using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Common;

namespace Wpf.Ui.Controls;

public static class RinControlMotion
{
    private enum MotionKind
    {
        Button,
        ComboBox,
        ComboBoxItem
    }

    private sealed class MotionState
    {
        public MotionKind Kind { get; set; }

        public Border? Root { get; set; }

        public ToggleButton? Toggle { get; set; }

        public bool Attached { get; set; }

        public bool ItemPressed { get; set; }

        public bool PointerOver { get; set; }

        public double TargetOpacity { get; set; } = double.NaN;

        public Color? TargetColor { get; set; }

        public double TargetBrushOpacity { get; set; } = double.NaN;
    }

    private static readonly DependencyPropertyDescriptor IsMouseOverDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.IsMouseOverProperty, typeof(UIElement));

    private static readonly DependencyPropertyDescriptor IsEnabledDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(UIElement));

    private static readonly DependencyPropertyDescriptor BackgroundDescriptor =
        DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(Control));

    private static readonly DependencyPropertyDescriptor IsPressedDescriptor =
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));

    private static readonly DependencyPropertyDescriptor AppearanceDescriptor =
        DependencyPropertyDescriptor.FromProperty(Button.AppearanceProperty, typeof(Button));

    private static readonly DependencyPropertyDescriptor MouseOverBackgroundDescriptor =
        DependencyPropertyDescriptor.FromProperty(Button.MouseOverBackgroundProperty, typeof(Button));

    private static readonly DependencyPropertyDescriptor PressedBackgroundDescriptor =
        DependencyPropertyDescriptor.FromProperty(Button.PressedBackgroundProperty, typeof(Button));

    private static readonly DependencyPropertyDescriptor IsHighlightedDescriptor =
        DependencyPropertyDescriptor.FromProperty(ComboBoxItem.IsHighlightedProperty, typeof(ComboBoxItem));

    private static readonly DependencyPropertyDescriptor IsSelectedDescriptor =
        DependencyPropertyDescriptor.FromProperty(ListBoxItem.IsSelectedProperty, typeof(ListBoxItem));

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(RinControlMotion),
        new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(MotionState),
        typeof(RinControlMotion),
        new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(EnabledProperty);
    }

    public static void SetEnabled(DependencyObject element, bool value)
    {
        element.SetValue(EnabledProperty, value);
    }

    private static MotionState? GetState(DependencyObject element)
    {
        return (MotionState?)element.GetValue(StateProperty);
    }

    private static void SetState(DependencyObject element, MotionState? value)
    {
        element.SetValue(StateProperty, value);
    }

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            Unwire(element);
        }

        if ((bool)e.NewValue)
        {
            Wire(element);
        }
    }

    private static void Wire(FrameworkElement element)
    {
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;

        if (element.IsLoaded)
        {
            Attach(element);
        }
    }

    private static void Unwire(FrameworkElement element)
    {
        Detach(element);
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        SetState(element, null);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Attach(element);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Detach(element);
        }
    }

    private static void Attach(FrameworkElement element)
    {
        MotionState state = GetState(element) ?? new MotionState();
        if (state.Attached)
        {
            return;
        }

        if (element is not Control control)
        {
            return;
        }

        control.ApplyTemplate();
        state.Root = control.Template?.FindName("ContentBorder", control) as Border;
        if (state.Root is null)
        {
            return;
        }

        SetState(element, state);

        if (element is ComboBoxItem item)
        {
            state.Kind = MotionKind.ComboBoxItem;
            AttachItem(item, state);
        }
        else if (element is ComboBox comboBox)
        {
            state.Kind = MotionKind.ComboBox;
            AttachComboBox(comboBox, state);
        }
        else if (element is ButtonBase button)
        {
            state.Kind = MotionKind.Button;
            AttachButton(button);
        }
        else
        {
            state.Root = null;
            return;
        }

        state.Attached = true;
        Update(element, false);
    }

    private static void Detach(FrameworkElement element)
    {
        MotionState? state = GetState(element);
        if (state is null || !state.Attached)
        {
            return;
        }

        if (state.Kind == MotionKind.ComboBoxItem && element is ComboBoxItem item)
        {
            DetachItem(item);
        }
        else if (state.Kind == MotionKind.ComboBox && element is ComboBox comboBox)
        {
            DetachComboBox(comboBox, state);
        }
        else if (state.Kind == MotionKind.Button && element is ButtonBase button)
        {
            DetachButton(button);
        }

        state.Attached = false;
        state.ItemPressed = false;
        state.PointerOver = false;
        state.Root = null;
        state.Toggle = null;
        state.TargetOpacity = double.NaN;
        state.TargetColor = null;
        state.TargetBrushOpacity = double.NaN;
    }

    private static void AttachButton(ButtonBase button)
    {
        IsMouseOverDescriptor.AddValueChanged(button, OnControlStateChanged);
        IsPressedDescriptor.AddValueChanged(button, OnControlStateChanged);
        IsEnabledDescriptor.AddValueChanged(button, OnControlStateChanged);
        BackgroundDescriptor.AddValueChanged(button, OnControlStateChanged);

        if (button is Button uiButton)
        {
            AppearanceDescriptor.AddValueChanged(uiButton, OnControlStateChanged);
            MouseOverBackgroundDescriptor.AddValueChanged(uiButton, OnControlStateChanged);
            PressedBackgroundDescriptor.AddValueChanged(uiButton, OnControlStateChanged);
        }
    }

    private static void DetachButton(ButtonBase button)
    {
        IsMouseOverDescriptor.RemoveValueChanged(button, OnControlStateChanged);
        IsPressedDescriptor.RemoveValueChanged(button, OnControlStateChanged);
        IsEnabledDescriptor.RemoveValueChanged(button, OnControlStateChanged);
        BackgroundDescriptor.RemoveValueChanged(button, OnControlStateChanged);

        if (button is Button uiButton)
        {
            AppearanceDescriptor.RemoveValueChanged(uiButton, OnControlStateChanged);
            MouseOverBackgroundDescriptor.RemoveValueChanged(uiButton, OnControlStateChanged);
            PressedBackgroundDescriptor.RemoveValueChanged(uiButton, OnControlStateChanged);
        }
    }

    private static void AttachComboBox(ComboBox comboBox, MotionState state)
    {
        IsEnabledDescriptor.AddValueChanged(comboBox, OnControlStateChanged);
        BackgroundDescriptor.AddValueChanged(comboBox, OnControlStateChanged);
        comboBox.MouseEnter += OnComboBoxMouseEnter;
        comboBox.MouseLeave += OnComboBoxMouseLeave;
        state.PointerOver = comboBox.IsMouseOver;

        state.Toggle = comboBox.Template?.FindName("ToggleButton", comboBox) as ToggleButton;
        if (state.Toggle is not null)
        {
            IsPressedDescriptor.AddValueChanged(state.Toggle, OnComboBoxToggleStateChanged);
        }
    }

    private static void DetachComboBox(ComboBox comboBox, MotionState state)
    {
        IsEnabledDescriptor.RemoveValueChanged(comboBox, OnControlStateChanged);
        BackgroundDescriptor.RemoveValueChanged(comboBox, OnControlStateChanged);
        comboBox.MouseEnter -= OnComboBoxMouseEnter;
        comboBox.MouseLeave -= OnComboBoxMouseLeave;

        if (state.Toggle is not null)
        {
            IsPressedDescriptor.RemoveValueChanged(state.Toggle, OnComboBoxToggleStateChanged);
        }
    }

    private static void AttachItem(ComboBoxItem item, MotionState state)
    {
        IsHighlightedDescriptor.AddValueChanged(item, OnControlStateChanged);
        IsSelectedDescriptor.AddValueChanged(item, OnControlStateChanged);
        IsEnabledDescriptor.AddValueChanged(item, OnControlStateChanged);
        item.MouseEnter += OnItemMouseEnter;
        item.MouseLeave += OnItemMouseLeave;
        item.PreviewMouseLeftButtonDown += OnItemMouseDown;
        item.PreviewMouseLeftButtonUp += OnItemMouseUp;
        item.LostMouseCapture += OnItemLostMouseCapture;
        state.ItemPressed = false;
        state.PointerOver = item.IsMouseOver;
    }

    private static void DetachItem(ComboBoxItem item)
    {
        IsHighlightedDescriptor.RemoveValueChanged(item, OnControlStateChanged);
        IsSelectedDescriptor.RemoveValueChanged(item, OnControlStateChanged);
        IsEnabledDescriptor.RemoveValueChanged(item, OnControlStateChanged);
        item.MouseEnter -= OnItemMouseEnter;
        item.MouseLeave -= OnItemMouseLeave;
        item.PreviewMouseLeftButtonDown -= OnItemMouseDown;
        item.PreviewMouseLeftButtonUp -= OnItemMouseUp;
        item.LostMouseCapture -= OnItemLostMouseCapture;
    }

    private static void OnControlStateChanged(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Update(element, true);
        }
    }

    private static void OnComboBoxToggleStateChanged(object? sender, EventArgs e)
    {
        if (sender is ToggleButton toggle && FindAncestor<ComboBox>(toggle) is ComboBox comboBox)
        {
            Update(comboBox, true);
        }
    }

    private static void OnComboBoxMouseEnter(object sender, MouseEventArgs e)
    {
        SetComboBoxPointerState(sender, true);
    }

    private static void OnComboBoxMouseLeave(object sender, MouseEventArgs e)
    {
        SetComboBoxPointerState(sender, false);
    }

    private static void SetComboBoxPointerState(object sender, bool pointerOver)
    {
        if (sender is ComboBox comboBox && GetState(comboBox) is MotionState state && state.PointerOver != pointerOver)
        {
            state.PointerOver = pointerOver;
            Update(comboBox, true);
        }
    }

    private static void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBoxItem item && GetState(item) is MotionState state)
        {
            state.ItemPressed = true;
            Update(item, true);
        }
    }

    private static void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        SetItemPointerState(sender, true);
    }

    private static void OnItemMouseLeave(object sender, MouseEventArgs e)
    {
        SetItemPointerState(sender, false);
    }

    private static void SetItemPointerState(object sender, bool pointerOver)
    {
        if (sender is ComboBoxItem item && GetState(item) is MotionState state && state.PointerOver != pointerOver)
        {
            state.PointerOver = pointerOver;
            Update(item, true);
        }
    }

    private static void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseItem(sender);
    }

    private static void OnItemLostMouseCapture(object sender, MouseEventArgs e)
    {
        ReleaseItem(sender);
    }

    private static void ReleaseItem(object sender)
    {
        if (sender is ComboBoxItem item && GetState(item) is MotionState state && state.ItemPressed)
        {
            state.ItemPressed = false;
            Update(item, true);
        }
    }

    private static void Update(FrameworkElement element, bool animate)
    {
        MotionState? state = GetState(element);
        if (state?.Root is null)
        {
            return;
        }

        if (state.Kind == MotionKind.Button && element is ButtonBase button)
        {
            UpdateButton(button, state, animate);
        }
        else if (state.Kind == MotionKind.ComboBox && element is ComboBox comboBox)
        {
            UpdateComboBox(comboBox, state, animate);
        }
        else if (state.Kind == MotionKind.ComboBoxItem && element is ComboBoxItem item)
        {
            UpdateItem(item, state, animate);
        }
    }

    private static void UpdateButton(ButtonBase button, MotionState state, bool animate)
    {
        Brush normal = button.Background ?? Brushes.Transparent;
        bool standard;
        bool flat;
        Brush hover;
        Brush pressed;

        if (button is Button uiButton)
        {
            standard = uiButton.Appearance == ControlAppearance.Secondary;
            flat = uiButton.Appearance == ControlAppearance.Transparent;
            hover = flat
                ? FindBrush(button, "SubtleFillColorSecondaryBrush", Brushes.Transparent)
                : uiButton.MouseOverBackground ?? normal;
            pressed = uiButton.PressedBackground ?? normal;
        }
        else
        {
            flat = IsTransparent(normal);
            Brush defaultBrush = FindBrush(button, "ControlFillColorDefaultBrush", normal);
            standard = !flat && BrushesMatch(normal, defaultBrush);
            hover = flat
                ? FindBrush(button, "SubtleFillColorSecondaryBrush", Brushes.Transparent)
                : FindBrush(button, "ControlFillColorSecondaryBrush", normal);
            pressed = FindBrush(button, "ControlFillColorTertiaryBrush", normal);
        }

        bool highlighted = !standard && !flat;
        double targetOpacity = !button.IsEnabled
            ? 0.65
            : button.IsPressed
                ? standard ? 0.7 : 0.65
                : button.IsMouseOver
                    ? standard ? 1.0 : 0.875
                    : 1.0;

        Brush target = normal;
        bool animateBrush = standard;
        if (!button.IsEnabled)
        {
            target = highlighted
                ? FindBrush(button, "TextFillColorPrimaryBrush", normal)
                : flat ? Brushes.Transparent : normal;
            animateBrush = highlighted || standard;
        }
        else if (flat)
        {
            target = button.IsMouseOver ? hover : Brushes.Transparent;
            animateBrush = false;
        }
        else if (standard)
        {
            target = button.IsMouseOver ? hover : button.IsPressed ? pressed : normal;
        }
        else if (highlighted)
        {
            target = normal;
            animateBrush = false;
        }

        ApplyOpacity(state, targetOpacity, 187, EasingMode.EaseOut, animate);
        ApplyBrush(state, target, 187, EasingMode.EaseOut, animate && animateBrush);
    }

    private static void UpdateComboBox(ComboBox comboBox, MotionState state, bool animate)
    {
        bool pressed = state.Toggle?.IsPressed == true;
        double targetOpacity = !comboBox.IsEnabled ? 0.4 : pressed ? 0.7 : 1.0;
        Brush target = comboBox.Background ?? Brushes.Transparent;

        if (comboBox.IsEnabled)
        {
            if (pressed)
            {
                target = FindBrush(comboBox, "ControlFillColorTertiaryBrush", target);
            }
            else if (state.PointerOver)
            {
                target = FindBrush(comboBox, "ControlFillColorSecondaryBrush", target);
            }
        }

        ApplyOpacity(state, targetOpacity, 150, EasingMode.EaseOut, animate);
        ApplyBrush(state, target, 187, EasingMode.EaseOut, animate);
    }

    private static void UpdateItem(ComboBoxItem item, MotionState state, bool animate)
    {
        Brush target = Brushes.Transparent;
        if (state.ItemPressed)
        {
            target = FindBrush(item, "SubtleFillColorTertiaryBrush", target);
        }
        else if (item.IsHighlighted || item.IsSelected || state.PointerOver)
        {
            target = FindBrush(item, "SubtleFillColorSecondaryBrush", target);
        }

        ApplyBrush(state, target, 187, EasingMode.EaseInOut, animate);
    }

    private static void ApplyOpacity(MotionState state, double target, int duration, EasingMode easingMode, bool animate)
    {
        if (state.Root is null || Math.Abs(state.TargetOpacity - target) < 0.0001)
        {
            return;
        }

        state.TargetOpacity = target;
        double current = state.Root.Opacity;
        state.Root.BeginAnimation(UIElement.OpacityProperty, null);
        state.Root.Opacity = target;

        if (!animate)
        {
            return;
        }

        state.Root.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation
            {
                From = current,
                To = target,
                Duration = TimeSpan.FromMilliseconds(duration),
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuarticEase { EasingMode = easingMode }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void ApplyBrush(MotionState state, Brush target, int duration, EasingMode easingMode, bool animate)
    {
        if (state.Root is null)
        {
            return;
        }

        if (target is not SolidColorBrush targetBrush)
        {
            state.TargetColor = null;
            state.TargetBrushOpacity = double.NaN;
            state.Root.Background = target.CloneCurrentValue();
            return;
        }

        if (state.TargetColor == targetBrush.Color && Math.Abs(state.TargetBrushOpacity - targetBrush.Opacity) < 0.0001)
        {
            return;
        }

        state.TargetColor = targetBrush.Color;
        state.TargetBrushOpacity = targetBrush.Opacity;

        SolidColorBrush currentBrush = state.Root.Background as SolidColorBrush ?? new SolidColorBrush(Colors.Transparent);
        Color currentColor = currentBrush.Color;
        double currentOpacity = currentBrush.Opacity;
        SolidColorBrush animatedBrush = new(targetBrush.Color) { Opacity = targetBrush.Opacity };
        state.Root.Background = animatedBrush;

        if (!animate)
        {
            return;
        }

        animatedBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation
            {
                From = currentColor,
                To = targetBrush.Color,
                Duration = TimeSpan.FromMilliseconds(duration),
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuarticEase { EasingMode = easingMode }
            },
            HandoffBehavior.SnapshotAndReplace);
        animatedBrush.BeginAnimation(
            Brush.OpacityProperty,
            new DoubleAnimation
            {
                From = currentOpacity,
                To = targetBrush.Opacity,
                Duration = TimeSpan.FromMilliseconds(duration),
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuarticEase { EasingMode = easingMode }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static Brush FindBrush(FrameworkElement element, string key, Brush fallback)
    {
        object? resource = element.TryFindResource(key);
        return resource switch
        {
            Brush brush => brush,
            Color color => new SolidColorBrush(color),
            _ => fallback
        };
    }

    private static bool IsTransparent(Brush brush)
    {
        return brush.Opacity <= 0 || brush is SolidColorBrush solid && solid.Color.A == 0;
    }

    private static bool BrushesMatch(Brush first, Brush second)
    {
        return first is SolidColorBrush firstSolid &&
               second is SolidColorBrush secondSolid &&
               firstSolid.Color == secondSolid.Color &&
               Math.Abs(firstSolid.Opacity - secondSolid.Opacity) < 0.0001;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
