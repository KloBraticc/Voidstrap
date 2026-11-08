namespace Voidstrap.Models;

public sealed class AccountChoice
{
    public required SavedAccount Account { get; init; }

    public string Username => Account.Username;

    public string? AvatarUrl { get; init; }
}
