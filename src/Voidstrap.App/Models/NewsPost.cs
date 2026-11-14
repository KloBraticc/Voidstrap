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

    private DateTime? ParsedDate => DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
        ? parsed
        : null;

    public string DateText => ParsedDate?.ToString("D", CultureInfo.CurrentCulture) ?? Date;

    public string DayText => ParsedDate?.Day.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public string MonthText => ParsedDate?.ToString("MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture) ?? string.Empty;

    public string YearText => ParsedDate?.Year.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public bool HasImage => !string.IsNullOrWhiteSpace(Image);

    public bool HasLinks => Links.Count > 0;
}

public sealed class NewsLink
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}
