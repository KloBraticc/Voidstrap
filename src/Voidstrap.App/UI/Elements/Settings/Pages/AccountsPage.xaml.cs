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

        private Point _dragStart;

        private AccountRow? _dragRow;

        private System.Windows.Controls.ListBoxItem? _dragContainer;

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

        private void SidebarList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ResetDragState();
            if (System.Windows.Controls.ItemsControl.ContainerFromElement(SidebarList, e.OriginalSource as DependencyObject) is System.Windows.Controls.ListBoxItem container && container.DataContext is AccountRow row)
            {
                _dragRow = row;
                _dragContainer = container;
                _dragStart = e.GetPosition(SidebarList);
            }
        }

        private void SidebarList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragRow is not AccountRow row || _dragContainer is not System.Windows.Controls.ListBoxItem container)
                return;
            Point position = e.GetPosition(SidebarList);
            if (
                Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance
            )
                return;
            ResetDragState();
            container.Opacity = 0.55;
            try
            {
                DragDrop.DoDragDrop(SidebarList, row, DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("AccountsPage::SidebarDrag", "The account could not be dragged: " + ex.Message);
            }
            finally
            {
                container.Opacity = 1;
            }
        }

        private void SidebarList_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(AccountRow)))
                return;
            e.Effects = DragDropEffects.Move;
            AutoScrollList(e);
            e.Handled = true;
        }

        private async void SidebarList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(AccountRow)) is not AccountRow source)
                return;
            e.Handled = true;
            try
            {
                await _viewModel.MoveToAsync(source, GetDropIndex(e));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AccountsPage::SidebarDrop", ex);
            }
        }

        private int GetDropIndex(DragEventArgs e)
        {
            for (int i = 0; i < SidebarList.Items.Count; i++)
            {
                if (
                    SidebarList.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.ListBoxItem { ActualHeight: > 0 } container
                    && e.GetPosition(container).Y < container.ActualHeight / 2
                )
                    return i;
            }
            return SidebarList.Items.Count;
        }

        private void AutoScrollList(DragEventArgs e)
        {
            System.Windows.Controls.ScrollViewer? viewer = FindScrollViewer(SidebarList);
            if (viewer == null)
                return;
            Point position = e.GetPosition(viewer);
            if (position.Y < 36)
                viewer.LineUp();
            else if (position.Y > viewer.ViewportHeight - 36)
                viewer.LineDown();
        }

        private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.ScrollViewer viewer)
                    return viewer;
                System.Windows.Controls.ScrollViewer? nested = FindScrollViewer(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private void ResetDragState()
        {
            _dragRow = null;
            _dragContainer = null;
        }
    }
}
