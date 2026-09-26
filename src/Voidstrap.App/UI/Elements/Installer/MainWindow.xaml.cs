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
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Installer;

public partial class MainWindow : WpfUiWindow,INavigationWindow{
	internal readonly MainWindowViewModel _viewModel = new MainWindowViewModel();

	private Type _currentPage = typeof(InstallPage);

	private List<Type> _pages = CreatePages();

	private DateTimeOffset _lastNavigation = DateTimeOffset.Now;

	private DependencyObject? _linuxTextContent;

	public Func<bool>? NextPageCallback;
	public Func<System.Threading.Tasks.Task<bool>>? NextPageAsyncCallback;
	private bool _navigating;

	public NextAction CloseAction;

	public bool Finished => _currentPage == _pages.Last();

	private static List<Type> CreatePages()
	{
		List<Type> pages =
		[
			typeof(InstallPage),
			typeof(ChannelPage)
		];
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			pages.Add(typeof(InstallerModsPage));
			pages.Add(typeof(InstallerAppearancePage));
			pages.Add(typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage));
			pages.Add(typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage));
		}

		pages.Add(typeof(CompletionPage));
		return pages;
	}

	public MainWindow()
	{
		SetButtonEnabled("next", state: true);
		_viewModel.CloseWindowRequest += OnCloseWindowRequest;
		_viewModel.PageRequest += OnPageRequest;
		base.DataContext = _viewModel;
		InitializeComponent();
		App.Logger.WriteLine("MainWindow", "Initializing installer window");
		base.Closing += MainWindow_Closing;
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			RootFrame.SizeChanged += RootFrame_SizeChanged;
			Voidstrap.UI.LinuxUiPerformance.ReducedMotionChanged += OnLinuxReducedMotionChanged;
			if (Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
				RootNavigation.TransitionDuration = 0;
		}
		base.Closed += MainWindow_Closed;
		_viewModel.SetStep(0, HeadingFor(typeof(InstallPage)));
		PaintSteps(0);
		ApplyChrome(typeof(InstallPage));
	}

	private void OnLinuxReducedMotionChanged(object? sender, EventArgs e)
	{
		RootNavigation.TransitionDuration = 0;
	}

	private void OnCloseWindowRequest(object? sender, EventArgs e)
	{
		CloseWindow();
	}

	private async void OnPageRequest(object? sender, string type)
	{
		if (!_navigating && !(DateTimeOffset.Now.Subtract(_lastNavigation).TotalMilliseconds < 500.0))
		{
			if (type == "next")
			{
				await NextPageAsync();
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
		Voidstrap.UI.LinuxUiPerformance.ReducedMotionChanged -= OnLinuxReducedMotionChanged;
		base.Closing -= MainWindow_Closing;
		base.Closed -= MainWindow_Closed;
		_viewModel.CloseWindowRequest -= OnCloseWindowRequest;
		_viewModel.PageRequest -= OnPageRequest;
		RootFrame.Navigated -= RootFrame_Navigated;
		RootFrame.SizeChanged -= RootFrame_SizeChanged;
		_linuxTextContent = null;
		RootFrame.Content = null;
		NextPageCallback = null;
		NextPageAsyncCallback = null;
		DataContext = null;
	}

	private async System.Threading.Tasks.Task NextPageAsync()
	{
		if (_currentPage == _pages.Last())
			return;
		_navigating = true;
		SetButtonEnabled("next", false);
		SetButtonEnabled("back", false);
		try
		{
			bool proceed = NextPageAsyncCallback is not null
				? await NextPageAsyncCallback()
				: NextPageCallback?.Invoke() ?? true;
			if (!proceed)
				return;
			Type type = _pages[_pages.IndexOf(_currentPage) + 1];
			Navigate(type);
			SetButtonEnabled("next", type != _pages.Last());
			SetButtonEnabled("back", type != _pages.Last());
		}
		finally
		{
			_navigating = false;
			SetButtonEnabled("back", _currentPage != _pages.First() && _currentPage != _pages.Last());
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
		NextPageAsyncCallback = null;
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
		StepIcon.Symbol = IconFor(pageType);
	}

	private static SymbolRegular IconFor(Type pageType)
	{
		if (pageType == typeof(InstallPage))
			return SymbolRegular.ArrowDownload24;
		if (pageType == typeof(ChannelPage))
			return SymbolRegular.Globe24;
		if (pageType == typeof(InstallerModsPage))
			return SymbolRegular.PaintBrush24;
		if (pageType == typeof(InstallerAppearancePage))
			return SymbolRegular.Color24;
		if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage))
			return SymbolRegular.Apps24;
		if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage))
			return SymbolRegular.PuzzlePiece24;
		return SymbolRegular.CheckmarkCircle24;
	}

	private static string HeadingFor(Type pageType)
	{
		if (pageType == typeof(InstallPage))
			return Strings.Installer_Install_Title;
		if (pageType == typeof(ChannelPage))
			return "Channel";
		if (pageType == typeof(InstallerModsPage))
			return Strings.Menu_Mods_Title;
		if (pageType == typeof(InstallerAppearancePage))
			return Strings.Menu_Appearance_Title;
		if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage))
			return "Manager";
		if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage))
			return "Extensions";
		if (pageType == typeof(CompletionPage))
			return Strings.Installer_Completion_Title;
		return Strings.Installer_Install_Title;
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
		if (Voidstrap.Utility.Platform.IsLinux && e.Content is DependencyObject content)
		{
			_linuxTextContent = content;
			Voidstrap.UI.LinuxTextGuard.AttachOwner(content, this);
		}
	}

	private void RootFrame_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (e.WidthChanged && _linuxTextContent != null)
		{
			Voidstrap.UI.LinuxTextGuard.CorrectOwner(_linuxTextContent, this);
		}
	}
}
