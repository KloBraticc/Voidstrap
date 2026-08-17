using System;
using System.ComponentModel;
using System.Windows;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Dialogs;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LanguageSelectorDialog : WpfUiWindow
{
	private readonly LanguageSelectorViewModel _viewModel;

	private bool _accepted;

	public LanguageSelectorDialog()
	{
		TranslationService.Initialize();
		LiveLanguageRefresher.Initialize();
		ResourceProxy.Inject();
		_viewModel = new LanguageSelectorViewModel();
		DataContext = _viewModel;
		InitializeComponent();
		_viewModel.CloseRequestEvent += OnCloseRequest;
		Closing += OnClosing;
		Closed += OnClosed;
	}

	private void OnCloseRequest(object? sender, bool accepted)
	{
		_accepted = accepted;
		DialogResult = accepted;
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		_viewModel.Cancel();
		DialogResult = false;
	}

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		if (!_accepted)
		{
			_viewModel.Cancel();
		}
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		_viewModel.CloseRequestEvent -= OnCloseRequest;
		Closing -= OnClosing;
		Closed -= OnClosed;
		DataContext = null;
	}
}
