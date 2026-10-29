using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.Resources;

namespace Voidstrap.UI.Elements.Controls;

public partial class RoReviewControl : UserControl
{
    public static readonly DependencyProperty PlaceIdProperty =
        DependencyProperty.Register(nameof(PlaceId), typeof(long), typeof(RoReviewControl),
            new PropertyMetadata(0L, (d, e) => ((RoReviewControl)d).OnPlaceIdChanged((long)e.NewValue)));

    public static readonly DependencyProperty UniverseIdProperty =
        DependencyProperty.Register(nameof(UniverseId), typeof(long), typeof(RoReviewControl),
            new PropertyMetadata(0L, (d, e) => ((RoReviewControl)d).OnUniverseIdChanged((long)e.NewValue)));

    public long PlaceId
    {
        get => (long)GetValue(PlaceIdProperty);
        set => SetValue(PlaceIdProperty, value);
    }

    public long UniverseId
    {
        get => (long)GetValue(UniverseIdProperty);
        set => SetValue(UniverseIdProperty, value);
    }

    public static readonly DependencyProperty GameNameProperty =
        DependencyProperty.Register(nameof(GameName), typeof(string), typeof(RoReviewControl),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                RoReviewControl ctrl = (RoReviewControl)d;
                string? name = e.NewValue as string;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    ctrl.GameNameText.Text = name;
                }
            }));

    public string GameName
    {
        get => (string)GetValue(GameNameProperty);
        set => SetValue(GameNameProperty, value);
    }

    private CancellationTokenSource? _loadCts;

    private (long, long) _lastRequest;
    private CancellationTokenSource? _lifetimeCts;
    private readonly ObservableCollection<RoReviewService.ReviewEntry> _reviews = new();
    private long _activePlaceId;
    private string? _userVote;
    private int _upCount;
    private int _downCount;
    private int _reviewCount;
    private bool _loading;
    private bool _reloadPending;
    private bool _submitting;
    private bool _subscribed;
    private bool _isUnloaded;
    private bool _profileVotePending;
    private string? _profileVoteBaseline;
    private int _profileUpBaseline;
    private int _profileDownBaseline;
    private long _profileVoteVersion;
    private long _reviewMutationSequence;
    private long _observedSessionUserId = RoReviewService.SessionUserId;
    private long _submitGeneration;
    private readonly Dictionary<string, long> _reviewMutationVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Vote, int Up, int Down)> _reviewVoteBaselines = new(StringComparer.Ordinal);
    private RoReviewService.ReviewEntry? _pendingSubmitEntry;
    private string _pendingSubmitContent = string.Empty;

    public RoReviewControl()
    {
        InitializeComponent();
        ReviewList.ItemsSource = _reviews;
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
    }

    public void SetGame(long universeId, long placeId, string? gameName = null)
    {
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            GameNameText.Text = gameName;
            HeaderPanel.Visibility = Visibility.Visible;
        }

        if (placeId > 0)
        {
            PlaceId = placeId;
        }
        else if (universeId > 0)
        {
            UniverseId = universeId;
        }

        if (IsLoaded && !_isUnloaded)
            _ = ReloadAsync();
    }

    private void OnPlaceIdChanged(long newPlaceId)
    {
        if (newPlaceId <= 0)
            return;
        if (IsLoaded && !_isUnloaded)
            _ = ReloadAsync();
    }

    private void OnUniverseIdChanged(long newUniverseId)
    {
        if (newUniverseId <= 0)
            return;
        if (PlaceId <= 0 && IsLoaded && !_isUnloaded)
            _ = ReloadAsync();
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        if (_lifetimeCts == null || _lifetimeCts.IsCancellationRequested)
        {
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
            _ = RunLivePollingLoopAsync(_lifetimeCts.Token);
        }
        if (!_subscribed)
        {
            RoReviewService.AuthenticationChanged += OnAuthChanged;
            RoReviewService.ReviewsUpdated += OnReviewsUpdated;
            _subscribed = true;
        }
        if (_observedSessionUserId != RoReviewService.SessionUserId)
            OnAuthChanged();
        _ = ReloadAsync();
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        if (_subscribed)
        {
            RoReviewService.AuthenticationChanged -= OnAuthChanged;
            RoReviewService.ReviewsUpdated -= OnReviewsUpdated;
            _subscribed = false;
        }
        _reloadPending = false;
        _profileVoteVersion++;
        _profileVotePending = false;
        _reviewMutationSequence++;
        _reviewMutationVersions.Clear();
        _reviewVoteBaselines.Clear();
        _submitGeneration++;
        _submitting = false;
        SubmitButton.IsEnabled = true;
        if (_pendingSubmitEntry != null)
        {
            if (_reviews.Remove(_pendingSubmitEntry))
                _reviewCount = Math.Max(0, _reviewCount - 1);
            if (string.IsNullOrEmpty(ReviewInput.Text))
                ReviewInput.Text = _pendingSubmitContent;
            _pendingSubmitEntry = null;
            _pendingSubmitContent = string.Empty;
            UpdateRatingDisplay();
            EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }

    private void OnReviewsUpdated(long placeId, RoReviewService.ReviewData data)
    {
        if (_isUnloaded || placeId != _activePlaceId)
            return;

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || placeId != _activePlaceId)
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
                if (!_reviewMutationVersions.ContainsKey(existing.Id))
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

    private void OnAuthChanged()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded)
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
                _profileVoteBaseline = null;
                _reviewMutationSequence++;
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
                _reviewMutationVersions.Clear();
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
                if (sessionAdded && review.Id.StartsWith("temp_", StringComparison.Ordinal) && ReferenceEquals(_pendingSubmitEntry, review))
                {
                    review.AuthorId = myId;
                    review.AuthorName = string.IsNullOrWhiteSpace(myName) ? Strings.Ui_Session_You : myName;
                }
                review.CanDelete = !review.Id.StartsWith("temp_", StringComparison.Ordinal)
                    && ((myId > 0 && review.AuthorId == myId)
                        || (!string.IsNullOrEmpty(myName) && string.Equals(review.AuthorName, myName, StringComparison.OrdinalIgnoreCase)));
            }
            UpdateRatingDisplay();
            long placeId = _activePlaceId;
            if (placeId > 0)
                _ = RoReviewService.FetchReviewsAsync(placeId, _lifetimeCts?.Token ?? CancellationToken.None);
        });
    }

    private async Task ReloadAsync(bool force = false)
    {
        if (_isUnloaded)
            return;
        (long, long) request = (PlaceId, UniverseId);
        if (!force && request == _lastRequest && _loadCts is { IsCancellationRequested: false })
            return;
        _lastRequest = request;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None);
        _loadCts = cts;
        CancellationToken ct = cts.Token;

        long targetPlaceId = PlaceId;
        if (targetPlaceId <= 0 && UniverseId > 0)
        {
            UniverseDetails? cached = UniverseDetails.LoadFromCache(UniverseId);
            if (cached?.Data != null && cached.Data.RootPlaceId > 0)
            {
                targetPlaceId = cached.Data.RootPlaceId;
            }
            else
            {
                try
                {
                    string json = await Voidstrap.Utility.Http.GetString(
                        "https://games.roblox.com/v1/games?universeIds=" + UniverseId.ToString(CultureInfo.InvariantCulture), ct);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        JsonElement first = arr[0];
                        if (first.TryGetProperty("rootPlaceId", out JsonElement rootPlaceEl) && rootPlaceEl.TryGetInt64(out long rootPlace) && rootPlace > 0)
                        {
                            targetPlaceId = rootPlace;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        if (targetPlaceId <= 0)
        {
            _activePlaceId = 0;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_isUnloaded)
                    return;
                ResetPanelState();
            });
            return;
        }

        if (ct.IsCancellationRequested || _isUnloaded)
            return;

        App.Logger.WriteLine("RoReviewControl", "Loading place " + targetPlaceId.ToString(CultureInfo.InvariantCulture));

        _activePlaceId = targetPlaceId;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || ct.IsCancellationRequested)
                return;
            ResetPanelState();
        });
        await LoadReviewsAsync(targetPlaceId, ct);
    }

    private void ResetPanelState()
    {
        _profileVoteVersion++;
        _profileVotePending = false;
        _reviewMutationSequence++;
        _reviewMutationVersions.Clear();
        _reviewVoteBaselines.Clear();
        _submitGeneration++;
        _submitting = false;
        SubmitButton.IsEnabled = true;
        if (_pendingSubmitEntry != null && string.IsNullOrEmpty(ReviewInput.Text))
            ReviewInput.Text = _pendingSubmitContent;
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

    private async Task RunLivePollingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
                if (ct.IsCancellationRequested)
                    break;

                long placeId = _activePlaceId;
                if (placeId <= 0)
                    continue;

                await RoReviewService.FetchReviewsAsync(placeId, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Live poll error: " + ex.Message);
            }
        }
    }

    private async Task LoadReviewsAsync(long placeId, CancellationToken ct)
    {
        if (_isUnloaded)
            return;
        if (_loading)
        {
            _reloadPending = true;
            return;
        }
        _loading = true;
        try
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!RoReviewService.IsAuthenticated)
                    AuthStatus.Text = Strings.Ui_Session_SigningIn;
                else
                    AuthStatus.Text = string.Empty;
            });

            if (!RoReviewService.IsAuthenticated)
                _ = AuthenticateAndClearStatusAsync();

            RoReviewService.ReviewData? result = await RoReviewService.FetchReviewsAsync(placeId, ct);
            if (ct.IsCancellationRequested || _isUnloaded)
                return;
            if (result == null)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!_isUnloaded && !ct.IsCancellationRequested)
                        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
                return;
            }

            App.Logger.WriteLine("RoReviewControl", "Showing " + result.ReviewCount + " reviews for place " + placeId.ToString(CultureInfo.InvariantCulture));

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_isUnloaded || ct.IsCancellationRequested)
                    return;
                ApplyReviewData(result);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewControl", "Load failed: " + ex.Message);
        }
        finally
        {
            _loading = false;
            if (_reloadPending)
            {
                _reloadPending = false;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!_isUnloaded)
                        _ = ReloadAsync(true);
                });
            }
        }
    }

    private async Task<bool> AuthenticateAndClearStatusAsync(RoReviewService.MutationContext? context = null)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_isUnloaded && !RoReviewService.IsAuthenticated)
                AuthStatus.Text = Strings.Ui_Session_SigningIn;
        });
        bool authenticated = false;
        try
        {
            authenticated = await RoReviewService.AuthenticateAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RoReviewControl", "Authentication attempt failed: " + ex.Message);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_isUnloaded)
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

        VoteUpButton.Appearance = isUp ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Transparent;
        VoteDownButton.Appearance = isDown ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Transparent;

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
        if (_isUnloaded || _activePlaceId <= 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        if (!_profileVotePending)
        {
            _profileVoteBaseline = _userVote;
            _profileUpBaseline = _upCount;
            _profileDownBaseline = _downCount;
        }
        _profileVotePending = true;
        long voteVersion = ++_profileVoteVersion;
        long sessionGeneration = RoReviewService.SessionGeneration;
        string? prevVote = _profileVoteBaseline;
        int prevUp = _profileUpBaseline;
        int prevDown = _profileDownBaseline;
        long currentPlaceId = _activePlaceId;

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

        CancellationToken ct = _lifetimeCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
                {
                    RollbackVote(prevVote, prevUp, prevDown, voteVersion, currentPlaceId, sessionGeneration);
                    return;
                }
                bool ok = await RoReviewService.VoteAsync(currentPlaceId, vote, mutationContext, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackVote(prevVote, prevUp, prevDown, voteVersion, currentPlaceId, sessionGeneration);
                    return;
                }
                CompleteProfileVote(voteVersion, currentPlaceId);
                await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Vote background failed: " + ex.Message);
                RollbackVote(prevVote, prevUp, prevDown, voteVersion, currentPlaceId, sessionGeneration);
            }
        }, ct);
    }

    private void RollbackVote(string? prevVote, int prevUp, int prevDown, long version, long placeId, long sessionGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || _activePlaceId != placeId || _profileVoteVersion != version)
                return;
            _profileVotePending = false;
            _userVote = sessionGeneration == RoReviewService.SessionGeneration ? prevVote : null;
            _upCount = prevUp;
            _downCount = prevDown;
            UpdateRatingDisplay();
        });
    }

    private void CompleteProfileVote(long version, long placeId)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || _activePlaceId != placeId || _profileVoteVersion != version)
                return;
            _profileVotePending = false;
        });
    }

    private long NextReviewMutationVersion(string reviewId)
    {
        long version = ++_reviewMutationSequence;
        _reviewMutationVersions[reviewId] = version;
        return version;
    }

    private bool IsCurrentReviewMutation(string reviewId, long version, long placeId)
    {
        return !_isUnloaded
            && _activePlaceId == placeId
            && _reviewMutationVersions.TryGetValue(reviewId, out long currentVersion)
            && currentVersion == version;
    }

    private void CompleteReviewMutation(string reviewId, long version, long placeId)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentReviewMutation(reviewId, version, placeId))
                return;
            _reviewMutationVersions.Remove(reviewId);
            _reviewVoteBaselines.Remove(reviewId);
        });
    }

    private void OnDeleteReview(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not RoReviewService.ReviewEntry entry || !entry.CanDelete)
            return;
        HandleDeleteReviewOptimistic(entry);
    }

    private void HandleDeleteReviewOptimistic(RoReviewService.ReviewEntry entry)
    {
        if (_isUnloaded || _activePlaceId <= 0 || entry.Id.StartsWith("temp_", StringComparison.Ordinal))
            return;

        int index = _reviews.IndexOf(entry);
        if (index < 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        _reviews.RemoveAt(index);
        _reviewCount = Math.Max(0, _reviewCount - 1);
        UpdateRatingDisplay();
        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (entry.Id.StartsWith("temp_", StringComparison.Ordinal))
            return;

        long currentPlaceId = _activePlaceId;
        long mutationVersion = NextReviewMutationVersion(entry.Id);
        CancellationToken ct = _lifetimeCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
                {
                    RollbackDelete(entry, index, mutationVersion, currentPlaceId);
                    return;
                }
                bool ok = await RoReviewService.DeleteReviewAsync(currentPlaceId, entry.Id, mutationContext, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackDelete(entry, index, mutationVersion, currentPlaceId);
                    return;
                }
                CompleteReviewMutation(entry.Id, mutationVersion, currentPlaceId);
                await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Delete background failed: " + ex.Message);
                RollbackDelete(entry, index, mutationVersion, currentPlaceId);
            }
        }, ct);
    }

    private void RollbackDelete(RoReviewService.ReviewEntry entry, int index, long version, long placeId)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentReviewMutation(entry.Id, version, placeId))
                return;
            _reviewMutationVersions.Remove(entry.Id);
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
        if (_isUnloaded || _activePlaceId <= 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        bool isTemporary = entry.Id.StartsWith("temp_", StringComparison.Ordinal);
        (string? Vote, int Up, int Down) baseline = (entry.UserVote, entry.UpCount, entry.DownCount);
        if (!isTemporary)
        {
            if (_reviewVoteBaselines.TryGetValue(entry.Id, out (string? Vote, int Up, int Down) savedBaseline))
                baseline = savedBaseline;
            else
                _reviewVoteBaselines[entry.Id] = baseline;
        }
        long currentPlaceId = _activePlaceId;
        long mutationVersion = isTemporary ? 0 : NextReviewMutationVersion(entry.Id);
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

        if (entry.Id.StartsWith("temp_", StringComparison.Ordinal))
            return;

        CancellationToken ct = _lifetimeCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
                {
                    RollbackReviewVote(entry, baseline.Vote, baseline.Up, baseline.Down, mutationVersion, currentPlaceId, sessionGeneration);
                    return;
                }
                bool ok = await RoReviewService.RateReviewAsync(currentPlaceId, entry.Id, vote, mutationContext, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackReviewVote(entry, baseline.Vote, baseline.Up, baseline.Down, mutationVersion, currentPlaceId, sessionGeneration);
                    return;
                }
                CompleteReviewMutation(entry.Id, mutationVersion, currentPlaceId);
                await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Rate review background failed: " + ex.Message);
                RollbackReviewVote(entry, baseline.Vote, baseline.Up, baseline.Down, mutationVersion, currentPlaceId, sessionGeneration);
            }
        }, ct);
    }

    private void RollbackReviewVote(RoReviewService.ReviewEntry entry, string? prevVote, int prevUp, int prevDown, long version, long placeId, long sessionGeneration)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentReviewMutation(entry.Id, version, placeId) || !_reviews.Contains(entry))
                return;
            _reviewMutationVersions.Remove(entry.Id);
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
        if (_submitting || _isUnloaded || _activePlaceId <= 0)
            return;
        string content = ReviewInput.Text?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return;

        RoReviewService.MutationContext mutationContext = RoReviewService.CaptureMutationContext();
        _submitting = true;
        SubmitButton.IsEnabled = false;
        long submitGeneration = ++_submitGeneration;
        _pendingSubmitContent = content;
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
        _pendingSubmitEntry = entry;

        _reviews.Insert(0, entry);
        _reviewCount++;
        UpdateRatingDisplay();
        EmptyPanel.Visibility = Visibility.Collapsed;

        long currentPlaceId = _activePlaceId;
        CancellationToken ct = _lifetimeCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            bool submitted = false;
            try
            {
                if (ct.IsCancellationRequested)
                {
                    RollbackSubmit(entry, content, submitGeneration, currentPlaceId);
                    return;
                }
                if (!RoReviewService.IsAuthenticated && !await AuthenticateAndClearStatusAsync(mutationContext).ConfigureAwait(false))
                {
                    RollbackSubmit(entry, content, submitGeneration, currentPlaceId);
                    return;
                }
                bool ok = await RoReviewService.SubmitReviewAsync(currentPlaceId, content, mutationContext, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackSubmit(entry, content, submitGeneration, currentPlaceId);
                    return;
                }
                submitted = true;

                RoReviewService.ReviewData? refreshed = await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_isUnloaded || ct.IsCancellationRequested || submitGeneration != _submitGeneration || _activePlaceId != currentPlaceId)
                        return;
                    _reviews.Remove(entry);
                    if (ReferenceEquals(_pendingSubmitEntry, entry))
                    {
                        _pendingSubmitEntry = null;
                        _pendingSubmitContent = string.Empty;
                    }
                    if (refreshed == null)
                    {
                        UpdateRatingDisplay();
                        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else
                    {
                        ApplyReviewData(refreshed);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Submit background failed: " + ex.Message);
                if (!submitted)
                    RollbackSubmit(entry, content, submitGeneration, currentPlaceId);
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_isUnloaded || submitGeneration != _submitGeneration)
                        return;
                    _submitting = false;
                    SubmitButton.IsEnabled = true;
                    if (ReferenceEquals(_pendingSubmitEntry, entry))
                    {
                        if (submitted)
                        {
                            _reviews.Remove(entry);
                            EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                        }
                        _pendingSubmitEntry = null;
                        _pendingSubmitContent = string.Empty;
                    }
                });
            }
        });
    }

    private void RollbackSubmit(RoReviewService.ReviewEntry entry, string restoredText, long generation, long placeId)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || generation != _submitGeneration || _activePlaceId != placeId)
                return;
            if (_reviews.Remove(entry))
                _reviewCount = Math.Max(0, _reviewCount - 1);
            if (string.IsNullOrEmpty(ReviewInput.Text))
            {
                ReviewInput.Text = restoredText;
                ReviewInput.Focus();
                ReviewInput.CaretIndex = restoredText.Length;
            }
            if (ReferenceEquals(_pendingSubmitEntry, entry))
            {
                _pendingSubmitEntry = null;
                _pendingSubmitContent = string.Empty;
            }
            UpdateRatingDisplay();
            EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnRoReviewLinkClicked(object sender, MouseButtonEventArgs e)
    {
        Utilities.OpenWebLink("https://github.com/Hermivore8151/RoReview");
    }

    private void OnReviewListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller)
            return;

        bool atTop = scroller.VerticalOffset <= 0;
        bool atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight;

        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            UIElement? parent = scroller.Parent as UIElement ?? VisualTreeHelper.GetParent(scroller) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            });
        }
    }
}
