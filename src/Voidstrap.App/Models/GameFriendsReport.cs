namespace Voidstrap.Models;

public sealed class GameFriendsReport
{
    public GameFriendsStatus Status { get; init; }

    public IReadOnlyList<GameFriend> Recent { get; init; } = [];

    public IReadOnlyList<GameFriend> Previous { get; init; } = [];

    public IReadOnlyList<GameFriendActivity> Activity { get; init; } = [];
}
