using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class AccountsPage : UiPage
    {
        private readonly AccountsViewModel _viewModel;

        private readonly PageSidebar _sidebar;

        public AccountsPage()
        {
            _viewModel = new AccountsViewModel();
            DataContext = _viewModel;
            InitializeComponent();
            _sidebar = new PageSidebar(AccountsSidebar, SidebarResizer, this, SetSidebarIconsOnly, SaveSidebarWidth);
            _sidebar.Apply(App.Settings.Prop.AccountsSidebarWidth);
            Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = _viewModel.LoadAsync();
        }

        public bool IsSidebarCollapsed => _sidebar.IsCollapsed;

        public void ToggleSidebar()
        {
            _sidebar.Toggle();
        }

        public void Release()
        {
            Loaded -= OnPageLoaded;
            _viewModel.Dispose();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            AddMenu.PlacementTarget = AddButton;
            AddMenu.Placement = PlacementMode.Bottom;
            AddMenu.DataContext = _viewModel;
            AddMenu.IsOpen = true;
        }

        private void SetSidebarIconsOnly(bool iconsOnly)
        {
            Visibility labels = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
            AddButton.Content = iconsOnly ? null : Voidstrap.Resources.Strings.Accounts_AddAccount;
            SidebarSearchBox.Visibility = labels;
            SidebarStatusRow.Visibility = labels;
            SidebarFooter.Visibility = labels;
            SidebarList.Tag = iconsOnly ? "icons" : "full";
        }

        private static void SaveSidebarWidth(double width)
        {
            App.Settings.Prop.AccountsSidebarWidth = width;
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
