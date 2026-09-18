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
            Loaded += OnHomePageLoaded;
        }

        private void OnHomePageLoaded(object sender, RoutedEventArgs e)
        {
            _ = _viewModel.LoadAsync();
        }

        private void ExploreNews_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow window)
                window.ShowRobloxNews();
            else
                Utilities.ShellExecute(Voidstrap.Integrations.RobloxNews.FeedPageUrl);
        }

        private void HomeTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tab })
                _viewModel.IsStudioTab = tab == "Studio";
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
            foreach (string action in new[] { "Open in Studio", "View game page", "Open in Creator Hub", "Copy place ID" })
            {
                MenuItem item = new() { Header = action, Tag = project };
                item.Click += StudioMenuItem_Click;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private void StudioMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: Voidstrap.Integrations.StudioProject project, Header: string action } item)
                return;

            item.Click -= StudioMenuItem_Click;
            switch (action)
            {
                case "Open in Studio":
                    Voidstrap.Integrations.StudioProjects.Open(project);
                    break;
                case "View game page":
                    NavigationService?.Navigate(new GamePage(project.PlaceId, project.UniverseId));
                    break;
                case "Open in Creator Hub":
                    Utilities.ShellExecute($"https://create.roblox.com/dashboard/creations/experiences/{project.UniverseId}/overview");
                    break;
                case "Copy place ID":
                    Voidstrap.Utility.ClipboardService.SetText(project.PlaceId.ToString());
                    break;
            }
        }

        private void GameThumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not HistoryGameEntry entry)
                return;

            if (entry.PlaceId == 0)
                return;

            e.Handled = true;
            NavigationService?.Navigate(new GamePage(entry.PlaceId, entry.UniverseId));
        }
    }
}
