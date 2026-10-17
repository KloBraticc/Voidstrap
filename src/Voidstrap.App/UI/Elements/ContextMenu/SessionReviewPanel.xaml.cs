using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.Integrations;
using Voidstrap.Resources;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionReviewPanel : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _ct;
    private long _placeId;
    private string? _userVote;
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
        Loaded += OnPanelLoaded;
        Closed += OnPanelClosed;
    }

    public SessionReviewPanel(long placeId)
    {
        _ct = _lifetime.Token;
        _placeId = placeId;
        InitializeComponent();
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
        Loaded -= OnPanelLoaded;
        Closed -= OnPanelClosed;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task LoadAsync()
    {
        if (_loading || _closed || _placeId <= 0)
            return;
        _loading = true;
        try
        {
            AuthStatus.Text = RoReviewService.IsAuthenticated ? string.Empty : Strings.Ui_Session_SigningIn;
            _ = Task.Run(() => RoReviewService.AuthenticateAsync(_ct), _ct);

            RoReviewService.ReviewData? result = await RoReviewService.FetchReviewsAsync(_placeId, _ct);
            if (result == null || _closed)
                return;

            _userVote = result.UserVote;
            UpCountText.Text = result.UpCount.ToString(Locale.CurrentCulture);
            DownCountText.Text = result.DownCount.ToString(Locale.CurrentCulture);
            ReviewCountText.Text = result.ReviewCount > 0
                ? result.ReviewCount.ToString(Locale.CurrentCulture) + " " + (result.ReviewCount == 1 ? Strings.Ui_Session_Review : Strings.Ui_Session_Reviews)
                : string.Empty;
            ApplyVoteHighlight();

            if (result.Reviews.Count > 0)
            {
                ReviewList.ItemsSource = result.Reviews.Take(50).ToList();
                EmptyLabel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ReviewList.ItemsSource = null;
                EmptyLabel.Visibility = Visibility.Visible;
            }

            if (!RoReviewService.IsAuthenticated)
            {
                await RoReviewService.AuthenticateAsync(_ct);
                if (_closed)
                    return;
            }
            AuthStatus.Text = string.Empty;
            ReviewInput.Visibility = RoReviewService.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed;
            SubmitButton.Visibility = ReviewInput.Visibility;
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
        _ = HandleVoteAsync("up");
    }

    private void OnVoteDown(object sender, RoutedEventArgs e)
    {
        _ = HandleVoteAsync("down");
    }

    private async Task HandleVoteAsync(string vote)
    {
        if (_closed || _placeId <= 0)
            return;
        if (!RoReviewService.IsAuthenticated)
        {
            AuthStatus.Text = Strings.Ui_Session_SigningIn;
            bool ok = await RoReviewService.AuthenticateAsync(_ct);
            if (_closed)
                return;
            AuthStatus.Text = string.Empty;
            if (!ok)
                return;
        }
        bool success = await RoReviewService.VoteAsync(_placeId, vote, _ct);
        if (!success || _closed)
            return;
        _loading = false;
        await LoadAsync();
    }

    private void OnSubmitReview(object sender, RoutedEventArgs e)
    {
        _ = HandleSubmitAsync();
    }

    private async Task HandleSubmitAsync()
    {
        if (_closed || _placeId <= 0)
            return;
        string content = ReviewInput.Text?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return;
        if (!RoReviewService.IsAuthenticated)
        {
            AuthStatus.Text = Strings.Ui_Session_SigningIn;
            bool ok = await RoReviewService.AuthenticateAsync(_ct);
            if (_closed)
                return;
            AuthStatus.Text = string.Empty;
            if (!ok)
                return;
        }
        SubmitButton.IsEnabled = false;
        bool success = await RoReviewService.SubmitReviewAsync(_placeId, content, _ct);
        if (_closed)
            return;
        SubmitButton.IsEnabled = true;
        if (!success)
            return;
        ReviewInput.Text = string.Empty;
        _loading = false;
        await LoadAsync();
    }
}
