using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.UI.ViewModels.Pages;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class HomePage : Page
    {
        private readonly HomePageViewModel _viewModel;

        public HomePage()
        {
            _viewModel = new HomePageViewModel();
            DataContext = _viewModel;
            InitializeComponent();
            ApplyHomeLayout();
            Loaded += OnHomePageLoaded;
            Unloaded += OnHomePageUnloaded;
            PreviewKeyDown += HomePage_PreviewKeyDown;
        }

        private void OnHomePageUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.RiShadeReleaseRequested -= OnRiShadeReleaseRequested;
            PreviewKeyDown -= HomePage_PreviewKeyDown;
            Voidstrap.Integrations.FavoriteGames.Changed -= OnFavoriteGameChanged;
        }

        private void OnFavoriteGameChanged(object? sender, long placeId)
        {
            if (Dispatcher.CheckAccess())
                _viewModel.RefreshFavorites(placeId);
            else
                Dispatcher.BeginInvoke(() => _viewModel.RefreshFavorites(placeId));
        }

        private void OnRiShadeReleaseRequested(object? sender, EventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow window)
                window.ShowRiShadeRelease();
        }

        private async void OnHomePageLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel.RiShadeReleaseRequested -= OnRiShadeReleaseRequested;
            _viewModel.RiShadeReleaseRequested += OnRiShadeReleaseRequested;
            Voidstrap.Integrations.FavoriteGames.Changed -= OnFavoriteGameChanged;
            Voidstrap.Integrations.FavoriteGames.Changed += OnFavoriteGameChanged;
            _viewModel.RefreshMatchmakerState();
            ApplyHomeLayout();
            Voidstrap.Integrations.Plugins.PluginAnchors.ApplyAll();
            await _viewModel.LoadAsync();
            Voidstrap.Integrations.Plugins.PluginAnchors.ApplyAll();
        }

        private void ExploreNews_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow window)
                window.ShowRobloxNews();
            else
                Utilities.ShellExecute(Voidstrap.Integrations.RobloxNews.FeedPageUrl);
        }

        private void CatalogSeeMore_Click(object sender, RoutedEventArgs e)
        {
            Utilities.OpenWebLink("https://www.roblox.com/catalog?Category=1&SortType=3");
        }

        public bool IsStudioTab
        {
            get => _viewModel.IsStudioTab;
            set => _viewModel.IsStudioTab = value;
        }

        private void StudioScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _viewModel.StudioColumns = Math.Max(1, (int)((e.NewSize.Width - 28) / 190));
        }

        private void StudioMore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: Voidstrap.Integrations.StudioProject project } button)
                return;

            System.Windows.Controls.ContextMenu menu = new() { PlacementTarget = button };
            foreach ((string action, string header) in new[]
            {
                ("studio", "Open in Studio"),
                ("view", Strings.Ui_Home_ViewGamePage),
                ("creator", "Open in Creator Hub"),
                ("copy", "Copy place ID"),
            })
            {
                MenuItem item = new() { Header = header, Tag = (action, project) };
                item.Click += StudioMenuItem_Click;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private void StudioMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: ValueTuple<string, Voidstrap.Integrations.StudioProject> target } item)
                return;

            item.Click -= StudioMenuItem_Click;
            (string action, Voidstrap.Integrations.StudioProject project) = target;
            switch (action)
            {
                case "studio":
                    Voidstrap.Integrations.StudioProjects.Open(project);
                    break;
                case "view":
                    if (Window.GetWindow(this) is MainWindow window)
                        window.ShowLibraryGame(project.PlaceId, project.UniverseId, project.Name);
                    break;
                case "creator":
                    Utilities.ShellExecute($"https://create.roblox.com/dashboard/creations/experiences/{project.UniverseId}/overview");
                    break;
                case "copy":
                    Voidstrap.Utility.ClipboardService.SetText(project.PlaceId.ToString());
                    break;
            }
        }

        private void GameThumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            (long placeId, long universeId, string name) = (sender is FrameworkElement { Tag: DiscoverGameEntry tagged } ? tagged : (sender as FrameworkElement)?.DataContext) switch
            {
                HistoryGameEntry history => (history.PlaceId, history.UniverseId, history.Name),
                DiscoverGameEntry discover => (discover.PlaceId, discover.UniverseId, discover.Name),
                _ => (0L, 0L, string.Empty),
            };
            if (placeId == 0)
                return;

            e.Handled = true;
            if (Window.GetWindow(this) is MainWindow window)
                window.ShowGameDetail(placeId, universeId, name);
        }

        private void ViewDiscoverGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: DiscoverGameEntry game } || Window.GetWindow(this) is not MainWindow window)
                return;
            e.Handled = true;
            window.ShowLibraryGame(game.PlaceId, game.UniverseId, game.Name);
        }

        private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.ContextMenu != null)
            {
                el.ContextMenu.PlacementTarget = el;
                el.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                el.ContextMenu.VerticalOffset = 4;
                el.ContextMenu.IsOpen = true;
            }
        }
    }
}
