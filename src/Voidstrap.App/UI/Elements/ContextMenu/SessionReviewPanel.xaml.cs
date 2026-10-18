using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Integrations;
using Voidstrap.Resources;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionReviewPanel : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _ct;
    private readonly ObservableCollection<RoReviewService.ReviewEntry> _reviews = new();
    private long _placeId;
    private string? _userVote;
    private int _upCount;
    private int _downCount;
    private int _reviewCount;
    private bool _loading;
    private bool _reloadPending;
    private bool _submitting;
    private bool _closed;
    private bool _panelVisible;
    private bool _profileVotePending;
    private string? _profileVoteBaseline;
    private int _profileUpBaseline;
    private int _profileDownBaseline;
    private long _observedSessionUserId = RoReviewService.SessionUserId;
    private long _placeGeneration;
    private long _profileVoteVersion;
    private long _mutationSequence;
    private long _submitGeneration;
    private readonly System.Collections.Generic.Dictionary<string, long> _mutationVersions = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, (string? Vote, int Up, int Down)> _reviewVoteBaselines = new(StringComparer.Ordinal);
    private RoReviewService.ReviewEntry? _pendingSubmitEntry;
    private string _pendingSubmitContent = string.Empty;

    public SessionReviewPanel(ActivityWatcher? activity = null)
    {
        _ct = _lifetime.Token;
        InitializeComponent();
        ReviewList.ItemsSource = _reviews;
        if (activity?.Data is { } data)
        {
            _placeId = data.PlaceId;
            if (!string.IsNullOrWhiteSpace(data.GameName))
            {
                GameNameText.Text = data.GameName;
                HeaderPanel.Visibility = Visibility.Visible;
            }
        }
        RoReviewService.AuthenticationChanged += OnAuthChanged;
        RoReviewService.ReviewsUpdated += OnReviewsUpdated;
        Loaded += OnPanelLoaded;
        Closed += OnPanelClosed;
    }

    public SessionReviewPanel(long placeId)
    {
        _ct = _lifetime.Token;
        _placeId = placeId;
        InitializeComponent();
        ReviewList.ItemsSource = _reviews;
        RoReviewService.AuthenticationChanged += OnAuthChanged;
        RoReviewService.ReviewsUpdated += OnReviewsUpdated;
        Loaded += OnPanelLoaded;
        Closed += OnPanelClosed;
    }

    public void SetPlaceId(long placeId, string? gameName = null)
    {
        if (placeId <= 0)
            return;
        if (_placeId != placeId)
        {
            _placeId = placeId;
            _placeGeneration++;
            ResetPanelState();
        }
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            GameNameText.Text = gameName;
            HeaderPanel.Visibility = Visibility.Visible;
        }
        if (_panelVisible && !_closed)
            _ = LoadAsync();
    }

    public void SetPanelVisible(bool visible)
    {
        if (_closed || _panelVisible == visible)
            return;
        _panelVisible = visible;
        if (visible)
            _ = LoadAsync();
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_panelVisible)
            _ = LoadAsync();
        _ = RunLivePollingLoopAsync();
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _panelVisible = false;
        _submitGeneration++;
        RoReviewService.AuthenticationChanged -= OnAuthChanged;
        RoReviewService.ReviewsUpdated -= OnReviewsUpdated;
        Loaded -= OnPanelLoaded;
        Closed -= OnPanelClosed;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void ResetPanelState()
    {
        _profileVoteVersion++;
        _profileVotePending = false;
        _mutationSequence++;
        _mutationVersions.Clear();
        _reviewVoteBaselines.Clear();
        _submitGeneration++;
        _submitting = false;
        SubmitButton.IsEnabled = true;
        _pendingSubmitEntry = null;
        _pendingSubmitContent = string.Empty;
        _reviews.Clear();
        _userVote = null;
        _upCount = 0;
        _downCount = 0;
        _reviewCount = 0;
        UpdateRatingDisplay();
        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private void OnReviewsUpdated(long placeId, RoReviewService.ReviewData data)
    {
        if (_closed || placeId != _placeId)
            return;

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed || placeId != _placeId)
                return;
            ApplyReviewData(data);
        });
    }

    private void ApplyReviewData(RoReviewService.ReviewData data)
    {
        if (data.SessionGeneration != RoReviewService.SessionGeneration)
            return;

        if (!_profileVotePending)
        {
            _userVote = data.UserVote;
            _upCount = data.UpCount;
            _downCount = data.DownCount;
        }
        if (_pendingSubmitEntry == null)
            _reviewCount = data.ReviewCount;
        UpdateRatingDisplay();

        long myId = data.SessionUserId;
        string? myName = data.SessionUsername;

        System.Collections.Generic.Dictionary<string, RoReviewService.ReviewEntry> incoming = new(StringComparer.Ordinal);
        foreach (RoReviewService.ReviewEntry r in data.Reviews)
        {
            if (!string.IsNullOrEmpty(r.Id))
                incoming[r.Id] = r;
        }

        for (int i = _reviews.Count - 1; i >= 0; i--)
        {
            RoReviewService.ReviewEntry existing = _reviews[i];
            if (existing.Id.StartsWith("temp_", StringComparison.Ordinal))
                continue;

            if (!incoming.TryGetValue(existing.Id, out RoReviewService.ReviewEntry? updated))
            {
                _reviews.RemoveAt(i);
            }
            else
            {
                if (!_mutationVersions.ContainsKey(existing.Id))
                {
                    existing.UpCount = updated.UpCount;
                    existing.DownCount = updated.DownCount;
                    existing.UserVote = updated.UserVote;
                }
                existing.AuthorId = updated.AuthorId;
                existing.Content = updated.Content;
                existing.AuthorName = updated.AuthorName;
                if (!string.IsNullOrEmpty(updated.AvatarUrl))
                    existing.AvatarUrl = updated.AvatarUrl;
                existing.CanDelete = (myId > 0 && existing.AuthorId == myId)
                    || (!string.IsNullOrEmpty(myName) && string.Equals(existing.AuthorName, myName, StringComparison.OrdinalIgnoreCase));
                existing.RefreshRating();
            }
        }

        for (int i = 0; i < data.Reviews.Count && i < 50; i++)
        {
            RoReviewService.ReviewEntry item = data.Reviews[i];
            if (_reviews.Any(r => r.Id == item.Id))
                continue;
            item.CanDelete = (myId > 0 && item.AuthorId == myId)
                || (!string.IsNullOrEmpty(myName) && string.Equals(item.AuthorName, myName, StringComparison.OrdinalIgnoreCase));
            _reviews.Insert(Math.Min(i, _reviews.Count), item);
        }

        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunLivePollingLoopAsync()
    {
        while (!_closed && !_ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), _ct);
                if (_closed || _ct.IsCancellationRequested)
                    break;
                if (!_panelVisible || _placeId <= 0)
                    continue;

                await RoReviewService.FetchReviewsAsync(_placeId, _ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionReviewPanel", "Live poll error: " + ex.Message);
            }
        }
    }

    private void OnAuthChanged()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            AuthStatus.Text = string.Empty;
            long myId = RoReviewService.SessionUserId;
            bool sessionAdded = _observedSessionUserId == 0 && myId > 0;
            bool sessionChanged = _observedSessionUserId > 0 && myId != _observedSessionUserId;
            _observedSessionUserId = myId;

            if (sessionChanged)
            {
                _profileVoteVersion++;
                if (_profileVotePending)
                {
                    _upCount = _profileUpBaseline;
                    _downCount = _profileDownBaseline;
                }
                _profileVotePending = false;
                _mutationSequence++;
                foreach (RoReviewService.ReviewEntry review in _reviews)
                {
                    if (_reviewVoteBaselines.TryGetValue(review.Id, out (string? Vote, int Up, int Down) baseline))
                    {
                        review.UpCount = baseline.Up;
                        review.DownCount = baseline.Down;
                    }
                    review.UserVote = null;
                    review.RefreshRating();
                }
                _mutationVersions.Clear();
                _reviewVoteBaselines.Clear();
                if (_pendingSubmitEntry is { } pending)
                {
                    if (_reviews.Remove(pending))
                        _reviewCount = Math.Max(0, _reviewCount - 1);
                    if (string.IsNullOrEmpty(ReviewInput.Text))
                        ReviewInput.Text = _pendingSubmitContent;
                    _pendingSubmitEntry = null;
                    _pendingSubmitContent = string.Empty;
                }
                _submitGeneration++;
                _submitting = false;
                SubmitButton.IsEnabled = true;
                _userVote = null;
            }

            string? myName = RoReviewService.SessionUsername;
            foreach (RoReviewService.ReviewEntry review in _reviews)
            {
                if (sessionAdded && review.Id.StartsWith("temp_", StringComparison.Ordinal))
                {
                    review.AuthorId = myId;
                    review.AuthorName = string.IsNullOrWhiteSpace(myName) ? Strings.Ui_Session_You : myName;
                }
                review.CanDelete = !review.Id.StartsWith("temp_", StringComparison.Ordinal)
                    && ((myId > 0 && review.AuthorId == myId)
                        || (!string.IsNullOrEmpty(myName) && string.Equals(review.AuthorName, myName, StringComparison.OrdinalIgnoreCase)));
            }
            UpdateRatingDisplay();
            if (_panelVisible && _placeId > 0)
                _ = RoReviewService.FetchReviewsAsync(_placeId, _ct);
        });
    }

    private async Task LoadAsync()
    {
        if (_closed || _placeId <= 0)
            return;
        if (_loading)
        {
            _reloadPending = true;
            return;
        }
        _loading = true;
        long placeId = _placeId;
        long placeGeneration = _placeGeneration;
        try
        {
            if (!RoReviewService.IsAuthenticated)
                _ = AuthenticateAndClearStatusAsync();
            else
                AuthStatus.Text = string.Empty;

            RoReviewService.ReviewData? result = await RoReviewService.FetchReviewsAsync(placeId, _ct);
            if (_closed || _ct.IsCancellationRequested)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || _placeId != placeId || _placeGeneration != placeGeneration)
                    return;
                if (result == null)
                    EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                else
                    ApplyReviewData(result);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionReviewPanel", "Load failed: " + ex.Message);
        }
        finally
        {
            _loading = false;
            if (_reloadPending && _panelVisible && !_closed)
            {
                _reloadPending = false;
                _ = Dispatcher.BeginInvoke(() => _ = LoadAsync());
            }
        }
    }

    private async Task<bool> AuthenticateAndClearStatusAsync(RoReviewService.MutationContext? context = null)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
                AuthStatus.Text = Strings.Ui_Session_SigningIn;
        });

        bool authenticated = false;
        try
        {
            authenticated = await RoReviewService.AuthenticateAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionReviewPanel", "Authentication attempt failed: " + ex.Message);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_closed)
                    AuthStatus.Text = string.Empty;
            });
        }
        return authenticated;
    }

    private void UpdateRatingDisplay()
    {
        UpCountText.Text = _upCount.ToString(Locale.CurrentCulture);
        DownCountText.Text = _downCount.ToString(Locale.CurrentCulture);
        ReviewCountText.Text = _reviewCount > 0
            ? _reviewCount.ToString(Locale.CurrentCulture) + " " + (_reviewCount == 1 ? Strings.Ui_Session_Review : Strings.Ui_Session_Reviews)
            : string.Empty;
        ApplyVoteHighlight();
    }

    private void ApplyVoteHighlight()
    {
        bool isUp = string.Equals(_userVote, "up", StringComparison.OrdinalIgnoreCase);
        bool isDown = string.Equals(_userVote, "down", StringComparison.OrdinalIgnoreCase);

        VoteUpButton.Appearance = isUp ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Transparent;
        VoteDownButton.Appearance = isDown ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Transparent;

        VoteUpIcon.SetResourceReference(TextBlock.ForegroundProperty, isUp ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        UpCountText.SetResourceReference(TextBlock.ForegroundProperty, isUp ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

        VoteDownIcon.SetResourceReference(TextBlock.ForegroundProperty, isDown ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        DownCountText.SetResourceReference(TextBlock.ForegroundProperty, isDown ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
    }

    private void OnVoteUp(object sender, RoutedEventArgs e)
    {
        HandleVoteOptimistic("up");
    }

    private void OnVoteDown(object sender, RoutedEventArgs e)
    {
        HandleVoteOptimistic("down");
    }

    private void HandleVoteOptimistic(string vote)
    {
        if (_closed || _placeId <= 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        if (!_profileVotePending)
        {
            _profileVoteBaseline = _userVote;
            _profileUpBaseline = _upCount;
            _profileDownBaseline = _downCount;
        }
        _profileVotePending = true;
        long version = ++_profileVoteVersion;
        long sessionGeneration = RoReviewService.SessionGeneration;
        string? baselineVote = _profileVoteBaseline;
        int baselineUp = _profileUpBaseline;
        int baselineDown = _profileDownBaseline;
        long placeId = _placeId;
        long placeGeneration = _placeGeneration;

        if (string.Equals(vote, "up", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(_userVote, "up", StringComparison.OrdinalIgnoreCase))
            {
                _userVote = null;
                _upCount = Math.Max(0, _upCount - 1);
            }
            else
            {
                if (string.Equals(_userVote, "down", StringComparison.OrdinalIgnoreCase))
                    _downCount = Math.Max(0, _downCount - 1);
                _userVote = "up";
                _upCount++;
            }
        }
        else
        {
            if (string.Equals(_userVote, "down", StringComparison.OrdinalIgnoreCase))
            {
                _userVote = null;
                _downCount = Math.Max(0, _downCount - 1);
            }
            else
            {
                if (string.Equals(_userVote, "up", StringComparison.OrdinalIgnoreCase))
                    _upCount = Math.Max(0, _upCount - 1);
                _userVote = "down";
                _downCount++;
            }
        }

        UpdateRatingDisplay();
        _ = RunProfileVoteAsync(vote, mutationContext, version, sessionGeneration, baselineVote, baselineUp, baselineDown, placeId, placeGeneration);
    }

    private async Task RunProfileVoteAsync(string vote, RoReviewService.MutationContext mutationContext, long version, long sessionGeneration, string? baselineVote, int baselineUp, int baselineDown, long placeId, long placeGeneration)
    {
        try
        {
            if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
            {
                RollbackVote(baselineVote, baselineUp, baselineDown, version, sessionGeneration, placeId, placeGeneration);
                return;
            }
            bool ok = await RoReviewService.VoteAsync(placeId, vote, mutationContext, _ct).ConfigureAwait(false);
            if (!ok)
            {
                RollbackVote(baselineVote, baselineUp, baselineDown, version, sessionGeneration, placeId, placeGeneration);
                return;
            }
            CompleteProfileVote(version, placeId, placeGeneration);
            await RoReviewService.FetchReviewsAsync(placeId, _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionReviewPanel", "Vote background failed: " + ex.Message);
            RollbackVote(baselineVote, baselineUp, baselineDown, version, sessionGeneration, placeId, placeGeneration);
        }
    }

    private void RollbackVote(string? prevVote, int prevUp, int prevDown, long version, long sessionGeneration, long placeId, long placeGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentProfileVote(version, placeId, placeGeneration))
                return;
            _profileVotePending = false;
            _profileVoteBaseline = null;
            _userVote = sessionGeneration == RoReviewService.SessionGeneration ? prevVote : null;
            _upCount = prevUp;
            _downCount = prevDown;
            UpdateRatingDisplay();
        });
    }

    private bool IsCurrentProfileVote(long version, long placeId, long placeGeneration) =>
        !_closed && _placeId == placeId && _placeGeneration == placeGeneration && _profileVoteVersion == version;

    private void CompleteProfileVote(long version, long placeId, long placeGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentProfileVote(version, placeId, placeGeneration))
                return;
            _profileVotePending = false;
            _profileVoteBaseline = null;
        });
    }

    private long NextReviewMutationVersion(string reviewId)
    {
        long version = ++_mutationSequence;
        _mutationVersions[reviewId] = version;
        return version;
    }

    private bool IsCurrentReviewMutation(string reviewId, long version, long placeId, long placeGeneration) =>
        !_closed
        && _placeId == placeId
        && _placeGeneration == placeGeneration
        && _mutationVersions.TryGetValue(reviewId, out long currentVersion)
        && currentVersion == version;

    private void CompleteReviewMutation(string reviewId, long version, long placeId, long placeGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentReviewMutation(reviewId, version, placeId, placeGeneration))
                return;
            _mutationVersions.Remove(reviewId);
            _reviewVoteBaselines.Remove(reviewId);
        });
    }

    private void OnDeleteReview(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not RoReviewService.ReviewEntry entry)
            return;
        HandleDeleteReviewOptimistic(entry);
    }

    private void HandleDeleteReviewOptimistic(RoReviewService.ReviewEntry entry)
    {
        if (_closed || _placeId <= 0 || !entry.CanDelete || entry.Id.StartsWith("temp_", StringComparison.Ordinal))
            return;

        int index = _reviews.IndexOf(entry);
        if (index < 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        _reviews.RemoveAt(index);
        _reviewCount = Math.Max(0, _reviewCount - 1);
        UpdateRatingDisplay();
        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        long placeId = _placeId;
        long placeGeneration = _placeGeneration;
        long version = NextReviewMutationVersion(entry.Id);
        _ = RunDeleteAsync(entry, index, mutationContext, version, placeId, placeGeneration);
    }

    private async Task RunDeleteAsync(RoReviewService.ReviewEntry entry, int index, RoReviewService.MutationContext mutationContext, long version, long placeId, long placeGeneration)
    {
        try
        {
            if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
            {
                RollbackDelete(entry, index, version, placeId, placeGeneration);
                return;
            }
            bool ok = await RoReviewService.DeleteReviewAsync(placeId, entry.Id, mutationContext, _ct).ConfigureAwait(false);
            if (!ok)
            {
                RollbackDelete(entry, index, version, placeId, placeGeneration);
                return;
            }
            CompleteReviewMutation(entry.Id, version, placeId, placeGeneration);
            await RoReviewService.FetchReviewsAsync(placeId, _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionReviewPanel", "Delete background failed: " + ex.Message);
            RollbackDelete(entry, index, version, placeId, placeGeneration);
        }
    }

    private void RollbackDelete(RoReviewService.ReviewEntry entry, int index, long version, long placeId, long placeGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentReviewMutation(entry.Id, version, placeId, placeGeneration))
                return;
            _mutationVersions.Remove(entry.Id);
            _reviewVoteBaselines.Remove(entry.Id);
            long myId = RoReviewService.SessionUserId;
            string? myName = RoReviewService.SessionUsername;
            entry.CanDelete = (myId > 0 && entry.AuthorId == myId)
                || (!string.IsNullOrEmpty(myName) && string.Equals(entry.AuthorName, myName, StringComparison.OrdinalIgnoreCase));
            if (!_reviews.Any(review => string.Equals(review.Id, entry.Id, StringComparison.Ordinal)))
            {
                int insertAt = Math.Clamp(index, 0, _reviews.Count);
                _reviews.Insert(insertAt, entry);
                _reviewCount++;
            }
            UpdateRatingDisplay();
            EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnReviewVoteUp(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not RoReviewService.ReviewEntry entry)
            return;
        HandleReviewVoteOptimistic(entry, "up");
    }

    private void OnReviewVoteDown(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not RoReviewService.ReviewEntry entry)
            return;
        HandleReviewVoteOptimistic(entry, "down");
    }

    private void HandleReviewVoteOptimistic(RoReviewService.ReviewEntry entry, string vote)
    {
        if (_closed || _placeId <= 0 || entry.Id.StartsWith("temp_", StringComparison.Ordinal) || !_reviews.Contains(entry))
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        (string? Vote, int Up, int Down) baseline = (entry.UserVote, entry.UpCount, entry.DownCount);
        if (_reviewVoteBaselines.TryGetValue(entry.Id, out (string? Vote, int Up, int Down) savedBaseline))
            baseline = savedBaseline;
        else
            _reviewVoteBaselines[entry.Id] = baseline;

        long placeId = _placeId;
        long placeGeneration = _placeGeneration;
        long mutationVersion = NextReviewMutationVersion(entry.Id);
        long sessionGeneration = RoReviewService.SessionGeneration;

        if (string.Equals(vote, "up", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(entry.UserVote, "up", StringComparison.OrdinalIgnoreCase))
            {
                entry.UserVote = null;
                entry.UpCount = Math.Max(0, entry.UpCount - 1);
            }
            else
            {
                if (string.Equals(entry.UserVote, "down", StringComparison.OrdinalIgnoreCase))
                    entry.DownCount = Math.Max(0, entry.DownCount - 1);
                entry.UserVote = "up";
                entry.UpCount++;
            }
        }
        else
        {
            if (string.Equals(entry.UserVote, "down", StringComparison.OrdinalIgnoreCase))
            {
                entry.UserVote = null;
                entry.DownCount = Math.Max(0, entry.DownCount - 1);
            }
            else
            {
                if (string.Equals(entry.UserVote, "up", StringComparison.OrdinalIgnoreCase))
                    entry.UpCount = Math.Max(0, entry.UpCount - 1);
                entry.UserVote = "down";
                entry.DownCount++;
            }
        }

        entry.RefreshRating();
        _ = RunReviewVoteAsync(entry, vote, baseline, mutationContext, mutationVersion, sessionGeneration, placeId, placeGeneration);
    }

    private async Task RunReviewVoteAsync(RoReviewService.ReviewEntry entry, string vote, (string? Vote, int Up, int Down) baseline, RoReviewService.MutationContext mutationContext, long version, long sessionGeneration, long placeId, long placeGeneration)
    {
        try
        {
            if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
            {
                RollbackReviewVote(entry, baseline.Vote, baseline.Up, baseline.Down, version, sessionGeneration, placeId, placeGeneration);
                return;
            }
            bool ok = await RoReviewService.RateReviewAsync(placeId, entry.Id, vote, mutationContext, _ct).ConfigureAwait(false);
            if (!ok)
            {
                RollbackReviewVote(entry, baseline.Vote, baseline.Up, baseline.Down, version, sessionGeneration, placeId, placeGeneration);
                return;
            }
            CompleteReviewMutation(entry.Id, version, placeId, placeGeneration);
            await RoReviewService.FetchReviewsAsync(placeId, _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionReviewPanel", "Rate review background failed: " + ex.Message);
            RollbackReviewVote(entry, baseline.Vote, baseline.Up, baseline.Down, version, sessionGeneration, placeId, placeGeneration);
        }
    }

    private void RollbackReviewVote(RoReviewService.ReviewEntry entry, string? prevVote, int prevUp, int prevDown, long version, long sessionGeneration, long placeId, long placeGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentReviewMutation(entry.Id, version, placeId, placeGeneration) || !_reviews.Contains(entry))
                return;
            _mutationVersions.Remove(entry.Id);
            _reviewVoteBaselines.Remove(entry.Id);
            entry.UserVote = sessionGeneration == RoReviewService.SessionGeneration ? prevVote : null;
            entry.UpCount = prevUp;
            entry.DownCount = prevDown;
            entry.RefreshRating();
        });
    }

    private void OnSubmitReview(object sender, RoutedEventArgs e)
    {
        HandleSubmitOptimistic();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            HandleSubmitOptimistic();
        }
    }

    private void HandleSubmitOptimistic()
    {
        if (_submitting || _closed || _placeId <= 0)
            return;
        string content = ReviewInput.Text?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        _submitting = true;
        SubmitButton.IsEnabled = false;
        ReviewInput.Text = string.Empty;

        string author = RoReviewService.SessionUsername ?? string.Empty;
        if (string.IsNullOrEmpty(author))
            author = Strings.Ui_Session_You;
        long myId = RoReviewService.SessionUserId;
        string avatar = myId > 0 ? RoReviewService.GetCachedAvatar(myId) : string.Empty;

        RoReviewService.ReviewEntry entry = new()
        {
            Id = "temp_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            AuthorId = myId,
            AuthorName = author,
            AvatarUrl = avatar,
            Content = content,
            CreatedAt = Strings.Ui_Session_JustNow,
            CanDelete = false
        };
        entry.RefreshRating();

        long placeId = _placeId;
        long placeGeneration = _placeGeneration;
        long submitGeneration = ++_submitGeneration;
        _pendingSubmitEntry = entry;
        _pendingSubmitContent = content;
        _reviews.Insert(0, entry);
        _reviewCount++;
        UpdateRatingDisplay();
        EmptyPanel.Visibility = Visibility.Collapsed;
        _ = RunSubmitAsync(entry, content, mutationContext, submitGeneration, placeId, placeGeneration);
    }

    private async Task RunSubmitAsync(RoReviewService.ReviewEntry entry, string content, RoReviewService.MutationContext mutationContext, long submitGeneration, long placeId, long placeGeneration)
    {
        bool submitted = false;
        try
        {
            if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
            {
                RollbackSubmit(entry, content, submitGeneration, placeId, placeGeneration);
                return;
            }
            if (!await Dispatcher.InvokeAsync(() => IsCurrentSubmit(entry, submitGeneration, placeId, placeGeneration)))
                return;

            if (!await RoReviewService.SubmitReviewAsync(placeId, content, mutationContext, _ct).ConfigureAwait(false))
            {
                RollbackSubmit(entry, content, submitGeneration, placeId, placeGeneration);
                return;
            }
            submitted = true;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentSubmit(entry, submitGeneration, placeId, placeGeneration))
                    return;
                _reviews.Remove(entry);
                _pendingSubmitEntry = null;
                _pendingSubmitContent = string.Empty;
                EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            });

            RoReviewService.ReviewData? refreshed = await RoReviewService.FetchReviewsAsync(placeId, _ct).ConfigureAwait(false);
            if (refreshed != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentSubmitOperation(submitGeneration, placeId, placeGeneration))
                        return;
                    if (refreshed.SessionGeneration == RoReviewService.SessionGeneration)
                        ApplyReviewData(refreshed);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionReviewPanel", "Submit background failed: " + ex.Message);
            if (!submitted)
                RollbackSubmit(entry, content, submitGeneration, placeId, placeGeneration);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_closed || _placeId != placeId || _placeGeneration != placeGeneration || _submitGeneration != submitGeneration)
                    return;
                if (ReferenceEquals(_pendingSubmitEntry, entry))
                {
                    _pendingSubmitEntry = null;
                    _pendingSubmitContent = string.Empty;
                }
                _submitting = false;
                SubmitButton.IsEnabled = true;
            });
        }
    }

    private bool IsCurrentSubmitOperation(long generation, long placeId, long placeGeneration) =>
        !_closed && _placeId == placeId && _placeGeneration == placeGeneration && _submitGeneration == generation;

    private bool IsCurrentSubmit(RoReviewService.ReviewEntry entry, long generation, long placeId, long placeGeneration) =>
        IsCurrentSubmitOperation(generation, placeId, placeGeneration) && ReferenceEquals(_pendingSubmitEntry, entry);

    private void RollbackSubmit(RoReviewService.ReviewEntry entry, string restoredText, long generation, long placeId, long placeGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentSubmit(entry, generation, placeId, placeGeneration))
                return;
            if (_reviews.Remove(entry))
                _reviewCount = Math.Max(0, _reviewCount - 1);
            if (string.IsNullOrEmpty(ReviewInput.Text))
            {
                ReviewInput.Text = restoredText;
                ReviewInput.Focus();
                ReviewInput.CaretIndex = restoredText.Length;
            }
            _pendingSubmitEntry = null;
            _pendingSubmitContent = string.Empty;
            UpdateRatingDisplay();
            EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnRoReviewLinkClicked(object sender, MouseButtonEventArgs e)
    {
        Utilities.OpenWebLink("https://github.com/Hermivore8151/RoReview");
    }
}
