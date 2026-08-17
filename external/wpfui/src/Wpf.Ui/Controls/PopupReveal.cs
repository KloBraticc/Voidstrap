using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wpf.Ui.Controls
{
    public static class PopupReveal
    {
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

            if ((bool)e.NewValue)
            {
                if (child is not null)
                {
                    child.BeginAnimation(UIElement.OpacityProperty, null);
                    child.Opacity = 1d;
                    child.IsHitTestVisible = true;
                }

                SetEffectiveIsOpen(popup, true);
                return;
            }

            if (child is null || !GetEffectiveIsOpen(popup))
            {
                SetEffectiveIsOpen(popup, false);
                return;
            }

            child.IsHitTestVisible = false;

            DoubleAnimation fade = new()
            {
                From = 1d,
                To = 0d,
                Duration = GetCloseDuration(popup),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };

            fade.Completed += (_, _) =>
            {
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

            RectangleGeometry revealClip = new(new Rect(-32d, -32d, width + 64d, target + 64d));
            element.Clip = revealClip;
            element.Opacity = 1d;

            DoubleAnimation fadeIn = new()
            {
                From = 0d,
                To = 1d,
                Duration = GetFadeDuration(element),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };

            element.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            if (target <= 0 || Math.Abs(target - from) < 0.5)
            {
                return;
            }

            Rect collapsed = OpensUpward(element)
                ? new Rect(-32d, target - from, width + 64d, from + 32d)
                : new Rect(-32d, -32d, width + 64d, from + 32d);

            RectAnimation animation = new()
            {
                From = collapsed,
                To = new Rect(-32d, -32d, width + 64d, target + 64d),
                Duration = GetDuration(element),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };

            revealClip.BeginAnimation(RectangleGeometry.RectProperty, animation);
        }

        private const System.Windows.Threading.DispatcherPriority DispatcherPriorityLoaded =
            System.Windows.Threading.DispatcherPriority.Loaded;
    }
}
