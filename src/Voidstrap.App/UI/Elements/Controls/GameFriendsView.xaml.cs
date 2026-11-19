using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private const int MaxPreviousAvatars = 24;

    private CancellationTokenSource? _cts;

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

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameFriendsView view && view.IsLoaded)
            view.Restart();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Restart();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void Restart()
    {
        Stop();
        TitleText.Text = ShowActivity ? Strings.Library_Friends_Activity : Strings.Library_Friends_Title;
        Rows.Children.Clear();
        StatusText.Visibility = Visibility.Collapsed;
        long universeId = UniverseId;
        if (universeId <= 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        if (RobloxGameFriends.TryGetCached(universeId, out GameFriendsReport? cached) && cached != null)
        {
            Render(cached);
            return;
        }
        LoadingBar.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        _ = LoadAsync(universeId, _cts.Token);
    }

    private async Task LoadAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "GameFriendsView::LoadAsync";

        try
        {
            GameFriendsReport report = await RobloxGameFriends.GetAsync(universeId, token);
            if (token.IsCancellationRequested || universeId != UniverseId)
                return;
            Render(report);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
            if (!token.IsCancellationRequested)
                Render(new GameFriendsReport { Status = GameFriendsStatus.Unavailable });
        }
    }

    private void Render(GameFriendsReport report)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
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
            if (Rows.Children.Count == 0)
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
