using System;
using System.Windows;

namespace Wpf.Ui.Controls
{
    public static class ExpanderMotion
    {
        public static readonly DependencyProperty IsLinuxProperty = DependencyProperty.RegisterAttached(
            "IsLinux",
            typeof(bool),
            typeof(ExpanderMotion),
            new PropertyMetadata(OperatingSystem.IsLinux()));

        public static readonly DependencyProperty UseLinuxAnimationClockProperty = DependencyProperty.RegisterAttached(
            "UseLinuxAnimationClock",
            typeof(bool),
            typeof(ExpanderMotion),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsLinux(DependencyObject element) => (bool)element.GetValue(IsLinuxProperty);

        public static void SetUseLinuxAnimationClock(DependencyObject element, bool value) => element.SetValue(UseLinuxAnimationClockProperty, value);

        public static bool GetUseLinuxAnimationClock(DependencyObject element) => (bool)element.GetValue(UseLinuxAnimationClockProperty);
    }
}
