using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class LibraryPage : UiPage
{
    private readonly LibraryViewModel _viewModel = new LibraryViewModel();

    private LibraryGameEntry? _shownGame;

    private readonly PageSidebar _sidebar;

    public bool IsSidebarCollapsed => _sidebar.IsCollapsed;

    public LibraryPage()
    {
        base.DataContext = _viewModel;
        InitializeComponent();
        _ = LoadLibraryTabIconAsync();
        _sidebar = new PageSidebar(LibrarySidebar, SidebarResizer, this, SetSidebarIconsOnly, SaveSidebarWidth);
        _sidebar.Apply(App.Settings.Prop.LibrarySidebarWidth);
    }

    private void GamePassGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement grid && e.NewSize.Width > 0)
            grid.Tag = Math.Max(2, (int)(e.NewSize.Width / 170));
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Voidstrap.UI.Elements.Controls.LibraryGamesControl.Prefetch();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
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

    private void AllGames_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.AllGamesColumns = Math.Max(1, (int)(e.NewSize.Width / AllGamesTileWidth));
    }

    private const double AllGamesTileWidth = 160;

    private bool _addGameOpen;

    private void AddGameToggle_Click(object sender, RoutedEventArgs e)
    {
        _addGameOpen = !_addGameOpen;
        TimeSpan duration = TimeSpan.FromMilliseconds(200);
        CubicEase ease = new() { EasingMode = EasingMode.EaseInOut };
        if (_addGameOpen)
            AddGamePanel.Visibility = Visibility.Visible;
        DoubleAnimation fade = new(_addGameOpen ? 1 : 0, duration) { EasingFunction = ease };
        fade.Completed += AddGameFade_Completed;
        AddGamePanel.BeginAnimation(OpacityProperty, fade);
        AddGamePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(_addGameOpen ? 1 : 0, duration) { EasingFunction = ease });
        AddGameToggleRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, new DoubleAnimation(_addGameOpen ? 45 : 0, duration) { EasingFunction = ease });
    }

    private void AddGameFade_Completed(object? sender, EventArgs e)
    {
        if (!_addGameOpen)
            AddGamePanel.Visibility = Visibility.Collapsed;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.SelectedGame) || ReferenceEquals(_shownGame, _viewModel.SelectedGame))
            return;
        _shownGame = _viewModel.SelectedGame;
        DetailScroll.ScrollToTop();
    }

    private async Task LoadLibraryTabIconAsync()
    {
        const string LOG_IDENT = "LibraryPage::LoadLibraryTabIconAsync";

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            (long universeId, long placeId)? latest = await TaskbarJumpList.ReadLatestGameAsync(timeout.Token);
            if (latest is not { universeId: > 0 } game)
                return;
            string? url = await TaskbarJumpList.ResolveIconUrlAsync(game.universeId, timeout.Token);
            if (string.IsNullOrWhiteSpace(url))
                return;
            var bitmap = await AppImage.LoadAsync(url, 64, timeout.Token);
            if (bitmap == null)
                return;
            LibraryTabIcon.Background = new System.Windows.Media.ImageBrush(bitmap) { Stretch = System.Windows.Media.Stretch.UniformToFill };
            LibraryTabIcon.Visibility = Visibility.Visible;
            LibraryTabSymbol.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private void RootTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == RootTabs && GamesView != null && GamesTab != null)
            GamesView.SetActive(GamesTab.IsSelected);
        if (e.OriginalSource == RootTabs && CommunityView != null && CommunityTab != null)
            CommunityView.SetActive(CommunityTab.IsSelected);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        GamesView.SetActive(false);
        CommunityView.SetActive(false);
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
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

    private void DashboardScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0)
            return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 480)
            _viewModel.LoadMoreGames();
    }
}
