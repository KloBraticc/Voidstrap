using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Integrations;
using Voidstrap.UI.ViewModels.Pages;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Dialogs
{
    public partial class GameDetailDialog : UserControl
    {
        private readonly long _placeId;
        private long _universeId;
        private readonly CancellationTokenSource _cts = new();
        private bool _released;

        public event EventHandler? CloseRequested;

        public event EventHandler? ViewPageRequested;

        public LibraryServersViewModel Servers { get; } = new();

        public long PlaceId => _placeId;

        public long UniverseId => _universeId;

        public GameDetailDialog(long placeId, long universeId, string name)
        {
            _placeId = placeId;
            _universeId = universeId;
            DataContext = this;
            InitializeComponent();
            TitleText.Text = string.IsNullOrEmpty(name) ? $"Place {placeId}" : name;
            Servers.IsServersTabSelected = true;
            Servers.SetViewActive(true);
            Servers.SetGame(new LibraryGameEntry { PlaceId = placeId, UniverseId = universeId, Name = TitleText.Text });
            _ = LoadAsync(_cts.Token);
            AccountBar.PlaceId = placeId;
            _ = RunLiveLoopAsync(_cts.Token);
            GameLiveData.Changed += OnLiveDataChanged;
        }

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            GameLiveData.Changed -= OnLiveDataChanged;
            _cts.Cancel();
            _cts.Dispose();
            Servers.SetViewActive(false);
            Servers.SetGame(null);
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
                if (_universeId != 0)
                    VoteBar.UniverseId = _universeId;
                if (_universeId != 0)
                    await Task.WhenAll(LoadDetailsAsync(token), LoadVotesAsync(token), LoadImagesAsync(token));
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
                ReviewControl.SetGame(_universeId, _placeId, TitleText.Text);
                CreatorText.Text = creator;
                CreatorChip.Visibility = creator.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                VerifiedIcon.Visibility = verified ? Visibility.Visible : Visibility.Collapsed;
                GenreText.Text = genre;
                GenreChip.Visibility = genre.Length > 0 && !genre.Equals("All", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                CountUpText.SetValue(ActiveText, playing);
                CountUpText.SetValue(VisitsText, visits);
                CountUpText.SetValue(FavoritesText, favorites);
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
                    CountUpText.SetValue(RatingText, up * 100.0 / (up + down));
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

        private void AccountBar_AccountSwitched(object? sender, EventArgs e)
        {
            if (!_released)
                Servers.RefreshCommand.Execute(null);
        }

        private async Task RunLiveLoopAsync(CancellationToken token)
        {
            const string LOG_IDENT = "GameDetailDialog::RunLiveLoopAsync";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(GameLiveData.InBackoff() ? 60 : 20), token);
                    long universeId = _universeId;
                    if (universeId == 0)
                        continue;
                    await Task.WhenAll(GameLiveData.RefreshStatsAsync(universeId, token), GameLiveData.RefreshVotesAsync(universeId, token));
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
                ShowLiveData(universeId);
            else
                Dispatcher.BeginInvoke(new Action<long>(ShowLiveData), universeId);
        }

        private void ShowLiveData(long universeId)
        {
            if (_released || universeId != _universeId)
                return;
            if (GameLiveData.TryGetStats(universeId, out GameLiveStats stats))
            {
                CountUpText.SetValue(ActiveText, stats.Playing);
                CountUpText.SetValue(VisitsText, stats.Visits);
                CountUpText.SetValue(FavoritesText, stats.Favorites);
            }
            if (GameLiveData.TryGetVotes(universeId, out GameVoteCounts votes) && !double.IsNaN(votes.LikePercent))
                CountUpText.SetValue(RatingText, votes.LikePercent);
        }

        private void Play_Click(object sender, RoutedEventArgs e) => HomePageViewModel.LaunchPlace(_placeId);

        private void ViewPage_Click(object sender, RoutedEventArgs e) => ViewPageRequested?.Invoke(this, EventArgs.Empty);

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
