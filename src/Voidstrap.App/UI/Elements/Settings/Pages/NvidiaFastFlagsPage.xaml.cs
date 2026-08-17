using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class NvidiaFastFlagsPage : UiPage{
	private readonly NvidiaFastFlagsViewModel _viewModel;

	public NvidiaFastFlagsPage()
	{
		_viewModel = new NvidiaFastFlagsViewModel();
		base.DataContext = _viewModel;
		InitializeComponent();
	}

	private void OpenRawEditor_Click(object sender, RoutedEventArgs e)
	{
		base.NavigationService?.Navigate(new NvidiaFFlagEditorPage());
	}

	private bool _applying;

	private async void Apply_Click(object sender, RoutedEventArgs e)
	{
		if (_applying)
			return;
		_applying = true;
		System.Windows.Controls.Control? button = sender as System.Windows.Controls.Control;
		if (button != null)
			button.IsEnabled = false;
		try
		{
			System.Collections.Generic.List<Voidstrap.Models.NvidiaEditorEntry> staged = _viewModel.StageEntries();
			if (await NvidiaApplyFlow.RunAsync(staged))
				_viewModel.ReloadFromDriver();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to apply NVIDIA settings:\n" + ex.Message, MessageBoxImage.Error);
		}
		finally
		{
			_applying = false;
			if (button != null)
				button.IsEnabled = true;
		}
	}
}
