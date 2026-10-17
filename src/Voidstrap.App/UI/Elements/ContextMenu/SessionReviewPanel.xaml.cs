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
    private bool _submitting;
    private bool _closed;

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
        _placeId = placeId;
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            GameNameText.Text = gameName;
            HeaderPanel.Visibility = Visibility.Visible;
        }
        if (IsLoaded && !_closed)
            _ = LoadAsync();
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        _ = LoadAsync();
        _ = RunLivePollingLoopAsync();
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        _closed = true;
        RoReviewService.AuthenticationChanged -= OnAuthChanged;
        RoReviewService.ReviewsUpdated -= OnReviewsUpdated;
        Loaded -= OnPanelLoaded;
        Closed -= OnPanelClosed;
        _lifetime.Cancel();
        _lifetime.Dispose();
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
        _userVote = data.UserVote;
        _upCount = data.UpCount;
        _downCount = data.DownCount;
        _reviewCount = data.ReviewCount;
        UpdateRatingDisplay();

        long myId = RoReviewService.SessionUserId;
        string? myName = RoReviewService.SessionUsername;

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
                existing.UpCount = updated.UpCount;
                existing.DownCount = updated.DownCount;
                existing.UserVote = updated.UserVote;
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
                await Task.Delay(TimeSpan.FromSeconds(8), _ct).ConfigureAwait(false);
                if (_closed || _ct.IsCancellationRequested || _placeId <= 0)
                    break;

                await RoReviewService.FetchReviewsAsync(_placeId, _ct).ConfigureAwait(false);
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
            string? myName = RoReviewService.SessionUsername;
            foreach (RoReviewService.ReviewEntry r in _reviews)
            {
                bool canDel = (myId > 0 && r.AuthorId == myId)
                    || (!string.IsNullOrEmpty(myName) && string.Equals(r.AuthorName, myName, StringComparison.OrdinalIgnoreCase))
                    || r.Id.StartsWith("temp_", StringComparison.Ordinal);
                r.CanDelete = canDel;
            }
        });
    }

    private async Task LoadAsync()
    {
        if (_loading || _closed || _placeId <= 0)
            return;
        _loading = true;
        try
        {
            if (!RoReviewService.IsAuthenticated)
            {
                AuthStatus.Text = Strings.Ui_Session_SigningIn;
                _ = RoReviewService.AuthenticateAsync(_ct);
            }
            else
            {
                AuthStatus.Text = string.Empty;
            }

            RoReviewService.ReviewData? result = await RoReviewService.FetchReviewsAsync(_placeId, _ct);
            if (result == null || _closed)
                return;

            ApplyReviewData(result);
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
        }
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

        string? prevVote = _userVote;
        int prevUp = _upCount;
        int prevDown = _downCount;

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

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(_ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackVote(prevVote, prevUp, prevDown);
                        return;
                    }
                }
                bool ok = await RoReviewService.VoteAsync(_placeId, vote, _ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackVote(prevVote, prevUp, prevDown);
                    return;
                }
                await RoReviewService.FetchReviewsAsync(_placeId, _ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionReviewPanel", "Vote background failed: " + ex.Message);
                RollbackVote(prevVote, prevUp, prevDown);
            }
        }, _ct);
    }

    private void RollbackVote(string? prevVote, int prevUp, int prevDown)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            _userVote = prevVote;
            _upCount = prevUp;
            _downCount = prevDown;
            UpdateRatingDisplay();
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
        if (_closed || _placeId <= 0)
            return;

        int index = _reviews.IndexOf(entry);
        if (index < 0)
            return;

        _reviews.RemoveAt(index);
        _reviewCount = Math.Max(0, _reviewCount - 1);
        UpdateRatingDisplay();
        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (entry.Id.StartsWith("temp_", StringComparison.Ordinal))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(_ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackDelete(entry, index);
                        return;
                    }
                }
                bool ok = await RoReviewService.DeleteReviewAsync(_placeId, entry.Id, _ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackDelete(entry, index);
                    return;
                }
                await RoReviewService.FetchReviewsAsync(_placeId, _ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionReviewPanel", "Delete background failed: " + ex.Message);
                RollbackDelete(entry, index);
            }
        }, _ct);
    }

    private void RollbackDelete(RoReviewService.ReviewEntry entry, int index)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            int insertAt = Math.Clamp(index, 0, _reviews.Count);
            _reviews.Insert(insertAt, entry);
            _reviewCount++;
            UpdateRatingDisplay();
            EmptyPanel.Visibility = Visibility.Collapsed;
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
        if (_closed || _placeId <= 0)
            return;

        string? prevVote = entry.UserVote;
        int prevUp = entry.UpCount;
        int prevDown = entry.DownCount;

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

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(_ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackReviewVote(entry, prevVote, prevUp, prevDown);
                        return;
                    }
                }
                bool ok = await RoReviewService.RateReviewAsync(_placeId, entry.Id, vote, _ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackReviewVote(entry, prevVote, prevUp, prevDown);
                    return;
                }
                await RoReviewService.FetchReviewsAsync(_placeId, _ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionReviewPanel", "Rate review background failed: " + ex.Message);
                RollbackReviewVote(entry, prevVote, prevUp, prevDown);
            }
        }, _ct);
    }

    private void RollbackReviewVote(RoReviewService.ReviewEntry entry, string? prevVote, int prevUp, int prevDown)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            entry.UserVote = prevVote;
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
            CanDelete = true
        };
        entry.RefreshRating();

        _reviews.Insert(0, entry);
        _reviewCount++;
        UpdateRatingDisplay();
        EmptyPanel.Visibility = Visibility.Collapsed;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(_ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackSubmit(entry, content);
                        return;
                    }
                }
                bool ok = await RoReviewService.SubmitReviewAsync(_placeId, content, _ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackSubmit(entry, content);
                    return;
                }

                RoReviewService.ReviewData? refreshed = await RoReviewService.FetchReviewsAsync(_placeId, _ct).ConfigureAwait(false);
                if (refreshed != null)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_closed)
                            return;
                        _userVote = refreshed.UserVote;
                        _upCount = refreshed.UpCount;
                        _downCount = refreshed.DownCount;
                        _reviewCount = refreshed.ReviewCount;
                        UpdateRatingDisplay();
                        _reviews.Clear();
                        foreach (RoReviewService.ReviewEntry r in refreshed.Reviews.Take(50))
                        {
                            _reviews.Add(r);
                        }
                        EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionReviewPanel", "Submit background failed: " + ex.Message);
                RollbackSubmit(entry, content);
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _submitting = false;
                    if (!_closed)
                        SubmitButton.IsEnabled = true;
                });
            }
        }, _ct);
    }

    private void RollbackSubmit(RoReviewService.ReviewEntry entry, string restoredText)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            _reviews.Remove(entry);
            _reviewCount = Math.Max(0, _reviewCount - 1);
            if (string.IsNullOrEmpty(ReviewInput.Text))
            {
                ReviewInput.Text = restoredText;
                ReviewInput.Focus();
                ReviewInput.CaretIndex = restoredText.Length;
            }
            UpdateRatingDisplay();
            EmptyPanel.Visibility = _reviews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnRoReviewLinkClicked(object sender, MouseButtonEventArgs e)
    {
        Utilities.OpenWebLink("https://github.com/Hermivore8151/RoReview");
    }
}
