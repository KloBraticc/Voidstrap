using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Wpf.Ui.Controls
{
    public static class PortableShadows
    {
        public static readonly DependencyProperty IsPortableProperty = DependencyProperty.RegisterAttached(
            "IsPortable",
            typeof(bool),
            typeof(PortableShadows),
            new PropertyMetadata(Wpf.Ui.Animations.PortableRenderer.IsActive));

        public static readonly DependencyProperty LayeredProperty = DependencyProperty.RegisterAttached(
            "Layered",
            typeof(bool),
            typeof(PortableShadows),
            new PropertyMetadata(false, OnLayeredChanged));

        public static readonly DropShadowEffect Flyout = CreateWindowsFlyout();

        public static bool GetLayered(DependencyObject element) => (bool)element.GetValue(LayeredProperty);

        public static void SetLayered(DependencyObject element, bool value) => element.SetValue(LayeredProperty, value);

        private static void OnLayeredChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true && d is FrameworkElement element)
                PortableShadow.Attach(element);
        }

        public static bool GetIsPortable(DependencyObject element) => (bool)element.GetValue(IsPortableProperty);

        public static void SetIsPortable(DependencyObject element, bool value) => element.SetValue(IsPortableProperty, value);

        private static DropShadowEffect CreateWindowsFlyout()
        {
            DropShadowEffect effect = new()
            {
                BlurRadius = 24d,
                ShadowDepth = 4d,
                Opacity = 0.35d,
                Direction = 270d,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance
            };
            effect.Freeze();
            return effect;
        }
    }
}
