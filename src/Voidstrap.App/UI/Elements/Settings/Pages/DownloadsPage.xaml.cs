using System.Windows;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class DownloadsPage : UiPage
{
    private readonly DownloadsViewModel _viewModel = DownloadsViewModel.Shared;

    private const double DetailPanelWidth = 300.0;

    private string _search = "";

    private readonly System.Predicate<object> _searchFilter;

    private const double PosterMinWidth = 190.0;

    public static readonly DependencyProperty ClassicColumnsProperty = DependencyProperty.Register(
        nameof(ClassicColumns),
        typeof(int),
        typeof(DownloadsPage),
        new PropertyMetadata(3)
    );

    public int ClassicColumns
    {
        get => (int)GetValue(ClassicColumnsProperty);
        set => SetValue(ClassicColumnsProperty, value);
    }

    public DownloadsPage()
    {
        _searchFilter = MatchesSearch;
        base.DataContext = _viewModel;
        InitializeComponent();
        Loaded += DownloadsPage_Loaded;
        Unloaded += DownloadsPage_Unloaded;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLayout(e.NewSize.Width);
    }

    private void ApplyLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0.0)
            return;

        bool showDetail = Window.GetWindow(this) is MainWindow;
        DetailPanel.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = new GridLength(showDetail ? DetailPanelWidth : 0.0);

        double cards = (showDetail ? width - DetailPanelWidth : width) - 40.0;
        int columns = System.Math.Max(1, (int)(cards / PosterMinWidth));
        if (columns != ClassicColumns)
            ClassicColumns = columns;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? "";
        ApplyFilters();
    }

    private void FilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private bool MatchesSearch(object item)
    {
        if (_search.Length == 0)
            return true;
        string title = item switch
        {
            DownloadsViewModel.DownloadItem download => download.Title + " " + download.Subtitle,
            DownloadsViewModel.ClientItem client => client.Title + " " + client.Description,
            DownloadsViewModel.VrItem vr => vr.Title + " " + vr.Subtitle + " SteamVR",
            _ => "",
        };
        return title.Contains(_search, System.StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilters()
    {
        if (RobloxItems == null || ClassicItems == null)
            return;
        int filter = FilterCombo?.SelectedIndex ?? 0;
        RobloxSection.Visibility = filter == 0 || filter == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClassicSection.Visibility = filter == 0 || filter == 2 ? Visibility.Visible : Visibility.Collapsed;
        VrSection.Visibility = _viewModel.HasVr && (filter == 0 || filter == 3) ? Visibility.Visible : Visibility.Collapsed;
        System.Predicate<object>? searchFilter = _search.Length == 0 ? null : _searchFilter;
        SetFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.Items), searchFilter);
        SetFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.ClientItems), searchFilter);
        SetFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.VrItems), searchFilter);
    }

    private static void SetFilter(System.ComponentModel.ICollectionView view, System.Predicate<object>? filter)
    {
        if (!ReferenceEquals(view.Filter, filter))
        {
            view.Filter = filter;
        }
        else if (filter != null)
        {
            view.Refresh();
        }
    }

    private void DownloadsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout(ActualWidth);
        _viewModel.RefreshAll();
        ApplyFilters();
        if (_viewModel.SelectedItem == null && _viewModel.Items.Count > 0)
            _viewModel.SelectedItem = _viewModel.Items[0];
    }

    private void Addon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                if (sender is FrameworkElement { Tag: "swifttunnel" })
                    ExtensionPage.RequestedExtension = "swifttunnel";
                mainWindow.RootNavigation?.Navigate(typeof(ExtensionPage));
            }
        }
        catch { }
    }

    private void DownloadsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        SetFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.Items), null);
        SetFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.ClientItems), null);
        SetFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.VrItems), null);
    }
}
