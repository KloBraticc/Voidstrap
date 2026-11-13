using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using Voidstrap.UI.Elements.Installer.Pages;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer;

internal sealed class InstallerArt
{
    public required FrameworkElement Back { get; init; }

    public required FrameworkElement Badge { get; init; }

    public FrameworkElement? Front { get; init; }

    public double BadgeAngle { get; init; }

    public double BadgeRise { get; init; } = 40;

    public Action<double>? Motion { get; init; }

    public static InstallerArt For(Type pageType)
    {
        if (pageType == typeof(InstallPage))
            return Folder();
        if (pageType == typeof(ChannelPage))
            return Glyph(SymbolRegular.Globe24, "#8A6CFF", "#3F7BFF", SymbolRegular.ArrowSync24, "#2BD39A", "#0E9E73", (icon, badge, d) =>
            {
                Keys(icon, Angle, d + 120, 1100, 0, -9, 7, -4, 2, 0);
                Keys(badge, Angle, d + 760, 1100, 0, 360, 720);
            });
        if (pageType == typeof(InstallerModsPage))
            return Glyph(SymbolRegular.PaintBrush24, "#FF6FA8", "#FF8A2B", SymbolRegular.Wand24, "#FFD54A", "#FFA000", (icon, badge, d) =>
            {
                Keys(icon, Angle, d + 160, 900, 0, -12, 8, -4, 0);
                Keys(icon, Y, d + 160, 900, 0, -6, 2, 0, 0);
                Keys(badge, Angle, d + 780, 900, 0, -22, 18, -12, 8, 0);
                Keys(badge, Size, d + 780, 900, 1, 1.15, 1, 1.08, 1);
            });
        if (pageType == typeof(InstallerAppearancePage))
            return Glyph(SymbolRegular.Color24, "#C46BFF", "#FF5F9E", SymbolRegular.PaintBrush24, "#4FA3FF", "#2A5BFF", (icon, badge, d) =>
            {
                Keys(icon, Angle, d + 120, 1200, 0, 7, -5, 3, 0);
                Keys(badge, Angle, d + 760, 1000, 0, -28, 22, -28, 0);
                Keys(badge, X, d + 760, 1000, 0, -6, 6, -6, 0);
            });
        if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage))
            return Glyph(SymbolRegular.Apps24, "#3FB6FF", "#2F6BFF", SymbolRegular.ArrowDownload24, "#3DDC84", "#16A35A", (icon, badge, d) =>
            {
                Keys(icon, Size, d + 200, 700, 1, 1.06, 0.98, 1);
                Keys(badge, Y, d + 760, 1000, 0, 9, 0, 6, 0, 2, 0);
            });
        if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage))
            return Glyph(SymbolRegular.PuzzlePiece24, "#2FD8C4", "#2C9BFF", SymbolRegular.Add24, "#B07BFF", "#6A3DFF", (icon, badge, d) =>
            {
                Keys(icon, Angle, d + 160, 900, 0, -8, 7, -5, 3, -1, 0);
                Keys(badge, Angle, d + 760, 700, 0, 180);
                Keys(badge, Size, d + 760, 700, 1, 1.2, 1);
            });
        return Check();
    }

    private const int Angle = 0;

    private const int X = 1;

    private const int Y = 2;

    private const int Size = 3;

    private static void Keys(FrameworkElement element, int kind, double delay, double ms, params double[] values)
    {
        if (element.RenderTransform is not TransformGroup { Children.Count: 3 } group)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            group = new TransformGroup { Children = { new ScaleTransform(), new RotateTransform(), new TranslateTransform() } };
            element.RenderTransform = group;
        }
        DoubleAnimationUsingKeyFrames frames = new() { BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, delay)) };
        if (delay < 0)
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(values[^1], KeyTime.FromTimeSpan(TimeSpan.Zero)));
        else
            for (int i = 0; i < values.Length; i++)
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms * i / Math.Max(1, values.Length - 1))), new SineEase { EasingMode = EasingMode.EaseInOut }));
        switch (kind)
        {
            case Angle:
                group.Children[1].BeginAnimation(RotateTransform.AngleProperty, frames);
                break;
            case X:
                group.Children[2].BeginAnimation(TranslateTransform.XProperty, frames);
                break;
            case Y:
                group.Children[2].BeginAnimation(TranslateTransform.YProperty, frames);
                break;
            default:
                group.Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, frames);
                group.Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, frames);
                break;
        }
    }

    private static LinearGradientBrush Gradient(string top, string bottom) =>
        new((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), new Point(0, 0), new Point(1, 1));

    private static InstallerArt Check()
    {
        System.Windows.Shapes.Path tick = new()
        {
            Data = Geometry.Parse("M 52,86 L 74,108 L 116,62"),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F2A24")),
            StrokeThickness = 14,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = new DoubleCollection { 7, 7 },
            StrokeDashOffset = 7,
        };
        Grid back = new() { Width = 168, Height = 168, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        back.Children.Add(new Ellipse { Fill = Gradient("#4BE38A", "#13A86A") });
        back.Children.Add(tick);
        InstallerArt art = Glyph(SymbolRegular.Star24, "#4BE38A", "#13A86A", SymbolRegular.Star24, "#FFD54A", "#FF9F1A", (icon, badge, d) =>
        {
            Keys(badge, Angle, d + 760, 900, 0, 72, 144);
            Keys(badge, Size, d + 760, 900, 1, 1.25, 1);
        });
        return new InstallerArt
        {
            Back = back,
            Badge = art.Badge,
            BadgeAngle = art.BadgeAngle,
            Motion = delay =>
            {
                art.Motion?.Invoke(delay);
                Keys(back, Size, delay + 700, 500, 1, 1.07, 1);
                tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(520))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delay + 180),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                });
            },
        };
    }

    private static InstallerArt Folder()
    {
        Border back = new()
        {
            Width = 140,
            Height = 150,
            Margin = new Thickness(0, 30, 0, 0),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Gradient("#FFC85A", "#FFA21F"),
        };
        Image tile = new()
        {
            Width = 84,
            Height = 84,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Source = new BitmapImage(new Uri("pack://application:,,,/Voidstrap.png")),
        };
        RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
        SymbolIcon arrow = new()
        {
            Symbol = SymbolRegular.ArrowDownload24,
            Filled = true,
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9740A")),
        };
        Border front = new()
        {
            Width = 156,
            Height = 124,
            Margin = new Thickness(0, 0, 0, 30),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Gradient("#FFB93A", "#FF8F0A"),
            Child = arrow,
        };
        return new InstallerArt
        {
            Back = back,
            Badge = tile,
            Front = front,
            BadgeAngle = -14,
            BadgeRise = 80,
            Motion = d =>
            {
                Keys(arrow, Y, d + 900, 1100, 0, 8, 0, 5, 0, 2, 0);
                Keys(arrow, Size, d + 900, 1100, 1, 0.9, 1.05, 0.96, 1, 1, 1);
            },
        };
    }

    private static InstallerArt Glyph(SymbolRegular symbol, string from, string to, SymbolRegular badgeSymbol, string badgeFrom, string badgeTo, Action<FrameworkElement, FrameworkElement, double> motion)
    {
        SymbolIcon icon = new()
        {
            Symbol = symbol,
            Filled = true,
            FontSize = 160,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Gradient(from, to),
        };
        SymbolIcon badgeIcon = new()
        {
            Symbol = badgeSymbol,
            Filled = true,
            FontSize = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        };
        Grid back = new();
        back.Children.Add(icon);
        Border badge = new()
        {
            Width = 68,
            Height = 68,
            Margin = new Thickness(0, 0, 16, 22),
            CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Gradient(badgeFrom, badgeTo),
            Child = badgeIcon,
        };
        return new InstallerArt
        {
            Back = back,
            Badge = badge,
            BadgeAngle = -10,
            Motion = delay => motion(icon, badgeIcon, delay),
        };
    }
}
