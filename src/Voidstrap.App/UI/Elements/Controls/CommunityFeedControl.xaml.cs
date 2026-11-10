using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.Elements.Controls;

public partial class CommunityFeedControl : UserControl
{
    private const int MaxCards = 2000;
    private readonly ObservableCollection<CommunityRow> _rows = new();
    private readonly List<CommunityCard> _cards = new();
    private readonly Dictionary<long, CommunityGame> _byPlace = new();
    private readonly object _pendingLock = new();
    private readonly List<(CommunityGameInfo Game, IReadOnlyList<CommunityReview> Reviews)> _pending = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _filter = string.Empty;
    private bool _dirty;
    private CancellationTokenSource? _cts;
    private bool _active;
    private bool _loaded;

    public CommunityFeedControl()
    {
        InitializeComponent();
        FeedList.ItemsSource = _rows;
    }

    public void SetActive(bool active)
    {
        if (_active == active)
            return;
        _active = active;
        if (active)
        {
            if (!_loaded)
            {
                _loaded = true;
                foreach ((CommunityGameInfo game, List<CommunityReview> reviews) in RoReviewCommunityFeed.LoadCached())
                    Upsert(game, reviews);
                Rebuild();
            }
            _flushTimer.Tick += FlushTimer_Tick;
            _flushTimer.Start();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ScanRing.Visibility = Visibility.Visible;
            _ = RunScanAsync(_cts.Token);
        }
        else
        {
            _flushTimer.Stop();
            _flushTimer.Tick -= FlushTimer_Tick;
            _cts?.Cancel();
        }
    }

    private async Task RunScanAsync(CancellationToken token)
    {
        const string LOG_IDENT = "CommunityFeedControl::RunScanAsync";
        try
        {
            await Task.Run(() => RoReviewCommunityFeed.RunAsync(OnReviews, static (_, _) => { }, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                ScanRing.Visibility = Visibility.Collapsed;
                Flush();
            }
        }
    }

    private void OnReviews(CommunityGameInfo game, IReadOnlyList<CommunityReview> reviews)
    {
        lock (_pendingLock)
            _pending.Add((game, reviews));
    }

    private void FlushTimer_Tick(object? sender, EventArgs e) => Flush();

    private void Flush()
    {
        List<(CommunityGameInfo Game, IReadOnlyList<CommunityReview> Reviews)> batch;
        lock (_pendingLock)
        {
            batch = new(_pending);
            _pending.Clear();
        }
        foreach ((CommunityGameInfo game, IReadOnlyList<CommunityReview> reviews) in batch)
            Upsert(game, reviews);
        if (FindScroller(FeedList) is not ScrollViewer scroller || scroller.VerticalOffset < 1)
            Rebuild();
    }

    private void Upsert(CommunityGameInfo info, IReadOnlyList<CommunityReview> reviews)
    {
        if (!_byPlace.TryGetValue(info.PlaceId, out CommunityGame? game))
        {
            game = new CommunityGame { PlaceId = info.PlaceId };
            _byPlace[info.PlaceId] = game;
        }
        game.Apply(info);
        if (game.Name.Length == 0 && reviews.Count > 0)
            game.Name = reviews[0].GameName;
        _cards.RemoveAll(card => card.Game.PlaceId == info.PlaceId);
        foreach (CommunityReview review in reviews)
            _cards.Add(new CommunityCard { Game = game, Review = review });
        _dirty = true;
    }

    private void Rebuild()
    {
        if (!_dirty)
            return;
        _dirty = false;
        List<CommunityCard> shown = _cards
            .Where(card => _filter.Length == 0 || Matches(card, _filter))
            .OrderByDescending(static card => card.Review.UnixTime)
            .Take(MaxCards)
            .ToList();
        List<CommunityRow> rows = new();
        int index = 0;
        int step = 0;
        while (index < shown.Count)
        {
            if (step % 3 == 0)
            {
                rows.Add(new CommunityRow { IsWide = true, First = shown[index++] });
            }
            else
            {
                CommunityCard first = shown[index++];
                rows.Add(new CommunityRow { First = first, Second = index < shown.Count ? shown[index++] : null });
            }
            step++;
        }
        _rows.Clear();
        foreach (CommunityRow row in rows)
            _rows.Add(row);
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        _dirty = true;
        if (_loaded)
            Rebuild();
    }

    private static bool Matches(CommunityCard card, string filter)
    {
        return card.Game.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || card.Game.CreatorName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || card.Review.AuthorName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || card.Review.Content.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommunityCard card })
            return;
        if (Window.GetWindow(this) is Settings.MainWindow window)
            window.ShowGameDetail(card.Game.PlaceId, card.Game.UniverseId, card.Game.Name);
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
                return scroller;
            if (FindScroller(child) is ScrollViewer nested)
                return nested;
        }
        return null;
    }
}
