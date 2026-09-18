using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Animations;

namespace Wpf.Ui.Controls
{
    public static class ExpanderReveal
    {
        private const double MinimumAnimatedHeight = 0.5d;

        private static readonly CubicBezierEase RevealEase = CreateEase(0.4d, 0d, 0.2d, 1d);

        private static readonly CubicBezierEase CollapseEase = CreateEase(0.45d, 0d, 0.55d, 1d);

        public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
            "IsOpen",
            typeof(bool),
            typeof(ExpanderReveal),
            new PropertyMetadata(false, OnIsOpenChanged));

        public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached(
            "Duration",
            typeof(Duration),
            typeof(ExpanderReveal),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(300d))));

        public static readonly DependencyProperty FadeDurationProperty = DependencyProperty.RegisterAttached(
            "FadeDuration",
            typeof(Duration),
            typeof(ExpanderReveal),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(180d))));

        private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
            "Generation",
            typeof(int),
            typeof(ExpanderReveal),
            new PropertyMetadata(0));

        private static readonly DependencyProperty NativeStateProperty = DependencyProperty.RegisterAttached(
            "NativeState",
            typeof(NativeAnimationState),
            typeof(ExpanderReveal),
            new PropertyMetadata(null));

        private static readonly DependencyProperty TweenStateProperty = DependencyProperty.RegisterAttached(
            "TweenState",
            typeof(Tween),
            typeof(ExpanderReveal),
            new PropertyMetadata(null));

        public static void SetIsOpen(DependencyObject element, bool value) => element.SetValue(IsOpenProperty, value);

        public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);

        public static void SetDuration(DependencyObject element, Duration value) => element.SetValue(DurationProperty, value);

        public static Duration GetDuration(DependencyObject element) => (Duration)element.GetValue(DurationProperty);

        public static void SetFadeDuration(DependencyObject element, Duration value) => element.SetValue(FadeDurationProperty, value);

        public static Duration GetFadeDuration(DependencyObject element) => (Duration)element.GetValue(FadeDurationProperty);

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
                return;

            int generation = (int)element.GetValue(GenerationProperty) + 1;
            element.SetValue(GenerationProperty, generation);

            if (!OperatingSystem.IsLinux())
            {
                AnimateWindows(element, generation, (bool)e.NewValue);
                return;
            }

            if (!ExpanderMotion.GetUseLinuxAnimationClock(element))
            {
                AnimateLegacyLinux(element, generation, (bool)e.NewValue);
                return;
            }

            AnimateLinux(element, generation, (bool)e.NewValue);
        }

        private static void AnimateWindows(FrameworkElement element, int generation, bool open)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);

            RenderOptions.SetClearTypeHint(element, ClearTypeHint.Enabled);
            element.UseLayoutRounding = true;

            double current = element.ActualHeight;
            double opacity = element.Opacity;

            if (open && element.ActualWidth <= 0d)
            {
                OpenWithoutAnimation(element);
                return;
            }

            element.ClipToBounds = true;
            element.Height = current;

            if (!open)
            {
                Duration collapse = GetDuration(element);
                element.BeginAnimation(UIElement.OpacityProperty, Animate(opacity, 0d, collapse, CollapseEase));

                if (current <= MinimumAnimatedHeight)
                {
                    SettleClosed(element, generation);
                    return;
                }

                DoubleAnimation shrink = Animate(current, 0d, collapse, CollapseEase);
                shrink.Completed += (_, _) => SettleClosed(element, generation);
                element.BeginAnimation(FrameworkElement.HeightProperty, shrink);
                return;
            }

            double target = Measure(element, current, element.ActualWidth);
            element.BeginAnimation(UIElement.OpacityProperty, Animate(opacity, 1d, GetFadeDuration(element), RevealEase));

            if (target <= MinimumAnimatedHeight || Math.Abs(target - current) < MinimumAnimatedHeight)
            {
                SettleOpen(element, generation);
                return;
            }

            DoubleAnimation grow = Animate(current, target, GetDuration(element), RevealEase);
            grow.Completed += (_, _) => SettleOpen(element, generation);
            element.BeginAnimation(FrameworkElement.HeightProperty, grow);
        }

        private static void AnimateLinux(FrameworkElement element, int generation, bool open)
        {
            double current = element.ActualHeight;
            double opacity = element.Opacity;
            StopActive(element);
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);

            RenderOptions.SetClearTypeHint(element, ClearTypeHint.Enabled);
            element.UseLayoutRounding = true;
            element.ClipToBounds = true;
            element.Height = current;
            element.Opacity = opacity;

            if (!CanAnimate(element))
            {
                if (open)
                    OpenWithoutAnimation(element);
                else
                    SettleClosed(element, generation);
                return;
            }

            if (!open)
            {
                element.IsHitTestVisible = false;
                if (current <= MinimumAnimatedHeight)
                {
                    double collapseWidth = ResolveWidth(element);
                    double natural = collapseWidth > 0d ? Measure(element, current, collapseWidth) : 0d;
                    if (natural > MinimumAnimatedHeight && element.Visibility == Visibility.Visible)
                    {
                        current = natural;
                        element.Height = current;
                    }
                    else
                    {
                        SettleClosed(element, generation);
                        return;
                    }
                }

                StartNative(element, generation, current, 0d, opacity, 0d, GetDuration(element), GetFadeDuration(element), CollapseEase, () => SettleClosed(element, generation));
                return;
            }

            element.IsHitTestVisible = true;
            double width = ResolveWidth(element);
            if (width <= 0d)
            {
                OpenWithoutAnimation(element);
                return;
            }

            double target = Measure(element, current, width);
            if (target <= MinimumAnimatedHeight || Math.Abs(target - current) < MinimumAnimatedHeight)
            {
                SettleOpen(element, generation);
                return;
            }

            StartNative(element, generation, current, target, opacity, 1d, GetDuration(element), GetFadeDuration(element), RevealEase, () => SettleOpen(element, generation));
        }

        private static void AnimateLegacyLinux(FrameworkElement element, int generation, bool open)
        {
            double current = element.ActualHeight;
            double opacity = element.Opacity;
            StopActive(element);
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.ClipToBounds = true;
            element.Height = current;
            element.Opacity = opacity;

            if (!CanAnimate(element))
            {
                if (open)
                    OpenWithoutAnimation(element);
                else
                    SettleClosed(element, generation);
                return;
            }

            if (!open)
            {
                element.IsHitTestVisible = false;
                if (current <= MinimumAnimatedHeight)
                {
                    SettleClosed(element, generation);
                    return;
                }

                Duration duration = GetDuration(element);
                RenderReady.Run(element, () =>
                {
                    if ((int)element.GetValue(GenerationProperty) != generation)
                        return;

                    Tween.Start(element, generation, current, 0d, opacity, 0d, duration, GetFadeDuration(element), CollapseEase, () => SettleClosed(element, generation));
                });
                return;
            }

            element.IsHitTestVisible = true;
            double width = ResolveWidth(element);
            if (width <= 0d)
            {
                OpenWithoutAnimation(element);
                return;
            }

            double target = Measure(element, current, width);
            if (target <= MinimumAnimatedHeight || Math.Abs(target - current) < MinimumAnimatedHeight)
            {
                SettleOpen(element, generation);
                return;
            }

            Duration reveal = GetDuration(element);
            RenderReady.Run(element, () =>
            {
                if ((int)element.GetValue(GenerationProperty) != generation)
                    return;

                Tween.Start(element, generation, current, target, opacity, 1d, reveal, GetFadeDuration(element), RevealEase, () => SettleOpen(element, generation));
            });
        }

        private static void StartNative(FrameworkElement element, int generation, double fromHeight, double toHeight, double fromOpacity, double toOpacity, Duration heightDuration, Duration fadeDuration, IEasingFunction easing, Action settle)
        {
            DoubleAnimation height = Animate(fromHeight, toHeight, heightDuration, easing);
            DoubleAnimation opacity = Animate(fromOpacity, toOpacity, fadeDuration, easing);
            NativeAnimationState state = new(element, generation, height, opacity, heightDuration, fadeDuration, settle);
            element.SetValue(NativeStateProperty, state);
            state.Start();
        }

        private static bool CanAnimate(FrameworkElement element)
        {
            if (!element.IsLoaded || !element.IsVisible)
                return false;

            Window? window = Window.GetWindow(element);
            return window is null || window.WindowState != WindowState.Minimized;
        }

        private static void StopActive(FrameworkElement element)
        {
            if (element.GetValue(NativeStateProperty) is NativeAnimationState native)
                native.Stop();
            if (element.GetValue(TweenStateProperty) is Tween tween)
                tween.Stop();
        }

        private static void OpenWithoutAnimation(FrameworkElement element)
        {
            StopActive(element);
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Height = double.NaN;
            element.Opacity = 1d;
            element.ClipToBounds = false;
            if (OperatingSystem.IsLinux())
                element.IsHitTestVisible = true;
        }

        private static void SettleOpen(FrameworkElement element, int generation)
        {
            if ((int)element.GetValue(GenerationProperty) != generation || !GetIsOpen(element))
                return;

            StopActive(element);
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Height = double.NaN;
            element.Opacity = 1d;
            element.ClipToBounds = false;
            if (OperatingSystem.IsLinux())
                element.IsHitTestVisible = true;
        }

        private static void SettleClosed(FrameworkElement element, int generation)
        {
            if ((int)element.GetValue(GenerationProperty) != generation || GetIsOpen(element))
                return;

            StopActive(element);
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Height = 0d;
            element.Opacity = 0d;
            if (OperatingSystem.IsLinux())
                element.IsHitTestVisible = false;
        }

        private static double ResolveWidth(FrameworkElement element)
        {
            if (element.ActualWidth > 0d)
                return element.ActualWidth;

            DependencyObject? parent = VisualTreeHelper.GetParent(element);
            for (int depth = 0; depth < 4 && parent is not null; depth++)
            {
                if (parent is FrameworkElement ancestor && ancestor.ActualWidth > 0d)
                    return ancestor.ActualWidth;
                parent = VisualTreeHelper.GetParent(parent);
            }

            return 0d;
        }

        private static double Measure(FrameworkElement element, double restore, double width)
        {
            element.Height = double.NaN;
            element.Measure(new Size(width, double.PositiveInfinity));
            double target = element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom;
            element.Height = restore;
            element.InvalidateMeasure();
            return target > 0d ? target : 0d;
        }

        private static DoubleAnimation Animate(double from, double to, Duration duration, IEasingFunction easing) => new(from, to, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        private static TimeSpan Extent(Duration height, Duration fade)
        {
            TimeSpan heightSpan = height.HasTimeSpan ? height.TimeSpan : TimeSpan.FromMilliseconds(300d);
            TimeSpan fadeSpan = fade.HasTimeSpan ? fade.TimeSpan : TimeSpan.FromMilliseconds(180d);
            return (heightSpan > fadeSpan ? heightSpan : fadeSpan) + TimeSpan.FromMilliseconds(250d);
        }

        private static CubicBezierEase CreateEase(double x1, double y1, double x2, double y2)
        {
            CubicBezierEase easing = new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
            easing.Freeze();
            return easing;
        }

        private sealed class NativeAnimationState
        {
            private readonly FrameworkElement _element;
            private readonly int _generation;
            private readonly DoubleAnimation _height;
            private readonly DoubleAnimation _opacity;
            private readonly DispatcherTimer _watchdog;
            private readonly Action _settle;
            private readonly Window? _window;
            private bool _running;

            internal NativeAnimationState(FrameworkElement element, int generation, DoubleAnimation height, DoubleAnimation opacity, Duration heightDuration, Duration fadeDuration, Action settle)
            {
                _element = element;
                _generation = generation;
                _height = height;
                _opacity = opacity;
                _settle = settle;
                _window = Window.GetWindow(element);
                _watchdog = new DispatcherTimer(DispatcherPriority.Background, element.Dispatcher)
                {
                    Interval = Extent(heightDuration, fadeDuration)
                };
            }

            internal void Start()
            {
                _running = true;
                _height.Completed += OnCompleted;
                _watchdog.Tick += OnWatchdog;
                _element.Unloaded += OnUnavailable;
                _element.IsVisibleChanged += OnVisibilityChanged;
                if (_window is not null)
                    _window.StateChanged += OnWindowStateChanged;
                _watchdog.Start();
                _element.BeginAnimation(FrameworkElement.HeightProperty, _height, HandoffBehavior.SnapshotAndReplace);
                _element.BeginAnimation(UIElement.OpacityProperty, _opacity, HandoffBehavior.SnapshotAndReplace);
            }

            internal void Stop()
            {
                if (!_running)
                    return;

                _running = false;
                _watchdog.Stop();
                _watchdog.Tick -= OnWatchdog;
                _height.Completed -= OnCompleted;
                _element.Unloaded -= OnUnavailable;
                _element.IsVisibleChanged -= OnVisibilityChanged;
                if (_window is not null)
                    _window.StateChanged -= OnWindowStateChanged;
                if (ReferenceEquals(_element.GetValue(NativeStateProperty), this))
                    _element.ClearValue(NativeStateProperty);
            }

            private void OnCompleted(object? sender, EventArgs e) => Complete();

            private void OnWatchdog(object? sender, EventArgs e) => Complete();

            private void OnUnavailable(object sender, RoutedEventArgs e) => Complete();

            private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (e.NewValue is false)
                    Complete();
            }

            private void OnWindowStateChanged(object? sender, EventArgs e)
            {
                if (_window?.WindowState == WindowState.Minimized)
                    Complete();
            }

            private void Complete()
            {
                if (!_running)
                    return;

                Stop();
                if ((int)_element.GetValue(GenerationProperty) == _generation)
                    _settle();
            }
        }

        private sealed class Tween
        {
            private readonly FrameworkElement _element;
            private readonly int _generation;
            private readonly double _fromHeight;
            private readonly double _toHeight;
            private readonly double _fromOpacity;
            private readonly double _toOpacity;
            private readonly double _heightMilliseconds;
            private readonly double _fadeMilliseconds;
            private readonly IEasingFunction _easing;
            private readonly Action _settle;
            private readonly DispatcherTimer _watchdog;
            private readonly long _started;
            private bool _running;

            private Tween(FrameworkElement element, int generation, double fromHeight, double toHeight, double fromOpacity, double toOpacity, Duration height, Duration fade, IEasingFunction easing, Action settle)
            {
                _element = element;
                _generation = generation;
                _fromHeight = fromHeight;
                _toHeight = toHeight;
                _fromOpacity = fromOpacity;
                _toOpacity = toOpacity;
                _heightMilliseconds = height.HasTimeSpan ? height.TimeSpan.TotalMilliseconds : 300d;
                _fadeMilliseconds = fade.HasTimeSpan ? fade.TimeSpan.TotalMilliseconds : 180d;
                _easing = easing;
                _settle = settle;
                _started = Environment.TickCount64;
                _watchdog = new DispatcherTimer(DispatcherPriority.Background, element.Dispatcher)
                {
                    Interval = Extent(height, fade)
                };
            }

            internal static void Start(FrameworkElement element, int generation, double fromHeight, double toHeight, double fromOpacity, double toOpacity, Duration height, Duration fade, IEasingFunction easing, Action settle)
            {
                if (element.GetValue(TweenStateProperty) is Tween running)
                    running.Stop();

                Tween tween = new(element, generation, fromHeight, toHeight, fromOpacity, toOpacity, height, fade, easing, settle);
                element.SetValue(TweenStateProperty, tween);
                tween.Start();
            }

            private void Start()
            {
                _running = true;
                CompositionTarget.Rendering += OnFrame;
                _watchdog.Tick += OnWatchdog;
                _element.Unloaded += OnUnavailable;
                _watchdog.Start();
                Apply(0d);
            }

            internal void Stop()
            {
                if (!_running)
                    return;

                _running = false;
                CompositionTarget.Rendering -= OnFrame;
                _watchdog.Stop();
                _watchdog.Tick -= OnWatchdog;
                _element.Unloaded -= OnUnavailable;
                if (ReferenceEquals(_element.GetValue(TweenStateProperty), this))
                    _element.ClearValue(TweenStateProperty);
            }

            private void OnFrame(object? sender, EventArgs e)
            {
                if ((int)_element.GetValue(GenerationProperty) != _generation)
                {
                    Stop();
                    return;
                }

                double elapsed = Environment.TickCount64 - _started;
                if (elapsed >= _heightMilliseconds)
                {
                    Complete();
                    return;
                }

                Apply(elapsed);
            }

            private void OnWatchdog(object? sender, EventArgs e) => Complete();

            private void OnUnavailable(object sender, RoutedEventArgs e) => Complete();

            private void Complete()
            {
                if (!_running)
                    return;

                Stop();
                if ((int)_element.GetValue(GenerationProperty) == _generation)
                    _settle();
            }

            private void Apply(double elapsed)
            {
                double progress = _heightMilliseconds > 0d ? Math.Min(1d, elapsed / _heightMilliseconds) : 1d;
                double eased = _easing.Ease(progress);
                _element.Height = _fromHeight + ((_toHeight - _fromHeight) * eased);

                double fade = _fadeMilliseconds > 0d ? Math.Min(1d, elapsed / _fadeMilliseconds) : 1d;
                _element.Opacity = _fromOpacity + ((_toOpacity - _fromOpacity) * _easing.Ease(fade));
            }
        }
    }
}
