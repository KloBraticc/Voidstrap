using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.ViewModels.Pages;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class LibraryGamesControl : UserControl
    {
        private const int MaxGames = 60;

        private readonly ObservableCollection<DiscoverGameEntry> _games = new();
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly string _session = Guid.NewGuid().ToString();
        private CancellationTokenSource? _cts;
        private List<DiscoverGameEntry>? _recommended;
        private bool _active;

        public LibraryGamesControl()
        {
            InitializeComponent();
            GamesList.ItemsSource = _games;
        }

        public void SetActive(bool active)
        {
            if (_active == active)
                return;
            _active = active;
            if (active)
            {
                _debounce.Tick += Debounce_Tick;
                if (_games.Count == 0)
                    _ = LoadAsync(SearchBox.Text.Trim());
            }
            else
            {
                _debounce.Stop();
                _debounce.Tick -= Debounce_Tick;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_active)
                return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Debounce_Tick(object? sender, EventArgs e)
        {
            _debounce.Stop();
            _ = LoadAsync(SearchBox.Text.Trim());
        }

        private async Task LoadAsync(string query)
        {
            const string LOG_IDENT = "LibraryGamesControl::LoadAsync";

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            HeaderText.Text = query.Length == 0 ? Strings.Ui_Home_RecommendedForYou : Strings.Ui_Session_Games;
            StatusText.Visibility = Visibility.Collapsed;
            LoadingBar.Visibility = Visibility.Visible;
            try
            {
                List<DiscoverGameEntry> games;
                if (query.Length == 0 && _recommended != null)
                    games = _recommended;
                else
                {
                    games = await Task.Run(async () =>
                    {
                        List<DiscoverGameEntry> found = query.Length == 0
                            ? await HomePageViewModel.ReadOmniRecommendationsAsync(_session).ConfigureAwait(false)
                            : await HomePageViewModel.SearchGamesAsync(query, _session, token).ConfigureAwait(false);
                        return found.GroupBy(g => g.UniverseId).Select(g => g.First()).Take(MaxGames).ToList();
                    }, token);
                    if (query.Length == 0)
                        _recommended = games;
                }
                if (token.IsCancellationRequested)
                    return;
                HashSet<long> favorites = (App.Settings.Prop.FavoriteGamePlaceIds ?? new List<long>()).ToHashSet();
                _games.Clear();
                foreach (DiscoverGameEntry game in games)
                {
                    game.IsFavorite = favorites.Contains(game.PlaceId);
                    _games.Add(game);
                }
                if (games.Count == 0)
                {
                    StatusText.Text = query.Length == 0 ? Strings.Ui_Bootstrapper_WindowIconsNoGamesFound : string.Format(Strings.ContextMenu_GameBrowser_NoResults, query);
                    StatusText.Visibility = Visibility.Visible;
                }
                List<DiscoverGameEntry> missing = games.Where(g => g.ThumbnailUrl == null).ToList();
                if (missing.Count > 0)
                    await HomePageViewModel.FillGameIconsAsync(missing, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, ex.Message);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    LoadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private static DiscoverGameEntry? GameOf(object sender) => (sender as FrameworkElement)?.DataContext as DiscoverGameEntry;

        private void ShowDetail(object sender)
        {
            if (GameOf(sender) is DiscoverGameEntry game && Window.GetWindow(this) is MainWindow window)
                window.ShowGameDetail(game.PlaceId, game.UniverseId, game.Name);
        }

        private void Tile_Click(object sender, MouseButtonEventArgs e) => ShowDetail(sender);

        private void View_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ShowDetail(sender);
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (GameOf(sender) is DiscoverGameEntry game)
                HomePageViewModel.LaunchPlace(game.PlaceId);
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (GameOf(sender) is not DiscoverGameEntry game)
                return;
            List<long> favorites = App.Settings.Prop.FavoriteGamePlaceIds ??= new List<long>();
            bool favorite = !favorites.Contains(game.PlaceId);
            favorites.RemoveAll(id => id == game.PlaceId);
            if (favorite)
                favorites.Insert(0, game.PlaceId);
            App.Settings.SaveDeferred();
            game.IsFavorite = favorite;
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button button || GameOf(sender) is not DiscoverGameEntry game)
                return;
            MenuItem open = new() { Header = Strings.Ui_Home_ViewGamePage, Tag = game.PlaceId, Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Common.SymbolRegular.Open24 } };
            open.Click += OpenPage_Click;
            System.Windows.Controls.ContextMenu menu = new() { PlacementTarget = button };
            menu.Items.Add(open);
            menu.Closed += Menu_Closed;
            menu.IsOpen = true;
        }

        private void OpenPage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: long placeId })
                Utilities.OpenWebLink("https://www.roblox.com/games/" + placeId);
        }

        private void Menu_Closed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ContextMenu menu)
                return;
            menu.Closed -= Menu_Closed;
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                item.Click -= OpenPage_Click;
        }
    }
}
