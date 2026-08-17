using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.UI.Elements.Controls;

internal static class DialogChrome
{
    public static Brush Background()
    {
        LinearGradientBrush brush = new()
        {
            StartPoint = new Point(1, 1),
            EndPoint = new Point(0, 0)
        };

        foreach ((double offset, string key) in new[]
        {
            (0.0, "WindowBackgroundColorPrimary"),
            (0.7, "WindowBackgroundColorSecondary"),
            (1.0, "WindowBackgroundColorThird")
        })
        {
            brush.GradientStops.Add(new GradientStop
            {
                Offset = offset,
                Color = Application.Current?.TryFindResource(key) is Color color ? color : Color.FromRgb(0x20, 0x20, 0x20)
            });
        }

        brush.Freeze();
        return brush;
    }

    public static Wpf.Ui.Controls.TitleBar TitleBar(string title)
    {
        Wpf.Ui.Controls.TitleBar bar = new()
        {
            Title = title,
            Padding = new Thickness(8),
            ShowMaximize = false,
            ShowMinimize = false,
            MinimizeToTray = false
        };

        try
        {
            bar.Icon = new BitmapImage(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
        }
        catch
        {
        }

        return bar;
    }

    public static Grid Host(Wpf.Ui.Controls.TitleBar titleBar, UIElement body)
    {
        Grid root = new() { Background = Background() };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);
        return root;
    }
}
