using System.Windows;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer.Pages;

public partial class InstallerModsPage : UiPage
{
	private readonly ModsViewModel _viewModel = new ModsViewModel();

	public InstallerModsPage()
	{
		DataContext = _viewModel;
		InitializeComponent();
	}

	private void UiPage_Loaded(object sender, RoutedEventArgs e)
	{
		if (Window.GetWindow(this) is MainWindow mainWindow)
		{
			mainWindow.SetNextButtonText(Strings.Common_Navigation_Next);
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
