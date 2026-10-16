using System.Windows;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ModGeneratorPage : UiPage
{
	public ModGeneratorPage()
	{
		InitializeComponent();
	}

	private void BackButton_Click(object sender, RoutedEventArgs e)
	{
		if (NavigationService?.CanGoBack == true)
			NavigationService.GoBack();
		else
			NavigationService?.Navigate(new ModsPage());
	}
}
