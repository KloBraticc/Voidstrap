using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Navigation;
using Voidstrap.Enums;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Installer.Pages;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Installer;

public partial class MainWindow : WpfUiWindow,INavigationWindow{
	internal readonly MainWindowViewModel _viewModel = new MainWindowViewModel();

	private Type _currentPage = typeof(SignInPage);

	private List<Type> _pages = new List<Type>
	{
		typeof(SignInPage),
		typeof(WelcomePage),
		typeof(InstallPage),
		typeof(ChannelPage),
		typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage),
		typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage),
		typeof(CompletionPage)
	};

	private DateTimeOffset _lastNavigation = DateTimeOffset.Now;

	public Func<bool>? NextPageCallback;

	public NextAction CloseAction;

	public bool Finished => _currentPage == _pages.Last();

	public MainWindow()
	{
		SetButtonEnabled("next", state: true);
		_viewModel.CloseWindowRequest += OnCloseWindowRequest;
		_viewModel.PageRequest += OnPageRequest;
		base.DataContext = _viewModel;
		InitializeComponent();
		App.Logger.WriteLine("MainWindow", "Initializing installer window");
		base.Closing += MainWindow_Closing;
		base.Closed += MainWindow_Closed;
		PaintSteps(0);
		ApplyChrome(typeof(SignInPage));
	}

	private void OnCloseWindowRequest(object? sender, EventArgs e)
	{
		CloseWindow();
	}

	private void OnPageRequest(object? sender, string type)
	{
		if (!(DateTimeOffset.Now.Subtract(_lastNavigation).TotalMilliseconds < 500.0))
		{
			if (type == "next")
			{
				NextPage();
			}
			else if (type == "back")
			{
				BackPage();
			}
			_lastNavigation = DateTimeOffset.Now;
		}
	}

	private void MainWindow_Closed(object? sender, EventArgs e)
	{
		base.Closing -= MainWindow_Closing;
		base.Closed -= MainWindow_Closed;
		_viewModel.CloseWindowRequest -= OnCloseWindowRequest;
		_viewModel.PageRequest -= OnPageRequest;
		RootFrame.Navigated -= RootFrame_Navigated;
		RootFrame.Content = null;
		NextPageCallback = null;
		DataContext = null;
	}

	private void NextPage()
	{
		if ((NextPageCallback == null || NextPageCallback()) && !(_currentPage == _pages.Last()))
		{
			Type type = _pages[_pages.IndexOf(_currentPage) + 1];
			Navigate(type);
			SetButtonEnabled("next", type != _pages.Last());
			SetButtonEnabled("back", state: true);
		}
	}

	private void BackPage()
	{
		if (!(_currentPage == _pages.First()))
		{
			Type type = _pages[_pages.IndexOf(_currentPage) - 1];
			Navigate(type);
			SetButtonEnabled("next", state: true);
			SetButtonEnabled("back", type != _pages.First());
		}
	}

	private void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (App.LaunchSettings.WindowAuditFlag.Active)
		{
			return;
		}
		if (!Finished && Frontend.ShowMessageBox(Strings.Installer_ShouldCancel, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			e.Cancel = true;
		}
	}

	public void SetNextButtonText(string text)
	{
		_viewModel.SetNextButtonText(text);
	}

	public void SetButtonEnabled(string type, bool state)
	{
		_viewModel.SetButtonEnabled(type, state);
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
		_currentPage = pageType;
		NextPageCallback = null;
		int index = _pages.IndexOf(pageType);
		if (index < 0)
			index = 0;
		_viewModel.SetNextButtonText(Strings.Common_Navigation_Next);
		_viewModel.SetStep(index, HeadingFor(pageType));
		PaintSteps(index);
		ApplyChrome(pageType);
		return RootNavigation.Navigate(pageType);
	}

	private void PaintSteps(int active)
	{
		if (RootNavigation != null && active >= 0 && active < RootNavigation.Items.Count)
		{
			RootNavigation.SelectedPageIndex = active;
		}
	}

	private void ApplyChrome(Type pageType)
	{
		bool standalone = pageType == typeof(SignInPage);
		Visibility chrome = standalone ? Visibility.Collapsed : Visibility.Visible;

		if (SidebarHost != null)
		{
			SidebarHost.Visibility = chrome;
		}

		if (NavButtonBar != null)
		{
			NavButtonBar.Visibility = chrome;
		}

		if (StepHeadingText != null)
		{
			StepHeadingText.Visibility = chrome;
		}

		if (RootGrid != null)
		{
			RootGrid.ColumnDefinitions[0].Width = standalone ? new GridLength(0.0) : new GridLength(250.0);
		}
	}

	private static string HeadingFor(Type pageType)
	{
		if (pageType == typeof(WelcomePage))
			return Strings.Installer_Welcome_Title;
		if (pageType == typeof(InstallPage))
			return Strings.Installer_Install_Title;
		if (pageType == typeof(ChannelPage))
			return "Channel";
		if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage))
			return "Manager";
		if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage))
			return "Extensions";
		if (pageType == typeof(CompletionPage))
			return Strings.Installer_Completion_Title;
		return "Welcome";
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
