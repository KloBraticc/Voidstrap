using System.Windows.Media;

namespace Voidstrap.Models;

public sealed class GameSocialLink
{
    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Label => string.IsNullOrWhiteSpace(Title) ? Type : Title;

    public Brush TileBrush => new SolidColorBrush(TileColor);

    private Color TileColor =>
        Type switch
        {
            "Discord" => Color.FromRgb(0x58, 0x65, 0xF2),
            "YouTube" => Color.FromRgb(0xFF, 0x00, 0x33),
            "Twitch" => Color.FromRgb(0x91, 0x46, 0xFF),
            "Twitter" => Color.FromRgb(0x1D, 0x1D, 0x1F),
            "Facebook" => Color.FromRgb(0x18, 0x77, 0xF2),
            "Guilded" => Color.FromRgb(0xF5, 0xC4, 0x00),
            _ => Color.FromRgb(0x33, 0x95, 0xFF),
        };
}
