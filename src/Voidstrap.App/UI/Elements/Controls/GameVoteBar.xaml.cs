using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using Voidstrap.Integrations;
using Voidstrap.Resources;

namespace Voidstrap.UI.Elements.Controls;

public partial class GameVoteBar : UserControl
{
    public static readonly DependencyProperty UniverseIdProperty = DependencyProperty.Register(
        nameof(UniverseId),
        typeof(long),
        typeof(GameVoteBar),
        new PropertyMetadata(0L, OnUniverseIdChanged)
    );

    public static readonly DependencyProperty ShowRatioProperty = DependencyProperty.Register(
        nameof(ShowRatio),
        typeof(bool),
        typeof(GameVoteBar),
        new PropertyMetadata(true, OnShowRatioChanged)
    );

    private CancellationTokenSource? _cts;

    private bool _voting;

    public GameVoteBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public long UniverseId
    {
        get => (long)GetValue(UniverseIdProperty);
        set => SetValue(UniverseIdProperty, value);
    }

    public bool ShowRatio
    {
        get => (bool)GetValue(ShowRatioProperty);
        set => SetValue(ShowRatioProperty, value);
    }

    private static void OnUniverseIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameVoteBar bar && bar.IsLoaded)
            bar.Restart();
    }

    private static void OnShowRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameVoteBar bar)
            bar.RatioBar.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        GameLiveData.Changed -= OnLiveDataChanged;
        GameLiveData.Changed += OnLiveDataChanged;
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        GameLiveData.Changed -= OnLiveDataChanged;
        Stop();
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void Restart()
    {
        Stop();
        StatusText.Visibility = Visibility.Collapsed;
        CountUpText.SetValue(LikesText, double.NaN);
        CountUpText.SetValue(DislikesText, double.NaN);
        RatioBar.BeginAnimation(RangeBase.ValueProperty, null);
        RatioBar.Value = 0;
        long universeId = UniverseId;
        if (universeId <= 0)
        {
            RenderVote(null);
            return;
        }
        Render();
        _cts = new CancellationTokenSource();
        _ = RunAsync(universeId, _cts.Token);
    }

    private static async Task RunAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "GameVoteBar::RunAsync";

        try
        {
            _ = GameLiveData.RefreshUserVoteAsync(universeId, token);
            int delay = 20;
            while (!token.IsCancellationRequested)
            {
                GameVoteCounts? counts = await GameLiveData.RefreshVotesAsync(universeId, token).ConfigureAwait(false);
                delay = counts == null || GameLiveData.InBackoff() ? Math.Min(delay * 2, 160) : 20;
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private void OnLiveDataChanged(object? sender, long universeId)
    {
        if (Dispatcher.CheckAccess())
            HandleChange(universeId);
        else
            Dispatcher.BeginInvoke(new Action<long>(HandleChange), universeId);
    }

    private void HandleChange(long universeId)
    {
        if (IsLoaded && universeId == UniverseId)
            Render();
    }

    private void Render()
    {
        long universeId = UniverseId;
        if (GameLiveData.TryGetVotes(universeId, out GameVoteCounts counts))
        {
            CountUpText.SetValue(LikesText, counts.Up);
            CountUpText.SetValue(DislikesText, counts.Down);
            double ratio = double.IsNaN(counts.LikePercent) ? 0 : counts.LikePercent;
            if (SystemParameters.ClientAreaAnimation)
                RatioBar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(ratio, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
                RatioBar.Value = ratio;
        }
        RenderVote(GameLiveData.GetUserVote(universeId));
    }

    private void RenderVote(bool? vote)
    {
        Paint(LikeIcon, vote == true);
        Paint(DislikeIcon, vote == false);
    }

    private static void Paint(Wpf.Ui.Controls.SymbolIcon icon, bool active)
    {
        icon.Filled = active;
        if (active)
            icon.SetResourceReference(ForegroundProperty, "SystemAccentColorSecondaryBrush");
        else
            icon.ClearValue(ForegroundProperty);
    }

    private void LikeButton_Click(object sender, RoutedEventArgs e) => _ = VoteAsync(true);

    private void DislikeButton_Click(object sender, RoutedEventArgs e) => _ = VoteAsync(false);

    private async Task VoteAsync(bool like)
    {
        long universeId = UniverseId;
        if (_voting || universeId <= 0)
            return;
        _voting = true;
        try
        {
            bool? next = GameLiveData.GetUserVote(universeId) == like ? null : like;
            GameVoteOutcome outcome = await GameLiveData.SetVoteAsync(universeId, next);
            if (universeId != UniverseId)
                return;
            string message = outcome switch
            {
                GameVoteOutcome.SignIn => Strings.Library_Vote_SignIn,
                GameVoteOutcome.PlayFirst => Strings.Library_Vote_PlayFirst,
                GameVoteOutcome.RateLimited or GameVoteOutcome.Failed => Strings.Library_Vote_Failed,
                _ => string.Empty,
            };
            StatusText.Text = message;
            StatusText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _voting = false;
        }
    }
}
