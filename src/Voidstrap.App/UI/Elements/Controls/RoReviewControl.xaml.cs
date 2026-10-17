using System;
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
    private readonly ObservableCollection<RoReviewService.ReviewEntry> _reviews = new();
    private long _activePlaceId;
    private string? _userVote;
    private int _upCount;
    private int _downCount;
    private int _reviewCount;
    private bool _loading;
    private bool _submitting;
    private bool _isUnloaded;

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
        RoReviewService.AuthenticationChanged += OnAuthChanged;
        RoReviewService.ReviewsUpdated += OnReviewsUpdated;
        _ = ReloadAsync();
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        RoReviewService.AuthenticationChanged -= OnAuthChanged;
        RoReviewService.ReviewsUpdated -= OnReviewsUpdated;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
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

    private void OnAuthChanged()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded)
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

    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        CancellationTokenSource cts = new();
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
                        "https://games.roblox.com/v1/games?universeIds=" + UniverseId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
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

        if (targetPlaceId <= 0 || ct.IsCancellationRequested || _isUnloaded)
            return;

        _activePlaceId = targetPlaceId;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || ct.IsCancellationRequested)
                return;
            _reviews.Clear();
            _userVote = null;
            _upCount = 0;
            _downCount = 0;
            _reviewCount = 0;
            UpdateRatingDisplay();
            EmptyPanel.Visibility = Visibility.Collapsed;
        });
        await LoadReviewsAsync(targetPlaceId, ct).ConfigureAwait(false);
        _ = RunLivePollingLoopAsync(targetPlaceId, ct);
    }

    private async Task RunLivePollingLoopAsync(long placeId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isUnloaded)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _isUnloaded || placeId != _activePlaceId)
                    break;

                await RoReviewService.FetchReviewsAsync(placeId, ct).ConfigureAwait(false);
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
        if (_loading || _isUnloaded)
            return;
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
                _ = RoReviewService.AuthenticateAsync(ct);

            RoReviewService.ReviewData? result = await RoReviewService.FetchReviewsAsync(placeId, ct).ConfigureAwait(false);
            if (result == null || ct.IsCancellationRequested || _isUnloaded)
                return;

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
        if (_isUnloaded || _activePlaceId <= 0)
            return;

        string? prevVote = _userVote;
        int prevUp = _upCount;
        int prevDown = _downCount;
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

        CancellationToken ct = _loadCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackVote(prevVote, prevUp, prevDown);
                        return;
                    }
                }
                bool ok = await RoReviewService.VoteAsync(currentPlaceId, vote, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackVote(prevVote, prevUp, prevDown);
                    return;
                }
                await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Vote background failed: " + ex.Message);
                RollbackVote(prevVote, prevUp, prevDown);
            }
        }, ct);
    }

    private void RollbackVote(string? prevVote, int prevUp, int prevDown)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded)
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
        if (_isUnloaded || _activePlaceId <= 0)
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

        long currentPlaceId = _activePlaceId;
        CancellationToken ct = _loadCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackDelete(entry, index);
                        return;
                    }
                }
                bool ok = await RoReviewService.DeleteReviewAsync(currentPlaceId, entry.Id, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackDelete(entry, index);
                    return;
                }
                await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Delete background failed: " + ex.Message);
                RollbackDelete(entry, index);
            }
        }, ct);
    }

    private void RollbackDelete(RoReviewService.ReviewEntry entry, int index)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded)
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
        if (_isUnloaded || _activePlaceId <= 0)
            return;

        string? prevVote = entry.UserVote;
        int prevUp = entry.UpCount;
        int prevDown = entry.DownCount;
        long currentPlaceId = _activePlaceId;

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

        CancellationToken ct = _loadCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackReviewVote(entry, prevVote, prevUp, prevDown);
                        return;
                    }
                }
                bool ok = await RoReviewService.RateReviewAsync(currentPlaceId, entry.Id, vote, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackReviewVote(entry, prevVote, prevUp, prevDown);
                    return;
                }
                await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RoReviewControl", "Rate review background failed: " + ex.Message);
                RollbackReviewVote(entry, prevVote, prevUp, prevDown);
            }
        }, ct);
    }

    private void RollbackReviewVote(RoReviewService.ReviewEntry entry, string? prevVote, int prevUp, int prevDown)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded)
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
        if (_submitting || _isUnloaded || _activePlaceId <= 0)
            return;
        string content = ReviewInput.Text?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return;

        _submitting = true;
        SubmitButton.IsEnabled = false;
        ReviewInput.Text = string.Empty;

        string author = RoReviewService.SessionUsername ?? string.Empty;
        if (string.IsNullOrEmpty(author))
            author = "You";
        long myId = RoReviewService.SessionUserId;
        string avatar = myId > 0 ? RoReviewService.GetCachedAvatar(myId) : string.Empty;

        RoReviewService.ReviewEntry entry = new()
        {
            Id = "temp_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            AuthorId = myId,
            AuthorName = author,
            AvatarUrl = avatar,
            Content = content,
            CreatedAt = "just now",
            CanDelete = true
        };
        entry.RefreshRating();

        _reviews.Insert(0, entry);
        _reviewCount++;
        UpdateRatingDisplay();
        EmptyPanel.Visibility = Visibility.Collapsed;

        long currentPlaceId = _activePlaceId;
        CancellationToken ct = _loadCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!RoReviewService.IsAuthenticated)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = Strings.Ui_Session_SigningIn;
                    });
                    bool authed = await RoReviewService.AuthenticateAsync(ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isUnloaded)
                            AuthStatus.Text = string.Empty;
                    });
                    if (!authed)
                    {
                        RollbackSubmit(entry, content);
                        return;
                    }
                }
                bool ok = await RoReviewService.SubmitReviewAsync(currentPlaceId, content, ct).ConfigureAwait(false);
                if (!ok)
                {
                    RollbackSubmit(entry, content);
                    return;
                }

                RoReviewService.ReviewData? refreshed = await RoReviewService.FetchReviewsAsync(currentPlaceId, ct).ConfigureAwait(false);
                if (refreshed != null)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_isUnloaded || ct.IsCancellationRequested)
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
                App.Logger.WriteLine("RoReviewControl", "Submit background failed: " + ex.Message);
                RollbackSubmit(entry, content);
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _submitting = false;
                    if (!_isUnloaded)
                        SubmitButton.IsEnabled = true;
                });
            }
        }, ct);
    }

    private void RollbackSubmit(RoReviewService.ReviewEntry entry, string restoredText)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded)
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
