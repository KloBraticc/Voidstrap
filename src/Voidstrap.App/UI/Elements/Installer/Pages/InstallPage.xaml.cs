using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Installer.Pages;

public partial class InstallPage : UiPage{
	private readonly InstallViewModel _viewModel = new InstallViewModel();

	public InstallPage()
	{
		base.DataContext = _viewModel;
		InstallViewModel viewModel = _viewModel;
		viewModel.SetCanContinueEvent = (EventHandler<bool>)Delegate.Combine(viewModel.SetCanContinueEvent, (EventHandler<bool>)delegate(object? _, bool state)
		{
			if (Window.GetWindow((DependencyObject)(object)this) is MainWindow mainWindow)
			{
				mainWindow.SetButtonEnabled("next", state);
			}
		});
		InitializeComponent();
	}

	private void UiPage_Loaded(object sender, RoutedEventArgs e)
	{
		if (Window.GetWindow((DependencyObject)(object)this) is MainWindow mainWindow)
		{
			mainWindow.SetNextButtonText(Strings.Common_Navigation_Install);
			mainWindow.NextPageCallback = (Func<bool>)Delegate.Combine(mainWindow.NextPageCallback, new Func<bool>(NextPageCallback));
		}
	}

	public bool NextPageCallback()
	{
		return _viewModel.DoInstall();
	}
}
