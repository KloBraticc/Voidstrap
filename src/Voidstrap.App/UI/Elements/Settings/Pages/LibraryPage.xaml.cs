using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class LibraryPage : UiPage
{
    private const double StickyThreshold = 220;

    private readonly LibraryViewModel _viewModel = new LibraryViewModel();

    private bool _stickyShown;

    private readonly PageSidebar _sidebar;

    public bool IsSidebarCollapsed => _sidebar.IsCollapsed;

    public LibraryPage()
    {
        base.DataContext = _viewModel;
        InitializeComponent();
        _sidebar = new PageSidebar(LibrarySidebar, SidebarResizer, this, SetSidebarIconsOnly, SaveSidebarWidth);
        _sidebar.Apply(App.Settings.Prop.LibrarySidebarWidth);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.SetSubplaceViewActive(true);
            _viewModel.Servers.SetViewActive(true);
            if (!_viewModel.HasLoaded)
                await _viewModel.LoadAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("LibraryPage::Page_Loaded", ex);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.SetSubplaceViewActive(false);
        _viewModel.Servers.SetViewActive(false);
    }

    private void SetSidebarIconsOnly(bool iconsOnly)
    {
        Visibility labels = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
        SidebarHomeLabel.Visibility = labels;
        SidebarHomeIcon.Margin = iconsOnly ? new Thickness(0) : new Thickness(0, 0, 14, 0);
        SidebarHomeContent.HorizontalAlignment = iconsOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SidebarSearchBox.Visibility = labels;
        SidebarRefreshRow.Visibility = labels;
        SidebarAddRow.Visibility = labels;
        SidebarList.Tag = iconsOnly ? "icons" : "full";
    }

    private static void SaveSidebarWidth(double width)
    {
        App.Settings.Prop.LibrarySidebarWidth = width;
        App.Settings.SaveDeferred();
    }

    public void ToggleSidebar()
    {
        _sidebar.Toggle();
    }

    private void SidebarResizer_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _sidebar.ResizerDown(e);
    }

    private void SidebarResizer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _sidebar.ResizerMove(e);
    }

    private void SidebarResizer_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _sidebar.ResizerUp();
    }

    private void DetailScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool shouldShow = e.VerticalOffset > StickyThreshold;
        if (shouldShow == _stickyShown)
            return;

        _stickyShown = shouldShow;
        StickyBar.IsHitTestVisible = shouldShow;

        var fade = new DoubleAnimation(shouldShow ? 1.0 : 0.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        StickyBar.BeginAnimation(OpacityProperty, fade);
    }

    private void DashboardScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0)
            return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 480)
            _viewModel.LoadMoreGames();
    }
}
