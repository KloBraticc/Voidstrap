using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Navigation;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.About;

public partial class MainWindow : WpfUiWindow,INavigationWindow{

	public MainWindow()
	{
		InitializeComponent();
		App.Logger.WriteLine("MainWindow", "Initializing about window");
		if (Locale.CurrentCulture.Name.StartsWith("tr"))
		{
			TranslatorsText.FontSize = 9.0;
		}
	}

	public Frame GetFrame()
	{
		return RootFrame;
	}

	public INavigation GetNavigation()
	{
		return RootNavigation;
	}

	public bool Navigate(Type pageType)
	{
		return RootNavigation.Navigate(pageType);
	}

	public void SetPageService(IPageService pageService)
	{
		RootNavigation.PageService = pageService;
	}

	public void ShowWindow()
	{
		Show();
	}

	public void CloseWindow()
	{
		Close();
	}

	private void RootFrame_Navigated(object sender, NavigationEventArgs e)
	{
	}
}
