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
            new PropertyMetadata(OperatingSystem.IsLinux()));

        public static readonly DropShadowEffect Flyout = CreateWindowsFlyout();

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
