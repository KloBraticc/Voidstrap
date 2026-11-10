namespace Voidstrap.Models.Entities;

public sealed class CommunityCard
{
    public required CommunityGame Game { get; init; }
    public required CommunityReview Review { get; init; }
}

public sealed class CommunityRow
{
    public bool IsWide { get; init; }
    public CommunityCard? First { get; init; }
    public CommunityCard? Second { get; init; }
}
