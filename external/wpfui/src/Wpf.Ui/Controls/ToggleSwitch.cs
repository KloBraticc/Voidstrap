// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Hardware;
using Point = System.Windows.Point;

namespace Wpf.Ui.Controls;

/// <summary>
/// Use <see cref="ToggleSwitch"/> to present users with two mutally exclusive options (like on/off).
/// </summary>
[ToolboxItem(true)]
[ToolboxBitmap(typeof(ToggleSwitch), "ToggleSwitch.bmp")]
[TemplatePart(Name = TrackPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = KnobHostPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = KnobPositionPartName, Type = typeof(TranslateTransform))]
[TemplatePart(Name = DragTransformPartName, Type = typeof(TranslateTransform))]
public class ToggleSwitch : System.Windows.Controls.Primitives.ToggleButton
{
    private const string TrackPartName = "PART_Track";
    private const string KnobHostPartName = "PART_KnobHost";
    private const string KnobPositionPartName = "PART_KnobPosition";
    private const string DragTransformPartName = "PART_DragTransform";
    private const double StateDurationMilliseconds = 200d;

    private static readonly QuarticEase SettleEase = CreateSettleEase();

    private FrameworkElement? _track;
    private FrameworkElement? _knobHost;
    private TranslateTransform? _knobPosition;
    private TranslateTransform? _dragTransform;
    private Window? _ownerWindow;
    private bool _templateReady;
    private bool _dragCandidate;
    private bool _dragging;
    private bool _completingDrag;
    private bool _systemEventsAttached;
    private Point _dragStart;
    private bool? _dragOriginalValue;
    private bool? _dragIntendedValue;
    private bool? _pendingDragValue;
    private double _dragBaseCenter;

    static ToggleSwitch()
    {
        EventManager.RegisterClassHandler(
            typeof(ToggleSwitch),
            LoadedEvent,
            new RoutedEventHandler(OnToggleLoaded));
        EventManager.RegisterClassHandler(
            typeof(ToggleSwitch),
            UnloadedEvent,
            new RoutedEventHandler(OnToggleUnloaded));
    }

    public override void OnApplyTemplate()
    {
        CancelDrag(false, true);
        _templateReady = false;
        _track = null;
        _knobHost = null;
        _knobPosition = null;
        _dragTransform = null;

        base.OnApplyTemplate();

        _track = Template?.FindName(TrackPartName, this) as FrameworkElement;
        _knobHost = Template?.FindName(KnobHostPartName, this) as FrameworkElement;
        _knobPosition = Template?.FindName(KnobPositionPartName, this) as TranslateTransform;
        _dragTransform = Template?.FindName(DragTransformPartName, this) as TranslateTransform;
        _templateReady = true;
        ResetDragTransform();
        ChangeVisualState(false);
    }

    private void ChangeVisualState(bool useTransitions)
    {
        if (!_templateReady)
        {
            return;
        }

        bool transitions = useTransitions && ShouldAnimate();
        string prefix = IsChecked switch
        {
            true => "SwitchChecked",
            false => "SwitchUnchecked",
            null => "SwitchIndeterminate"
        };
        string suffix;

        if (!IsEnabled)
        {
            suffix = "SwitchDisabled";
        }
        else if (_dragging)
        {
            suffix = "SwitchDragging";
        }
        else if (IsPressed)
        {
            suffix = "SwitchPressed";
        }
        else if (IsMouseOver)
        {
            suffix = "SwitchPointerOver";
        }
        else
        {
            suffix = "SwitchNormal";
        }

        VisualStateManager.GoToState(this, prefix, transitions);
        VisualStateManager.GoToState(this, suffix, transitions);
        VisualStateManager.GoToState(
            this,
            IsEnabled && IsKeyboardFocused ? "KeyboardFocused" : "KeyboardUnfocused",
            transitions);
    }

    protected override void OnToggle()
    {
        if (_pendingDragValue is bool value)
        {
            _pendingDragValue = null;
            SetCurrentValue(IsCheckedProperty, value);
            return;
        }

        base.OnToggle();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (!IsEnabled || _track is null || !IsInsideTrack(e.GetPosition(_track)))
        {
            ResetDragSession();
            return;
        }

        ResetDragTransform();
        _dragCandidate = true;
        _dragging = false;
        _dragStart = e.GetPosition(_track);
        _dragOriginalValue = IsChecked;
        _dragIntendedValue = IsChecked;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragCandidate || _track is null || _dragTransform is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag(true, false);
            return;
        }

        Point point = e.GetPosition(_track);
        double horizontal = point.X - _dragStart.X;
        double vertical = point.Y - _dragStart.Y;

        if (!_dragging)
        {
            if (Math.Abs(horizontal) < SystemParameters.MinimumHorizontalDragDistance)
            {
                return;
            }

            if (Math.Abs(horizontal) <= Math.Abs(vertical))
            {
                ResetDragSession();
                return;
            }

            _dragging = true;
            ChangeVisualState(false);
            _dragBaseCenter = ResolveKnobCenter();
        }

        UpdateDragVisual(point.X);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        if (!_dragging)
        {
            ResetDragSession();
            return;
        }

