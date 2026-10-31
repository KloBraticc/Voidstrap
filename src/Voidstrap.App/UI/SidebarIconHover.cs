using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using FontFamily = System.Windows.Media.FontFamily;
using Path = System.Windows.Shapes.Path;

namespace Voidstrap.UI;

internal sealed class SidebarIconHover
{
    private const int PointsPerContour = 96;

    private const double DurationMs = 280;

    private sealed class Morph
    {
        public required SymbolIcon Icon { get; init; }

        public required Panel Host { get; init; }

        public required Canvas Surface { get; init; }

        public required Path Shape { get; init; }

        public required List<Point[]> From { get; set; }

        public required List<Point[]> To { get; set; }

        public required List<Point[]> Current { get; set; }

        public required Stopwatch Clock { get; set; }
    }

    private readonly Dictionary<NavigationItem, Morph> _playing = [];

    private bool _rendering;

    public void Change(NavigationItem item, SymbolRegular symbol)
    {
        SymbolRegular previous = item.Icon;
        if (previous == symbol)
            return;
        SymbolIcon? icon = item.Template?.FindName("ItemIcon", item) as SymbolIcon;
        List<Point[]>? source = null;
        if (_playing.TryGetValue(item, out Morph? running))
            source = running.Current;
        else if (icon is { IsVisible: true })
            source = Outline(icon);
        item.Icon = symbol;
        List<Point[]>? target = icon is { IsVisible: true } ? Outline(icon) : null;
        if (icon == null || source == null || target == null || source.Count == 0 || target.Count == 0)
        {
            Finish(item);
            return;
        }
        (List<Point[]> from, List<Point[]> to) = Match(source, target);
        if (running != null)
        {
            running.From = from;
            running.To = to;
            running.Current = from;
            running.Clock = Stopwatch.StartNew();
            return;
        }
        if (icon.Parent is not Panel host)
            return;
        Path shape = new() { Fill = icon.Foreground, IsHitTestVisible = false };
        Canvas surface = new()
        {
            Width = icon.ActualWidth,
            Height = icon.ActualHeight,
            Margin = icon.Margin,
            HorizontalAlignment = icon.HorizontalAlignment,
            VerticalAlignment = icon.VerticalAlignment,
            IsHitTestVisible = false,
        };
        surface.Children.Add(shape);
        Grid.SetColumn(surface, Grid.GetColumn(icon));
        Grid.SetRow(surface, Grid.GetRow(icon));
        host.Children.Add(surface);
        shape.Data = BuildGeometry(from);
        icon.Opacity = 0;
        _playing[item] = new Morph
        {
            Icon = icon,
            Host = host,
            Surface = surface,
            Shape = shape,
            From = from,
            To = to,
            Current = from,
            Clock = Stopwatch.StartNew(),
        };
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public void Stop()
    {
        foreach (NavigationItem item in _playing.Keys.ToArray())
            Finish(item);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        foreach ((NavigationItem item, Morph morph) in _playing.ToArray())
        {
            double t = Math.Min(1, morph.Clock.Elapsed.TotalMilliseconds / DurationMs);
            if (t >= 1)
            {
                Finish(item);
                continue;
            }
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            morph.Current = Blend(morph.From, morph.To, eased);
            morph.Shape.Data = BuildGeometry(morph.Current);
        }
    }

    private void Finish(NavigationItem item)
    {
        if (_playing.Remove(item, out Morph? morph))
        {
            morph.Host.Children.Remove(morph.Surface);
            morph.Icon.Opacity = 1;
        }
        if (_playing.Count == 0 && _rendering)
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static List<Point[]>? Outline(SymbolIcon icon)
    {
        icon.ApplyTemplate();
        ContentPresenter? presenter = icon.Template?.FindName("ContentPresenter", icon) as ContentPresenter;
        if (presenter == null || string.IsNullOrEmpty(icon.RawSymbol))
            return null;
        presenter.UpdateLayout();
        Point origin = presenter.TranslatePoint(new Point(0, 0), icon);
        FormattedText text = new(
            icon.RawSymbol,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement_GetFamily(presenter), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            icon.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(icon).PixelsPerDip
        );
        Geometry glyph = text.BuildGeometry(origin);
        List<Point[]> contours = [];
        foreach (PathFigure figure in glyph.GetFlattenedPathGeometry(0.02, ToleranceType.Absolute).Figures)
        {
            List<Point> points = [figure.StartPoint];
            foreach (PathSegment segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly)
                    points.AddRange(poly.Points);
                else if (segment is LineSegment line)
                    points.Add(line.Point);
            }
            if (points.Count >= 3)
                contours.Add(Resample(points));
        }
        return contours.OrderByDescending(contour => Math.Abs(Area(contour))).ToList();
    }

    private static FontFamily TextElement_GetFamily(ContentPresenter presenter) => System.Windows.Documents.TextElement.GetFontFamily(presenter);

    private static Point[] Resample(List<Point> points)
    {
        List<Point> ring = [.. points];
        if (ring[0] != ring[^1])
            ring.Add(ring[0]);
        double total = 0;
        for (int i = 1; i < ring.Count; i++)
            total += (ring[i] - ring[i - 1]).Length;
        Point[] result = new Point[PointsPerContour];
        int index = 1;
        double walked = 0;
        for (int i = 0; i < PointsPerContour; i++)
        {
            double wanted = total * i / PointsPerContour;
            while (index < ring.Count - 1 && walked + (ring[index] - ring[index - 1]).Length < wanted)
            {
                walked += (ring[index] - ring[index - 1]).Length;
                index++;
            }
            double length = (ring[index] - ring[index - 1]).Length;
            double along = length > 0 ? (wanted - walked) / length : 0;
            result[i] = ring[index - 1] + (ring[index] - ring[index - 1]) * along;
        }
        return Area(result) < 0 ? [.. result.Reverse()] : result;
    }

    private static double Area(Point[] contour)
    {
        double sum = 0;
        for (int i = 0; i < contour.Length; i++)
        {
            Point a = contour[i];
            Point b = contour[(i + 1) % contour.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static (List<Point[]> From, List<Point[]> To) Match(List<Point[]> source, List<Point[]> target)
    {
        List<Point[]> from = [];
        List<Point[]> to = [];
        int count = Math.Max(source.Count, target.Count);
        for (int i = 0; i < count; i++)
        {
            Point[]? a = i < source.Count ? source[i] : null;
            Point[]? b = i < target.Count ? target[i] : null;
            a ??= Collapsed(b!);
            b ??= Collapsed(a);
            from.Add(a);
            to.Add(Align(a, b));
        }
        return (from, to);
    }

    private static Point[] Collapsed(Point[] contour)
    {
        Point center = new(contour.Average(point => point.X), contour.Average(point => point.Y));
        return Enumerable.Repeat(center, contour.Length).ToArray();
    }

    private static Point[] Align(Point[] from, Point[] to)
    {
        int best = 0;
        double bestCost = double.MaxValue;
        for (int shift = 0; shift < to.Length; shift += 2)
        {
            double cost = 0;
            for (int i = 0; i < from.Length && cost < bestCost; i++)
                cost += (from[i] - to[(i + shift) % to.Length]).LengthSquared;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = shift;
            }
        }
        Point[] aligned = new Point[to.Length];
        for (int i = 0; i < to.Length; i++)
            aligned[i] = to[(i + best) % to.Length];
        return aligned;
    }

    private static List<Point[]> Blend(List<Point[]> from, List<Point[]> to, double amount)
    {
        List<Point[]> result = new(from.Count);
        for (int c = 0; c < from.Count; c++)
        {
            Point[] mixed = new Point[from[c].Length];
            for (int i = 0; i < mixed.Length; i++)
                mixed[i] = from[c][i] + (to[c][i] - from[c][i]) * amount;
            result.Add(mixed);
        }
        return result;
    }

    private static StreamGeometry BuildGeometry(List<Point[]> contours)
    {
        StreamGeometry geometry = new() { FillRule = FillRule.EvenOdd };
        using (StreamGeometryContext context = geometry.Open())
        {
            foreach (Point[] contour in contours)
            {
                context.BeginFigure(contour[0], true, true);
                context.PolyLineTo(contour[1..], false, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
