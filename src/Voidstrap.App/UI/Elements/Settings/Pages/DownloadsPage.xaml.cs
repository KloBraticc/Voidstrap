using System.ComponentModel;
using System.Windows;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class DownloadsPage : UiPage
{
    private readonly DownloadsViewModel _viewModel = new DownloadsViewModel();
    private DownloadProgressWindow _progressWindow;

    // this page is hosted by the settings window and by the installer, which is a
    // lot narrower, so the side panel and the card grid adapt instead of clipping
    private const double DetailPanelWidth = 300.0;
    private const double NeedsDetailPanel = 680.0;
    private const double NeedsTwoCards = 440.0;

    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns),
        typeof(int),
        typeof(DownloadsPage),
        new PropertyMetadata(2));

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    public DownloadsPage()
    {
        base.DataContext = _viewModel;
        InitializeComponent();
        Loaded += DownloadsPage_Loaded;
        Unloaded += DownloadsPage_Unloaded;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (double.IsNaN(width) || width <= 0.0)
            return;

        bool showDetail = width >= NeedsDetailPanel;
        if (DetailPanel != null)
            DetailPanel.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
        if (DetailColumn != null)
            DetailColumn.Width = new GridLength(showDetail ? DetailPanelWidth : 0.0);

        double cards = showDetail ? width - DetailPanelWidth : width;
        CardColumns = cards >= NeedsTwoCards ? 2 : 1;
    }

    private void DownloadsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.RefreshAll();
    }

    private void Addon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
                mainWindow.RootNavigation?.Navigate(typeof(ExtensionPage));
        }
        catch
        {
        }
    }

    private void DownloadsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        CloseProgressWindow();
    }

    private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadsViewModel.IsDownloading))
            return;
        if (_viewModel.IsDownloading)
            ShowProgressWindow();
        else
            CloseProgressWindow();
    }

    private void ShowProgressWindow()
    {
        if (_progressWindow != null)
            return;
        _progressWindow = new DownloadProgressWindow(_viewModel)
        {
            Owner = Window.GetWindow(this)
        };
        _progressWindow.Closed += OnProgressWindowClosed;
        _progressWindow.Show();
    }

    private void OnProgressWindowClosed(object sender, System.EventArgs e)
    {
        if (_progressWindow != null)
            _progressWindow.Closed -= OnProgressWindowClosed;
        _progressWindow = null;
    }

    private void CloseProgressWindow()
    {
		DownloadProgressWindow progressWindow = _progressWindow;
		_progressWindow = null;
		if (progressWindow == null)
			return;

		progressWindow.Closed -= OnProgressWindowClosed;
        try
        {
			progressWindow.Close();
        }
        catch
        {
        }
    }
}
