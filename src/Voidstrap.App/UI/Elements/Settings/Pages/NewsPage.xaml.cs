using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class NewsPage : UiPage{
	private readonly NewsViewModel _viewModel = new NewsViewModel();
	private readonly ForumsViewModel _forumsViewModel = new ForumsViewModel();
	private Window? _ownerWindow;

	public NewsPage()
	{
		base.DataContext = _viewModel;
		InitializeComponent();
		ForumsRoot.DataContext = _forumsViewModel;
		Loaded += OnPageLoaded;
		Unloaded += OnPageUnloaded;
	}

	private void OnPageLoaded(object sender, RoutedEventArgs e)
	{
		Window? owner = Window.GetWindow(this);
		if (!ReferenceEquals(_ownerWindow, owner))
		{
			if (_ownerWindow != null)
			{
				_ownerWindow.Closed -= OnOwnerWindowClosed;
			}
			_ownerWindow = owner;
			if (_ownerWindow != null)
			{
				_ownerWindow.Closed += OnOwnerWindowClosed;
			}
		}
		_forumsViewModel.Activate();
		_forumsViewModel.RefreshCommand.Execute(null);
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		_forumsViewModel.Deactivate();
	}

	private void OnOwnerWindowClosed(object? sender, EventArgs e)
	{
		if (_ownerWindow != null)
		{
			_ownerWindow.Closed -= OnOwnerWindowClosed;
			_ownerWindow = null;
		}
		Loaded -= OnPageLoaded;
		Unloaded -= OnPageUnloaded;
		_forumsViewModel.Deactivate();
		_viewModel.Dispose();
	}

	public void SelectForumsTab()
	{
		NewsTabs.SelectedIndex = 0;
	}

	private void OpenItemButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (sender is FrameworkElement { DataContext: NewsItem dataContext })
			{
				NewsItemDialog newsItemDialog = new NewsItemDialog(dataContext);
				newsItemDialog.Owner = Window.GetWindow((DependencyObject)(object)this);
				newsItemDialog.ShowDialog();
			}
		}
		catch (Exception)
		{
		}
	}
}
