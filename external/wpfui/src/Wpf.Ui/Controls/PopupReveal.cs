using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wpf.Ui.Controls
{
    public static class PopupReveal
    {
        private static readonly IEasingFunction FadeEase = Freeze(new QuarticEase { EasingMode = EasingMode.EaseInOut });

        private static readonly IEasingFunction RevealEase = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

        private static EasingFunctionBase Freeze(EasingFunctionBase ease)
        {
            ease.Freeze();
            return ease;
        }

        public static readonly DependencyProperty CloseTargetProperty = DependencyProperty.RegisterAttached(
            "CloseTarget",
            typeof(bool),
            typeof(PopupReveal),
            new PropertyMetadata(false, OnCloseTargetChanged));

        public static readonly DependencyProperty CloseDurationProperty = DependencyProperty.RegisterAttached(
            "CloseDuration",
            typeof(Duration),
            typeof(PopupReveal),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(150))));

        public static readonly DependencyProperty EffectiveIsOpenProperty = DependencyProperty.RegisterAttached(
            "EffectiveIsOpen",
            typeof(bool),
            typeof(PopupReveal),
            new PropertyMetadata(false));

        private static readonly DependencyProperty CloseGenerationProperty = DependencyProperty.RegisterAttached(
            "CloseGeneration",
            typeof(int),
            typeof(PopupReveal),
            new PropertyMetadata(0));

        private static readonly DependencyProperty CloseStateProperty = DependencyProperty.RegisterAttached(
            "CloseState",
            typeof(CloseState),
            typeof(PopupReveal),
            new PropertyMetadata(null));

        public static void SetCloseTarget(DependencyObject element, bool value) => element.SetValue(CloseTargetProperty, value);

        public static bool GetCloseTarget(DependencyObject element) => (bool)element.GetValue(CloseTargetProperty);

        public static void SetCloseDuration(DependencyObject element, Duration value) => element.SetValue(CloseDurationProperty, value);

        public static Duration GetCloseDuration(DependencyObject element) => (Duration)element.GetValue(CloseDurationProperty);

        public static void SetEffectiveIsOpen(DependencyObject element, bool value) => element.SetValue(EffectiveIsOpenProperty, value);

        public static bool GetEffectiveIsOpen(DependencyObject element) => (bool)element.GetValue(EffectiveIsOpenProperty);

        private static void OnCloseTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Popup popup)
            {
                return;
            }

            int generation = (int)popup.GetValue(CloseGenerationProperty) + 1;
            popup.SetValue(CloseGenerationProperty, generation);

            FrameworkElement child = popup.Child as FrameworkElement;
            CloseState? closeState = OperatingSystem.IsLinux() ? GetCloseState(popup) : null;

            if ((bool)e.NewValue)
            {
                closeState?.Cancel();
                if (child is not null)
                {
                    double current = child.Opacity;
                    bool closing = GetEffectiveIsOpen(popup) && current < 1d;
                    child.BeginAnimation(UIElement.OpacityProperty, null);
                    child.Opacity = 1d;
                    child.IsHitTestVisible = true;
                    if (closing)
                    {
                        child.BeginAnimation(
                            UIElement.OpacityProperty,
                            new DoubleAnimation
                            {
                                From = current,
                                To = 1d,
                                Duration = GetFadeDuration(child),
                                EasingFunction = FadeEase,
                                FillBehavior = FillBehavior.Stop
                            });
                    }
                }

                SetEffectiveIsOpen(popup, true);
                closeState?.BeginOpen(generation, child);
                return;
            }

            if (child is null || !GetEffectiveIsOpen(popup))
            {
                closeState?.SetClosed(generation);
                SetEffectiveIsOpen(popup, false);
                return;
            }

            child.IsHitTestVisible = false;

            DoubleAnimation fade = new()
            {
                From = child.Opacity,
                To = 0d,
                Duration = GetCloseDuration(popup),
                EasingFunction = FadeEase,
                FillBehavior = FillBehavior.HoldEnd
            };

            fade.Completed += (_, _) =>
            {
                if (closeState is not null)
                {
                    CompleteClose(popup, child, closeState, generation);
                    return;
                }

                if ((int)popup.GetValue(CloseGenerationProperty) != generation)
                {
                    return;
                }

                SetEffectiveIsOpen(popup, false);
                child.BeginAnimation(UIElement.OpacityProperty, null);
                child.Opacity = 1d;
                child.IsHitTestVisible = true;
            };

            child.BeginAnimation(UIElement.OpacityProperty, fade);
            if (closeState is not null)
            {
                closeState.BeginClose(generation, () => CompleteClose(popup, child, closeState, generation));
            }
        }

        public static readonly DependencyProperty FromHeightProperty = DependencyProperty.RegisterAttached(
            "FromHeight",
            typeof(double),
            typeof(PopupReveal),
            new PropertyMetadata(double.NaN, OnFromHeightChanged));

        public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached(
            "Duration",
            typeof(Duration),
            typeof(PopupReveal),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(667))));

        public static readonly DependencyProperty MaximumHeightProperty = DependencyProperty.RegisterAttached(
            "MaximumHeight",
            typeof(double),
            typeof(PopupReveal),
            new PropertyMetadata(double.PositiveInfinity));

        public static readonly DependencyProperty FadeDurationProperty = DependencyProperty.RegisterAttached(
            "FadeDuration",
            typeof(Duration),
            typeof(PopupReveal),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(70))));

        public static void SetFadeDuration(DependencyObject element, Duration value) => element.SetValue(FadeDurationProperty, value);

        public static Duration GetFadeDuration(DependencyObject element) => (Duration)element.GetValue(FadeDurationProperty);

        public static void SetFromHeight(DependencyObject element, double value) => element.SetValue(FromHeightProperty, value);

        public static double GetFromHeight(DependencyObject element) => (double)element.GetValue(FromHeightProperty);

        public static void SetDuration(DependencyObject element, Duration value) => element.SetValue(DurationProperty, value);

        public static Duration GetDuration(DependencyObject element) => (Duration)element.GetValue(DurationProperty);

        public static void SetMaximumHeight(DependencyObject element, double value) => element.SetValue(MaximumHeightProperty, value);

        public static double GetMaximumHeight(DependencyObject element) => (double)element.GetValue(MaximumHeightProperty);

        private static void OnFromHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
            {
                return;
            }

            element.IsVisibleChanged -= OnIsVisibleChanged;

            if (!double.IsNaN((double)e.NewValue))
            {
                element.IsVisibleChanged += OnIsVisibleChanged;
            }
        }

        private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            element.BeginAnimation(UIElement.OpacityProperty, null);

            if (!element.IsVisible)
            {
                element.Clip = null;
                element.Opacity = 1d;
                return;
            }

            element.Opacity = 0d;

            _ = element.Dispatcher.BeginInvoke(
                DispatcherPriorityLoaded,
                new Action(() => Reveal(element)));
        }

        private static bool OpensUpward(FrameworkElement element)
        {
            DependencyObject current = element;
            Popup popup = null;

            for (int depth = 0; depth < 8 && current is not null; depth++)
            {
                if (current is Popup found)
                {
                    popup = found;
                    break;
                }

                current = LogicalTreeHelper.GetParent(current);
            }

            if (popup?.PlacementTarget is not UIElement target)
            {
                return false;
            }

            if (!element.IsVisible || !target.IsVisible)
            {
                return false;
            }

            try
            {
                Point elementTop = element.PointToScreen(new Point(0d, 0d));
                Point targetTop = target.PointToScreen(new Point(0d, 0d));
                return elementTop.Y + 1d < targetTop.Y;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void Reveal(FrameworkElement element)
        {
            if (!element.IsVisible)
            {
                return;
            }

            element.Opacity = 1d;
            if (!OperatingSystem.IsWindows())
                element.UpdateLayout();

            double from = GetFromHeight(element);
            double maximum = GetMaximumHeight(element);
            double width = element.ActualWidth;
            double target = element.ActualHeight;

            if (width <= 0 || target <= 0)
            {
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = element.DesiredSize.Width;
                target = element.DesiredSize.Height;
            }

            target = double.IsInfinity(maximum) ? target : Math.Min(target, maximum);
            from = Math.Min(from, target);

            Rect expanded = new(-32d, -32d, width + 64d, target + 64d);
            RectangleGeometry revealClip = new(expanded);
            element.Clip = revealClip;

            DoubleAnimation fadeIn = new()
            {
                From = 0d,
                To = 1d,
                Duration = GetFadeDuration(element),
                EasingFunction = FadeEase,
                FillBehavior = FillBehavior.Stop
            };

            RectAnimation? animation = null;
            if (target > 0 && Math.Abs(target - from) >= 0.5)
            {
                Rect collapsed = OpensUpward(element)
                    ? new Rect(-32d, target - from, width + 64d, from + 32d)
                    : new Rect(-32d, -32d, width + 64d, from + 32d);
                revealClip.Rect = collapsed;
                animation = new RectAnimation
                {
                    From = collapsed,
                    To = expanded,
                    Duration = GetDuration(element),
                    EasingFunction = RevealEase,
                    FillBehavior = FillBehavior.Stop
                };
            }

            if (!OperatingSystem.IsWindows())
            {
                StartReveal(element, revealClip, fadeIn, animation, expanded);
                return;
            }

            element.Opacity = PrerenderOpacity;
            fadeIn.From = PrerenderOpacity;
            new FirstFrameReveal(element, revealClip, fadeIn, animation, expanded).Arm();
        }

        private static void StartReveal(FrameworkElement element, RectangleGeometry revealClip, DoubleAnimation fadeIn, RectAnimation? animation, Rect expanded)
        {
            element.Opacity = 1d;
            element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            if (animation is null)
            {
                if (ReferenceEquals(element.Clip, revealClip))
                    element.Clip = null;
                return;
            }

            revealClip.Rect = expanded;
            animation.Completed += (_, _) =>
            {
                if (ReferenceEquals(element.Clip, revealClip))
                    element.Clip = null;
            };
            revealClip.BeginAnimation(RectangleGeometry.RectProperty, animation);
        }

        private sealed class FirstFrameReveal
        {
            private readonly FrameworkElement _element;
            private readonly RectangleGeometry _clip;
            private readonly DoubleAnimation _fadeIn;
            private readonly RectAnimation? _animation;
            private readonly Rect _expanded;
            private int _frames;

            internal FirstFrameReveal(FrameworkElement element, RectangleGeometry clip, DoubleAnimation fadeIn, RectAnimation? animation, Rect expanded)
            {
                _element = element;
                _clip = clip;
                _fadeIn = fadeIn;
                _animation = animation;
                _expanded = expanded;
            }

            internal void Arm() => CompositionTarget.Rendering += OnRendering;

            private void OnRendering(object? sender, EventArgs e)
            {
                if (++_frames < 2)
                {
                    return;
                }

                CompositionTarget.Rendering -= OnRendering;
                if (!_element.IsVisible || !ReferenceEquals(_element.Clip, _clip))
                {
                    return;
                }

                StartReveal(_element, _clip, _fadeIn, _animation, _expanded);
            }
        }

        private const double PrerenderOpacity = 0.004d;

        private const System.Windows.Threading.DispatcherPriority DispatcherPriorityLoaded =
            System.Windows.Threading.DispatcherPriority.Loaded;

        private static TimeSpan GetCloseWatchdogDuration(Popup popup)
        {
            Duration duration = GetCloseDuration(popup);
            double milliseconds = duration.HasTimeSpan ? duration.TimeSpan.TotalMilliseconds : 150d;
            return TimeSpan.FromMilliseconds(Math.Max(500d, milliseconds + 100d));
        }

        private static CloseState GetCloseState(Popup popup)
        {
            if (popup.GetValue(CloseStateProperty) is CloseState state)
            {
                return state;
            }

            state = new CloseState(popup);
            popup.SetValue(CloseStateProperty, state);
            return state;
        }

        private static void CompleteClose(Popup popup, FrameworkElement child, CloseState state, int generation)
        {
            if ((int)popup.GetValue(CloseGenerationProperty) != generation)
            {
                return;
            }

            state.Cancel();
            SetEffectiveIsOpen(popup, false);
            child.BeginAnimation(UIElement.OpacityProperty, null);
            child.Opacity = 1d;
            child.IsHitTestVisible = true;
        }

        private sealed class CloseState
        {
            private static readonly TimeSpan OpenWatchdogDuration = TimeSpan.FromMilliseconds(500);

            private readonly Popup _popup;
            private readonly Dispatcher _dispatcher;
            private DispatcherTimer? _timer;
            private Action? _completion;
            private int _generation;
            private int _repairCount;
            private bool _desiredOpen;
            private bool _reconcileQueued;

            internal CloseState(Popup popup)
            {
                _popup = popup;
                _dispatcher = popup.Dispatcher;
                _popup.Opened += OnOpened;
                _popup.Closed += OnClosed;
                _popup.Unloaded += OnUnloaded;
            }

            internal void BeginOpen(int generation, FrameworkElement? child)
            {
                Cancel();
                _generation = generation;
                _repairCount = 0;
                _desiredOpen = true;
                RestoreChild(child);
                EnsureOpen();
                Schedule(OpenWatchdogDuration, VerifyOpen);
            }

            internal void BeginClose(int generation, Action completion)
            {
                Cancel();
                _generation = generation;
                _desiredOpen = false;
                Schedule(GetCloseWatchdogDuration(_popup), completion);
            }

            internal void SetClosed(int generation)
            {
                Cancel();
                _generation = generation;
                _desiredOpen = false;
            }

            private void Schedule(TimeSpan interval, Action completion)
            {
                Cancel();
                _completion = completion;
                _timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
                {
                    Interval = interval
                };
                _timer.Tick += OnTick;
                _timer.Start();
            }

            internal void Cancel()
            {
                if (_timer is not null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTick;
                    _timer = null;
                }

                _completion = null;
            }

            private void EnsureOpen()
            {
                if (!_desiredOpen || !GetCloseTarget(_popup))
                {
                    return;
                }

                FrameworkElement? child = _popup.Child as FrameworkElement;
                RestoreChild(child);
                if (!GetEffectiveIsOpen(_popup))
                {
                    SetEffectiveIsOpen(_popup, true);
                }

                if (_popup.IsOpen)
                {
                    return;
                }

                SetEffectiveIsOpen(_popup, false);
                SetEffectiveIsOpen(_popup, true);
                if (!_popup.IsOpen)
                {
                    _popup.SetCurrentValue(Popup.IsOpenProperty, true);
                }
            }

            private void VerifyOpen()
            {
                if (!_desiredOpen || !GetCloseTarget(_popup))
                {
                    return;
                }

                FrameworkElement? child = _popup.Child as FrameworkElement;
                bool presented = child is not null
                    && PresentationSource.FromVisual(child) is not null
                    && child.ActualWidth > 0d
                    && child.ActualHeight > 0d;
                if (_popup.IsOpen && GetEffectiveIsOpen(_popup) && presented)
                {
                    RestoreChild(child);
                    return;
                }

                if (_repairCount++ == 0)
                {
                    EnsureOpen();
                    Schedule(OpenWatchdogDuration, VerifyOpen);
                    return;
                }

                _desiredOpen = false;
                SetEffectiveIsOpen(_popup, false);
                if (_popup.PlacementTarget is ComboBox comboBox && comboBox.IsDropDownOpen)
                {
                    comboBox.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
                }
            }

            private void OnOpened(object? sender, EventArgs e)
            {
                if (!_desiredOpen || !GetCloseTarget(_popup))
                {
                    return;
                }

                RestoreChild(_popup.Child as FrameworkElement);
                Schedule(OpenWatchdogDuration, VerifyOpen);
            }

            private void OnClosed(object? sender, EventArgs e)
            {
                if (!_desiredOpen || !GetCloseTarget(_popup) || _reconcileQueued)
                {
                    return;
                }

                _reconcileQueued = true;
                int generation = _generation;
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    _reconcileQueued = false;
                    if (_desiredOpen && generation == _generation && GetCloseTarget(_popup))
                    {
                        EnsureOpen();
                    }
                }));
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                if (_desiredOpen
                    && GetCloseTarget(_popup)
                    && _popup.PlacementTarget is FrameworkElement target
                    && target.IsLoaded
                    && target.IsVisible)
                {
                    OnClosed(sender, EventArgs.Empty);
                    return;
                }

                _desiredOpen = false;
                Cancel();
                SetEffectiveIsOpen(_popup, false);
            }

            private static void RestoreChild(FrameworkElement? child)
            {
                if (child is null)
                {
                    return;
                }

                child.BeginAnimation(UIElement.OpacityProperty, null);
                child.Opacity = 1d;
                child.IsHitTestVisible = true;
            }

            private void OnTick(object? sender, EventArgs e)
            {
                Action? completion = _completion;
                Cancel();
                completion?.Invoke();
            }
        }
    }
}
