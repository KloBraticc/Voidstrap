using System.Collections.ObjectModel;
using System.Windows;
using Voidstrap.Integrations;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionHubPanel : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<GameUpdatePost> _news = new();
    private readonly ObservableCollection<LibraryEventEntry> _events = new();
    private readonly ObservableCollection<GameSocialLink> _links = new();
    private readonly ObservableCollection<LibraryBadgeEntry> _badges = new();
    private readonly SessionReviewPanel _review;
    private readonly long _universeId;
    private readonly string _gameName;
    private bool _closed;

    private sealed record HubCache(DateTime At, List<GameUpdatePost> News, List<GameSocialLink> Links, List<LibraryEventEntry> Events);

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly object CacheLock = new();

    private static readonly Dictionary<long, HubCache> Cache = new();

    public SessionHubPanel(ActivityWatcher? activity)
    {
        InitializeComponent();
        _universeId = activity?.Data?.UniverseId ?? 0;
        _gameName = activity?.Data?.GameName ?? string.Empty;
        NewsList.ItemsSource = _news;
        EventList.ItemsSource = _events;
        LinkList.ItemsSource = _links;
        BadgeList.ItemsSource = _badges;
        _review = new SessionReviewPanel(activity);
        ReviewHost.Content = _review.DetachContent();
        Closed += OnHubClosed;
        _ = LoadAsync();
    }

    private void OnHubClosed(object? sender, EventArgs e)
    {
        Closed -= OnHubClosed;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        ReviewHost.Content = null;
        _review.ReleaseDetached();
    }

    private async Task LoadAsync()
    {
        const string LOG_IDENT = "SessionHubPanel::LoadAsync";
        if (_universeId <= 0)
        {
            ShowEmptyStates();
            BadgesLoading.Visibility = Visibility.Collapsed;
            return;
        }
        CancellationToken token = _lifetime.Token;
        Task badges = LoadBadgesAsync(token);
        HubCache? cached;
        lock (CacheLock)
            Cache.TryGetValue(_universeId, out cached);
        if (cached != null && DateTime.UtcNow - cached.At < CacheLifetime)
        {
            Fill(_news, cached.News);
            Fill(_links, cached.Links);
            Fill(_events, cached.Events);
            ShowEmptyStates();
            await badges;
            return;
        }
        try
        {
            Task<List<GameUpdatePost>> news = FillWhenReady(_news, NewsEmpty, RobloxGameExtras.GetUpdatesAsync(_universeId, token));
            Task<List<GameSocialLink>> links = FillWhenReady(_links, LinksEmpty, RobloxGameExtras.GetSocialLinksAsync(_universeId, token));
            Task<List<LibraryEventEntry>> events = FillWhenReady(_events, EventsEmpty, LoadEventsAsync(token));
            await Task.WhenAll(news, links, events);
            List<GameSocialLink> linkList = await links;
            if (linkList.Count > 0)
            {
                await RobloxGameExtras.ResolveLinkIconsAsync(linkList, token);
                if (linkList.Any(static link => link.IconUrl.Length > 0))
                    Fill(_links, linkList);
            }
            List<GameUpdatePost> newsList = await news;
            List<LibraryEventEntry> eventList = await events;
            lock (CacheLock)
            {
                DateTime now = DateTime.UtcNow;
                foreach (long stale in Cache.Where(pair => now - pair.Value.At >= CacheLifetime).Select(static pair => pair.Key).ToList())
                    Cache.Remove(stale);
                Cache[_universeId] = new HubCache(now, newsList, linkList, eventList);
            }
            await badges;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The session hub could not be loaded: " + ex.Message);
            if (!_closed)
                ShowEmptyStates();
        }
    }

    private async Task<List<LibraryEventEntry>> LoadEventsAsync(CancellationToken token)
    {
        List<LibraryEventEntry> events = await LibraryViewModel.FetchUniverseEventsAsync(_universeId, _gameName, token);
        events = events.OrderBy(static entry => entry.StartUtc ?? DateTime.MaxValue).Take(12).ToList();
        await LibraryViewModel.ResolveEventThumbnailsAsync(events, token);
        return events;
    }

    private async Task LoadBadgesAsync(CancellationToken token)
    {
        const string LOG_IDENT = "SessionHubPanel::LoadBadgesAsync";
        try
        {
            RobloxBadges.Progress progress = await RobloxBadges.GetProgressAsync(_universeId, true, token, foreground: true);
            if (_closed)
                return;
            List<LibraryBadgeEntry> entries = progress
                .Badges.Select(badge =>
                {
                    bool earned = progress.Awarded.TryGetValue(badge.Id, out DateTime when);
                    return new LibraryBadgeEntry
                    {
                        Id = badge.Id,
                        Name = badge.Name,
                        Description = badge.Description,
                        Earned = earned,
                        AwardedUtc = earned ? when : DateTime.MinValue,
                        WinRate = badge.WinRate,
                    };
                })
                .OrderByDescending(static entry => entry.Earned)
                .ThenByDescending(static entry => entry.AwardedUtc)
                .ThenByDescending(static entry => entry.WinRate)
                .ToList();
            Fill(_badges, entries);
            BadgesLoading.Visibility = Visibility.Collapsed;
            if (progress.Total == 0)
                SetBadgeStatus(Strings.Library_Badges_None);
            else if (!progress.SignedIn)
                SetBadgeStatus(Strings.Library_Badges_SignedOut);
            if (progress.SignedIn && progress.Total > 0)
            {
                double percent = progress.Earned * 100.0 / progress.Total;
                BadgeSummaryText.Text = string.Format(Strings.Library_Badges_Summary, progress.Earned.ToString("N0"), progress.Total.ToString("N0"));
                BadgePercentText.Text = string.Format(Strings.Library_Badges_Percent, Math.Floor(percent).ToString("0"));
                BadgeProgress.Value = percent;
                BadgeSummary.Visibility = Visibility.Visible;
            }
            if (entries.Count == 0)
                return;
            IReadOnlyDictionary<long, string> icons = await RobloxBadges.GetIconsAsync(entries.Select(static entry => entry.Id).ToList(), token);
            foreach (LibraryBadgeEntry entry in entries)
            {
                if (icons.TryGetValue(entry.Id, out string? icon))
                    entry.IconUrl = icon;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Badges could not be loaded for universe " + _universeId + ": " + ex.Message);
            if (!_closed)
            {
                BadgesLoading.Visibility = Visibility.Collapsed;
                SetBadgeStatus(Strings.Library_Badges_Failed);
            }
        }
    }

    private void SetBadgeStatus(string text)
    {
        BadgeStatus.Text = text;
        BadgeStatus.Visibility = Visibility.Visible;
    }

    private void ShowEmptyStates()
    {
        NewsEmpty.Visibility = _news.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EventsEmpty.Visibility = _events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LinksEmpty.Visibility = _links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<List<T>> FillWhenReady<T>(ObservableCollection<T> target, System.Windows.Controls.TextBlock empty, Task<List<T>> source)
    {
        List<T> items = await source;
        Fill(target, items);
        if (!_closed)
            empty.Visibility = target.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return items;
    }

    private void Fill<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        if (_closed)
            return;
        target.Clear();
        foreach (T item in source)
            target.Add(item);
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            Utilities.OpenWebLink(uri.AbsoluteUri);
    }

    private void OpenBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long id } && id > 0)
            Utilities.OpenWebLink("https://www.roblox.com/badges/" + id);
    }
}
