using System.Windows;
using System.Windows.Media;

namespace Voidstrap.Utility;

public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius",
        typeof(double),
        typeof(RoundedClip),
        new PropertyMetadata(0.0, OnRadiusChanged)
    );

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;
        element.SizeChanged -= Element_SizeChanged;
        element.SizeChanged += Element_SizeChanged;
        Apply(element);
    }

    private static void Element_SizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        double radius = GetRadius(element);
        element.Clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
    }
}
