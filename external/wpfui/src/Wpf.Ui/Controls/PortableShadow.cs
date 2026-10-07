using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wpf.Ui.Controls
{
    public sealed class PortableShadow : FrameworkElement
    {
        private const int Layers = 14;

        private const double Spread = 16d;

        private const double OffsetY = 4d;

        private static readonly SolidColorBrush LayerBrush = CreateLayerBrush();

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(PortableShadow),
            new FrameworkPropertyMetadata(new CornerRadius(8d), FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly DependencyProperty AttachedShadowProperty = DependencyProperty.RegisterAttached(
            "AttachedShadow",
            typeof(PortableShadow),
            typeof(PortableShadow),
            new PropertyMetadata(null));

        public PortableShadow()
        {
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = false;
        }

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static bool IsSupported => Wpf.Ui.Animations.PortableRenderer.IsActive && OperatingSystem.IsMacOS();

        internal static void Attach(FrameworkElement target)
        {
            if (!IsSupported || target.GetValue(AttachedShadowProperty) is not null)
                return;
            target.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Insert(target)));
        }

        private static void Insert(FrameworkElement target)
        {
            if (target.GetValue(AttachedShadowProperty) is not null || VisualTreeHelper.GetParent(target) is not Panel panel)
                return;
            int index = panel.Children.IndexOf(target);
            if (index < 0)
                return;

            PortableShadow shadow = new()
            {
                CornerRadius = target is Border border ? border.CornerRadius : new CornerRadius(8d),
                HorizontalAlignment = target.HorizontalAlignment,
                VerticalAlignment = target.VerticalAlignment
            };
            Grid.SetRow(shadow, Grid.GetRow(target));
            Grid.SetColumn(shadow, Grid.GetColumn(target));
            Grid.SetRowSpan(shadow, Grid.GetRowSpan(target));
            Grid.SetColumnSpan(shadow, Grid.GetColumnSpan(target));
            Bind(shadow, MarginProperty, target, MarginProperty);
            Bind(shadow, OpacityProperty, target, OpacityProperty);
            Bind(shadow, VisibilityProperty, target, VisibilityProperty);
            Bind(shadow, RenderTransformProperty, target, RenderTransformProperty);
            Bind(shadow, RenderTransformOriginProperty, target, RenderTransformOriginProperty);
            target.SetValue(AttachedShadowProperty, shadow);
            panel.Children.Insert(index, shadow);
        }

        private static void Bind(FrameworkElement shadow, DependencyProperty property, FrameworkElement source, DependencyProperty sourceProperty)
        {
            shadow.SetBinding(property, new Binding(sourceProperty.Name) { Source = source, Mode = BindingMode.OneWay });
        }

        protected override Size MeasureOverride(Size availableSize) => new(0d, 0d);

        protected override Size ArrangeOverride(Size finalSize) => finalSize;

        protected override void OnRender(DrawingContext drawingContext)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0d || height <= 0d)
                return;

            CornerRadius corners = CornerRadius;
            double radius = Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight));
            RectangleGeometry inner = new(new Rect(0d, 0d, width, height), radius, radius);
            inner.Freeze();
            for (int layer = Layers; layer >= 1; layer--)
            {
                double inflate = Spread * layer / Layers;
                GeometryGroup ring = new() { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new RectangleGeometry(new Rect(-inflate, -inflate + OffsetY, width + inflate * 2d, height + inflate * 2d), radius + inflate, radius + inflate));
                ring.Children.Add(inner);
                ring.Freeze();
                drawingContext.DrawGeometry(LayerBrush, null, ring);
            }
        }

        private static SolidColorBrush CreateLayerBrush()
        {
            SolidColorBrush brush = new(Color.FromArgb(7, 0, 0, 0));
            brush.Freeze();
            return brush;
        }
    }
}
