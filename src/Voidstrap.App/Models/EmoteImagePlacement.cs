namespace Voidstrap.Models;

public sealed class EmoteImagePlacement
{
    public string? Path { get; set; }

    public double Scale { get; set; } = 1.0;

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Rotation { get; set; }

    public double Opacity { get; set; } = 1.0;
}
