using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(300))));

        public static readonly DependencyProperty FadeDurationProperty = DependencyProperty.RegisterAttached(
            "FadeDuration",
            typeof(Duration),
            typeof(ExpanderReveal),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(180))));

        private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
            "Generation",
            typeof(int),
            typeof(ExpanderReveal),
            new PropertyMetadata(0));

        public static void SetIsOpen(DependencyObject element, bool value) => element.SetValue(IsOpenProperty, value);

        public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);

        public static void SetDuration(DependencyObject element, Duration value) => element.SetValue(DurationProperty, value);

        public static Duration GetDuration(DependencyObject element) => (Duration)element.GetValue(DurationProperty);

        public static void SetFadeDuration(DependencyObject element, Duration value) => element.SetValue(FadeDurationProperty, value);

        public static Duration GetFadeDuration(DependencyObject element) => (Duration)element.GetValue(FadeDurationProperty);

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
            {
                return;
            }

            int generation = (int)element.GetValue(GenerationProperty) + 1;
            element.SetValue(GenerationProperty, generation);

            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);

            RenderOptions.SetClearTypeHint(element, ClearTypeHint.Enabled);
            element.UseLayoutRounding = true;

            double current = element.ActualHeight;
            double opacity = element.Opacity;
            bool open = (bool)e.NewValue;

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

            double target = Measure(element, current);

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

        private static void OpenWithoutAnimation(FrameworkElement element)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Height = double.NaN;
            element.Opacity = 1d;
            element.ClipToBounds = false;
        }

        private static void SettleOpen(FrameworkElement element, int generation)
        {
            if ((int)element.GetValue(GenerationProperty) != generation || !GetIsOpen(element))
            {
                return;
            }

            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Height = double.NaN;
            element.Opacity = 1d;
            element.ClipToBounds = false;
        }

        private static void SettleClosed(FrameworkElement element, int generation)
        {
            if ((int)element.GetValue(GenerationProperty) != generation || GetIsOpen(element))
            {
                return;
            }

            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Height = 0d;
            element.Opacity = 0d;
        }

        private static double Measure(FrameworkElement element, double restore)
        {
            double width = element.ActualWidth;

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

        private static CubicBezierEase CreateEase(double x1, double y1, double x2, double y2)
        {
            CubicBezierEase easing = new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
            easing.Freeze();
            return easing;
        }
    }
}
