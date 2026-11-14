using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations.Plugins;
using Voidstrap.UI.ViewModels.Settings;

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

    [ObservableProperty]
    [Newtonsoft.Json.JsonIgnore]
    [NotifyPropertyChangedFor(nameof(LikesText))]
    public partial int Likes { get; set; }

    [ObservableProperty]
    [Newtonsoft.Json.JsonIgnore]
    [NotifyPropertyChangedFor(nameof(DislikesText))]
    public partial int Dislikes { get; set; }

    [ObservableProperty]
    [Newtonsoft.Json.JsonIgnore]
    [NotifyPropertyChangedFor(nameof(IsLiked), nameof(IsDisliked))]
    public partial int Mine { get; set; }

    [ObservableProperty]
    [Newtonsoft.Json.JsonIgnore]
    [NotifyCanExecuteChangedFor(nameof(LikeCommand), nameof(DislikeCommand))]
    public partial bool VotePending { get; set; }

    public string? Id { get; set; }

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

    public bool HasVotes => !string.IsNullOrEmpty(Id);

    public string VoteId => "news/" + Id;

    public bool IsLiked => Mine == 1;

    public bool IsDisliked => Mine == -1;

    public string LikesText => Likes.ToString("N0", CultureInfo.CurrentCulture);

    public string DislikesText => Dislikes.ToString("N0", CultureInfo.CurrentCulture);

    public void LoadVotes()
    {
        if (!HasVotes)
            return;
        ModVotes.Tally tally = ModVotes.Get(VoteId);
        Likes = tally.Likes;
        Dislikes = tally.Dislikes;
        Mine = tally.Mine;
        VotePending = NewsVoting.IsPending(VoteId);
        LikeCommand.NotifyCanExecuteChanged();
        DislikeCommand.NotifyCanExecuteChanged();
    }

    private bool CanVote() => HasVotes && !VotePending && ModVotes.Available;

    [RelayCommand(CanExecute = nameof(CanVote))]
    private void Like() => NewsVoting.Vote(this, 1);

    [RelayCommand(CanExecute = nameof(CanVote))]
    private void Dislike() => NewsVoting.Vote(this, -1);
}

public sealed class NewsLink
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}
