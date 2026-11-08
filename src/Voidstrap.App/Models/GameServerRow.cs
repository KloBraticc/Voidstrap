namespace Voidstrap.Models;

public sealed class GameServerRow
{
    public string JobId { get; init; } = string.Empty;

    public int Playing { get; init; }

    public int MaxPlayers { get; init; }

    public int Ping { get; init; }

    public double Fps { get; init; }

    public string Capacity { get; init; } = string.Empty;

    public string Metrics { get; init; } = string.Empty;

    public double Fill => MaxPlayers > 0 ? Math.Clamp(Playing * 100.0 / MaxPlayers, 0, 100) : 0;
}
