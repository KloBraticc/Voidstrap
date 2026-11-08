using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.ViewModels.Pages;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class LibraryGamesControl : UserControl
    {
        private const int MaxGames = 150;

        private const int SearchPages = 4;

        private const double TileGap = 14;

        private const double TargetTile = 180;

        public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(LibraryGamesControl), new PropertyMetadata(TargetTile));

        public double TileSize
        {
            get => (double)GetValue(TileSizeProperty);
            set => SetValue(TileSizeProperty, value);
        }

        private readonly ObservableCollection<DiscoverGameEntry> _games = new();
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly string _session = Guid.NewGuid().ToString();
        private CancellationTokenSource? _cts;
        private List<DiscoverGameEntry>? _recommended;
        private bool _active;
        private List<DiscoverGameEntry>? _lastGames;
        private string _lastQuery = string.Empty;

        public LibraryGamesControl()
        {
            InitializeComponent();
            GamesList.ItemsSource = _games;
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Integrations.FavoriteGames.Changed -= OnFavoriteGameChanged;
            Integrations.FavoriteGames.Changed += OnFavoriteGameChanged;
            HashSet<long> favorites = Integrations.FavoriteGames.Snapshot();
            foreach (DiscoverGameEntry game in _games)
                game.IsFavorite = favorites.Contains(game.PlaceId);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e) => Integrations.FavoriteGames.Changed -= OnFavoriteGameChanged;

        private void OnFavoriteGameChanged(object? sender, long placeId)
        {
            Dispatcher.BeginInvoke(() =>
            {
                foreach (DiscoverGameEntry game in _games)
                {
                    if (game.PlaceId == placeId)
                        game.IsFavorite = Integrations.FavoriteGames.Contains(placeId);
                }
            });
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

        private void GamesList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width = e.NewSize.Width;
            if (width <= 0)
                return;
            int columns = Math.Max(1, (int)Math.Floor(width / (TargetTile + TileGap)));
            TileSize = Math.Floor(width / columns - TileGap);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_active)
                return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _debounce.Stop();
                _ = LoadAsync(SearchBox.Text.Trim());
            }
            else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
            {
                e.Handled = true;
                SearchBox.Text = string.Empty;
            }
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        }

        private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lastGames != null)
                Show(_lastGames, _lastQuery);
        }

        private static long ParsePlaceId(string query)
        {
            Match match = Regex.Match(query, @"(?:roblox\.com/(?:[a-z]{2}/)?games/|placeId=)(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long fromUrl))
                return fromUrl;
            return query.Length >= 3 && query.All(char.IsAsciiDigit) && long.TryParse(query, out long id) ? id : 0;
        }

        private IEnumerable<DiscoverGameEntry> Sorted(List<DiscoverGameEntry> games) => SortBox?.SelectedIndex switch
        {
            1 => games.OrderByDescending(g => g.Players),
            2 => games.OrderBy(g => g.Players),
            3 => games.OrderByDescending(g => g.Rating).ThenByDescending(g => g.Players),
            4 => games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => games,
        };

        private void Show(List<DiscoverGameEntry> games, string query)
        {
            _lastGames = games;
            _lastQuery = query;
            HashSet<long> favorites = Integrations.FavoriteGames.Snapshot();
            _games.Clear();
            foreach (DiscoverGameEntry game in Sorted(games))
            {
                game.IsFavorite = favorites.Contains(game.PlaceId);
                _games.Add(game);
            }
            StatusText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (games.Count == 0)
                StatusText.Text = query.Length == 0 ? Strings.Ui_Bootstrapper_WindowIconsNoGamesFound : string.Format(Strings.ContextMenu_GameBrowser_NoResults, query);
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
                        List<DiscoverGameEntry> found = new();
                        long placeId = ParsePlaceId(query);
                        if (placeId > 0 && await HomePageViewModel.LookupPlaceAsync(placeId, token).ConfigureAwait(false) is DiscoverGameEntry direct)
                            found.Add(direct);
                        if (placeId <= 0 || !query.Contains('/'))
                            found.AddRange(query.Length == 0
                                ? await HomePageViewModel.ReadOmniRecommendationsAsync(_session).ConfigureAwait(false)
                                : await HomePageViewModel.SearchGamesAsync(query, _session, SearchPages, token).ConfigureAwait(false));
                        return found.GroupBy(g => g.UniverseId).Select(g => g.First()).Take(MaxGames).ToList();
                    }, token);
                    if (query.Length == 0)
                        _recommended = games;
                }
                if (token.IsCancellationRequested)
                    return;
                Show(games, query);
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
            game.IsFavorite = Integrations.FavoriteGames.Toggle(game.PlaceId);
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button button || GameOf(sender) is not DiscoverGameEntry game)
                return;
            System.Windows.Controls.ContextMenu menu = new() { PlacementTarget = button, DataContext = game };
            AddItem(menu, Strings.Ui_Pages_Play, Wpf.Ui.Common.SymbolRegular.Play24, "play");
            AddItem(menu, Strings.Ui_Mods_Details, Wpf.Ui.Common.SymbolRegular.Info24, "details");
            AddItem(menu, Strings.ContextMenu_GameBrowser_Favorites, Wpf.Ui.Common.SymbolRegular.Heart24, "favorite");
            menu.Items.Add(new Separator());
            AddItem(menu, Strings.Library_CopyGameLink, Wpf.Ui.Common.SymbolRegular.Link24, "link");
            AddItem(menu, Strings.Ui_Integrations_PlaceID, Wpf.Ui.Common.SymbolRegular.Copy24, "id");
            AddItem(menu, Strings.Ui_Home_ViewGamePage, Wpf.Ui.Common.SymbolRegular.Open24, "web");
            menu.Closed += Menu_Closed;
            menu.IsOpen = true;
        }

        private void AddItem(System.Windows.Controls.ContextMenu menu, string header, Wpf.Ui.Common.SymbolRegular symbol, string action)
        {
            MenuItem item = new() { Header = header, Tag = action, Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = symbol } };
            item.Click += MenuItem_Click;
            menu.Items.Add(item);
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string action, DataContext: DiscoverGameEntry game })
                return;
            string url = "https://www.roblox.com/games/" + game.PlaceId;
            switch (action)
            {
                case "play":
                    HomePageViewModel.LaunchPlace(game.PlaceId);
                    break;
                case "details":
                    if (Window.GetWindow(this) is MainWindow window)
                        window.ShowGameDetail(game.PlaceId, game.UniverseId, game.Name);
                    break;
                case "favorite":
                    Favorite_Click(sender, e);
                    break;
                case "link":
                    ClipboardService.SetDataObject(url, true);
                    break;
                case "id":
                    ClipboardService.SetDataObject(game.PlaceId.ToString(CultureInfo.InvariantCulture), true);
                    break;
                case "web":
                    Utilities.OpenWebLink(url);
                    break;
            }
        }

        private void Menu_Closed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ContextMenu menu)
                return;
            menu.Closed -= Menu_Closed;
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                item.Click -= MenuItem_Click;
        }
    }
}
