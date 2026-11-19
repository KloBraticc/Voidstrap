namespace Voidstrap.Models;

public sealed class GameFriendActivity
{
    public long UserId { get; init; }

    public string Name { get; set; } = "";

    public string HeadshotUrl { get; set; } = "";

    public DateTime WhenUtc { get; init; }

    public bool FirstPlay { get; init; }

    public long BadgeId { get; init; }

    public string BadgeName { get; init; } = "";

    public string BadgeIconUrl { get; set; } = "";
}
