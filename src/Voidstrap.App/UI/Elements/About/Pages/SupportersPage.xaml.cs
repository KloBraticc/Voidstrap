using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.ViewModels.About;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class SupportersPage : UiPage{
	private readonly SupportersViewModel _viewModel = new SupportersViewModel();

	public SupportersPage()
	{
		base.DataContext = _viewModel;
		InitializeComponent();
	}

	private void UiPage_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		_viewModel.WindowResizeEvent?.Invoke(sender, e);
	}

	private void UiPage_Unloaded(object sender, RoutedEventArgs e)
	{
		_viewModel.Dispose();
	}
}
