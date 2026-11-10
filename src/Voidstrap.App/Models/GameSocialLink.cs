using System.Windows.Media;

namespace Voidstrap.Models;

public sealed class GameSocialLink
{
    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string IconUrl { get; set; } = string.Empty;

    public string Label => string.IsNullOrWhiteSpace(Title) ? Type : Title;

    public Brush TileBrush => new SolidColorBrush(TileColor);

    public Geometry? Glyph => Glyphs.TryGetValue(Type, out Geometry? glyph) ? glyph : null;

    public System.Windows.Visibility FallbackVisibility => Glyph == null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    private static readonly Dictionary<string, Geometry> Glyphs = new()
    {
        ["YouTube"] = Freeze("M9,7 L17.5,12 L9,17 Z"),
        ["Twitter"] = Freeze("M4,4 L10.4,12.6 L4,20 L5.9,20 L11.4,13.8 L16,20 L20,20 L13.3,11.1 L19.3,4 L17.4,4 L12.3,9.9 L8,4 Z"),
        ["Facebook"] = Freeze("M13.5,21 L13.5,13.5 L16,13.5 L16.4,10.5 L13.5,10.5 L13.5,8.7 C13.5,7.8 13.8,7.2 15,7.2 L16.5,7.2 L16.5,4.5 C16.2,4.5 15.3,4.4 14.2,4.4 C12,4.4 10.5,5.7 10.5,8.2 L10.5,10.5 L8,10.5 L8,13.5 L10.5,13.5 L10.5,21 Z"),
        ["Twitch"] = Freeze("F0 M5,3 L4,6 L4,19 L8,19 L8,22 L10.5,22 L13.5,19 L17,19 L21,15 L21,3 Z M19,14 L16.5,16.5 L12.5,16.5 L10,19 L10,16.5 L6.5,16.5 L6.5,5 L19,5 Z M15,7.5 L17,7.5 L17,12 L15,12 Z M10.5,7.5 L12.5,7.5 L12.5,12 L10.5,12 Z"),
        ["Discord"] = Freeze("F0 M19.5,5.5 C18,4.8 16.5,4.4 15,4.2 L14.6,5 C13,4.8 11,4.8 9.4,5 L9,4.2 C7.5,4.4 6,4.8 4.5,5.5 C2,9.3 1.4,13 1.7,16.6 C3.5,17.9 5.2,18.7 6.9,19.2 L8,17.5 C7.4,17.3 6.8,17 6.3,16.7 L6.7,16.4 C10.1,18 13.9,18 17.3,16.4 L17.7,16.7 C17.2,17 16.6,17.3 16,17.5 L17.1,19.2 C18.8,18.7 20.5,17.9 22.3,16.6 C22.7,12.4 21.6,8.8 19.5,5.5 Z M9.2,14.4 C8.2,14.4 7.4,13.5 7.4,12.4 C7.4,11.3 8.2,10.4 9.2,10.4 C10.2,10.4 11,11.3 11,12.4 C11,13.5 10.2,14.4 9.2,14.4 Z M14.8,14.4 C13.8,14.4 13,13.5 13,12.4 C13,11.3 13.8,10.4 14.8,10.4 C15.8,10.4 16.6,11.3 16.6,12.4 C16.6,13.5 15.8,14.4 14.8,14.4 Z"),
        ["Guilded"] = Freeze("M5,4 L19,4 L18.4,7.5 L9,7.5 C9.2,11 10.6,14.4 12.6,16.6 C14,15.4 15,13.8 15.6,12 L12,12 L12.6,9 L19.6,9 C19,14 16.6,18 12.4,20.6 C7.4,17.4 5,11.4 5,4 Z"),
    };

    private static Geometry Freeze(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

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
