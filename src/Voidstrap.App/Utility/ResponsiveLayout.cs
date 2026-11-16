using System.Windows;
using System.Windows.Controls.Primitives;

namespace Voidstrap.Utility
{
    public static class ResponsiveLayout
    {
        public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.RegisterAttached(
            "MinItemWidth",
            typeof(double),
            typeof(ResponsiveLayout),
            new PropertyMetadata(0.0, OnMinItemWidthChanged));

        public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.RegisterAttached(
            "AspectRatio",
            typeof(double),
            typeof(ResponsiveLayout),
            new PropertyMetadata(0.0, OnAspectRatioChanged));

        public static double GetMinItemWidth(DependencyObject element) => (double)element.GetValue(MinItemWidthProperty);

        public static void SetMinItemWidth(DependencyObject element, double value) => element.SetValue(MinItemWidthProperty, value);

        public static double GetAspectRatio(DependencyObject element) => (double)element.GetValue(AspectRatioProperty);

        public static void SetAspectRatio(DependencyObject element, double value) => element.SetValue(AspectRatioProperty, value);

        private static void OnMinItemWidthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not UniformGrid grid)
                return;
            grid.SizeChanged -= Grid_SizeChanged;
            if (e.NewValue is double width && width > 0 && double.IsFinite(width))
            {
                grid.SizeChanged += Grid_SizeChanged;
                ApplyColumns(grid);
            }
        }

        private static void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged && sender is UniformGrid grid)
                ApplyColumns(grid);
        }

        private static void ApplyColumns(UniformGrid grid)
        {
            double width = grid.ActualWidth;
            if (width <= 0)
                return;
            int columns = Math.Max(1, (int)(width / GetMinItemWidth(grid)));
            if (grid.Columns != columns)
                grid.Columns = columns;
        }

        private static void OnAspectRatioChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;
            element.SizeChanged -= Element_SizeChanged;
            if (e.NewValue is double ratio && ratio > 0 && double.IsFinite(ratio))
            {
                element.SizeChanged += Element_SizeChanged;
                ApplyHeight(element);
            }
        }

        private static void Element_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged && sender is FrameworkElement element)
                ApplyHeight(element);
        }

        private static void ApplyHeight(FrameworkElement element)
        {
            double width = element.ActualWidth;
            if (width <= 0)
                return;
            double height = Math.Round(width / GetAspectRatio(element));
            if (Math.Abs(element.Height - height) >= 1 || double.IsNaN(element.Height))
                element.Height = height;
        }
    }
}
