using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI;

internal sealed class SidebarIconHover
{
    private sealed class Swap
    {
        public required Panel Host { get; init; }

        public required SymbolIcon Icon { get; init; }

        public required SymbolIcon Outgoing { get; init; }

        public required SymbolIcon Incoming { get; init; }
    }

    private static readonly Duration Length = new(TimeSpan.FromMilliseconds(200));

    private const double SmallScale = 0.6;

    private readonly Dictionary<NavigationItem, Swap> _playing = [];

    public void Change(NavigationItem item, SymbolRegular symbol)
    {
        Finish(item);
        SymbolRegular previous = item.Icon;
        item.Icon = symbol;
        if (previous == symbol || item.Template?.FindName("ItemIcon", item) is not SymbolIcon icon)
            return;
        if (icon.Parent is not Panel host || !icon.IsVisible)
            return;

        Swap swap = new()
        {
            Host = host,
            Icon = icon,
            Outgoing = CopyOf(icon, previous),
            Incoming = CopyOf(icon, symbol),
        };
        _playing[item] = swap;
        host.Children.Add(swap.Outgoing);
        host.Children.Add(swap.Incoming);
        icon.Opacity = 0;

        Play(swap.Outgoing, 1, 0, 1, SmallScale, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        DoubleAnimation arrival = Play(swap.Incoming, 0, 1, SmallScale, 1, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });
        arrival.Completed += (_, _) =>
        {
            if (_playing.TryGetValue(item, out Swap? current) && ReferenceEquals(current, swap))
                Finish(item);
        };
        ScaleTransform scale = (ScaleTransform)swap.Incoming.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, arrival);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, arrival);
    }

    private void Finish(NavigationItem item)
    {
        if (!_playing.Remove(item, out Swap? swap))
            return;
        swap.Host.Children.Remove(swap.Outgoing);
        swap.Host.Children.Remove(swap.Incoming);
        swap.Icon.Opacity = 1;
    }

    private static SymbolIcon CopyOf(SymbolIcon icon, SymbolRegular symbol)
    {
        SymbolIcon copy = new()
        {
            Symbol = symbol,
            Filled = icon.Filled,
            FontSize = icon.FontSize,
            Foreground = icon.Foreground,
            Width = icon.Width,
            Margin = icon.Margin,
            HorizontalAlignment = icon.HorizontalAlignment,
            VerticalAlignment = icon.VerticalAlignment,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
        };
        Grid.SetColumn(copy, Grid.GetColumn(icon));
        Grid.SetRow(copy, Grid.GetRow(icon));
        return copy;
    }

    private static DoubleAnimation Play(SymbolIcon target, double fromOpacity, double toOpacity, double fromScale, double toScale, IEasingFunction ease)
    {
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, Length));
        DoubleAnimation scaling = new(fromScale, toScale, Length) { EasingFunction = ease };
        if (toScale < fromScale)
        {
            ScaleTransform scale = (ScaleTransform)target.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaling);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaling);
        }
        return scaling;
    }
}
