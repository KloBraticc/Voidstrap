using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public static InstallerArt For(Type pageType)
    {
        if (pageType == typeof(InstallPage))
            return Folder();
        if (pageType == typeof(ChannelPage))
            return Glyph(SymbolRegular.Globe24, "#8A6CFF", "#3F7BFF", SymbolRegular.ArrowSync24, "#2BD39A", "#0E9E73");
        if (pageType == typeof(InstallerModsPage))
            return Glyph(SymbolRegular.PaintBrush24, "#FF6FA8", "#FF8A2B", SymbolRegular.Wand24, "#FFD54A", "#FFA000");
        if (pageType == typeof(InstallerAppearancePage))
            return Glyph(SymbolRegular.Color24, "#C46BFF", "#FF5F9E", SymbolRegular.PaintBrush24, "#4FA3FF", "#2A5BFF");
        if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage))
            return Glyph(SymbolRegular.Apps24, "#3FB6FF", "#2F6BFF", SymbolRegular.ArrowDownload24, "#3DDC84", "#16A35A");
        if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage))
            return Glyph(SymbolRegular.PuzzlePiece24, "#2FD8C4", "#2C9BFF", SymbolRegular.Add24, "#B07BFF", "#6A3DFF");
        return Glyph(SymbolRegular.CheckmarkCircle24, "#4BE38A", "#13A86A", SymbolRegular.Star24, "#FFD54A", "#FF9F1A");
    }

    private static LinearGradientBrush Gradient(string top, string bottom) =>
        new((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), new Point(0, 0), new Point(1, 1));

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
        Border front = new()
        {
            Width = 156,
            Height = 124,
            Margin = new Thickness(0, 0, 0, 30),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Gradient("#FFB93A", "#FF8F0A"),
            Child = new SymbolIcon
            {
                Symbol = SymbolRegular.ArrowDownload24,
                Filled = true,
                FontSize = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9740A")),
            },
        };
        return new InstallerArt { Back = back, Badge = tile, Front = front, BadgeAngle = -14, BadgeRise = 80 };
    }

    private static InstallerArt Glyph(SymbolRegular symbol, string from, string to, SymbolRegular badgeSymbol, string badgeFrom, string badgeTo)
    {
        SymbolIcon back = new()
        {
            Symbol = symbol,
            Filled = true,
            FontSize = 160,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Gradient(from, to),
        };
        Border badge = new()
        {
            Width = 68,
            Height = 68,
            Margin = new Thickness(0, 0, 16, 22),
            CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Gradient(badgeFrom, badgeTo),
            Child = new SymbolIcon
            {
                Symbol = badgeSymbol,
                Filled = true,
                FontSize = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
            },
        };
        return new InstallerArt { Back = back, Badge = badge, BadgeAngle = -10 };
    }
}