        CompleteDrag();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_dragCandidate || _dragging))
        {
            CancelDrag(true, true);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (!_completingDrag)
        {
            CancelDrag(true, false);
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == IsEnabledProperty && e.NewValue is false)
        {
            CancelDrag(false, true);
        }
        else if (e.Property == IsCheckedProperty && _dragging && !_completingDrag)
        {
            CancelDrag(true, true);
        }

        if (e.Property == IsCheckedProperty ||
            e.Property == IsEnabledProperty ||
            e.Property == IsMouseOverProperty ||
            e.Property == IsPressedProperty ||
            e.Property == IsKeyboardFocusedProperty ||
            e.Property == BackgroundProperty)
        {
            ChangeVisualState(true);
        }
    }

    private static void OnToggleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggleSwitch)
        {
            toggleSwitch.AttachSystemEvents();
            toggleSwitch.ChangeVisualState(false);
        }
    }

    private static void OnToggleUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggleSwitch)
        {
            toggleSwitch.CancelDrag(false, true);
            toggleSwitch.DetachSystemEvents();
        }
    }

    private void AttachSystemEvents()
    {
        if (_systemEventsAttached)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated += OnOwnerWindowDeactivated;
        }
        _systemEventsAttached = true;
    }

    private void DetachSystemEvents()
    {
        if (!_systemEventsAttached)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OnOwnerWindowDeactivated;
            _ownerWindow = null;
        }
        _systemEventsAttached = false;
    }

    private void OnOwnerWindowDeactivated(object? sender, EventArgs e)
    {
        CancelDrag(true, true);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshSystemVisualState);
            return;
        }

        RefreshSystemVisualState();
    }

    private void RefreshSystemVisualState()
    {
        if (!ShouldAnimate())
        {
            ResetDragTransform();
        }

        ChangeVisualState(false);
    }

    private void CompleteDrag()
    {
        bool? intended = _dragIntendedValue;
        bool changed = intended is bool value && IsChecked != value;
        _dragCandidate = false;
        _dragging = false;
        _completingDrag = true;

        try
        {
            if (changed && intended is bool target)
            {
                _pendingDragValue = target;
                OnClick();
            }
            else
            {
                ChangeVisualState(true);
            }

            SettleDragTransform();

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        finally
        {
            _pendingDragValue = null;
            _completingDrag = false;
            _dragOriginalValue = null;
            _dragIntendedValue = null;
            ChangeVisualState(true);
        }
    }

    private void CancelDrag(bool animate, bool releaseCapture)
    {
        bool hadSession = _dragCandidate || _dragging;
        _dragCandidate = false;
        _dragging = false;
        _pendingDragValue = null;
        _dragOriginalValue = null;
        _dragIntendedValue = null;

        if (hadSession && animate)
        {
            SettleDragTransform();
        }
        else
        {
            ResetDragTransform();
        }

        if (releaseCapture && IsMouseCaptured)
        {
            _completingDrag = true;
            try
            {
                ReleaseMouseCapture();
            }
            finally
            {
                _completingDrag = false;
            }
        }

        ChangeVisualState(animate);
    }

    private void ResetDragSession()
    {
        _dragCandidate = false;
        _dragging = false;
        _dragOriginalValue = null;
        _dragIntendedValue = null;
    }

    private void UpdateDragVisual(double pointerX)
    {
        if (_track is null || _knobHost is null || _dragTransform is null)
        {
            return;
        }

        double trackWidth = Math.Max(0d, _track.ActualWidth);
        double knobWidth = Math.Max(1d, _knobHost.ActualWidth);
        double halfKnob = knobWidth * 0.5d;
        double center = Math.Clamp(pointerX, halfKnob, Math.Max(halfKnob, trackWidth - halfKnob));
        double trackCenter = trackWidth * 0.5d;
        double delta = center - trackCenter;

        _dragTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _dragTransform.SetCurrentValue(TranslateTransform.XProperty, center - _dragBaseCenter);

        if (Math.Abs(delta) < 0.001d)
        {
            _dragIntendedValue = _dragOriginalValue;
        }
        else
        {
            _dragIntendedValue = delta > 0d;
        }
    }

    private double ResolveKnobCenter()
    {
        if (_track is null || _knobHost is null)
        {
            return IsChecked switch
            {
                true => 30d,
                false => 10d,
                null => 20d
            };
        }

        try
        {
            Point center = _knobHost.TranslatePoint(
                new Point(_knobHost.ActualWidth * 0.5d, _knobHost.ActualHeight * 0.5d),
                _track);
            return center.X;
        }
        catch (InvalidOperationException)
        {
            return _track.ActualWidth * 0.5d;
        }
    }

    private void SettleDragTransform()
    {
        if (_dragTransform is null)
        {
            return;
        }

        double current = _dragTransform.X;
        _dragTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _dragTransform.SetCurrentValue(TranslateTransform.XProperty, 0d);

        if (!ShouldAnimate() || Math.Abs(current) < 0.001d)
        {
            return;
        }

        _dragTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(current, 0d, TimeSpan.FromMilliseconds(StateDurationMilliseconds))
            {
                EasingFunction = SettleEase,
                FillBehavior = FillBehavior.Stop
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetDragTransform()
    {
        if (_dragTransform is null)
        {
            return;
        }

        _dragTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _dragTransform.SetCurrentValue(TranslateTransform.XProperty, 0d);
    }

    private bool IsInsideTrack(Point point)
    {
        return _track is not null &&
               point.X >= 0d &&
               point.Y >= 0d &&
               point.X <= _track.ActualWidth &&
               point.Y <= _track.ActualHeight;
    }

    private static bool ShouldAnimate()
    {
        return !HardwareAcceleration.AnimationsDisabled &&
               SystemParameters.ClientAreaAnimation &&
               !SystemParameters.HighContrast;
    }

    private static QuarticEase CreateSettleEase()
    {
        QuarticEase easing = new() { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }
}
