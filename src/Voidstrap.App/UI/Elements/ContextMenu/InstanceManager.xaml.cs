using System;
using System.Windows.Controls;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class InstanceManager : WpfUiWindow, INavigationWindow
{
	public InstanceManager()
	{
		InitializeComponent();
		App.Logger.WriteLine("InstanceManager", "Initializing instance window");
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
}
