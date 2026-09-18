using System;
using System.Windows;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class NewsPage : UiPage{
	private readonly NewsViewModel _viewModel = new NewsViewModel();
	private Window? _ownerWindow;

	public NewsPage()
	{
		base.DataContext = _viewModel;
		InitializeComponent();
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
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		if (_ownerWindow != null)
		{
			_ownerWindow.Closed -= OnOwnerWindowClosed;
			_ownerWindow = null;
		}
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
		_viewModel.Dispose();
	}

	private void OpenItemButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (sender is FrameworkElement { DataContext: NewsItem dataContext })
			{
				NewsItemDialog newsItemDialog = new NewsItemDialog(dataContext);
				newsItemDialog.Owner = Window.GetWindow((DependencyObject)(object)this);
				newsItemDialog.ShowOwnedDialog();
			}
		}
		catch (Exception)
		{
		}
	}
}
