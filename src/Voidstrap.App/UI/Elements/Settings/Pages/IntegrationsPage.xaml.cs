using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class IntegrationsPage : UiPage{
	private readonly IntegrationsViewModel _viewModel;
	private Window? _ownerWindow;

	public IntegrationsPage()
	{
		ActivityWatcher? activeWatcher = Watcher.Current?.ActivityWatcher;
		_viewModel = new IntegrationsViewModel(activeWatcher ?? new ActivityWatcher(), activeWatcher == null);
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
		_viewModel.OnPropertyChanged("UncapFpsToggleEnabled");
		_viewModel.OnPropertyChanged("UncapFPS");
		StartRpcPreview();
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		StopRpcPreview();
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
		StopRpcPreview();
		_viewModel.Dispose();
		DataContext = null;
	}

	private void ValidateUInt32(object sender, TextCompositionEventArgs e)
	{
		e.Handled = !uint.TryParse(e.Text, out var _);
	}

	public void CustomIntegrationSelection(object sender, SelectionChangedEventArgs e)
	{
		_viewModel.SelectedCustomIntegration = (CustomIntegration)((ListBox)sender).SelectedItem;
		_viewModel.OnPropertyChanged("SelectedCustomIntegration");
	}

	private void ToggleSwitch_Checked(object sender, RoutedEventArgs e)
	{
	}

	private void OpenCustomEditor_Click(object sender, RoutedEventArgs e)
	{
		base.NavigationService.Navigate(new MobilePage());
	}

	private void OpenMobileExplain_Click(object sender, RoutedEventArgs e)
	{
		base.NavigationService.Navigate(new MobilePageExplain());
	}
}
