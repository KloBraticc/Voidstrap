using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class GamePage
    {
        private const string LOG_IDENT = "GamePage";

        private readonly long _placeId;
        private long _universeId;
        private string _gameName = "";
        private CancellationTokenSource? _cts;

        public GamePage(long placeId, long universeId = 0)
        {
            _placeId = placeId;
            _universeId = universeId;

            InitializeComponent();
        }

        private async void GamePage_Loaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            await LoadGameAsync(_cts.Token);
        }

        private void GamePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            BannerImage.Source = null;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true)
                NavigationService.GoBack();
            else
                NavigationService?.Navigate(new HomePage());
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_placeId == 0)
                return;

            try
            {
                string uri = $"roblox://experiences/start?placeId={_placeId}";
                string voidstrapPath = Paths.Process;

                Process.Start(new ProcessStartInfo
                {
                    FileName = voidstrapPath,
                    Arguments = $"-player \"{uri}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(voidstrapPath) ?? ""
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::Play", ex);
            }
        }

        private async Task LoadGameAsync(CancellationToken token)
        {
            try
            {
                if (_universeId == 0)
                {
					string uniJson = await Voidstrap.Utility.Http.GetString(
						$"https://apis.roblox.com/universes/v1/places/{_placeId}/universe", token);
                    using var uniDoc = JsonDocument.Parse(uniJson);
                    if (uniDoc.RootElement.TryGetProperty("universeId", out var uidEl) && uidEl.ValueKind == JsonValueKind.Number)
                        _universeId = uidEl.GetInt64();
                }

                if (_universeId == 0)
                {
                    GameTitleText.Text = $"Place {_placeId}";
                    return;
                }

                await Task.WhenAll(
                    LoadDetailsAsync(token),
                    LoadVotesAsync(token),
                    LoadBannerAsync(token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::Load", ex);
                try { GameTitleText.Text = $"Place {_placeId}"; } catch { }
            }
        }

        private async Task LoadDetailsAsync(CancellationToken token)
        {
            try
            {
				string json = await Voidstrap.Utility.Http.GetString(
					$"https://games.roblox.com/v1/games?universeIds={_universeId}", token);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() == 0)
                    return;

                var g = data[0];

                string name = g.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                string creator = "";
                bool verified = false;
                if (g.TryGetProperty("creator", out var creatorEl))
                {
                    if (creatorEl.TryGetProperty("name", out var cn)) creator = cn.GetString() ?? "";
                    if (creatorEl.TryGetProperty("hasVerifiedBadge", out var vb)) verified = vb.GetBoolean();
                }

                string description = g.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "";
                long playing = g.TryGetProperty("playing", out var pl) ? pl.GetInt64() : 0;
                long visits = g.TryGetProperty("visits", out var vi) ? vi.GetInt64() : 0;
                int maxPlayers = g.TryGetProperty("maxPlayers", out var mp) ? mp.GetInt32() : 0;
                long favorites = g.TryGetProperty("favoritedCount", out var fav) ? fav.GetInt64() : 0;
                string genre = g.TryGetProperty("genre", out var ge) ? ge.GetString() ?? "" : "";
                DateTime created = g.TryGetProperty("created", out var cr) && cr.TryGetDateTime(out var crd) ? crd : DateTime.MinValue;
                DateTime updated = g.TryGetProperty("updated", out var up) && up.TryGetDateTime(out var upd) ? upd : DateTime.MinValue;

				token.ThrowIfCancellationRequested();
				await Dispatcher.InvokeAsync(() =>
				{
					if (token.IsCancellationRequested)
						return;
                    _gameName = name;
                    GameTitleText.Text = string.IsNullOrEmpty(name) ? $"Place {_placeId}" : name;
                    CreatorText.Text = string.IsNullOrEmpty(creator) ? "" : $"By {creator}{(verified ? " ☑️" : "")}";
                    GenreText.Text = string.IsNullOrEmpty(genre) ? "" : genre;
                    DescriptionText.Text = description;
                    DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;

                    ActiveText.Text = playing.ToString("N0");
                    FavoritesText.Text = FormatCount(favorites);
                    VisitsText.Text = FormatCount(visits);
                    CreatedText.Text = created == DateTime.MinValue ? "--" : created.ToString("M/d/yyyy");
                    UpdatedText.Text = updated == DateTime.MinValue ? "--" : updated.ToString("M/d/yyyy");
                    ServerSizeText.Text = maxPlayers > 0 ? maxPlayers.ToString() : "--";
                    GenreStatText.Text = string.IsNullOrEmpty(genre) ? "--" : genre;
                });
            }
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex)
            {
                App.Logger.WriteLine($"{LOG_IDENT}::Details", ex.Message);
            }
        }

        private async Task LoadVotesAsync(CancellationToken token)
        {
            try
            {
				string json = await Voidstrap.Utility.Http.GetString(
					$"https://games.roblox.com/v1/games/votes?universeIds={_universeId}", token);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() == 0)
                    return;

                var v = data[0];
                long up = v.TryGetProperty("upVotes", out var upEl) ? upEl.GetInt64() : 0;
                long down = v.TryGetProperty("downVotes", out var downEl) ? downEl.GetInt64() : 0;
                long total = up + down;

				token.ThrowIfCancellationRequested();
				await Dispatcher.InvokeAsync(() =>
				{
					if (token.IsCancellationRequested)
						return;
                    LikesText.Text = FormatCount(up);
                    DislikesText.Text = FormatCount(down);
                    RatingBar.Value = total > 0 ? up * 100.0 / total : 0;
                });
            }
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex)
            {
                App.Logger.WriteLine($"{LOG_IDENT}::Votes", ex.Message);
            }
        }

        private async Task LoadBannerAsync(CancellationToken token)
        {
            try
            {
				string json = await Voidstrap.Utility.Http.GetString(
					$"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={_universeId}&countPerUniverse=1&defaults=true&size=768x432&format=Png&isCircular=false", token);

                string? imageUrl = null;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array
                        && data.GetArrayLength() > 0
                        && data[0].TryGetProperty("thumbnails", out var thumbs)
                        && thumbs.ValueKind == JsonValueKind.Array
                        && thumbs.GetArrayLength() > 0
                        && thumbs[0].TryGetProperty("imageUrl", out var urlEl))
                    {
                        imageUrl = urlEl.GetString();
                    }
                }

                if (string.IsNullOrEmpty(imageUrl))
                    return;

				var bmp = await Voidstrap.Utility.GradientWebsite.LoadBannerImageAsync(imageUrl, token).ConfigureAwait(false)
					?? await Voidstrap.Utility.AppImage.LoadAsync(imageUrl, 960, token).ConfigureAwait(false);
				if (bmp == null)
					return;
				token.ThrowIfCancellationRequested();
				await Dispatcher.InvokeAsync(() =>
				{
					if (!token.IsCancellationRequested)
						BannerImage.Source = bmp;
				});
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (Exception ex)
            {
                App.Logger.WriteLine($"{LOG_IDENT}::Banner", ex.Message);
            }
        }

        private static string Truncate(string text, int max)
            => text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        private static string FormatCount(long count)
        {
            if (count >= 1_000_000_000)
                return $"{count / 1_000_000_000.0:0.#}B+";
            if (count >= 1_000_000)
                return $"{count / 1_000_000.0:0.#}M+";
            if (count >= 1_000)
                return $"{count / 1_000.0:0.#}K+";
            return count.ToString();
        }
    }
}
