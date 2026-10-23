using System.Windows;
using System.Windows.Input;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class PrivateServersPage : UiPage
    {
        private readonly PrivateServersViewModel _viewModel;

        private readonly PageSidebar _sidebar;

        public PrivateServersPage()
        {
            _viewModel = new PrivateServersViewModel();
            DataContext = _viewModel;
            InitializeComponent();
            _sidebar = new PageSidebar(ServersSidebar, SidebarResizer, this, SetSidebarIconsOnly, SaveSidebarWidth);
            _sidebar.Apply(App.Settings.Prop.PrivateServersSidebarWidth);
            Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = _viewModel.LoadAsync(false);
        }

        public void ToggleSidebar()
        {
            _sidebar.Toggle();
        }

        private void SetSidebarIconsOnly(bool iconsOnly)
        {
            Visibility labels = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
            SidebarTabsRow.Visibility = labels;
            SidebarTabsIcons.Visibility = iconsOnly ? Visibility.Visible : Visibility.Collapsed;
            SidebarSearchBox.Visibility = labels;
            SidebarRefreshRow.Visibility = labels;
            SidebarList.Tag = iconsOnly ? "icons" : "full";
        }

        private static void SaveSidebarWidth(double width)
        {
            App.Settings.Prop.PrivateServersSidebarWidth = width;
            App.Settings.SaveDeferred();
        }

        private void SidebarResizer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sidebar.ResizerDown(e);
        }

        private void SidebarResizer_MouseMove(object sender, MouseEventArgs e)
        {
            _sidebar.ResizerMove(e);
        }

        private void SidebarResizer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _sidebar.ResizerUp();
        }
    }
}
