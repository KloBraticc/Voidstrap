namespace Voidstrap.Models;

public sealed class GameUpdatePost
{
    public string Content { get; init; } = string.Empty;

    public DateTime CreatedUtc { get; init; }

    public string DateDisplay => CreatedUtc > DateTime.MinValue ? CreatedUtc.ToLocalTime().ToString("d") : string.Empty;
}
