using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Voidstrap.Integrations;

namespace Voidstrap.UI.Elements.Controls;

public partial class GameFriendsView : UserControl
{
    public static readonly DependencyProperty UniverseIdProperty = DependencyProperty.Register(
        nameof(UniverseId),
        typeof(long),
        typeof(GameFriendsView),
        new PropertyMetadata(0L, OnInputChanged)
    );

    public static readonly DependencyProperty GameNameProperty = DependencyProperty.Register(
        nameof(GameName),
        typeof(string),
        typeof(GameFriendsView),
        new PropertyMetadata("", OnInputChanged)
    );

    public static readonly DependencyProperty ShowActivityProperty = DependencyProperty.Register(
        nameof(ShowActivity),
        typeof(bool),
        typeof(GameFriendsView),
        new PropertyMetadata(false, OnInputChanged)
    );

    public static readonly DependencyProperty PlayingOnlyProperty = DependencyProperty.Register(
        nameof(PlayingOnly),
        typeof(bool),
        typeof(GameFriendsView),
        new PropertyMetadata(false, OnInputChanged)
    );

    private const int MaxPreviousAvatars = 24;

    private static readonly TimeSpan PlayingRefreshInterval = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan PlayingRequestTimeout = TimeSpan.FromSeconds(25);

    private long _shownUniverseId;

    private CancellationTokenSource? _playingRefresh;

    public GameFriendsView()
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

    public string GameName
    {
        get => (string)GetValue(GameNameProperty);
        set => SetValue(GameNameProperty, value);
    }

    public bool ShowActivity
    {
        get => (bool)GetValue(ShowActivityProperty);
        set => SetValue(ShowActivityProperty, value);
    }

