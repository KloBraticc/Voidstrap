using CommunityToolkit.Mvvm.ComponentModel;

namespace Voidstrap.Models;

public sealed class NewsFeed
{
    public int Version { get; set; }

    public List<NewsPost> Posts { get; set; } = [];
}

public sealed partial class NewsPost : ObservableObject
{
    [ObservableProperty]
    [Newtonsoft.Json.JsonIgnore]
    public partial bool IsCurrent { get; set; }

    public string Date { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string? Image { get; set; }

    public List<NewsLink> Links { get; set; } = [];

    public string DateText => DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
        ? parsed.ToString("D", CultureInfo.CurrentCulture)
        : Date;

    public bool HasImage => !string.IsNullOrWhiteSpace(Image);

    public bool HasLinks => Links.Count > 0;
}

public sealed class NewsLink
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}
