using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Wpf.Ui.Controls
{
    public static class SmoothScroll
    {
        private const double WheelStepPixels = 96d;
        private const double GlideFactor = 0.22;
        private const double OvershootResistance = 0.35;
        private const double OvershootMax = 44d;
        private const double SpringFactor = 0.12;

        private static bool _registered;
        private static bool _globalEnabled;

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false));

        private static readonly DependencyProperty DriverProperty = DependencyProperty.RegisterAttached(
            "Driver",
            typeof(Driver),
            typeof(SmoothScroll),
            new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        public static void SetGlobalEnabled(bool value) => _globalEnabled = value;

        public static void Register()
        {
            if (_registered)
                return;
            _registered = true;
            EventManager.RegisterClassHandler(
                typeof(ScrollViewer),
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnPreviewMouseWheel),
                false);
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
				if (OperatingSystem.IsLinux())
					return;
                if (e.Handled || sender is not ScrollViewer sv)
                    return;
                if (!GetIsEnabled(sv) && !_globalEnabled)
                    return;
                if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                    return;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    return;
                if (sv.ScrollableHeight <= 0)
                    return;
                if (HasInnerScrollable(sv, e.OriginalSource as DependencyObject, e.Delta))
                    return;

                e.Handled = true;

                if (sv.GetValue(DriverProperty) is not Driver driver)
                {
                    driver = new Driver(sv);
                    sv.SetValue(DriverProperty, driver);
                }

                driver.Wheel(e.Delta);
            }
            catch
            {
            }
        }

        private static bool HasInnerScrollable(ScrollViewer outer, DependencyObject? source, int delta)
        {
            try
            {
                var node = source;
                int depth = 0;
                while (node != null && node != outer && depth < 64)
                {
                    if (node is ScrollViewer sv && sv != outer && sv.ScrollableHeight > 0)
                    {
                        if (delta < 0 && sv.VerticalOffset < sv.ScrollableHeight)
                            return true;
                        if (delta > 0 && sv.VerticalOffset > 0)
                            return true;
                    }
                    else if (node is TextBoxBase)
                    {
                        return true;
                    }

                    node = node is Visual or System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(node)
                        : LogicalTreeHelper.GetParent(node);
                    depth++;
                }
            }
            catch
            {
            }
            return false;
        }

        private sealed class Driver
        {
            private readonly ScrollViewer _sv;
            private double _target;
            private double _current;
            private double _overshoot;
            private bool _hooked;
            private TranslateTransform? _transform;

            public Driver(ScrollViewer sv)
            {
                _sv = sv;
                sv.ScrollChanged += OnScrollChanged;
                sv.Unloaded += OnUnloaded;
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                _sv.ScrollChanged -= OnScrollChanged;
                _sv.Unloaded -= OnUnloaded;
                Unhook();
                if (_transform != null)
                    _transform.Y = 0;
                _sv.ClearValue(DriverProperty);
            }

            private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
            {
                if (!_hooked && Math.Abs(e.VerticalChange) > 0)
                {
                    _target = _sv.VerticalOffset;
                    _current = _sv.VerticalOffset;
                }
            }

            public void Wheel(int delta)
            {
                if (!_hooked)
                {
                    _target = _sv.VerticalOffset;
                    _current = _sv.VerticalOffset;
                }

                double unit = _sv.CanContentScroll
                    ? Math.Max(1, SystemParameters.WheelScrollLines)
                    : WheelStepPixels;
                double step = delta / 120d * unit;
                double proposed = _target - step;
                double max = _sv.ScrollableHeight;
                double restEps = _sv.CanContentScroll ? 0.05 : 1.0;

                if (proposed < 0)
                {
                    bool restingAtTop = !_hooked && _current <= restEps && Math.Abs(_overshoot) < 0.3;
                    if (restingAtTop)
                        return;
                    double px = _sv.CanContentScroll ? (0 - proposed) * 24 : (0 - proposed);
                    _overshoot = Math.Min(OvershootMax, _overshoot + px * OvershootResistance);
                    _target = 0;
                }
                else if (proposed > max)
                {
                    bool restingAtBottom = !_hooked && _current >= max - restEps && Math.Abs(_overshoot) < 0.3;
                    if (restingAtBottom)
                        return;
                    double px = _sv.CanContentScroll ? (proposed - max) * 24 : (proposed - max);
                    _overshoot = Math.Max(-OvershootMax, _overshoot - px * OvershootResistance);
                    _target = max;
                }
                else
                {
                    _target = proposed;
                }

                Hook();
            }

            private void Hook()
            {
                if (_hooked)
                    return;
                _hooked = true;
                CompositionTarget.Rendering += OnRender;
            }

            private void Unhook()
            {
                if (!_hooked)
                    return;
                _hooked = false;
                CompositionTarget.Rendering -= OnRender;
            }

            private void OnRender(object? sender, EventArgs e)
            {
                bool busy = false;

                double diff = _target - _current;
                double minStep = _sv.CanContentScroll ? 0.02 : 0.6;
                if (Math.Abs(diff) > 0)
                {
                    double step = diff * GlideFactor;
                    if (Math.Abs(step) < minStep)
                        step = Math.Sign(diff) * Math.Min(minStep, Math.Abs(diff));
                    _current += step;
                    if ((diff > 0 && _current >= _target) || (diff < 0 && _current <= _target))
                        _current = _target;
                    _sv.ScrollToVerticalOffset(_current);
                    busy = _current != _target;
                }

                if (Math.Abs(_overshoot) > 0.3)
                {
                    _overshoot -= _overshoot * SpringFactor;
                    ApplyOvershoot(_overshoot);
                    busy = true;
                }
                else if (_overshoot != 0)
                {
                    _overshoot = 0;
                    ApplyOvershoot(0);
                }

                if (!busy)
                    Unhook();
            }

            private void ApplyOvershoot(double amount)
            {
                if (_sv.Content is not UIElement el)
                    return;

                if (_transform == null)
                {
                    if (el.RenderTransform is TranslateTransform existing)
                    {
                        _transform = existing;
                    }
                    else if (el.RenderTransform == null || el.RenderTransform == Transform.Identity || (el.RenderTransform is MatrixTransform mt && mt.Matrix.IsIdentity))
                    {
                        _transform = new TranslateTransform();
                        el.RenderTransform = _transform;
                    }
                    else
                    {
                        return;
                    }
                }

                _transform.Y = amount;
            }
        }
    }
}
