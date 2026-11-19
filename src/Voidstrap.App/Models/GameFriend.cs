namespace Voidstrap.Models;

public sealed class GameFriend
{
    public long UserId { get; init; }

    public string Name { get; set; } = "";

    public string HeadshotUrl { get; set; } = "";

    public bool InGame { get; init; }

    public int Earned { get; init; }

    public int Total { get; init; }

    public DateTime LastAwardUtc { get; init; }
}
