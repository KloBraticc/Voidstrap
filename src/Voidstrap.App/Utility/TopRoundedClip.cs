using System;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.Utility
{
    public static class TopRoundedClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(TopRoundedClip),
            new PropertyMetadata(0.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

        public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

        private static void OnRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;
            element.SizeChanged -= Element_SizeChanged;
            if (e.NewValue is double radius && radius > 0 && double.IsFinite(radius))
            {
                element.SizeChanged += Element_SizeChanged;
                Apply(element);
            }
            else
            {
                element.ClearValue(UIElement.ClipProperty);
            }
        }

        private static void Element_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
                Apply(element);
        }

        private static void Apply(FrameworkElement element)
        {
            double width = element.ActualWidth;
            double height = element.ActualHeight;
            if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height))
                return;
            double radius = Math.Min(GetRadius(element), Math.Min(width, height) / 2.0);
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(0, height), true, true);
                context.LineTo(new Point(0, radius), false, false);
                context.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
                context.LineTo(new Point(width - radius, 0), false, false);
                context.ArcTo(new Point(width, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
                context.LineTo(new Point(width, height), false, false);
            }
            geometry.Freeze();
            element.Clip = geometry;
        }
    }
}
