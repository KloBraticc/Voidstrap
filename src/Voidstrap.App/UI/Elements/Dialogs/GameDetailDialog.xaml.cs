using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.UI.ViewModels.Pages;

namespace Voidstrap.UI.Elements.Dialogs
{
    public partial class GameDetailDialog : UserControl
    {
        private readonly long _placeId;
        private long _universeId;
        private readonly CancellationTokenSource _cts = new();
        private List<GameServerRow> _servers = new();
        private bool _released;

        public event EventHandler? CloseRequested;

        public event EventHandler? ViewPageRequested;

        public long PlaceId => _placeId;

        public long UniverseId => _universeId;

        public GameDetailDialog(long placeId, long universeId, string name)
        {
            _placeId = placeId;
            _universeId = universeId;
            InitializeComponent();
            TitleText.Text = string.IsNullOrEmpty(name) ? $"Place {placeId}" : name;
            _ = LoadAsync(_cts.Token);
        }

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            _cts.Cancel();
            _cts.Dispose();
            ServerList.ItemsSource = null;
        }

        private async Task LoadAsync(CancellationToken token)
        {
            const string LOG_IDENT = "GameDetailDialog::LoadAsync";

            try
            {
                if (_universeId == 0)
                {
                    string uniJson = await Http.GetString($"https://apis.roblox.com/universes/v1/places/{_placeId}/universe", token);
                    using JsonDocument uniDoc = JsonDocument.Parse(uniJson);
                    if (uniDoc.RootElement.TryGetProperty("universeId", out JsonElement uid) && uid.ValueKind == JsonValueKind.Number)
                        _universeId = uid.GetInt64();
                }
                Task servers = LoadServersAsync(token);
                if (_universeId != 0)
                    await Task.WhenAll(LoadDetailsAsync(token), LoadVotesAsync(token), LoadImagesAsync(token), servers);
                else
                    await servers;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, ex.Message);
            }
        }

        private async Task LoadDetailsAsync(CancellationToken token)
        {
            const string LOG_IDENT = "GameDetailDialog::LoadDetailsAsync";

            try
            {
                string json = await Http.GetString($"https://games.roblox.com/v1/games?universeIds={_universeId}", token);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    return;
                JsonElement g = data[0];
                string name = g.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                string creator = "";
                bool verified = false;
                if (g.TryGetProperty("creator", out JsonElement c))
                {
                    creator = c.TryGetProperty("name", out JsonElement cn) ? cn.GetString() ?? "" : "";
                    verified = c.TryGetProperty("hasVerifiedBadge", out JsonElement vb) && vb.ValueKind == JsonValueKind.True;
                }
                string genre = g.TryGetProperty("genre", out JsonElement ge) ? ge.GetString() ?? "" : "";
                long playing = g.TryGetProperty("playing", out JsonElement pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : 0;
                long visits = g.TryGetProperty("visits", out JsonElement vi) && vi.ValueKind == JsonValueKind.Number ? vi.GetInt64() : 0;
                long favorites = g.TryGetProperty("favoritedCount", out JsonElement fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt64() : 0;
                if (token.IsCancellationRequested)
                    return;
                if (name.Length > 0)
                    TitleText.Text = name;
                CreatorText.Text = creator;
                CreatorChip.Visibility = creator.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                VerifiedIcon.Visibility = verified ? Visibility.Visible : Visibility.Collapsed;
                GenreText.Text = genre;
                GenreChip.Visibility = genre.Length > 0 && !genre.Equals("All", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                ActiveText.Text = Compact(playing);
                VisitsText.Text = Compact(visits);
                FavoritesText.Text = Compact(favorites);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, ex.Message);
            }
        }

        private async Task LoadVotesAsync(CancellationToken token)
        {
            const string LOG_IDENT = "GameDetailDialog::LoadVotesAsync";

            try
            {
                string json = await Http.GetString($"https://games.roblox.com/v1/games/votes?universeIds={_universeId}", token);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    return;
                long up = data[0].TryGetProperty("upVotes", out JsonElement u) ? u.GetInt64() : 0;
                long down = data[0].TryGetProperty("downVotes", out JsonElement d) ? d.GetInt64() : 0;
                if (!token.IsCancellationRequested && up + down > 0)
                    RatingText.Text = $"{Math.Round(up * 100.0 / (up + down))}%";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, ex.Message);
            }
        }

        private async Task LoadImagesAsync(CancellationToken token)
        {
            await Task.WhenAll(
                LoadImageAsync(
                    $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={_universeId}&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false",
                    true,
                    960,
                    BannerHost,
                    token
                ),
                LoadImageAsync(
                    $"https://thumbnails.roblox.com/v1/games/icons?universeIds={_universeId}&size=150x150&format=Png&isCircular=false",
                    false,
                    160,
                    IconHost,
                    token
                )
            );
        }

        private async Task LoadImageAsync(string api, bool nested, int width, Border target, CancellationToken token)
        {
            const string LOG_IDENT = "GameDetailDialog::LoadImageAsync";

            try
            {
                string json = await Http.GetString(api, token);
                string? url = null;
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    {
                        JsonElement item = data[0];
                        if (nested)
                        {
                            if (item.TryGetProperty("thumbnails", out JsonElement thumbs) && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
                                item = thumbs[0];
                            else
                                return;
                        }
                        if (item.TryGetProperty("imageUrl", out JsonElement u))
                            url = u.GetString();
                    }
                }
                if (string.IsNullOrEmpty(url))
                    return;
                var bitmap = await AppImage.LoadAsync(url, width, token);
                if (bitmap != null && !token.IsCancellationRequested)
                    target.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, ex.Message);
            }
        }

        private async Task LoadServersAsync(CancellationToken token)
        {
            const string LOG_IDENT = "GameDetailDialog::LoadServersAsync";

            StatusText.Text = Strings.ContextMenu_ServerBrowser_Loading;
            ServerList.ItemsSource = null;
            try
            {
                string json = await Http.GetString($"https://games.roblox.com/v1/games/{_placeId}/servers/Public?limit=100&sortOrder=Desc", token);
                List<GameServerRow> rows = new();
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement s in data.EnumerateArray())
                        {
                            string id = s.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
                            if (!Guid.TryParse(id, out _))
                                continue;
                            int playing = s.TryGetProperty("playing", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                            int max = s.TryGetProperty("maxPlayers", out JsonElement m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
                            int ping = s.TryGetProperty("ping", out JsonElement pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt32() : 0;
                            double fps = s.TryGetProperty("fps", out JsonElement f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
                            rows.Add(
                                new GameServerRow
                                {
                                    JobId = id,
                                    Playing = playing,
                                    MaxPlayers = max,
                                    Ping = ping,
                                    Fps = fps,
                                    Capacity = string.Format(Strings.ContextMenu_ServerBrowser_Capacity, playing, max),
                                    Metrics = string.Format(Strings.ContextMenu_ServerBrowser_Metrics, ping, Math.Round(fps)),
                                }
                            );
                        }
                    }
                }
                if (token.IsCancellationRequested)
                    return;
                _servers = rows;
                ApplyFilter();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, ex.Message);
                if (!token.IsCancellationRequested)
                    StatusText.Text = Strings.ContextMenu_ServerBrowser_Failed;
            }
        }

        private void ApplyFilter()
        {
            if (ServerList == null || SortBox == null || HideFullBox == null)
                return;
            IEnumerable<GameServerRow> rows = _servers;
            if (HideFullBox.IsChecked == true)
                rows = rows.Where(x => x.MaxPlayers <= 0 || x.Playing < x.MaxPlayers);
            rows = SortBox.SelectedIndex switch
            {
                1 => rows.OrderByDescending(x => x.Playing),
                2 => rows.OrderBy(x => x.Playing),
                _ => rows.OrderBy(x => x.Ping <= 0 ? int.MaxValue : x.Ping),
            };
            List<GameServerRow> list = rows.ToList();
            ServerList.ItemsSource = list;
            StatusText.Text = list.Count == 0 ? Strings.ContextMenu_ServerBrowser_NoServers : string.Empty;
        }

        private static string Compact(long count)
        {
            if (count >= 1_000_000_000)
                return $"{count / 1_000_000_000.0:0.#}B";
            if (count >= 1_000_000)
                return $"{count / 1_000_000.0:0.#}M";
            if (count >= 1_000)
                return $"{count / 1_000.0:0.#}K";
            return count.ToString(CultureInfo.CurrentCulture);
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_released)
                _ = LoadServersAsync(_cts.Token);
        }

        private void Join_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: GameServerRow row })
                HomePageViewModel.LaunchPlace(_placeId, row.JobId);
        }

        private void Play_Click(object sender, RoutedEventArgs e) => HomePageViewModel.LaunchPlace(_placeId);

        private void ViewPage_Click(object sender, RoutedEventArgs e) => ViewPageRequested?.Invoke(this, EventArgs.Empty);

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
