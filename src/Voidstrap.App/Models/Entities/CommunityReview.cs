namespace Voidstrap.Models.Entities;

public sealed class CommunityReview
{
    public string Id { get; set; } = string.Empty;
    public long PlaceId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public long AuthorId { get; set; }
    public string AvatarUrl { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public long UnixTime { get; set; }
    public int UpCount { get; set; }
    public int DownCount { get; set; }
}
