using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Hardware;

namespace Wpf.Ui.Animations
{
    public static class AnimationState
    {
        public static bool IsLoading { get; set; }
    }

    public static class Transitions
    {
        private const int MinDuration = 250;
        private const int MaxDuration = 2000;
        private static readonly IEasingFunction FadeEase = CreateEase();
        private static readonly IEasingFunction MoveEase = CreateEase();
        private static readonly DependencyProperty IsTransitionTransformProperty =
            DependencyProperty.RegisterAttached(
                "IsTransitionTransform",
                typeof(bool),
                typeof(Transitions),
                new PropertyMetadata(false));
        private static readonly DependencyProperty AnimationGenerationProperty =
            DependencyProperty.RegisterAttached(
                "AnimationGeneration",
                typeof(long),
                typeof(Transitions),
                new PropertyMetadata(0L));

        public static bool ApplyTransition(object element, TransitionType type, int duration)
        {
            if (type == TransitionType.None ||
                element is not FrameworkElement frameworkElement ||
                AnimationState.IsLoading ||
                HardwareAcceleration.AnimationsDisabled ||
                !frameworkElement.Dispatcher.CheckAccess() ||
                frameworkElement.Dispatcher.HasShutdownStarted ||
                frameworkElement.Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            duration = Math.Clamp(duration, MinDuration, MaxDuration);

            try
            {
                switch (type)
                {
                    case TransitionType.FadeIn:
                        FadeIn(frameworkElement, duration);
                        return true;
                    case TransitionType.SlideBottom:
                        Slide(frameworkElement, 0, 18, duration, false);
                        return true;
                    case TransitionType.SlideRight:
                        Slide(frameworkElement, 16, 0, duration, false);
                        return true;
                    case TransitionType.SlideLeft:
                        Slide(frameworkElement, -16, 0, duration, false);
                        return true;
                    case TransitionType.FadeInWithSlide:
                        Slide(frameworkElement, 0, 18, duration, true);
                        return true;
                    case TransitionType.FadeInWithSlideRight:
                        Slide(frameworkElement, 16, 0, duration, true);
                        return true;
                    case TransitionType.FadeInWithSlideLeft:
                        Slide(frameworkElement, -16, 0, duration, true);
                        return true;
                    default:
                        return false;
                }
            }
            catch (InvalidOperationException)
            {
                Reset(element: frameworkElement);
                return false;
            }
            catch (ArgumentException)
            {
                Reset(element: frameworkElement);
                return false;
            }
        }

        private static void FadeIn(FrameworkElement element, int durationMilliseconds)
        {
            ResetAnimation(element, UIElement.OpacityProperty, 1);
            Animate(element, UIElement.OpacityProperty, 0, 1, durationMilliseconds, FadeEase);
        }

        private static void Slide(
            FrameworkElement element,
            double offsetX,
            double offsetY,
            int durationMilliseconds,
            bool fade)
        {
            TranslateTransform transform = GetTransitionTransform(element);
            ResetAnimation(transform, TranslateTransform.XProperty, 0);
            ResetAnimation(transform, TranslateTransform.YProperty, 0);
            ResetAnimation(element, UIElement.OpacityProperty, 1);

            if (offsetX != 0)
                Animate(transform, TranslateTransform.XProperty, offsetX, 0, durationMilliseconds, MoveEase);

            if (offsetY != 0)
                Animate(transform, TranslateTransform.YProperty, offsetY, 0, durationMilliseconds, MoveEase);

            if (fade)
                Animate(element, UIElement.OpacityProperty, 0, 1, durationMilliseconds, FadeEase);
        }

        private static void Animate(
            Animatable target,
            DependencyProperty property,
            double from,
            double to,
            int durationMilliseconds,
            IEasingFunction easingFunction)
        {
            DoubleAnimation animation = new(from, to, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = easingFunction,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animation, null);

            long generation = NextGeneration(target);
            WeakReference<Animatable> weakTarget = new(target);
            AnimationClock clock = (AnimationClock)animation.CreateClock(true);
            EventHandler? completed = null;
            completed = (_, _) =>
            {
                clock.Completed -= completed;
                try
                {
                    if (weakTarget.TryGetTarget(out Animatable? current) &&
                        (long)current.GetValue(AnimationGenerationProperty) == generation)
                    {
                        current.ApplyAnimationClock(property, null);
                    }
                }
                finally
                {
                    clock.Controller?.Remove();
                }
            };
            clock.Completed += completed;
            target.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        }

        private static void Animate(
            UIElement target,
            DependencyProperty property,
            double from,
            double to,
            int durationMilliseconds,
            IEasingFunction easingFunction)
        {
            DoubleAnimation animation = new(from, to, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = easingFunction,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animation, null);

            long generation = NextGeneration(target);
            WeakReference<UIElement> weakTarget = new(target);
            EventHandler? completed = null;
            AnimationClock clock = (AnimationClock)animation.CreateClock(true);
            completed = (_, _) =>
            {
                clock.Completed -= completed;
                try
                {
                    if (weakTarget.TryGetTarget(out UIElement? current) &&
                        (long)current.GetValue(AnimationGenerationProperty) == generation)
                    {
                        current.ApplyAnimationClock(property, null);
                    }
                }
                finally
                {
                    clock.Controller?.Remove();
                }
            };
            clock.Completed += completed;
            target.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        }

        private static void ResetAnimation(Animatable target, DependencyProperty property, double value)
        {
            NextGeneration(target);
            target.ApplyAnimationClock(property, null);
            target.SetCurrentValue(property, value);
        }

        private static void ResetAnimation(UIElement target, DependencyProperty property, double value)
        {
            NextGeneration(target);
            target.ApplyAnimationClock(property, null);
            target.SetCurrentValue(property, value);
        }

        private static long NextGeneration(DependencyObject target)
        {
            long generation = unchecked((long)target.GetValue(AnimationGenerationProperty) + 1);
            target.SetValue(AnimationGenerationProperty, generation);
            return generation;
        }

        private static void Reset(FrameworkElement element)
        {
            ResetAnimation(element, UIElement.OpacityProperty, 1);

            if (FindTransitionTransform(element.RenderTransform) is not TranslateTransform transform || transform.IsFrozen)
                return;

            ResetAnimation(transform, TranslateTransform.XProperty, 0);
            ResetAnimation(transform, TranslateTransform.YProperty, 0);
        }

        private static TranslateTransform GetTransitionTransform(FrameworkElement element)
        {
            if (FindTransitionTransform(element.RenderTransform) is TranslateTransform existing && !existing.IsFrozen)
                return existing;

            TranslateTransform transform = new();
            transform.SetValue(IsTransitionTransformProperty, true);

            TransformGroup group = new();
            if (element.RenderTransform is Transform current)
                group.Children.Add(current);
            group.Children.Add(transform);
            element.SetCurrentValue(UIElement.RenderTransformProperty, group);
            return transform;
        }

        private static TranslateTransform? FindTransitionTransform(Transform? root)
        {
            if (root is TranslateTransform transform &&
                (bool)transform.GetValue(IsTransitionTransformProperty))
            {
                return transform;
            }

            if (root is not TransformGroup group)
                return null;

            foreach (Transform child in group.Children)
            {
                if (child is TranslateTransform candidate &&
                    (bool)candidate.GetValue(IsTransitionTransformProperty))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEasingFunction CreateEase()
        {
            CubicEase easing = new() { EasingMode = EasingMode.EaseOut };
            easing.Freeze();
            return easing;
        }
    }
}
