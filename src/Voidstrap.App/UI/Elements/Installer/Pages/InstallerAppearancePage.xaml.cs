using System.Windows;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer.Pages;

public partial class InstallerAppearancePage : UiPage
{
	private readonly AppearanceViewModel _viewModel = new AppearanceViewModel();

	public InstallerAppearancePage()
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

	private static bool NextPageCallback()
	{
		App.Settings.SaveDeferred();
		return true;
	}
}