    public bool PlayingOnly
    {
        get => (bool)GetValue(PlayingOnlyProperty);
        set => SetValue(PlayingOnlyProperty, value);
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameFriendsView view && view.IsLoaded)
            view.Restart();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RobloxGameFriends.ReportUpdated -= OnReportUpdated;
        RobloxGameFriends.ReportUpdated += OnReportUpdated;
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RobloxGameFriends.ReportUpdated -= OnReportUpdated;
        _shownUniverseId = 0;
        StopPlayingRefresh();
    }

    private void OnReportUpdated(long universeId, GameFriendsReport report)
    {
        if (universeId != _shownUniverseId)
            return;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (universeId == _shownUniverseId && IsLoaded && !PlayingOnly)
                    Render(report);
            }
        );
    }

    private void Restart()
    {
        StopPlayingRefresh();
        Rows.Children.Clear();
        StatusText.Visibility = Visibility.Collapsed;
        long universeId = UniverseId;
        _shownUniverseId = PlayingOnly ? 0 : universeId;
        if (universeId <= 0)
        {
            Visibility = Visibility.Collapsed;
            LoadingBar.Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        if (PlayingOnly)
        {
            UpdatePlayingRefresh();
            return;
        }
        if (RobloxGameFriends.TryGetCurrent(universeId, out GameFriendsReport? current) && current != null)
        {
            Render(current);
            if (current.Complete)
                return;
        }
        else
        {
            LoadingBar.Visibility = Visibility.Visible;
        }
        RobloxGameFriends.Start(universeId);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && PlayingOnly)
            UpdatePlayingRefresh();
    }

    private void UpdatePlayingRefresh()
    {
        if (!IsVisible || UniverseId <= 0)
        {
            StopPlayingRefresh();
            return;
        }
        if (_playingRefresh != null)
            return;

        Rows.Children.Clear();
        StatusText.Visibility = Visibility.Collapsed;
        LoadingBar.Visibility = Visibility.Visible;
        _playingRefresh = new CancellationTokenSource();
        _ = RefreshPlayingAsync(UniverseId, _playingRefresh.Token);
    }

    private void StopPlayingRefresh()
    {
        _playingRefresh?.Cancel();
        _playingRefresh?.Dispose();
        _playingRefresh = null;
    }

    private async Task RefreshPlayingAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "GameFriendsView::RefreshPlayingAsync";

        try
        {
            while (!token.IsCancellationRequested)
            {
                (GameFriendsStatus status, IReadOnlyList<GameFriend> friends) = await FetchPlayingAsync(universeId, token);
                token.ThrowIfCancellationRequested();
                RenderPlaying(status, friends);
                await Task.Delay(PlayingRefreshInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not display friends currently playing: " + ex.Message);
            if (!token.IsCancellationRequested)
                RenderPlaying(GameFriendsStatus.Unavailable, []);
        }
    }

    private static async Task<(GameFriendsStatus, IReadOnlyList<GameFriend>)> FetchPlayingAsync(long universeId, CancellationToken token)
    {
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(PlayingRequestTimeout);
        try
        {
            return await Task.Run(() => RobloxGameFriends.GetPlayingAsync(universeId, request.Token), request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (GameFriendsStatus.Unavailable, []);
        }
    }

    private void RenderPlaying(GameFriendsStatus status, IReadOnlyList<GameFriend> friends)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        Rows.Children.Clear();
        string? statusKey = status switch
        {
            GameFriendsStatus.NotSignedIn => nameof(Strings.Library_Friends_SignIn),
            GameFriendsStatus.Unavailable => nameof(Strings.Library_Friends_Unavailable),
            _ when friends.Count == 0 => nameof(Strings.Library_Friends_NoOnePlaying),
            _ => null,
        };
        StatusText.Visibility = statusKey == null ? Visibility.Collapsed : Visibility.Visible;
        if (statusKey != null)
        {
            StatusText.SetBinding(TextBlock.TextProperty, new Binding($"[{statusKey}]") { Source = LocalizedStrings.Instance });
            return;
        }

        UniformGrid grid = new() { Columns = 2 };
        foreach (GameFriend friend in friends)
            grid.Children.Add(RecentRow(friend));
        Rows.Children.Add(grid);
    }

    private void Render(GameFriendsReport report)
    {
        LoadingBar.Visibility = report.Complete ? Visibility.Collapsed : Visibility.Visible;
        Rows.Children.Clear();
        string? status = report.Status switch
        {
            GameFriendsStatus.NotSignedIn => Strings.Library_Friends_SignIn,
            GameFriendsStatus.Unavailable => Strings.Library_Friends_Unavailable,
            _ => null,
        };
        if (status == null)
        {
            if (ShowActivity)
                RenderActivity(report.Activity);
            else
                RenderFriends(report);
            if (Rows.Children.Count == 0 && report.Complete)
                status = ShowActivity ? Strings.Library_Friends_NoActivity : Strings.Library_Friends_None;
        }
        StatusText.Text = status ?? "";
        StatusText.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderFriends(GameFriendsReport report)
    {
        if (report.Recent.Count > 0)
        {
            Rows.Children.Add(SectionHeader(string.Format(Strings.Library_Friends_Recent, report.Recent.Count), false));
            UniformGrid grid = new() { Columns = 2 };
            foreach (GameFriend friend in report.Recent)
                grid.Children.Add(RecentRow(friend));
            Rows.Children.Add(grid);
        }
        if (report.Previous.Count > 0)
        {
            Rows.Children.Add(SectionHeader(string.Format(Strings.Library_Friends_Previous, report.Previous.Count), report.Recent.Count > 0));
            WrapPanel avatars = new();
            foreach (GameFriend friend in report.Previous.Take(MaxPreviousAvatars))
            {
                FrameworkElement avatar = Avatar(friend.HeadshotUrl, 32);
                avatar.Margin = new Thickness(0, 0, 6, 6);
                avatar.ToolTip = friend.Name + "\n" + string.Format(Strings.Library_Friends_BadgeProgress, friend.Earned, friend.Total);
                Clickable(avatar, friend.UserId);
                avatars.Children.Add(avatar);
            }
            Rows.Children.Add(avatars);
        }
    }

    private FrameworkElement RecentRow(GameFriend friend)
    {
        Grid row = new() { Margin = new Thickness(0, 0, 8, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Avatar(friend.HeadshotUrl, 32));
        StackPanel text = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(
            new TextBlock
            {
                Text = friend.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush(friend.InGame ? "SystemFillColorSuccessBrush" : "TextFillColorPrimaryBrush"),
            }
        );
        text.Children.Add(
            new TextBlock
            {
                Text = friend.InGame ? Strings.Library_Friends_PlayingNow : string.Format(Strings.Library_Friends_BadgeProgress, friend.Earned, friend.Total),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("TextFillColorTertiaryBrush"),
            }
        );
        row.Children.Add(text);
        if (friend.LastAwardUtc > DateTime.MinValue)
            row.ToolTip = friend.LastAwardUtc.ToLocalTime().ToString("D", CultureInfo.CurrentUICulture);
        Clickable(row, friend.UserId);
        return row;
    }

    private void RenderActivity(IReadOnlyList<GameFriendActivity> activity)
    {
        DateTime? day = null;
        foreach (GameFriendActivity entry in activity)
        {
            DateTime local = entry.WhenUtc.ToLocalTime();
            if (day != local.Date)
            {
                day = local.Date;
                Rows.Children.Add(DayHeader(local, Rows.Children.Count > 0));
            }
            Rows.Children.Add(ActivityRow(entry));
        }
    }

    private FrameworkElement DayHeader(DateTime date, bool spaced)
    {
        Grid header = new() { Margin = new Thickness(0, spaced ? 10 : 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(
            new TextBlock
            {
                Text = date.ToString("M", CultureInfo.CurrentUICulture).ToUpper(CultureInfo.CurrentUICulture),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextFillColorTertiaryBrush"),
            }
        );
        Rectangle line = new()
        {
            Height = 1,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = Brush("ControlStrokeColorDefaultBrush"),
        };
        Grid.SetColumn(line, 1);
        header.Children.Add(line);
        return header;
    }

    private FrameworkElement ActivityRow(GameFriendActivity entry)
    {
        Grid row = new() { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement avatar = Avatar(entry.HeadshotUrl, 28);
        Clickable(avatar, entry.UserId);
        row.Children.Add(avatar);
        TextBlock text = new()
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextFillColorSecondaryBrush"),
        };
        Grid.SetColumn(text, 1);
        string format = entry.FirstPlay ? Strings.Library_Friends_FirstPlay : Strings.Library_Friends_EarnedBadge;
        string item = entry.FirstPlay ? (string.IsNullOrWhiteSpace(GameName) ? "" : GameName) : entry.BadgeName;
        AddFormatted(text.Inlines, format, entry.Name, item, entry.FirstPlay ? "" : entry.BadgeIconUrl);
        row.Children.Add(text);
        row.ToolTip = entry.WhenUtc.ToLocalTime().ToString("f", CultureInfo.CurrentUICulture);
        return row;
    }

    private void AddFormatted(InlineCollection inlines, string format, string name, string item, string iconUrl)
    {
        int index = 0;
        while (index < format.Length)
        {
            int nameAt = format.IndexOf("{0}", index, StringComparison.Ordinal);
            int itemAt = format.IndexOf("{1}", index, StringComparison.Ordinal);
            int next = nameAt < 0 ? itemAt : itemAt < 0 ? nameAt : Math.Min(nameAt, itemAt);
            if (next < 0)
            {
                inlines.Add(new Run(format[index..]));
                break;
            }
            if (next > index)
                inlines.Add(new Run(format[index..next]));
            if (next == nameAt)
            {
                inlines.Add(new Run(name) { Foreground = Brush("SystemAccentColorSecondaryBrush") });
            }
            else
            {
                if (iconUrl.Length > 0)
                {
                    FrameworkElement icon = Avatar(iconUrl, 18);
                    icon.Margin = new Thickness(0, 0, 4, -4);
                    inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
                }
                inlines.Add(new Run(item) { FontWeight = FontWeights.SemiBold, Foreground = Brush("TextFillColorPrimaryBrush") });
            }
            index = next + 3;
        }
    }

    private TextBlock SectionHeader(string text, bool spaced) =>
        new()
        {
            Text = text,
            Margin = new Thickness(0, spaced ? 8 : 0, 0, 8),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextFillColorPrimaryBrush"),
        };

    private FrameworkElement Avatar(string url, double size)
    {
        Grid host = new()
        {
            Width = size,
            Height = size,
            Background = Brush("ControlFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RoundedClip.SetRadius(host, 4);
        if (!string.IsNullOrEmpty(url))
        {
            Image image = new() { Stretch = Stretch.UniformToFill };
            DynamicRenderSystem.SetLazyImageSource(image, url);
            host.Children.Add(image);
        }
        return host;
    }

    private static void Clickable(FrameworkElement element, long userId)
    {
        element.Cursor = Cursors.Hand;
        element.Tag = userId;
        element.MouseLeftButtonUp -= OpenProfile;
        element.MouseLeftButtonUp += OpenProfile;
    }

    private static void OpenProfile(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long userId } && userId > 0)
            Utilities.OpenWebLink("https://www.roblox.com/users/" + userId.ToString(CultureInfo.InvariantCulture) + "/profile");
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush;
}
