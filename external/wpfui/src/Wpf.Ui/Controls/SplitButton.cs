// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Wpf.Ui.Controls;

/// <summary>
/// The <see cref="SplitButton"/> is a <see cref="DropDownButton"/>, but with an addition execution hit target.
/// </summary>
public class SplitButton : Wpf.Ui.Controls.DropDownButton
{
    public static readonly DependencyProperty DropDownOnRightProperty = DependencyProperty.Register(
        nameof(DropDownOnRight), typeof(bool), typeof(SplitButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsDropDownOpenProperty = DependencyProperty.Register(
        nameof(IsDropDownOpen), typeof(bool), typeof(SplitButton), new PropertyMetadata(false));

    public static readonly DependencyProperty DropDownToolTipProperty = DependencyProperty.Register(
        nameof(DropDownToolTip), typeof(string), typeof(SplitButton), new PropertyMetadata(null));

    public static readonly RoutedEvent DropDownClickEvent = EventManager.RegisterRoutedEvent(
        nameof(DropDownClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SplitButton));

    static SplitButton()
    {
        EventManager.RegisterClassHandler(typeof(SplitButton), ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick));
    }

    public bool DropDownOnRight
    {
        get => (bool)GetValue(DropDownOnRightProperty);
        set => SetValue(DropDownOnRightProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => (bool)GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    public string? DropDownToolTip
    {
        get => (string?)GetValue(DropDownToolTipProperty);
        set => SetValue(DropDownToolTipProperty, value);
    }

    public event RoutedEventHandler DropDownClick
    {
        add => AddHandler(DropDownClickEvent, value);
        remove => RemoveHandler(DropDownClickEvent, value);
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is SplitButton button && e.OriginalSource is ButtonBase { Name: "PART_DropDownButton" } part
            && ReferenceEquals(part.TemplatedParent, button))
        {
            e.Handled = true;
            button.RaiseEvent(new RoutedEventArgs(DropDownClickEvent, button));
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsEnabled && (e.Key == Key.F4 || (e.Key == Key.System && e.SystemKey == Key.Down)))
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(DropDownClickEvent, this));
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
