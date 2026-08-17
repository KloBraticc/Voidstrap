using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Navigation;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer.Pages;

public partial class WelcomePage : UiPage{
	private readonly WelcomeViewModel _viewModel = new WelcomeViewModel();

	public WelcomePage()
	{
		if (Window.GetWindow((DependencyObject)(object)this) is MainWindow mainWindow)
		{
			mainWindow.SetButtonEnabled("next", state: true);
		}
		base.DataContext = _viewModel;
		InitializeComponent();
	}

	private void UiPage_Loaded(object sender, RoutedEventArgs e)
	{
		if (Window.GetWindow((DependencyObject)(object)this) is MainWindow mainWindow)
		{
			mainWindow.SetNextButtonText(Strings.Common_Navigation_Next);
		}
	}

	private static void OpenLink(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("WelcomePage::OpenLink", "Could not open " + url + ": " + ex.Message);
		}
	}

	private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
	{
		OpenLink(e.Uri.AbsoluteUri);
		e.Handled = true;
	}

	private void LinkCard_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement element && element.Tag is string url && url.Length > 0)
			OpenLink(url);
	}

	private void DonateButton_Click(object sender, RoutedEventArgs e)
	{
		OpenLink(App.WebsiteBaseUrl + "/donate/donate");
	}

	private void ContributorsButton_Click(object sender, RoutedEventArgs e)
	{
		OpenLink(App.WebsiteBaseUrl + "/contributors/contributors");
	}
}
