using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Voidstrap.Integrations;
using Voidstrap.Resources;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionReviewPanel : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _ct;
    private long _placeId;
    private string? _userVote;
    private int _upCount;
    private int _downCount;
    private int _reviewCount;
    private List<RoReviewService.ReviewEntry> _reviews = new();
    private bool _loading;
    private bool _closed;

    public SessionReviewPanel(ActivityWatcher? activity = null)
    {
        _ct = _lifetime.Token;
        InitializeComponent();
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
        Loaded += OnPanelLoaded;
        Closed += OnPanelClosed;
    }

    public SessionReviewPanel(long placeId)
    {
        _ct = _lifetime.Token;
        _placeId = placeId;
        InitializeComponent();
        RoReviewService.AuthenticationChanged += OnAuthChanged;
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
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        _closed = true;
        RoReviewService.AuthenticationChanged -= OnAuthChanged;
        Loaded -= OnPanelLoaded;
        Closed -= OnPanelClosed;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnAuthChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            AuthStatus.Text = string.Empty;
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

            _userVote = result.UserVote;
            _upCount = result.UpCount;
            _downCount = result.DownCount;
            _reviewCount = result.ReviewCount;
            UpdateRatingDisplay();

            _reviews = result.Reviews.Take(50).ToList();
            UpdateReviewList();
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

    private void UpdateReviewList()
    {
        if (_reviews.Count > 0)
        {
            ReviewList.ItemsSource = null;
            ReviewList.ItemsSource = _reviews;
            EmptyLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ReviewList.ItemsSource = null;
            EmptyLabel.Visibility = Visibility.Visible;
        }
    }

    private void ApplyVoteHighlight()
    {
        if (string.Equals(_userVote, "up", StringComparison.OrdinalIgnoreCase))
        {
            VoteUpButton.Appearance = Wpf.Ui.Common.ControlAppearance.Success;
            VoteDownButton.Appearance = Wpf.Ui.Common.ControlAppearance.Transparent;
        }
        else if (string.Equals(_userVote, "down", StringComparison.OrdinalIgnoreCase))
        {
            VoteUpButton.Appearance = Wpf.Ui.Common.ControlAppearance.Transparent;
            VoteDownButton.Appearance = Wpf.Ui.Common.ControlAppearance.Caution;
        }
        else
        {
            VoteUpButton.Appearance = Wpf.Ui.Common.ControlAppearance.Transparent;
            VoteDownButton.Appearance = Wpf.Ui.Common.ControlAppearance.Transparent;
        }
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
                }
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
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            _userVote = prevVote;
            _upCount = prevUp;
            _downCount = prevDown;
            UpdateRatingDisplay();
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
        if (_closed || _placeId <= 0)
            return;
        string content = ReviewInput.Text?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return;

        ReviewInput.Text = string.Empty;

        string author = RoReviewService.SessionUsername ?? string.Empty;
        if (string.IsNullOrEmpty(author))
            author = "You";

        RoReviewService.ReviewEntry entry = new()
        {
            Id = "temp_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            AuthorId = RoReviewService.SessionUserId,
            AuthorName = author,
            Content = content,
            CreatedAt = "just now"
        };

        _reviews.Insert(0, entry);
        _reviewCount++;
        UpdateRatingDisplay();
        UpdateReviewList();

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
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionReviewPanel", "Submit background failed: " + ex.Message);
                RollbackSubmit(entry, content);
            }
        }, _ct);
    }

    private void RollbackSubmit(RoReviewService.ReviewEntry entry, string restoredText)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            _reviews.Remove(entry);
            _reviewCount = Math.Max(0, _reviewCount - 1);
            if (string.IsNullOrEmpty(ReviewInput.Text))
                ReviewInput.Text = restoredText;
            UpdateRatingDisplay();
            UpdateReviewList();
        });
    }
}
