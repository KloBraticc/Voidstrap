using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Wpf.Ui.Controls
{
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(DynamicScrollViewer), "DynamicScrollViewer.bmp")]
    public class DynamicScrollViewer : ScrollViewer
    {
        private readonly DispatcherTimer _verticalTimer = new();
        private readonly DispatcherTimer _horizontalTimer = new();
        private bool _verticalTimerAttached;
        private bool _horizontalTimerAttached;

        private int _timeout = 900;
        private double _minimalChange = 1d;

        public DynamicScrollViewer()
        {
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _verticalTimer.Stop();
            _horizontalTimer.Stop();
            if (_verticalTimerAttached)
            {
                _verticalTimer.Tick -= OnVerticalTimerTick;
                _verticalTimerAttached = false;
            }
            if (_horizontalTimerAttached)
            {
                _horizontalTimer.Tick -= OnHorizontalTimerTick;
                _horizontalTimerAttached = false;
            }
            IsScrollingVertically = false;
            IsScrollingHorizontally = false;
        }

        public static readonly DependencyProperty IsScrollingVerticallyProperty =
            DependencyProperty.Register(
                nameof(IsScrollingVertically),
                typeof(bool),
                typeof(DynamicScrollViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsScrollingHorizontallyProperty =
            DependencyProperty.Register(
                nameof(IsScrollingHorizontally),
                typeof(bool),
                typeof(DynamicScrollViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty MinimalChangeProperty =
            DependencyProperty.Register(
                nameof(MinimalChange),
                typeof(double),
                typeof(DynamicScrollViewer),
                new PropertyMetadata(1d, OnMinimalChangeChanged));

        public static readonly DependencyProperty TimeoutProperty =
            DependencyProperty.Register(
                nameof(Timeout),
                typeof(int),
                typeof(DynamicScrollViewer),
                new PropertyMetadata(900, OnTimeoutChanged));

        public bool IsScrollingVertically
        {
            get => (bool)GetValue(IsScrollingVerticallyProperty);
            private set => SetValue(IsScrollingVerticallyProperty, value);
        }

        public bool IsScrollingHorizontally
        {
            get => (bool)GetValue(IsScrollingHorizontallyProperty);
            private set => SetValue(IsScrollingHorizontallyProperty, value);
        }

        public double MinimalChange
        {
            get => _minimalChange;
            set => SetValue(MinimalChangeProperty, value);
        }

        public int Timeout
        {
            get => _timeout;
            set => SetValue(TimeoutProperty, value);
        }

        protected override void OnScrollChanged(ScrollChangedEventArgs e)
        {
            base.OnScrollChanged(e);

            if (Math.Abs(e.VerticalChange) >= _minimalChange)
                TriggerScrollState(isVertical: true);

            if (Math.Abs(e.HorizontalChange) >= _minimalChange)
                TriggerScrollState(isVertical: false);
        }

        private void TriggerScrollState(bool isVertical)
        {
            if (isVertical)
            {
                if (!IsScrollingVertically)
                    IsScrollingVertically = true;
                if (!_verticalTimerAttached)
                {
                    _verticalTimer.Tick += OnVerticalTimerTick;
                    _verticalTimerAttached = true;
                }
                _verticalTimer.Stop();
                _verticalTimer.Interval = TimeSpan.FromMilliseconds(_timeout);
                _verticalTimer.Start();
            }
            else
            {
                if (!IsScrollingHorizontally)
                    IsScrollingHorizontally = true;
                if (!_horizontalTimerAttached)
                {
                    _horizontalTimer.Tick += OnHorizontalTimerTick;
                    _horizontalTimerAttached = true;
                }
                _horizontalTimer.Stop();
                _horizontalTimer.Interval = TimeSpan.FromMilliseconds(_timeout);
                _horizontalTimer.Start();
            }
        }

        private void OnVerticalTimerTick(object? sender, EventArgs e)
        {
			_verticalTimer.Stop();
			IsScrollingVertically = false;
        }

        private void OnHorizontalTimerTick(object? sender, EventArgs e)
        {
			_horizontalTimer.Stop();
			IsScrollingHorizontally = false;
        }

        private static void OnMinimalChangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DynamicScrollViewer scroll)
                scroll._minimalChange = Math.Max(1d, (double)e.NewValue);
        }

        private static void OnTimeoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DynamicScrollViewer scroll)
                scroll._timeout = Math.Max(100, (int)e.NewValue);
        }
    }
}
