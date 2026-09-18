// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT License was not distributed with this file, you can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wpf.Ui.Controls;

/// <summary>
/// A custom <see cref="System.Windows.Controls.Primitives.ScrollBar"/> that detects user interaction and scrolling activity.
/// </summary>
[ToolboxItem(true)]
[ToolboxBitmap(typeof(DynamicScrollBar), "DynamicScrollBar.bmp")]
public class DynamicScrollBar : System.Windows.Controls.Primitives.ScrollBar
{
    private bool _isScrolling;
    private bool _isInteracted;
    private readonly DispatcherTimer _interactionTimer;

    public DynamicScrollBar()
    {
        _interactionTimer = new DispatcherTimer();
        _interactionTimer.Tick += OnInteractionTimerTick;
        Unloaded += OnUnloaded;
    }

    #region Dependency Properties

    public static readonly DependencyProperty IsScrollingProperty =
        DependencyProperty.Register(
            nameof(IsScrolling),
            typeof(bool),
            typeof(DynamicScrollBar),
            new PropertyMetadata(false, OnIsScrollingChanged));

    public static readonly DependencyProperty IsInteractedProperty =
        DependencyProperty.Register(
            nameof(IsInteracted),
            typeof(bool),
            typeof(DynamicScrollBar),
            new PropertyMetadata(false, OnIsInteractedChanged));

    public static readonly DependencyProperty TimeoutProperty =
        DependencyProperty.Register(
            nameof(Timeout),
            typeof(int),
            typeof(DynamicScrollBar),
            new PropertyMetadata(1000));

    #endregion

    #region Properties

    /// <summary>
    /// Indicates whether the user is currently scrolling.
    /// </summary>
    public bool IsScrolling
    {
        get => (bool)GetValue(IsScrollingProperty);
        set => SetValue(IsScrollingProperty, value);
    }

    /// <summary>
    /// Indicates whether the scrollbar is currently being interacted with (mouse over or scrolling).
    /// </summary>
    public bool IsInteracted
    {
        get => (bool)GetValue(IsInteractedProperty);
        set
        {
            if (_isInteracted != value)
                SetValue(IsInteractedProperty, value);
        }
    }

    /// <summary>
    /// Delay in milliseconds before the scrollbar hides after interaction ends.
    /// </summary>
    public int Timeout
    {
        get => (int)GetValue(TimeoutProperty);
        set => SetValue(TimeoutProperty, value);
    }

    #endregion

    #region Event Handlers

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateInteractionState();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateInteractionState();
    }

    #endregion

    #region Interaction Logic

    private void UpdateInteractionState()
    {
        bool shouldBeInteracted = IsMouseOver || _isScrolling;

        if (shouldBeInteracted)
        {
            _interactionTimer.Stop();
            IsInteracted = true;
            return;
        }

        if (!_isInteracted)
            return;

        int timeout = System.Math.Max(0, Timeout);
        if (timeout == 0)
        {
            IsInteracted = false;
            return;
        }

        _interactionTimer.Interval = System.TimeSpan.FromMilliseconds(timeout);
        _interactionTimer.Stop();
        _interactionTimer.Start();
    }

    private void OnInteractionTimerTick(object? sender, System.EventArgs e)
    {
        _interactionTimer.Stop();

        if (!IsMouseOver && !_isScrolling)
            IsInteracted = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _interactionTimer.Stop();
        _isInteracted = false;
        SetValue(IsInteractedProperty, false);
    }

    private static void OnIsScrollingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DynamicScrollBar scrollbar)
        {
            bool newValue = (bool)e.NewValue;
            if (scrollbar._isScrolling != newValue)
            {
                scrollbar._isScrolling = newValue;
                scrollbar.UpdateInteractionState();
            }
        }
    }

    private static void OnIsInteractedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DynamicScrollBar scrollbar)
        {
            bool newValue = (bool)e.NewValue;
            if (scrollbar._isInteracted != newValue)
            {
                scrollbar._isInteracted = newValue;
                scrollbar.UpdateInteractionState();
            }
        }
    }

    #endregion
}
