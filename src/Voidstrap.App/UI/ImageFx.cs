using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Voidstrap.UI
{
    public static class ImageFx
    {
        public static readonly DependencyProperty SmoothLoadProperty = DependencyProperty.RegisterAttached(
            "SmoothLoad", typeof(bool), typeof(ImageFx), new PropertyMetadata(false, OnSmoothLoadChanged));

        public static void SetSmoothLoad(DependencyObject element, bool value) => element.SetValue(SmoothLoadProperty, value);

        public static bool GetSmoothLoad(DependencyObject element) => (bool)element.GetValue(SmoothLoadProperty);

        private static readonly DependencyPropertyDescriptor SourceDescriptor =
            DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));

        private static void OnSmoothLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image)
                return;

            if ((bool)e.NewValue)
            {
                SourceDescriptor.AddValueChanged(image, OnSourceChanged);
                image.Unloaded += OnUnloaded;
            }
            else
            {
                Detach(image);
            }
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
                Detach(image);
        }

        private static void Detach(Image image)
        {
            SourceDescriptor.RemoveValueChanged(image, OnSourceChanged);
            image.Unloaded -= OnUnloaded;
        }

        private static void OnSourceChanged(object sender, EventArgs e)
        {
            if (sender is not Image image || image.Source == null)
                return;

            var animation = new DoubleAnimation(0.25, 1.0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            image.BeginAnimation(UIElement.OpacityProperty, animation);
        }
    }
}
