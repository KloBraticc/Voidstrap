using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class FastFlagEditorWarningPage : UiPage{
	private FastFlagEditorWarningViewModel _viewModel;

	public FastFlagEditorWarningPage()
	{
		_viewModel = new FastFlagEditorWarningViewModel(this);
		base.DataContext = _viewModel;
		InitializeComponent();
	}

	private void Page_Loaded(object sender, RoutedEventArgs e)
	{
		_viewModel.StartCountdown();
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		_viewModel.StopCountdown();
	}
}
