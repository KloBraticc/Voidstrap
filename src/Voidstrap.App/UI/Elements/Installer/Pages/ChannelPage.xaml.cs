using System.Windows;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer.Pages;

public partial class ChannelPage : UiPage
{
    private readonly ChannelViewModel _viewModel = new ChannelViewModel();

    public ChannelPage()
    {
        base.DataContext = _viewModel;
        InitializeComponent();
    }

    private void UiPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.SetNextButtonText(Voidstrap.Resources.Strings.Common_Navigation_Next);
            mainWindow.NextPageCallback = NextPageCallback;
        }
    }

    private void UiPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Apply();
    }

    private bool NextPageCallback()
    {
        _viewModel.Apply();
        return true;
    }
}
