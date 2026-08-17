using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Utility;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class QuestItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _iconUrl = string.Empty;
    private string _gameName = string.Empty;
    private string _gameStats = string.Empty;
    private string _gameDescription = string.Empty;
    private int _up;
    private int _down;
    private int _mine;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string GameLine { get; init; } = string.Empty;

    public int Goal { get; init; }

    public int Progress { get; init; }

    public int Xp { get; init; }

    public bool Complete { get; init; }

    public bool Claimed { get; init; }

    public Voidstrap.Models.QuestLiveSession? Session { get; set; }

    public int LiveProgress => Session is null
        ? Progress
        : Session.LiveProgress(Kind, UniverseId, Goal, Progress, Claimed);

    public bool LiveComplete => Complete || Claimed || (Goal > 0 && LiveProgress >= Goal);

    public double Percent => Goal > 0 ? Math.Max(0.0, Math.Min(100.0, LiveProgress * 100.0 / Goal)) : 0.0;

    public string StatusText => Claimed ? "+" + Xp + " XP" : LiveComplete ? "+" + Xp + " XP" : LiveProgress + " / " + Goal + "  worth " + Xp + " XP";

    public void NotifyLive()
    {
        Changed(nameof(LiveProgress));
        Changed(nameof(LiveComplete));
        Changed(nameof(Percent));
        Changed(nameof(StatusText));
    }

    public long UniverseId { get; init; }

    public long PlaceId { get; set; }

    public bool HasGame => UniverseId > 0 && _gameName.Length > 0;

    public bool HasIcon => _iconUrl.Length > 0;

    public string IconUrl
    {
        get => _iconUrl;
        set { _iconUrl = value ?? string.Empty; Changed(nameof(IconUrl)); Changed(nameof(HasIcon)); }
    }

    public string GameName
    {
        get => _gameName;
        set { _gameName = value ?? string.Empty; Changed(nameof(GameName)); Changed(nameof(HasGame)); }
    }

    public string GameStats
    {
        get => _gameStats;
        set { _gameStats = value ?? string.Empty; Changed(nameof(GameStats)); }
    }

    public string GameDescription
    {
        get => _gameDescription;
        set { _gameDescription = value ?? string.Empty; Changed(nameof(GameDescription)); }
    }

    public int Up
    {
        get => _up;
        set { _up = value; Changed(nameof(Up)); Changed(nameof(LikeLabel)); Changed(nameof(RatioText)); }
    }

    public int Down
    {
        get => _down;
        set { _down = value; Changed(nameof(Down)); Changed(nameof(DislikeLabel)); Changed(nameof(RatioText)); }
    }

    public int Mine
    {
        get => _mine;
        set { _mine = value; Changed(nameof(Mine)); }
    }

    public string LikeLabel => "Like " + Compact(_up);

    public string DislikeLabel => "Dislike " + Compact(_down);

    public string RatioText
    {
        get
        {
            int total = _up + _down;
            return total == 0 ? string.Empty : (int)Math.Round(_up * 100.0 / total) + "%";
        }
    }

    private static string Compact(int value)
    {
        if (value >= 1000000)
            return (value / 1000000.0).ToString("0.#") + "M";
        if (value >= 1000)
            return (value / 1000.0).ToString("0.#") + "K";
        return value.ToString();
    }

    public Brush BarBrush
    {
        get
        {
            Color color = Complete ? Color.FromRgb(0x3B, 0xA5, 0x5D) : Color.FromRgb(0x7A, 0x7A, 0x82);
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    public Brush StatusBrush
    {
        get
        {
            Color color = Claimed
                ? Color.FromRgb(0x8A, 0x8A, 0x8A)
                : Complete
                    ? Color.FromRgb(0x3B, 0xA5, 0x5D)
                    : Color.FromRgb(0xA1, 0xA1, 0xAA);
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}

public partial class QuestsPage : UiPage
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12.0);

    private const int MaximumQuests = 256;

    private const int MaximumGameIdsPerRequest = 50;

    private CancellationTokenSource? _pageCts;

    private CancellationTokenSource? _loadCts;

    private bool _loaded;

    private bool _liveTimerAttached;

    private bool _voting;

    public ObservableCollection<QuestItem> Quests { get; } = new ObservableCollection<QuestItem>();

    public event EventHandler? BackRequested;

    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new System.Windows.Threading.DispatcherTimer
    {
        Interval = TimeSpan.FromSeconds(1.0),
    };

    private Voidstrap.Models.QuestLiveSession? _session;

    public QuestsPage()
    {
        InitializeComponent();
        QuestList.ItemsSource = Quests;
    }

    private void LiveTimer_Tick(object? sender, EventArgs e)
    {
        if (_session is null || !_session.IsBeating)
        {
            _liveTimer.Stop();
            return;
        }
        foreach (QuestItem item in Quests)
            item.NotifyLive();
    }

    private void SyncLiveTimer()
    {
        if (_loaded && _session is not null && _session.IsBeating)
            _liveTimer.Start();
        else
            _liveTimer.Stop();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        if (!_liveTimerAttached)
        {
            _liveTimer.Tick += LiveTimer_Tick;
            _liveTimerAttached = true;
        }
        _pageCts = new CancellationTokenSource();
        _ = LoadAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _loaded = false;
        _liveTimer.Stop();
        if (_liveTimerAttached)
        {
            _liveTimer.Tick -= LiveTimer_Tick;
            _liveTimerAttached = false;
        }
        _loadCts?.Cancel();
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            _ = LoadAsync();
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(App.WebsiteBaseUrl + "/pages/quests.html") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("QuestsPage::OpenSite", ex);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BackRequested != null)
            {
                BackRequested(this, EventArgs.Empty);
                return;
            }
            if (NavigationService != null && NavigationService.CanGoBack)
                NavigationService.GoBack();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("QuestsPage::Back", ex);
        }
    }

    private async Task LoadAsync()
    {
        if (!_loaded || _pageCts is null)
            return;
        _loadCts?.Cancel();
        CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        _loadCts = loadCts;
        CancellationToken token = loadCts.Token;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Loading your quests";
        try
        {
            string? websiteToken = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(websiteToken))
            {
                Quests.Clear();
                LevelText.Text = string.Empty;
                LevelXpText.Text = string.Empty;
                EarnedText.Text = string.Empty;
                LevelBar.Value = 0.0;
                StatusText.Text = "Sign in on the Home page to see your daily quests.";
                return;
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + "/api/quests");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", websiteToken);
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                Quests.Clear();
                StatusText.Text = "Could not load your quests.";
                return;
            }
            string body = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(true);
            if (token.IsCancellationRequested || !_loaded)
                return;
            Apply(body);
            await EnrichGamesAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Load failed: " + ex.Message);
            StatusText.Text = "Sign in on the Home page to see your daily quests.";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, loadCts))
            {
                _loadCts = null;
                RefreshButton.IsEnabled = _loaded;
            }
            loadCts.Dispose();
        }
    }

    private void Apply(string body)
    {
        List<QuestItem> parsed = new List<QuestItem>();
        Voidstrap.Models.QuestLiveSession? live = null;
        string levelText = string.Empty;
        string xpText = string.Empty;
        double barValue = 0.0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                live = Voidstrap.Models.QuestLiveSession.FromResponse(root);
                if (root.TryGetProperty("level", out JsonElement level) && level.ValueKind == JsonValueKind.Number)
                    levelText = "Level " + level.GetInt32();
                if (root.TryGetProperty("levelProgress", out JsonElement lp) && lp.ValueKind == JsonValueKind.Object)
                {
                    bool maxed = lp.TryGetProperty("max", out JsonElement maxFlag) && maxFlag.ValueKind == JsonValueKind.True;
                    int into = ReadInt(lp, "into");
                    int needed = ReadInt(lp, "needed");
                    int percent = ReadInt(lp, "percent");
                    xpText = maxed ? "Max level" : into + " / " + needed + " XP";
                    barValue = maxed ? 100.0 : Math.Max(0.0, Math.Min(100.0, percent));
                }
                if (root.TryGetProperty("quests", out JsonElement quests) && quests.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement quest in quests.EnumerateArray())
                    {
                        if (parsed.Count >= MaximumQuests)
                            break;
                        if (quest.ValueKind != JsonValueKind.Object)
                            continue;
                        string game = ReadText(quest, "game");

                        parsed.Add(new QuestItem
                        {
                            Session = live,
                            Id = ReadText(quest, "id"),
                            UniverseId = ReadLong(quest, "universeId"),
                            Title = ReadText(quest, "title"),
                            Kind = ReadText(quest, "kind"),
                            GameLine = game.Length > 0 ? "Experience: " + game : string.Empty,
                            Goal = ReadInt(quest, "goal"),
                            Progress = ReadInt(quest, "progress"),
                            Xp = ReadInt(quest, "xp"),
                            Complete = ReadFlag(quest, "complete"),
                            Claimed = ReadFlag(quest, "claimed"),
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Parse failed: " + ex.Message);
        }

        _session = live;
        Quests.Clear();
        foreach (QuestItem item in parsed)
            Quests.Add(item);
        SyncLiveTimer();
        LevelText.Text = levelText;
        LevelXpText.Text = xpText;
        LevelBar.Value = barValue;

        int earnedToday = 0;
        int availableToday = 0;
        foreach (QuestItem item in parsed)
        {
            availableToday += item.Xp;
            if (item.Claimed)
                earnedToday += item.Xp;
        }
        EarnedText.Text = "Earned " + earnedToday + " of " + availableToday + " XP from today's quests";

        int ready = 0;
        int done = 0;
        foreach (QuestItem item in parsed)
        {
            if (item.Claimed)
                done++;
            else if (item.Complete)
                ready++;
        }
        if (parsed.Count == 0)
            StatusText.Text = "No quests available right now.";
        else if (ready > 0)
            StatusText.Text = ready == 1
                ? "1 quest is ready to claim on the website."
                : ready + " quests are ready to claim on the website.";
        else
            StatusText.Text = done + " of " + parsed.Count + " claimed today";
    }

    private async Task EnrichGamesAsync(CancellationToken token)
    {
        List<long> ids = new List<long>();
        foreach (QuestItem item in Quests)
        {
            if (item.UniverseId > 0 && !ids.Contains(item.UniverseId))
                ids.Add(item.UniverseId);
        }
        if (ids.Count == 0)
            return;
        try
        {
            for (int offset = 0; offset < ids.Count; offset += MaximumGameIdsPerRequest)
            {
                token.ThrowIfCancellationRequested();
                string joined = string.Join(",", ids.GetRange(offset, Math.Min(MaximumGameIdsPerRequest, ids.Count - offset)));
                string? details = await GetAsync(App.WebsiteBaseUrl + "/api/games?action=details&universeIds=" + joined, false, token).ConfigureAwait(true);
                string? thumbs = await GetAsync(App.WebsiteBaseUrl + "/api/games?action=thumbnails&universeIds=" + joined, false, token).ConfigureAwait(true);
                string? votes = await GetAsync(App.WebsiteBaseUrl + "/api/gamevote?universeIds=" + joined, true, token).ConfigureAwait(true);
                ApplyDetails(details);
                ApplyVotes(votes);
                await ApplyThumbnailsAsync(thumbs, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Game info failed: " + ex.Message);
        }
    }

    private async Task<string?> GetAsync(string url, bool authenticated, CancellationToken token)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authenticated)
        {
            string? websiteToken = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(websiteToken))
                return null;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", websiteToken);
        }
        using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
            return null;
        return await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, token).ConfigureAwait(true);
    }

    private QuestItem? ByUniverse(long universeId)
    {
        foreach (QuestItem item in Quests)
        {
            if (item.UniverseId == universeId)
                return item;
        }
        return null;
    }

    private void ApplyDetails(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                QuestItem? item = ByUniverse(ReadLong(entry, "id"));
                if (item == null)
                    continue;
                item.PlaceId = ReadLong(entry, "rootPlaceId");
                item.GameStats = Format(ReadLong(entry, "playing")) + " playing, " + Format(ReadLong(entry, "visits")) + " visits";
                string description = ReadText(entry, "description");
                item.GameDescription = description.Length > 220 ? description.Substring(0, 220) : description;
                item.GameName = ReadText(entry, "name");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Details parse failed: " + ex.Message);
        }
    }

    private void ApplyVotes(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("votes", out JsonElement votes) || votes.ValueKind != JsonValueKind.Object)
                return;
            foreach (JsonProperty entry in votes.EnumerateObject())
            {
                if (!long.TryParse(entry.Name, out long universeId))
                    continue;
                QuestItem? item = ByUniverse(universeId);
                if (item == null || entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                item.Up = ReadInt(entry.Value, "up");
                item.Down = ReadInt(entry.Value, "down");
                item.Mine = ReadInt(entry.Value, "mine");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Votes parse failed: " + ex.Message);
        }
    }

    private async Task ApplyThumbnailsAsync(string? body, CancellationToken token)
    {
        if (string.IsNullOrEmpty(body))
            return;
        List<(QuestItem Item, string Url)> pending = new List<(QuestItem, string)>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                QuestItem? item = ByUniverse(ReadLong(entry, "targetId"));
                string url = ReadText(entry, "imageUrl");
                if (item != null && url.Length > 0)
                    pending.Add((item, url));
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Thumbnail parse failed: " + ex.Message);
            return;
        }
        foreach ((QuestItem item, string url) in pending)
        {
            if (token.IsCancellationRequested)
                return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                continue;
            item.IconUrl = uri.AbsoluteUri;
        }
    }

    private static string Format(long value)
    {
        if (value >= 1000000L)
            return (value / 1000000.0).ToString("0.#") + "M";
        if (value >= 1000L)
            return (value / 1000.0).ToString("0.#") + "K";
        return value.ToString();
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuestItem item })
            return;
        if (item.PlaceId <= 0)
        {
            StatusText.Text = "That experience could not be launched.";
            return;
        }
        try
        {
            string uri = "roblox://experiences/start?placeId=" + item.PlaceId;
            string voidstrapPath = Paths.Process;
            Process.Start(new ProcessStartInfo
            {
                FileName = voidstrapPath,
                Arguments = "-player \"" + uri + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(voidstrapPath) ?? "",
            });
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("QuestsPage::Join", ex);
            StatusText.Text = "That experience could not be launched.";
        }
    }

    private void Like_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QuestItem item })
            _ = VoteAsync(item, 1);
    }

    private void Dislike_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QuestItem item })
            _ = VoteAsync(item, -1);
    }

    private async Task VoteAsync(QuestItem item, int value)
    {
        if (item.UniverseId <= 0 || _voting)
            return;
        _voting = true;
        try
        {
            string? websiteToken = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(websiteToken))
            {
                StatusText.Text = "Sign in on the Home page to vote.";
                return;
            }
            int next = item.Mine == value ? 0 : value;
            CancellationToken token = _pageCts?.Token ?? CancellationToken.None;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, App.WebsiteBaseUrl + "/api/gamevote");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", websiteToken);
            request.Content = new StringContent("{\"universeId\":" + item.UniverseId + ",\"vote\":" + next + "}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(true);
            string body = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 1024 * 1024, timeout.Token).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
                return;
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;
            item.Up = ReadInt(document.RootElement, "up");
            item.Down = ReadInt(document.RootElement, "down");
            item.Mine = ReadInt(document.RootElement, "mine");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("QuestsPage", "Vote failed: " + ex.Message);
        }
        finally
        {
            _voting = false;
        }
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0L;
    }

    private static string ReadText(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static bool ReadFlag(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }
}
