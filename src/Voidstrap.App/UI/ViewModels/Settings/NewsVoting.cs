using System.Windows;
using Voidstrap.Integrations.Plugins;

namespace Voidstrap.UI.ViewModels.Settings;

internal static class NewsVoting
{
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);

    private static readonly int[] BrowserRefreshSeconds = [15, 30, 60];

    public static bool IsPending(string voteId) => Pending.Contains(voteId);

    public static void Vote(NewsPost post, int value)
    {
        if (!post.HasVotes || !Pending.Add(post.VoteId))
            return;
        string voteId = post.VoteId;
        int vote = ModVotes.Get(voteId).Mine == value ? 0 : value;
        post.VotePending = true;
        ModVoteWindow.Show(voteId, vote, (tally, error) => Finished(post, voteId, tally, error));
    }

    private static void Finished(NewsPost post, string voteId, ModVotes.Tally? tally, int error)
    {
        const string LOG_IDENT = "NewsVoting::Finished";
        App.Logger.WriteLine(LOG_IDENT, tally != null ? "Saved the vote on " + voteId : error == 0 ? "The vote window for " + voteId + " was closed" : error < 0 ? "Opened the vote for " + voteId + " in the browser" : "The vote on " + voteId + " failed with status " + error);
        Pending.Remove(voteId);
        if (tally != null)
            ModVotes.Apply(voteId, tally);
        post.LoadVotes();
        if (error < 0)
        {
            _ = RefreshLaterAsync();
            return;
        }
        if (error > 0)
            Frontend.ShowMessageBox(
                error == 403 ? Strings.Ui_VoidstrapMods_VoteBlocked
                    : error == 429 ? Strings.Ui_VoidstrapMods_VoteBusy
                    : Strings.Library_Vote_Failed,
                MessageBoxImage.Warning
            );
    }

    private static async Task RefreshLaterAsync()
    {
        foreach (int seconds in BrowserRefreshSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await ModVotes.RefreshAsync(CancellationToken.None, true);
        }
    }
}
