using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.Elements.Controls;

public partial class CommunityFeedControl : UserControl
{
    private const int MaxShown = 3000;
    private readonly Dictionary<long, List<CommunityReview>> _byPlace = new();
    private readonly object _pendingLock = new();
    private readonly List<(long PlaceId, IReadOnlyList<CommunityReview> Reviews)> _pending = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private CancellationTokenSource? _cts;
    private bool _active;
    private bool _loaded;
    private bool _dirty;
    private int _done;
    private int _total;

    public CommunityFeedControl()
    {
        InitializeComponent();
    }

    public void SetActive(bool active)
    {
        if (_active == active)
            return;
        _active = active;
        if (active)
        {
            _flushTimer.Tick += FlushTimer_Tick;
            _flushTimer.Start();
            if (!_loaded)
            {
                _loaded = true;
                foreach (CommunityReview review in RoReviewCommunityFeed.LoadCached())
                    AddToPlace(review.PlaceId, review);
                _dirty = true;
                Rebuild();
            }
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
            await Task.Run(() => RoReviewCommunityFeed.RunAsync(OnReviews, OnProgress, token), token).ConfigureAwait(true);
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

    private void OnReviews(IReadOnlyList<CommunityReview> reviews, long placeId)
    {
        lock (_pendingLock)
            _pending.Add((placeId, reviews));
    }

    private void OnProgress(int done, int total)
    {
        Volatile.Write(ref _done, done);
        Volatile.Write(ref _total, total);
    }

    private void FlushTimer_Tick(object? sender, EventArgs e) => Flush();

    private void Flush()
    {
        lock (_pendingLock)
        {
            foreach ((long placeId, IReadOnlyList<CommunityReview> reviews) in _pending)
            {
                _byPlace.Remove(placeId);
                foreach (CommunityReview review in reviews)
                    AddToPlace(placeId, review);
                _dirty = true;
            }
            _pending.Clear();
        }
        int total = Volatile.Read(ref _total);
        StatusText.Text = total > 0 ? string.Format(Strings.Ui_Library_CommunityScanning, Volatile.Read(ref _done), total) : string.Empty;
        Rebuild();
    }

    private void AddToPlace(long placeId, CommunityReview review)
    {
        if (!_byPlace.TryGetValue(placeId, out List<CommunityReview>? list))
            _byPlace[placeId] = list = new List<CommunityReview>();
        list.Add(review);
    }

    private void Rebuild()
    {
        if (!_dirty)
            return;
        _dirty = false;
        string filter = SearchBox.Text.Trim();
        IEnumerable<CommunityReview> all = _byPlace.Values.SelectMany(static list => list);
        if (filter.Length > 0)
            all = all.Where(r => r.GameName.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.AuthorName.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Content.Contains(filter, StringComparison.OrdinalIgnoreCase));
        List<CommunityReview> shown = all.OrderByDescending(static r => r.UnixTime).Take(MaxShown).ToList();
        FeedList.ItemsSource = shown;
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _dirty = true;
        if (_loaded)
            Rebuild();
    }
}
