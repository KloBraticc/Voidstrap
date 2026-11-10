using System.Windows;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Controls;

public class FillWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(FillWrapPanel),
        new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure)
    );

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = GetColumns(availableSize.Width);
        double itemWidth = double.IsInfinity(availableSize.Width) ? MinItemWidth : availableSize.Width / columns;
        double height = 0;
        double rowHeight = 0;
        int column = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++column == columns)
            {
                height += rowHeight;
                rowHeight = 0;
                column = 0;
            }
        }
        return new Size(itemWidth * columns, height + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = GetColumns(finalSize.Width);
        double itemWidth = finalSize.Width / columns;
        List<UIElement> row = new(columns);
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            row.Add(child);
            if (row.Count == columns)
                y += ArrangeRow(row, itemWidth, y);
        }
        ArrangeRow(row, itemWidth, y);
        return finalSize;
    }

    private static double ArrangeRow(List<UIElement> row, double itemWidth, double y)
    {
        double height = 0;
        foreach (UIElement child in row)
            height = Math.Max(height, child.DesiredSize.Height);
        for (int i = 0; i < row.Count; i++)
            row[i].Arrange(new Rect(i * itemWidth, y, itemWidth, height));
        row.Clear();
        return height;
    }

    private int GetColumns(double width)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || MinItemWidth <= 0)
            return Math.Max(1, InternalChildren.Count);
        return Math.Max(1, (int)(width / MinItemWidth));
    }
}
